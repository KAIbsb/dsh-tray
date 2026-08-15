using System;
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

    readonly AppConfig cfg;
    readonly object stateLock = new object();
    DshState state = DshState.Stopped;   // all transitions happen under stateLock
    Process dshProc;
    bool userStopped;                    // no longer publicly writable
    int lastStartTick;
    int lastAutoRestartTick;
    bool autoRestartEnabled;
    Win32.IntegrityLevel selfIntegrity;

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
            trigger = state == DshState.Stopped && autoRestartEnabled && !userStopped &&
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
        return "/c \"\"%DSH_TRAY_NODE%\" \"%DSH_TRAY_ENTRY%\" web >> \"%DSH_TRAY_LOG%\" 2>&1\"";
    }

    // copy the launch parameters into the child environment (must run before Process.Start)
    public void ApplyLaunchEnv(ProcessStartInfo psi, string dshLog)
    {
        psi.EnvironmentVariables["DSH_TRAY_NODE"] = cfg.NodePath;
        psi.EnvironmentVariables["DSH_TRAY_ENTRY"] = cfg.DshEntry;
        psi.EnvironmentVariables["DSH_TRAY_LOG"] = dshLog;
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
        if (IsPortServed())
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
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = BuildLaunchCmd(),
                WorkingDirectory = cfg.DshWorkDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ApplyLaunchEnv(psi, dshLog);
            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += dshProcExited;
            proc.Start();
            dshProc = proc;
            Logging.Log("StartDsh: launched pid=" + dshProc.Id + " (log=" + dshLog + ")");
            return StartResult.Launched;
        }
        catch (Exception ex) { Logging.Log("StartDsh failed: " + ex.Message); return StartResult.Failed; }
    }

    // true when the port is already served by a node process (adopt case); identity check so a
    // non-node occupant never blocks a start
    bool IsPortServed()
    {
        if (!PortOpen(cfg.Port)) return false;
        int pid = FindPidOnPort(cfg.Port);
        return pid > 0 && IsNodeProcess(pid);
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
                // probe outside the lock (TCP + maybe netstat): only collapse to Stopped when
                // the port is really down — our process may have died on a port conflict while
                // another node instance still serves the harness
                bool served = IsPortServed();
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
    // the port to free. Called only while state == Stopping.
    void StopCore()
    {
        bool owned = false;
        int ownedPid = 0;
        lock (stateLock)
        {
            if (dshProc != null)
            {
                try { owned = !dshProc.HasExited; ownedPid = dshProc.Id; } catch { owned = false; }
            }
        }
        if (owned)
        {
            Logging.Log("StopDsh: killing owned pid=" + ownedPid);
            Process p = dshProc;
            KillTree(ownedPid);
            try { if (p != null) p.WaitForExit(ProcessWaitExitMs); } catch (Exception ex) { Logging.Log("StopDsh WaitForExit failed: " + ex.Message); }
        }
        DisposeDshProc();
        dshProc = null;

        if (PortOpen(cfg.Port))
        {
            int pid = FindPidOnPort(cfg.Port);
            if (pid > 0)
            {
                // only kill the port owner if it is actually node.exe; refusing to kill an
                // unrelated process that happens to hold our port avoids an identity mix-up
                if (IsNodeProcess(pid))
                {
                    Logging.Log("StopDsh: killing external pid=" + pid);
                    KillTree(pid);
                }
                else
                {
                    Logging.Log("StopDsh: pid=" + pid + " on port " + cfg.Port + " is not node, refusing to kill");
                }
            }
        }
        WaitForPortFree();
        Logging.Log("StopDsh: done, port open=" + PortOpen(cfg.Port));
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
        await Task.Run(() => StopCore());
        lock (stateLock) { state = DshState.Stopped; }
    }

    // Running -> (stop -> start) via Stopping/Stopping->Stopped->Starting->Running; Starting or
    // Stopping is a no-op (no double clicks); Stopped delegates to StartAsync.
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
        // cur == Running
        Logging.Log("=== RestartDsh ===");
        await Task.Run(() => StopCore());
        lock (stateLock) { state = DshState.Stopped; userStopped = false; }
        await StartAsync();
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

    // detach the Exited handler then dispose the process object; safe to call when null
    void DisposeDshProc()
    {
        if (dshProc != null)
        {
            try { dshProc.Exited -= dshProcExited; } catch { }
            try { dshProc.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        DisposeDshProc();
        dshProc = null;
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

    void RunElevatedKill(int pid)
    {
        string tokenPath = ElevateTokenPath(pid);
        try
        {
            // one-time nonce: the elevated helper verifies it plus the dsh entry before killing,
            // so a stray/non-originated --elevated-kill invocation is rejected (fail-closed)
            string nonce = Guid.NewGuid().ToString("N");
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
            try { p = Process.Start(psi); }
            catch (Exception ex) { Logging.Log("elevated kill launch failed (UAC declined?): " + ex.Message); }
            if (p != null)
            {
                p.WaitForExit(ElevatedKillWaitMs);
                Logging.Log("elevated kill helper: exit=" + p.ExitCode);
            }
            else Logging.Log("elevated kill helper: Process.Start returned null");
        }
        catch (Exception ex)
        {
            Logging.Log("elevated kill failed: " + ex.Message);
        }
        finally
        {
            // idempotent cleanup: the helper deletes it after validation; this covers the
            // declined / never-started / aborted paths so the token never lingers
            try { File.Delete(tokenPath); } catch { }
        }
    }

    static string ElevateTokenPath(int pid)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-tray");
        return Path.Combine(dir, "elevate-" + pid + ".tmp");
    }

    // ---- runs as elevated helper: verify origin + target identity, then kill one pid + tree ----
    // Fail-closed: any check that fails logs the reason, cleans up and returns false (no kill).
    public bool RunElevatedKillDirect(int pid, string nonce)
    {
        if (string.IsNullOrEmpty(nonce)) { Reject(pid, "missing nonce"); return false; }

        // 1. token file must exist with a matching nonce and the same dsh entry
        string tokenPath = ElevateTokenPath(pid);
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
        if (!CommandLineContainsDshEntry(pid)) { Reject(pid, "command line does not contain dsh entry"); return false; }

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

    bool CommandLineContainsDshEntry(int pid)
    {
        try
        {
            string dshEntry = cfg.DshEntry;
            if (string.IsNullOrEmpty(dshEntry)) return false;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    object cl = obj["CommandLine"];
                    if (cl != null && cl.ToString().IndexOf(dshEntry, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
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
