using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Process state machine for the DSH harness: spawn/kill/restart, port liveness, integrity-based
// elevation, and the self-heal poll. Instance class (one per session); depends only on
// Config / Win32 / Logging. The constructor has NO side effects, so headless modes can safely
// construct one for probing.
//
// All state transitions happen inside `stateLock`; external callers only trigger actions
// (Start/Stop/Restart), never write the state directly.
public enum DshState { Stopped, Starting, Running, Stopping }

// Launch spec the --restart-helper replays. Mirrors dshmarket's restartLaunch(): the exact file,
// args, cwd and log destination of the running host, plus the port the replacement must bind.
class RestartSpec
{
    public int Port;
    public string File;
    public string Cwd;
    public string LogPath;
    public string[] Args;
}

class DshProcess
{
    // ---- timing constants (moved verbatim; values unchanged) ----
    const int PortWaitMs = 30000;                 // max time to wait for the port to come up
    const int PortFreeWaitMs = 8000;              // max time to wait for the port to be released
    const int PortPollStepMs = 200;               // sleep step while polling port open/free
    const int PortProbeTimeoutMs = 300;           // TCP connect timeout in PortOpen
    const int KillSleepMs = 300;                  // pause after a kill before checking liveness
    const int ProcessWaitExitMs = 3000;           // WaitForExit timeout for a killed process
    const int TaskkillWaitMs = 8000;              // taskkill subprocess wait timeout
    const int NetstatWaitMs = 5000;               // netstat subprocess wait timeout
    const int ElevatedKillWaitMs = 30000;         // elevated kill helper wait timeout
    const int AutoRestartStartCooldownMs = 10000; // min age of a start attempt before auto-restart
    const int AutoRestartRetryCooldownMs = 30000; // min gap between two auto-restart attempts
    const int SoftRestartDeadlineMs = 90000;      // max time to wait for the soft-restart handoff
    const int SoftRestartPortFreeMs = 30000;      // helper: max time to wait for the old port to release
    const int SoftRestartPortUpMs = 20000;        // helper: max time to wait for the replacement to bind

    readonly AppConfig cfg;
    readonly object stateLock = new object();
    DshState state = DshState.Stopped;   // all transitions happen under stateLock
    Process dshProc;
    bool userStopped;                    // no longer publicly writable
    int lastStartTick;
    int lastAutoRestartTick;
    bool autoRestartEnabled;
    Win32.IntegrityLevel selfIntegrity;
    // ---- plugin-market style soft restart (ported from dshmarket's restart.ts) ----
    // The tray schedules a detached --restart-helper, lets the old host go, and the helper
    // waits for the port to release, respawns the EXACT launch invocation and confirms it binds.
    // The old kill+respawn path remains as the fallback.
    Process restartHelper;
    bool softRestartActive;
    int softRestartDeadlineTick;
    string softRestartSpecPath;
    int softRestartOldPid;
    TaskCompletionSource<bool> softRestartTcs;

    public DshProcess(AppConfig config)
    {
        cfg = config;
    }

    // current state snapshot (thread-safe)
    public DshState State
    {
        get { lock (stateLock) return state; }
    }

    // integrity level of THIS tray process; set by the caller (no side effects in the ctor)
    public Win32.IntegrityLevel SelfIntegrity
    {
        get { return selfIntegrity; }
        set { selfIntegrity = value; }
    }

    public bool AutoRestartEnabled
    {
        get { lock (stateLock) return autoRestartEnabled; }
        set { lock (stateLock) autoRestartEnabled = value; }
    }

    public void ToggleAutoRestart()
    {
        AutoRestartEnabled = !AutoRestartEnabled;
        Config.SaveAutoRestart(AutoRestartEnabled);
        Logging.Log("autoRestart = " + AutoRestartEnabled);
    }

    // self-heal poll: only Stopped and not user-stopped, past the cooldowns, triggers a start.
    // The CAS (Stopped->Starting) is done under the lock; the actual spawn/wait runs as a
    // fire-and-forget flow so the poll (UI timer) never blocks or double-starts.
    public bool PollAutoRestart()
    {
        bool trigger;
        lock (stateLock)
        {
            trigger = state == DshState.Stopped && autoRestartEnabled && !userStopped && !softRestartActive &&
                Environment.TickCount - lastStartTick > AutoRestartStartCooldownMs &&
                Environment.TickCount - lastAutoRestartTick > AutoRestartRetryCooldownMs;
            if (trigger)
            {
                lastAutoRestartTick = Environment.TickCount;
                state = DshState.Starting;
            }
        }
        if (trigger)
        {
            Logging.Log("AutoRestart: harness is down, restarting");
#pragma warning disable 4014 // fire-and-forget is intentional (poll must not await/block)
            Task.Run(() => StartFlow());
#pragma warning restore 4014
            return true;
        }
        return false;
    }

    // Build the cmd wrapper command with %VAR% placeholders. The actual node/entry/log values
    // are passed via environment variables (ApplyLaunchEnv) so cmd expands them literally — a
    // value containing `& | ^ ( ) < >` stays literal and cannot break the quoting structure or
    // inject commands. Windows paths cannot contain `"`, so the quote structure is safe.
    public static string BuildLaunchCmd()
    {
        // dsh web (>= rc.8) opens the browser by default; --no-open keeps that from spawning a new
        // tab on every start/restart. The tray's own ReloadAppWindow refreshes the existing app window.
        return "/c \"\"%DSH_TRAY_NODE%\" \"%DSH_TRAY_ENTRY%\" web --no-open >> \"%DSH_TRAY_LOG%\" 2>&1\"";
    }

    // copy the launch parameters into the child environment (must run before Process.Start)
    public void ApplyLaunchEnv(ProcessStartInfo psi, string dshLog)
    {
        psi.EnvironmentVariables["DSH_TRAY_NODE"] = cfg.NodePath;
        psi.EnvironmentVariables["DSH_TRAY_ENTRY"] = cfg.DshEntry;
        psi.EnvironmentVariables["DSH_TRAY_LOG"] = dshLog;
        // a value containing % would be double-expanded when cmd references the variable inside
        // BuildLaunchCmd's quotes; it is almost always a misconfigured path. Warn only (never
        // block): the path may still be valid if the % pair is a legit cmd variable.
        WarnIfContainsPercent("DSH_TRAY_NODE", cfg.NodePath);
        WarnIfContainsPercent("DSH_TRAY_ENTRY", cfg.DshEntry);
        WarnIfContainsPercent("DSH_TRAY_LOG", dshLog);
    }

    static void WarnIfContainsPercent(string name, string value)
    {
        if (value == null || value.IndexOf('%') < 0) return;
        Logging.Log("path contains %, cmd will double-expand: " + name + "=" + value);
    }

    // result of the core spawn
    enum StartResult { Launched, AlreadyUp, Failed }

    // core spawn (no state transition): precondition checks + cmd spawn. Returns whether the
    // process was actually launched. Called only while state == Starting.
    StartResult StartCore()
    {
        lastStartTick = Environment.TickCount;
        // adopt an already-running harness (e.g. left up by a previous tray session): never
        // spawn a second instance — it dies on the port conflict and the Exited handler would
        // wrongly collapse the state to Stopped
        if (PortServedByDsh())
        {
            Logging.Log("StartCore: existing harness on port " + cfg.Port + ", adopting");
            return StartResult.AlreadyUp;
        }
        if (cfg.NodePath == null || !File.Exists(cfg.NodePath)) { Logging.Log("StartDsh failed: node.exe not found (set 'node' in dshtray.ini)"); return StartResult.Failed; }
        if (cfg.DshEntry == null || !File.Exists(cfg.DshEntry)) { Logging.Log("StartDsh failed: dsh entry not found (set 'dshentry' in dshtray.ini)"); return StartResult.Failed; }
        try
        {
            // spawn via cmd with stdout/stderr redirected to a FILE: the harness must not
            // depend on the tray's lifetime (a broken pipe EPIPE kills node in ~1s)
            string dshLog = Path.Combine(Path.GetDirectoryName(Logging.LogPath), "harness.log");
            // the log dir may have been deleted since init (or be otherwise absent); guarantee
            // it exists so cmd's `>>` redirection never fails the whole launch line
            try { Directory.CreateDirectory(Path.GetDirectoryName(dshLog)); } catch (Exception ex) { Logging.Log("StartCore: ensure harness.log dir failed: " + ex.Message); }
            // harness.log is appended by the child and never goes through Log()'s rotation; rotate
            // it here before a fresh spawn (the previous harness should be gone, so the file is free)
            Logging.RotateIfLarge(dshLog);
            // WorkingDirectory fallback: prefer the configured work dir; when unset, fall back
            // to the dsh entry's directory; only if both are empty do we leave it as the current dir
            string workDir = cfg.DshWorkDir;
            if (string.IsNullOrEmpty(workDir) && !string.IsNullOrEmpty(cfg.DshEntry))
                workDir = Path.GetDirectoryName(cfg.DshEntry);
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = BuildLaunchCmd(),
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ApplyLaunchEnv(psi, dshLog);
            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += dshProcExited;
            proc.Start();
            lock (stateLock) { dshProc = proc; }
            Logging.Log("StartDsh: launched pid=" + proc.Id + " (log=" + dshLog + ")");
            return StartResult.Launched;
        }
        catch (Exception ex) { Logging.Log("StartDsh failed: " + ex.Message); return StartResult.Failed; }
    }

    // true when the port is already served by a dsh harness (adopt case). Identity check is
    // node + a dsh-looking command line so a non-node occupant, or an unrelated node process,
    // never blocks a start or gets adopted. WMI runs 100-300ms, but these call sites are all
    // low-frequency (one per start/stop/crash), so the cost is acceptable.
    bool PortServedByDsh()
    {
        if (!PortOpen(cfg.Port)) return false;
        int pid = FindPidOnPort(cfg.Port);
        bool served = pid > 0 && LooksLikeDshOrElevatedNode(pid);
        if (pid > 0 && !served)
            Logging.Log("PortServedByDsh: pid=" + pid + " node=" + IsNodeProcess(pid) +
                " looksLikeDsh=" + CommandLineLooksLikeDsh(pid));
        return served;
    }

    // Identity check for adoption/stop. A normal dsh harness is verified by command-line markers.
    // When the harness runs elevated (High) and the tray is Medium, the WMI command line may be
    // unreadable and the marker check fails closed. In that case a node process on our configured
    // port with higher integrity than the tray is treated as dsh: for the stop path this is still
    // safe because KillTree will elevate and the elevated helper re-verifies the command line
    // before killing (fail-closed if it is actually an unrelated node).
    bool LooksLikeDshOrElevatedNode(int pid)
    {
        if (!IsNodeProcess(pid)) return false;
        if (CommandLineLooksLikeDsh(pid)) return true;
        Win32.IntegrityLevel target = Win32.GetIntegrity(pid);
        return target != Win32.IntegrityLevel.Unknown && target > selfIntegrity;
    }

    // named handler so it can be unsubscribed before Dispose; uses sender.Id (not dshProc)
    // because dshProc may already reference a newer process by the time this fires
    void dshProcExited(object sender, EventArgs e)
    {
        try
        {
            var p = sender as Process;
            bool current;
            lock (stateLock) { current = p != null && ReferenceEquals(p, dshProc); }
            if (current)
            {
                // During a soft restart the helper owns the port handoff: the old process exiting
                // is EXPECTED and must not collapse the state to Stopped (which would let the
                // auto-restart poll race the helper into spawning a second host).
                bool soft;
                lock (stateLock) { soft = softRestartActive; }
                if (soft)
                {
                    Logging.Log("dsh process exited during soft restart; replacement helper will bring it up");
                    return;
                }
                // probe outside the lock (TCP + maybe netstat): only collapse to Stopped when
                // the port is really down — our process may have died on a port conflict while
                // another node instance still serves the harness
                bool served = PortServedByDsh();
                lock (stateLock)
                {
                    if (p != null && ReferenceEquals(p, dshProc) &&
                        (state == DshState.Running || state == DshState.Starting))
                    {
                        if (served)
                            Logging.Log("dsh process exited but port still served by another node; staying up");
                        else
                            state = DshState.Stopped;
                    }
                }
            }
            Logging.Log("dsh process exited pid=" + (p != null ? p.Id : -1) + (current ? "" : " (stale)"));
        }
        catch { }
    }

    // core stop (no state transition): kill owned process + any node on the port, then wait for
    // the port to free. Called only while state == Stopping. Returns whether the port actually freed.
    bool StopCore()
    {
        bool owned = false;
        int ownedPid = 0;
        Process p = null;
        lock (stateLock)
        {
            if (dshProc != null)
            {
                try { owned = !dshProc.HasExited; ownedPid = dshProc.Id; p = dshProc; } catch { owned = false; }
            }
            // detach + dispose + null are all fast; do them under the lock (no WaitForExit/KillTree here)
            DisposeDshProcLocked();
        }
        if (owned)
        {
            Logging.Log("StopDsh: killing owned pid=" + ownedPid);
            KillTree(ownedPid); // slow; outside the lock
            try { p.WaitForExit(ProcessWaitExitMs); } catch (Exception ex) { Logging.Log("StopDsh WaitForExit failed: " + ex.Message); }
        }

        if (PortOpen(cfg.Port))
        {
            int pid = FindPidOnPort(cfg.Port);
            if (pid > 0)
            {
                // only kill the port owner if it is a node process whose command line looks like
                // dsh (or an elevated node that we cannot read but the elevated helper will
                // re-verify); an unrelated node that happens to hold our port is never killed
                if (LooksLikeDshOrElevatedNode(pid))
                {
                    Logging.Log("StopDsh: killing external pid=" + pid);
                    KillTree(pid);
                }
                else
                {
                    Logging.Log("StopDsh: pid=" + pid + " on port " + cfg.Port + " is not a dsh node process, refusing to kill");
                }
            }
        }
        WaitForPortFree();
        bool freed = !PortOpen(cfg.Port);
        Logging.Log("StopDsh: done, port open=" + !freed);
        return freed;
    }

    // ---- async state machine (external callers trigger actions, never write state) ----

    // CAS Stopped->Starting, then spawn + wait; only transitions to Running if still Starting
    // (so StopAsync always wins and never gets overwritten). Returns when settled.
    public async Task StartAsync()
    {
        lock (stateLock)
        {
            if (state != DshState.Stopped) return;
            state = DshState.Starting;
        }
        await StartFlow();
    }

    // continuation after the Starting handoff: shared by StartAsync and PollAutoRestart
    async Task StartFlow()
    {
        StartResult r = StartCore(); // synchronous spawn (fast, non-blocking); no closure needed
        if (r == StartResult.AlreadyUp)
        {
            // nothing was spawned: the running harness is adopted as ours
            lock (stateLock)
            {
                if (state == DshState.Starting)
                {
                    state = DshState.Running;
                    userStopped = false;
                }
            }
            Logging.Log("StartFlow: adopted existing harness (running)");
            return;
        }
        bool up = (r == StartResult.Launched) && await WaitForPortUpAsync();
        lock (stateLock)
        {
            if (state == DshState.Starting)
            {
                state = up ? DshState.Running : DshState.Stopped;
                if (up) userStopped = false;
                else Logging.Log("StartAsync: start failed or port wait timed out");
            }
        }
    }

    // CAS Running/Starting -> Stopping, kill, then -> Stopped. Stop always beats a concurrent
    // start (the start's final CAS only fires while still Starting, which stop has already moved).
    public async Task StopAsync()
    {
        lock (stateLock)
        {
            if (state != DshState.Running && state != DshState.Starting) return;
            userStopped = true;
            state = DshState.Stopping;
        }
        bool freed = await Task.Run(() => StopCore());
        // a refused stop (e.g. elevated kill declined) can leave a node still serving the port:
        // adopt it instead of lying with a Stopped state over a live harness
        bool served = PortServedByDsh();
        lock (stateLock)
        {
            if (!freed && served)
            {
                Logging.Log("StopAsync: stop failed, adopting running harness (userStopped reset)");
                state = DshState.Running;
                userStopped = false;
            }
            else
            {
                state = DshState.Stopped;
            }
        }
    }

    // Running -> restart. Tries the plugin-market-style soft restart first: a detached helper
    // waits for the port to release, replays the EXACT boot invocation of the running host, and
    // confirms the replacement binds. If any part cannot be prepared, or the handoff fails, the
    // original kill+respawn path (RestartHardCoreAsync) is used instead.
    // Starting or Stopping is a no-op (no double clicks); Stopped delegates to StartAsync.
    public async Task RestartAsync()
    {
        DshState cur;
        lock (stateLock)
        {
            cur = state;
            if (cur == DshState.Starting || cur == DshState.Stopping) return;
            if (cur == DshState.Running) { state = DshState.Stopping; userStopped = true; }
        }
        if (cur == DshState.Stopped)
        {
            await StartAsync();
            return;
        }
        TaskCompletionSource<bool> tcs = null;
        if (await TryStartSoftRestartAsync())
        {
            lock (stateLock) { tcs = softRestartTcs; }
            if (tcs != null)
            {
                // completes when the helper handoff settled OR the hard fallback finished
                await tcs.Task.ConfigureAwait(false);
                return;
            }
            Logging.Log("SoftRestart: no completion source, leaving the poll to settle state");
            return;
        }
        await RestartHardCoreAsync();
    }

    // The original kill+respawn path, now also used as the soft-restart fallback. Safe to call
    // from Stopping (soft restart aborted) as well as Running; Stopped delegates to StartAsync.
    async Task RestartHardCoreAsync()
    {
        DshState cur;
        lock (stateLock)
        {
            cur = state;
            if (cur == DshState.Starting) return;
        }
        if (cur == DshState.Stopped)
        {
            await StartAsync();
            return;
        }
        Logging.Log("=== RestartDsh (hard) ===");
        bool freed = await Task.Run(() => StopCore());
        bool served = PortServedByDsh();
        lock (stateLock)
        {
            if (!freed && served)
            {
                // stop failed but a node still serves the port: adopt, no fresh start
                state = DshState.Running;
                userStopped = false;
                Logging.Log("RestartAsync: stop failed, adopting running harness");
                return;
            }
            state = DshState.Stopped;
            userStopped = false;
        }
        await StartAsync();
    }

    // Prepare + schedule the soft restart. Returns true when the helper handoff is in flight
    // (the poll or the completion source owns the rest); false means the caller must use the
    // hard fallback. Never blocks the UI thread on WMI/netstat/taskkill (all run on the caller's
    // async context via Task.Run where needed).
    async Task<bool> TryStartSoftRestartAsync()
    {
        int pid = FindRestartTargetPid();
        RestartSpec spec;
        if (pid <= 0 || !TryBuildRestartSpec(pid, out spec))
            return false;

        int helperPid;
        string specPath;
        if (!StartRestartHelper(spec, out helperPid, out specPath))
            return false;

        lock (stateLock)
        {
            softRestartActive = true;
            softRestartDeadlineTick = Environment.TickCount + SoftRestartDeadlineMs;
            softRestartSpecPath = specPath;
            softRestartOldPid = pid;
            softRestartTcs = new TaskCompletionSource<bool>();
        }
        Logging.Log("=== RestartDsh (soft) helper pid=" + helperPid + " old pid=" + pid + " ===");

        // On Windows Node's process.kill(SIGTERM) maps to TerminateProcess and never delivers a
        // JS SIGTERM handler (verified), so a "graceful" signal would not dispose DSH's Cordis
        // tree and would orphan its MCP children. The tray is the external supervisor: kill the
        // OLD process tree the hard way (taskkill /T /F, elevation if needed), then let the
        // detached helper wait for the port to release and replay the exact launch. The hard
        // fallback remains available if this stop is refused.
        Logging.Log("SoftRestart: hard-stopping old host pid=" + pid);
        bool freed = await Task.Run(() => StopCore()).ConfigureAwait(false);
        // Abort only when the OLD pid is still serving (usually an integrity mismatch). A
        // replacement that already bound a NEW pid is NOT a failure: the helper handoff is in
        // progress, and aborting there would kill a just-started host and restart it again.
        int curPid = FindPidOnPort(cfg.Port);
        bool oldStillServing = curPid > 0 && curPid == pid && LooksLikeDshOrElevatedNode(curPid);
        bool abort = false;
        lock (stateLock)
        {
            if (!freed && oldStillServing)
            {
                // The old host would not die even with taskkill (usually an integrity mismatch):
                // abort the soft path and let the caller run the full hard fallback, which elevates.
                AbortSoftRestartLocked();
                abort = true;
            }
        }
        if (abort)
        {
            Logging.Log("SoftRestart: old host still serving, aborting to hard fallback");
            KillRestartHelper();
            CleanupRestartFiles(specPath);
            return false;
        }
        return true;
    }

    // Polled from TrayMenu's UI timer via PollSoftRestartAsync. The heavy WMI/netstat probe is
    // offloaded, and success requires the port to be served by a DIFFERENT pid than the old host
    // (softRestartOldPid), so a still-closing host can never be mistaken for the replacement.
    public async Task PollSoftRestartAsync()
    {
        bool active;
        bool expired;
        int oldPid;
        lock (stateLock)
        {
            active = softRestartActive;
            expired = softRestartActive && Environment.TickCount - softRestartDeadlineTick > 0;
            oldPid = softRestartOldPid;
        }
        if (!active) return;
        if (expired)
        {
            FailSoftRestart();
            return;
        }
        if (HelperExitedWithFailure())
        {
            FailSoftRestart();
            return;
        }
        bool success = await Task.Run(() =>
        {
            int pid = FindPidOnPort(cfg.Port);
            return pid > 0 && pid != oldPid && LooksLikeDshOrElevatedNode(pid);
        }).ConfigureAwait(false);
        if (success) CompleteSoftRestartSuccess();
    }

    // The host whose exact command line must be replayed: always the NODE process actually
    // serving the configured port. (The tray's own dshProc is the cmd.exe wrapper, which is not
    // a node process and would make the soft path fall back to hard restart forever; the port
    // owner is the real host in both the tray-launched and the adopted cases.)
    int FindRestartTargetPid()
    {
        int pid = FindPidOnPort(cfg.Port);
        return LooksLikeDshOrElevatedNode(pid) ? pid : 0;
    }

    // Rebuild the boot invocation from the live process command line (same idea as dshArgv() in
    // dshmarket: replay exactly what is running, including execArgv on source launches). The
    // caller passes the node PID serving the port, NOT the cmd wrapper.
    bool TryBuildRestartSpec(int pid, out RestartSpec spec)
    {
        spec = null;
        if (!IsNodeProcess(pid)) return false;
        List<string> toks;
        if (!TryReadCommandLine(pid, out toks) || toks.Count < 2) return false;
        string file = toks[0];
        if (!File.Exists(file))
        {
            // WMI can report the bare name when node was resolved from PATH.
            if (string.Equals(toks[0], "node", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toks[0], "node.exe", StringComparison.OrdinalIgnoreCase))
                file = cfg.NodePath;
            else
                return false;
            if (file == null || !File.Exists(file)) return false;
        }
        string entry = FindDshEntryToken(toks);
        string cwd = cfg.DshWorkDir;
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            cwd = entry != null ? Path.GetDirectoryName(entry) : null;
        string log = Path.Combine(Path.GetDirectoryName(Logging.LogPath), "harness.log");
        if (string.IsNullOrEmpty(log)) log = Path.Combine(Path.GetTempPath(), "dsh-harness.log");
        spec = new RestartSpec
        {
            Port = cfg.Port,
            File = file,
            Cwd = cwd,
            LogPath = log,
            Args = toks.GetRange(1, toks.Count - 1).ToArray()
        };
        return true;
    }

    // The argv token that is the actual dsh entry: prefer the configured entry, then a .js/.ts
    // file (skips --import/tsx runner tokens), then the first existing file token.
    string FindDshEntryToken(List<string> toks)
    {
        if (toks == null) return null;
        foreach (string t in toks)
        {
            if (!string.IsNullOrEmpty(cfg.DshEntry) &&
                string.Equals(t, cfg.DshEntry, StringComparison.OrdinalIgnoreCase)) return t;
        }
        foreach (string t in toks)
        {
            if (!File.Exists(t)) continue;
            string ext = Path.GetExtension(t).ToLowerInvariant();
            if (ext == ".js" || ext == ".ts" || ext == ".mjs" || ext == ".cjs") return t;
        }
        foreach (string t in toks)
            if (File.Exists(t)) return t;
        return null;
    }

    bool StartRestartHelper(RestartSpec spec, out int helperPid, out string specPath)
    {
        helperPid = 0;
        specPath = null;
        try
        {
            specPath = WriteRestartSpec(spec);
            var psi = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = "\"--restart-helper\" \"" + specPath.Replace("\"", "") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)
            };
            var p = Process.Start(psi);
            if (p == null)
            {
                Logging.Log("StartRestartHelper: Process.Start returned null");
                CleanupRestartFiles(specPath);
                return false;
            }
            lock (stateLock) { restartHelper = p; }
            helperPid = p.Id;
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log("StartRestartHelper failed: " + ex.Message);
            CleanupRestartFiles(specPath);
            return false;
        }
    }

    void CompleteSoftRestartSuccess()
    {
        TaskCompletionSource<bool> tcs = null;
        string specPath = null;
        Process h = null;
        lock (stateLock)
        {
            if (!softRestartActive) return;
            softRestartActive = false;
            softRestartDeadlineTick = 0;
            specPath = softRestartSpecPath;
            softRestartSpecPath = null;
            softRestartOldPid = 0;
            tcs = softRestartTcs;
            softRestartTcs = null;
            // Adopt BEFORE declaring Running: the poll must never leave "Running but nobody is
            // tracked" (which would disable auto-restart). If adoption fails, fall back to Stopped.
            h = restartHelper;
            restartHelper = null;
        }
        Logging.Log("SoftRestart: replacement is up");
        CleanupRestartFiles(specPath);
        bool adopted = AdoptReplacementProcess();
        if (!adopted)
        {
            lock (stateLock)
            {
                state = DshState.Stopped;
                userStopped = false;
            }
            Logging.Log("SoftRestart: replacement up but could not adopt; state=Stopped (poll/start will adopt)");
        }
        else
        {
            lock (stateLock)
            {
                state = DshState.Running;
                userStopped = false;
            }
            Logging.Log("SoftRestart: replacement is running");
        }
        if (tcs != null) tcs.TrySetResult(true);
        // The helper may still be alive observing the port; it exits on its own. Never kill it
        // here — it is only an observer by this point.
        if (h != null) { try { h.Dispose(); } catch { } }
    }

    // The helper launched the replacement (cmd -> node), so the tray's dshProc still points at
    // the OLD exited host. Re-bind the process identity to the new host's node PID so crash
    // detection, stop and status keep working after a soft restart.
    bool AdoptReplacementProcess()
    {
        int pid = FindPidOnPort(cfg.Port);
        if (pid <= 0 || !LooksLikeDshOrElevatedNode(pid))
        {
            Logging.Log("AdoptReplacementProcess: no dsh node on port, skipping");
            return false;
        }
        try
        {
            var p = Process.GetProcessById(pid);
            if (p == null) { Logging.Log("AdoptReplacementProcess: GetProcessById returned null"); return false; }
            p.EnableRaisingEvents = true;
            p.Exited += dshProcExited;
            lock (stateLock)
            {
                if (dshProc != null && !ReferenceEquals(dshProc, p))
                {
                    try { dshProc.Exited -= dshProcExited; } catch { }
                    try { dshProc.Dispose(); } catch { }
                    dshProc = null;
                }
                dshProc = p;
                lastStartTick = Environment.TickCount;
            }
            Logging.Log("SoftRestart: adopted new harness process pid=" + pid);
            return true;
        }
        catch (Exception ex) { Logging.Log("AdoptReplacementProcess failed: " + ex.Message); return false; }
    }

    void FailSoftRestart()
    {
        TaskCompletionSource<bool> tcs = null;
        string specPath = null;
        lock (stateLock)
        {
            if (!softRestartActive) return;
            softRestartActive = false;
            softRestartDeadlineTick = 0;
            specPath = softRestartSpecPath;
            softRestartSpecPath = null;
            softRestartOldPid = 0;
            tcs = softRestartTcs;
            softRestartTcs = null;
            // Keep Stopping: the hard fallback owns the stop/start transition from here.
            state = DshState.Stopping;
            userStopped = true;
        }
        Logging.Log("SoftRestart failed; falling back to hard restart");
        KillRestartHelper();
        CleanupRestartFiles(specPath);
#pragma warning disable 4014 // fire-and-forget is intentional: called from the 3s poll timer
        Task.Run(async () =>
        {
            try { await RestartHardCoreAsync(); }
            finally { if (tcs != null) tcs.TrySetResult(true); }
        });
#pragma warning restore 4014
    }

    void AbortSoftRestartLocked()
    {
        softRestartActive = false;
        softRestartDeadlineTick = 0;
        softRestartSpecPath = null;
        softRestartOldPid = 0;
        softRestartTcs = null;
    }

    void KillRestartHelper()
    {
        Process h = null;
        lock (stateLock) { h = restartHelper; restartHelper = null; }
        if (h == null) return;
        try { if (!h.HasExited) h.Kill(); } catch { }
        try { h.Dispose(); } catch { }
    }

    bool HelperExitedWithFailure()
    {
        try
        {
            string resultPath = null;
            bool helperFailed = false;
            lock (stateLock)
            {
                resultPath = softRestartSpecPath == null ? null : softRestartSpecPath + ".result";
                Process h = restartHelper;
                if (h != null)
                {
                    try
                    {
                        if (h.HasExited && h.ExitCode != 0) helperFailed = true;
                    }
                    catch { /* process already disposed; not a failure signal */ }
                }
            }
            if (helperFailed) return true;
            if (resultPath != null && File.Exists(resultPath))
            {
                // Encoding.UTF8 on .NET Framework may prepend a BOM; strip it before comparing.
                string first = File.ReadAllLines(resultPath, Encoding.UTF8)[0].Trim().TrimStart('\uFEFF');
                if (first == "FAIL") return true;
            }
        }
        catch { }
        return false;
    }

    // ---- restart-helper support (ported from dshmarket's restart.ts) ----

    // Write the respawn spec the detached helper reads. Base64 per line keeps arbitrary
    // args (paths, quotes, metachars) lossless and avoids Windows command-line quoting.
    static string WriteRestartSpec(RestartSpec spec)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-tray");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "restart-" + Guid.NewGuid().ToString("N") + ".spec");
        using (var w = new StreamWriter(path, false, Encoding.UTF8))
        {
            w.WriteLine("dsh-tray-restart-spec-v1");
            w.WriteLine(spec.Port.ToString());
            w.WriteLine(EncodeSpecLine(spec.File));
            w.WriteLine(EncodeSpecLine(spec.Cwd ?? ""));
            w.WriteLine(EncodeSpecLine(spec.LogPath ?? ""));
            w.WriteLine(spec.Args.Length.ToString());
            foreach (string a in spec.Args) w.WriteLine(EncodeSpecLine(a));
        }
        return path;
    }

    static RestartSpec ReadRestartSpec(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using (var r = new StreamReader(path, Encoding.UTF8))
            {
                if (r.ReadLine() != "dsh-tray-restart-spec-v1") return null;
                int port;
                if (!int.TryParse(r.ReadLine(), out port)) return null;
                string file = DecodeSpecLine(r.ReadLine());
                string cwd = DecodeSpecLine(r.ReadLine());
                string log = DecodeSpecLine(r.ReadLine());
                int n;
                if (!int.TryParse(r.ReadLine(), out n) || n < 0 || n > 256) return null;
                string[] args = new string[n];
                for (int i = 0; i < n; i++) args[i] = DecodeSpecLine(r.ReadLine());
                return new RestartSpec { Port = port, File = file, Cwd = cwd, LogPath = log, Args = args };
            }
        }
        catch (Exception ex) { Logging.Log("ReadRestartSpec failed: " + ex.Message); return null; }
    }

    static string EncodeSpecLine(string s)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
    }

    static string DecodeSpecLine(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
        catch { return ""; }
    }

    static void WriteRestartResult(string specPath, bool ok, string message)
    {
        if (string.IsNullOrEmpty(specPath)) return;
        try
        {
            File.WriteAllText(specPath + ".result",
                (ok ? "OK" : "FAIL") + Environment.NewLine + (message ?? ""), Encoding.UTF8);
        }
        catch (Exception ex) { Logging.Log("WriteRestartResult failed: " + ex.Message); }
    }

    static void CleanupRestartFiles(string specPath)
    {
        if (string.IsNullOrEmpty(specPath)) return;
        try { File.Delete(specPath); } catch { }
        try { File.Delete(specPath + ".result"); } catch { }
    }

    // Detached helper: validates its spec, waits for the old port to release (TCP probe, not
    // bind — binding would hold the port the replacement needs), then replays the exact launch.
    // Success requires the port to be served by a DIFFERENT pid than the one recorded before the
    // spawn; a still-open OLD host (or a replacement that died on EADDRINUSE) is a FAILURE, and
    // the spawned wrapper tree is killed so it cannot become an untracked host.
    public static bool RunRestartHelper(string specPath)
    {
        Logging.Log("RestartHelper: starting spec=" + specPath);
        if (!IsTrustedRestartSpecPath(specPath))
        {
            Logging.Log("RestartHelper: refusing untrusted spec path");
            WriteRestartResult(specPath, false, "untrusted spec path");
            CleanupRestartFiles(specPath);
            return false;
        }
        RestartSpec spec = ReadRestartSpec(specPath);
        if (spec == null)
        {
            WriteRestartResult(specPath, false, "spec unreadable");
            CleanupRestartFiles(specPath);
            return false;
        }
        var dp = new DshProcess(new AppConfig());
        dp.SelfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
        int beforePid = dp.FindPidOnPort(spec.Port);
        int waited = 0;
        while (dp.PortOpen(spec.Port) && waited < SoftRestartPortFreeMs)
        {
            Thread.Sleep(PortPollStepMs + 50); // ~250ms
            waited += PortPollStepMs + 50;
        }
        bool oldStillUp = dp.PortOpen(spec.Port);
        if (oldStillUp)
            Logging.Log("RestartHelper: port " + spec.Port + " still in use after " + SoftRestartPortFreeMs + "ms; trying replacement anyway");
        Thread.Sleep(KillSleepMs); // 300ms TIME_WAIT cushion

        Process replacement = null;
        string err = null;
        try
        {
            if (!string.IsNullOrEmpty(spec.LogPath)) Logging.RotateIfLarge(spec.LogPath);
            replacement = SpawnReplacement(spec, out err);
        }
        catch (Exception ex)
        {
            err = ex.Message;
            Logging.Log("RestartHelper: spawn failed: " + err);
            WriteRestartResult(specPath, false, "spawn failed: " + err);
            CleanupRestartFiles(specPath);
            return false;
        }
        if (replacement == null)
        {
            Logging.Log("RestartHelper: no replacement (" + (err ?? "unknown") + ")");
            WriteRestartResult(specPath, false, err ?? "spawn failed");
            CleanupRestartFiles(specPath);
            return false;
        }

        int upWaited = 0;
        bool up = false;
        while (upWaited < SoftRestartPortUpMs)
        {
            if (dp.PortOpen(spec.Port)) { up = true; break; }
            Thread.Sleep(500);
            upWaited += 500;
        }
        int afterPid = dp.FindPidOnPort(spec.Port);
        bool boundNew = up && afterPid > 0 && (beforePid <= 0 || afterPid != beforePid) &&
            dp.LooksLikeDshOrElevatedNode(afterPid);
        Logging.Log("RestartHelper: wrapper pid=" + replacement.Id + " actual node pid=" + afterPid +
            " up=" + up + " oldStillUp=" + oldStillUp + " beforePid=" + beforePid + " boundNew=" + boundNew);
        if (!boundNew)
        {
            Logging.Log("RestartHelper: port not rebound by a NEW process; killing wrapper pid=" + replacement.Id);
            try { dp.KillTree(replacement.Id); }
            catch (Exception ex) { Logging.Log("RestartHelper: kill replacement failed: " + ex.Message); }
            WriteRestartResult(specPath, false,
                "old host did not release the port or the replacement did not bind as a new pid");
            CleanupRestartFiles(specPath);
            return false;
        }
        WriteRestartResult(specPath, true, "");
        CleanupRestartFiles(specPath);
        return true;
    }

    // Fail-closed: --restart-helper only accepts spec files under the tray's LocalAppData dir.
    static bool IsTrustedRestartSpecPath(string path)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-tray");
            string full = Path.GetFullPath(path);
            string dirFull = Path.GetFullPath(dir);
            if (!full.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            string name = Path.GetFileName(full);
            return name.StartsWith("restart-", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".spec", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Windows default: replay through the same cmd /c ... >> log 2>&1 wrapper the tray uses at
    // first launch — CreateNoWindow keeps it hidden and the child keeps the original log
    // semantics (no PowerShell NativeCommandError noise). PowerShell -WindowStyle Hidden remains
    // the fallback for the (theoretical) case a token contains a double quote, which cmd's
    // quote grammar cannot represent safely. POSIX keeps a direct spawn.
    static Process SpawnReplacement(RestartSpec spec, out string error)
    {
        error = null;
        string wd = (!string.IsNullOrEmpty(spec.Cwd) && Directory.Exists(spec.Cwd)) ? spec.Cwd : string.Empty;
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            bool hasQuote = ContainsDoubleQuote(spec.File) || ContainsDoubleQuote(spec.LogPath) || ContainsDoubleQuote(spec.Args);
            bool hasPercent = ContainsPercent(spec.File) || ContainsPercent(spec.LogPath) || ContainsPercent(spec.Args);
            if (!hasQuote && !hasPercent)
            {
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                if (!File.Exists(cmd)) cmd = "cmd.exe";
                string inner = CmdQuote(spec.File) + " " + JoinCmdArgs(spec.Args);
                if (!string.IsNullOrEmpty(spec.LogPath))
                    inner += " >> " + CmdQuote(spec.LogPath) + " 2>&1";
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = "/c \"" + inner + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = wd
                };
                psi.EnvironmentVariables["DSH_TRAY_NODE"] = spec.File;
                psi.EnvironmentVariables["DSH_TRAY_ENTRY"] = spec.Args.Length > 0 ? spec.Args[0] : "";
                psi.EnvironmentVariables["DSH_TRAY_LOG"] = spec.LogPath ?? "";
                return Process.Start(psi);
            }
            if (hasPercent && !hasQuote)
            {
                // cmd.exe would expand %VAR% even inside double quotes; PowerShell single quotes keep it literal.
                string psCmd = "& " + PwshQuote(spec.File) + " " + JoinPwshArgs(spec.Args);
                if (!string.IsNullOrEmpty(spec.LogPath))
                    psCmd += " >> " + PwshQuote(spec.LogPath) + " 2>&1";
                string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershell)) powershell = "powershell.exe";
                var psi2 = new ProcessStartInfo
                {
                    FileName = powershell,
                    Arguments = "-NoProfile -WindowStyle Hidden -Command \"" + psCmd + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = wd
                };
                psi2.EnvironmentVariables["DSH_TRAY_NODE"] = spec.File;
                psi2.EnvironmentVariables["DSH_TRAY_ENTRY"] = spec.Args.Length > 0 ? spec.Args[0] : "";
                psi2.EnvironmentVariables["DSH_TRAY_LOG"] = spec.LogPath ?? "";
                return Process.Start(psi2);
            }
            // A literal double quote cannot be represented safely through our cmd/PowerShell quote
            // framing; fail closed so the tray hard-restarts instead of spawning a mangled command.
            error = "replay args contain a double quote; soft restart not supported";
            return null;
        }
        var psi3 = new ProcessStartInfo
        {
            FileName = spec.File,
            Arguments = JoinPosixArgs(spec.Args),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = wd
        };
        psi3.EnvironmentVariables["DSH_TRAY_NODE"] = spec.File;
        psi3.EnvironmentVariables["DSH_TRAY_ENTRY"] = spec.Args.Length > 0 ? spec.Args[0] : "";
        psi3.EnvironmentVariables["DSH_TRAY_LOG"] = spec.LogPath ?? "";
        return Process.Start(psi3);
    }

    static bool ContainsDoubleQuote(string s)
    {
        return !string.IsNullOrEmpty(s) && s.IndexOf('"') >= 0;
    }

    static bool ContainsDoubleQuote(string[] args)
    {
        if (args == null) return false;
        foreach (string a in args) if (ContainsDoubleQuote(a)) return true;
        return false;
    }

    static bool ContainsPercent(string s)
    {
        return s != null && s.IndexOf('%') >= 0;
    }

    static bool ContainsPercent(string[] args)
    {
        if (args == null) return false;
        foreach (string a in args) if (ContainsPercent(a)) return true;
        return false;
    }

    static string CmdQuote(string s)
    {
        return "\"" + (s ?? "").Replace("\"", "") + "\"";
    }

    static string JoinCmdArgs(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(CmdQuote(args[i]));
        }
        return sb.ToString();
    }

    static string PwshQuote(string s)
    {
        return "'" + (s ?? "").Replace("'", "''") + "'";
    }

    static string JoinPwshArgs(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(PwshQuote(args[i]));
        }
        return sb.ToString();
    }

    static string JoinPosixArgs(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            string a = args[i] ?? "";
            bool needQuote = a.IndexOf(' ') >= 0 || a.Length == 0;
            if (needQuote) sb.Append('"').Append(a.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            else sb.Append(a);
        }
        return sb.ToString();
    }

    // WMI command line of the running host, split into argv-like tokens the helper replays.
    static bool TryReadCommandLine(int pid, out List<string> tokens)
    {
        tokens = null;
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    object cl = obj["CommandLine"];
                    if (cl == null) continue;
                    tokens = SplitCommandLine(cl.ToString());
                    return tokens != null && tokens.Count > 0;
                }
            }
        }
        catch (Exception ex) { Logging.Log("TryReadCommandLine failed: " + ex.Message); }
        return false;
    }

    static List<string> SplitCommandLine(string cmdLine)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(cmdLine)) return parts;
        var cur = new StringBuilder();
        bool inQuotes = false;
        bool tokenStarted = false; // quoted "" is a real empty argument
        for (int i = 0; i < cmdLine.Length; i++)
        {
            char c = cmdLine[i];
            if (c == '\\')
            {
                // CommandLineToArgvW parity: an ODD run of backslashes escapes the following
                // quote (the quote stays in the token); an EVEN run means the quote is a delimiter.
                int backslashes = 0;
                while (i < cmdLine.Length && cmdLine[i] == '\\') { backslashes++; i++; }
                if (i < cmdLine.Length && cmdLine[i] == '"')
                {
                    for (int j = 0; j < backslashes / 2; j++) cur.Append('\\');
                    if ((backslashes & 1) == 1) { cur.Append('"'); tokenStarted = true; i++; }
                    else { inQuotes = !inQuotes; tokenStarted = true; i++; }
                }
                else
                {
                    for (int j = 0; j < backslashes; j++) cur.Append('\\');
                    tokenStarted = true;
                    i--; // process the non-backslash char on the next loop iteration
                }
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                continue;
            }
            if ((c == ' ' || c == '\t') && !inQuotes)
            {
                if (tokenStarted) { parts.Add(cur.ToString()); cur.Length = 0; tokenStarted = false; }
                continue;
            }
            cur.Append(c);
            tokenStarted = true;
        }
        if (tokenStarted) parts.Add(cur.ToString());
        return parts;
    }

    // non-blocking port wait: Task.Delay instead of Thread.Sleep; same bounds and logging.
    // Returns whether the port came up.
    async Task<bool> WaitForPortUpAsync()
    {
        int waited = 0;
        while (!PortOpen(cfg.Port) && waited < PortWaitMs)
        {
            await Task.Delay(PortPollStepMs).ConfigureAwait(false);
            waited += PortPollStepMs;
        }
        bool up = PortOpen(cfg.Port);
        Logging.Log("WaitForPortUpAsync: waited=" + waited + "ms up=" + up);
        return up;
    }

    void WaitForPortFree()
    {
        int waited = 0;
        while (PortOpen(cfg.Port) && waited < PortFreeWaitMs)
        {
            Thread.Sleep(PortPollStepMs);
            waited += PortPollStepMs;
        }
        if (waited >= PortFreeWaitMs && PortOpen(cfg.Port)) Logging.Log("WaitForPortFree: timed out, port still open");
    }

    // detach the Exited handler, dispose and null the process object. MUST be called under
    // stateLock (all dshProc reads/writes are in the lock); the operations are all fast.
    void DisposeDshProcLocked()
    {
        if (dshProc != null)
        {
            try { dshProc.Exited -= dshProcExited; } catch { }
            try { dshProc.Dispose(); } catch { }
            dshProc = null;
        }
    }

    public void Dispose()
    {
        Process h = null;
        lock (stateLock)
        {
            if (softRestartActive)
            {
                softRestartActive = false;
                softRestartDeadlineTick = 0;
                softRestartSpecPath = null;
                softRestartOldPid = 0;
                if (softRestartTcs != null) softRestartTcs.TrySetResult(false);
                softRestartTcs = null;
                // The helper is intentionally left running: if the tray exits mid-handoff it may
                // still be the only thing bringing the host back. Only release our handle here; the
                // helper deletes the spec/result files itself when it finishes, so do NOT delete
                // them now (it may not have read them yet).
                h = restartHelper;
                restartHelper = null;
            }
            DisposeDshProcLocked();
        }
        if (h != null) { try { h.Dispose(); } catch { } }
    }

    // kill a pid + its tree; elevate if the target runs at higher integrity
    void KillTree(int pid)
    {
        Win32.IntegrityLevel target = Win32.GetIntegrity(pid);
        Logging.Log("KillTree: pid=" + pid + " targetIntegrity=" + target + " selfIntegrity=" + selfIntegrity);

        bool needElevate = (target != Win32.IntegrityLevel.Unknown) && (target > selfIntegrity);
        if (needElevate)
        {
            Logging.Log("KillTree: elevating to kill higher-integrity pid=" + pid);
            RunElevatedKill(pid);
            return;
        }

        Taskkill(pid);
        TryProcessKill(pid);

        Thread.Sleep(KillSleepMs);
        if (IsAlive(pid))
        {
            Logging.Log("KillTree: pid=" + pid + " still alive after normal kill, elevating");
            RunElevatedKill(pid);
        }
    }

    // Spawn an elevated helper to kill one pid + tree. Returns whether the helper launched and
    // exited with code 0. A UAC decline, a launch failure, a non-zero exit, or a wait timeout all
    // return false so the caller can log an explicit "stop may be incomplete" signal — the caller
    // (StopCore) never changes its adoption logic; it only gets a clearer failure trace.
    bool RunElevatedKill(int pid)
    {
        // one-time nonce: the elevated helper verifies it plus the dsh entry before killing,
        // so a stray/non-originated --elevated-kill invocation is rejected (fail-closed)
        string nonce = Guid.NewGuid().ToString("N");
        string tokenPath = ElevateTokenPath(nonce);
        // The helper deletes the token after validating it. We must not delete it while the helper
        // may still be alive: a slow UAC approval (>30s) would otherwise make the approved helper
        // find a missing token and refuse the kill. Only clean up when we know it is not running.
        bool helperAlive = false;
        try
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tokenPath));
                File.WriteAllText(tokenPath, nonce + Environment.NewLine + (cfg.DshEntry ?? ""), Encoding.UTF8);
            }
            catch (Exception ex) { Logging.Log("elevate token write failed: " + ex.Message); }

            var psi = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = "--elevated-kill " + pid + " " + nonce,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process p = null;
            try { p = Process.Start(psi); helperAlive = (p != null); }
            catch (Exception ex) { Logging.Log("elevated kill launch failed (UAC declined?): " + ex.Message); }
            if (p != null)
            {
                bool exited = p.WaitForExit(ElevatedKillWaitMs);
                if (exited)
                {
                    helperAlive = false;
                    Logging.Log("elevated kill helper: exit=" + p.ExitCode);
                    if (p.ExitCode == 0) return true;
                    Logging.Log("elevated kill failed/refused, stop may be incomplete (helper exit=" + p.ExitCode + ")");
                    return false;
                }
                Logging.Log("elevated kill helper still running after " + ElevatedKillWaitMs + "ms, leaving token for helper cleanup");
                return false;
            }
            Logging.Log("elevated kill helper: Process.Start returned null");
            return false;
        }
        catch (Exception ex)
        {
            Logging.Log("elevated kill failed: " + ex.Message);
            return false;
        }
        finally
        {
            // Clean up only when no helper is still alive. If it is alive, leave the token so a
            // late UAC approval still passes validation; the helper itself deletes it.
            if (!helperAlive)
            {
                try { File.Delete(tokenPath); } catch { }
            }
        }
    }

    static string ElevateTokenPath(string nonce)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-tray");
        return Path.Combine(dir, "elevate-" + nonce + ".tmp");
    }

    // ---- runs as elevated helper: verify origin + target identity, then kill one pid + tree ----
    // Fail-closed: any check that fails logs the reason, cleans up and returns false (no kill).
    public bool RunElevatedKillDirect(int pid, string nonce)
    {
        if (string.IsNullOrEmpty(nonce)) { Reject(pid, "missing nonce"); return false; }

        // 1. token file must exist with a matching nonce and the same dsh entry
        string tokenPath = ElevateTokenPath(nonce);
        string tokenNonce, tokenEntry;
        if (!ReadToken(tokenPath, out tokenNonce, out tokenEntry)) { Reject(pid, "token file missing/unreadable"); CleanupToken(tokenPath); return false; }
        if (tokenNonce != nonce) { Reject(pid, "nonce mismatch"); CleanupToken(tokenPath); return false; }
        if (tokenEntry != (cfg.DshEntry ?? "")) { Reject(pid, "dsh entry mismatch"); CleanupToken(tokenPath); return false; }

        // 2. validation passed -> delete the token (this pid's file only)
        CleanupToken(tokenPath);

        // 3. target must be a node process (our harness)
        if (!IsNodeProcess(pid)) { Reject(pid, "target is not node.exe"); return false; }

        // 4. target integrity must be <= self (helper runs elevated; refuse anything higher,
        //    e.g. System); Unknown is treated as suspicious -> refuse
        Win32.IntegrityLevel targetIntegrity = Win32.GetIntegrity(pid);
        if (targetIntegrity == Win32.IntegrityLevel.Unknown) { Reject(pid, "target integrity unknown"); return false; }
        if (targetIntegrity > selfIntegrity) { Reject(pid, "target integrity higher than self"); return false; }

        // 5. target command line must contain our dsh entry (WMI); empty/error -> refuse
        if (!CommandLineLooksLikeDsh(pid)) { Reject(pid, "command line does not look like dsh"); return false; }

        Logging.Log("=== elevated kill start: pid=" + pid + " myIntegrity=" + selfIntegrity + " ===");
        Taskkill(pid);
        TryProcessKill(pid);
        Thread.Sleep(KillSleepMs);
        Logging.Log("elevated kill: pid=" + pid + " alive=" + IsAlive(pid));
        return true;
    }

    static bool ReadToken(string path, out string nonce, out string entry)
    {
        nonce = null; entry = null;
        try
        {
            if (!File.Exists(path)) return false;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) return false;
            nonce = lines[0].Trim();
            entry = lines[1].Trim();
            return true;
        }
        catch (Exception ex) { Logging.Log("elevate token read failed: " + ex.Message); return false; }
    }

    static void CleanupToken(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static void Reject(int pid, string reason)
    {
        Logging.Log("elevated kill refused (pid=" + pid + "): " + reason);
    }

    // fail-closed identity check for the elevated kill: the target command line must look like
    // the dsh harness. Markers instead of the exact current entry path, so a harness started by
    // an older build or a different install can still be stopped, while arbitrary node processes
    // (and anything non-node) are still refused.
    bool CommandLineLooksLikeDsh(int pid)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    object cl = obj["CommandLine"];
                    if (cl == null) continue;
                    string cmd = cl.ToString();
                    if (cmd.IndexOf("@deepseek-ai", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (cmd.IndexOf("bin.js", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (cmd.IndexOf("\\dsh\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }
        catch (Exception ex) { Logging.Log("elevated kill WMI query failed: " + ex.Message); return false; }
    }

    string Taskkill(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                Arguments = "/PID " + pid + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                // start async reads first (drains both pipes concurrently) to avoid the classic
                // full-pipe deadlock that hit "ReadToEnd then WaitForExit" on a chatty child
                var readOut = p.StandardOutput.ReadToEndAsync();
                var readErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(TaskkillWaitMs))
                {
                    try { p.Kill(); } catch { }
                    Logging.Log("taskkill pid=" + pid + " timed out, killed");
                }
                string outp = readOut.Result;
                string err = readErr.Result;
                string msg = "taskkill pid=" + pid + " exit=" + p.ExitCode +
                    " out=" + outp.Trim() + " err=" + err.Trim();
                Logging.Log(msg);
                return msg;
            }
        }
        catch (Exception ex)
        {
            string msg = "taskkill pid=" + pid + " exception: " + ex.Message;
            Logging.Log(msg);
            return msg;
        }
    }

    bool TryProcessKill(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                p.Kill();
                p.WaitForExit(ProcessWaitExitMs);
            }
            Logging.Log("Process.Kill pid=" + pid + " ok");
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log("Process.Kill pid=" + pid + " failed: " + ex.Message);
            return false;
        }
    }

    bool IsAlive(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                return !p.HasExited;
            }
        }
        catch { return false; }
    }

    public bool PortOpen(int port)
    {
        using (var c = new TcpClient())
        {
            try
            {
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(PortProbeTimeoutMs, false);
                if (!ok) return false;
                c.EndConnect(ar);
                return true;
            }
            catch { return false; }
        }
    }

    // Is the process owning `pid` a node.exe? Used to verify a port listener is really our harness.
    bool IsNodeProcess(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                return string.Equals(p.ProcessName, "node", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { return false; }
    }

    // Is the netstat local-address HOST (port suffix already stripped) a loopback/any
    // listener? Only these can be ours; anything else (e.g. the remote address on an
    // ESTABLISHED line) is never a local port owner.
    bool IsLocalListenAddress(string localAddr)
    {
        return localAddr == "127.0.0.1" || localAddr == "0.0.0.0" ||
               localAddr == "[::1]" || localAddr == "[::]";
    }

    public int FindPidOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                var readOut = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(NetstatWaitMs))
                {
                    try { p.Kill(); } catch { }
                    Logging.Log("FindPidOnPort netstat timed out, killed");
                }
                string output = readOut.Result;
                string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    // only LISTENING lines carry a local listener; skip ESTABLISHED/other states
                    if (line.IndexOf("LISTENING", StringComparison.Ordinal) < 0) continue;
                    string[] cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // expected netstat -ano tcp columns: Proto LocalAddress ForeignAddress State PID
                    if (cols.Length < 5) continue;
                    string localAddr = cols[1]; // local address column, e.g. "127.0.0.1:3080" or "[::1]:3080"
                    string portSuffix = ":" + port;
                    // require the local address to END with ":port" and be a loopback/any address,
                    // so a remote "1.2.3.4:3080" (ESTABLISHED) or an unrelated local IP is never matched
                    if (!localAddr.EndsWith(portSuffix, StringComparison.Ordinal)) continue;
                    string addrHost = localAddr.Substring(0, localAddr.Length - portSuffix.Length);
                    if (!IsLocalListenAddress(addrHost)) continue;
                    int pid;
                    if (int.TryParse(cols[cols.Length - 1], out pid)) return pid;
                }
            }
        }
        catch (Exception ex) { Logging.Log("FindPidOnPort failed: " + ex.Message); }
        return 0;
    }
}
