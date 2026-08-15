using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Process state machine for the DSH harness: spawn/kill/restart, port liveness, integrity-based
// elevation, and the self-heal poll. Instance class (one per session); depends only on
// Config / Win32 / Logging. The constructor has NO side effects, so headless modes can safely
// construct one for probing.
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
    Process dshProc;
    bool userStopped;
    int lastStartTick;
    int lastAutoRestartTick;
    bool autoRestartEnabled;
    string lastDshUpWarn;
    Win32.IntegrityLevel selfIntegrity;
    volatile bool isStarting;

    public DshProcess(AppConfig config)
    {
        cfg = config;
    }

    // true while an async start/restart is in flight (the tray flashes the icon while starting)
    public bool IsStarting { get { return isStarting; } }

    // integrity level of THIS tray process; set by the caller (no side effects in the ctor)
    public Win32.IntegrityLevel SelfIntegrity
    {
        get { return selfIntegrity; }
        set { selfIntegrity = value; }
    }

    public bool AutoRestartEnabled
    {
        get { return autoRestartEnabled; }
        set { autoRestartEnabled = value; }
    }

    // whether the user manually stopped the harness (never auto-restart while true)
    public bool UserStopped
    {
        get { return userStopped; }
        set { userStopped = value; }
    }

    public bool IsUp
    {
        get
        {
            try
            {
                if (dshProc != null && !dshProc.HasExited) { lastDshUpWarn = null; return true; }
            }
            catch (Exception ex) { Logging.Log("IsUp process check failed: " + ex.Message); }
            // process object may have been disposed concurrently (background thread vs UI); fall
            // through to the port probe
            if (!PortOpen(cfg.Port)) { lastDshUpWarn = null; return false; }
            // port is open but we don't own the listener: only treat it as "up" if the owner is node
            int pid = FindPidOnPort(cfg.Port);
            if (pid <= 0) { LogDshUpWarnOnce("port " + cfg.Port + " open but no listener pid found"); return false; }
            if (!IsNodeProcess(pid)) { LogDshUpWarnOnce("port " + cfg.Port + " owned by non-node pid=" + pid + "; treating as down"); return false; }
            lastDshUpWarn = null;
            return true;
        }
    }

    public void ToggleAutoRestart()
    {
        autoRestartEnabled = !autoRestartEnabled;
        Config.SaveAutoRestart(autoRestartEnabled);
        Logging.Log("autoRestart = " + autoRestartEnabled);
    }

    // self-heal poll: if auto-restart is on, the user hasn't stopped, and the harness is down
    // past the cooldowns, restart it. Returns whether a restart was triggered.
    public bool PollAutoRestart()
    {
        // never auto-restart while an async start/restart is in flight (double-start race)
        if (!isStarting && autoRestartEnabled && !userStopped && !IsUp &&
            Environment.TickCount - lastStartTick > AutoRestartStartCooldownMs &&
            Environment.TickCount - lastAutoRestartTick > AutoRestartRetryCooldownMs)
        {
            lastAutoRestartTick = Environment.TickCount;
            Logging.Log("AutoRestart: harness is down, restarting");
            StartDsh();
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

    public void StartDsh()
    {
        userStopped = false;
        lastStartTick = Environment.TickCount;
        if (cfg.NodePath == null || !File.Exists(cfg.NodePath)) { Logging.Log("StartDsh failed: node.exe not found (set 'node' in dshtray.ini)"); return; }
        if (cfg.DshEntry == null || !File.Exists(cfg.DshEntry)) { Logging.Log("StartDsh failed: dsh entry not found (set 'dshentry' in dshtray.ini)"); return; }
        if (IsUp) { Logging.Log("StartDsh: already up, skip"); return; }
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
        }
        catch (Exception ex) { Logging.Log("StartDsh failed: " + ex.Message); }
    }

    // named handler so it can be unsubscribed before Dispose; uses sender.Id (not dshProc)
    // because dshProc may already reference a newer process by the time this fires
    void dshProcExited(object sender, EventArgs e)
    {
        try
        {
            var p = sender as Process;
            Logging.Log("dsh process exited pid=" + (p != null ? p.Id : -1));
        }
        catch { }
    }

    public void StopDsh()
    {
        bool owned = (dshProc != null && !dshProc.HasExited);
        if (owned)
        {
            Logging.Log("StopDsh: killing owned pid=" + dshProc.Id);
            KillTree(dshProc.Id);
            try { dshProc.WaitForExit(ProcessWaitExitMs); } catch (Exception ex) { Logging.Log("StopDsh WaitForExit failed: " + ex.Message); }
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

    // process-level restart: stop then start then wait for the port. UI coordination (tray icon
    // refresh, Chrome window reload) is the caller's job (TrayMenu), so this keeps no UI deps.
    public void RestartDsh()
    {
        Logging.Log("=== RestartDsh ===");
        StopDsh();
        StartDsh();
        WaitForPortUp();
    }

    public void WaitForPortUp()
    {
        int waited = 0;
        while (!PortOpen(cfg.Port) && waited < PortWaitMs)
        {
            Thread.Sleep(PortPollStepMs);
            waited += PortPollStepMs;
        }
        Logging.Log("WaitForPortUp: waited=" + waited + "ms up=" + PortOpen(cfg.Port));
    }

    // ---- async lifecycle (UI actions use these so the message pump is not blocked) ----

    public async Task RestartAsync()
    {
        if (isStarting) return;
        isStarting = true;
        try
        {
            await Task.Run(() => { StopDsh(); StartDsh(); });
            await WaitForPortUpAsync();
        }
        finally { isStarting = false; }
    }

    public async Task StartAndWaitAsync()
    {
        if (isStarting) return;
        isStarting = true;
        try
        {
            await Task.Run(() => StartDsh());
            await WaitForPortUpAsync();
        }
        finally { isStarting = false; }
    }

    public async Task StopAsync()
    {
        await Task.Run(() => StopDsh());
    }

    // non-blocking port wait: Task.Delay instead of Thread.Sleep; same bounds and logging
    async Task WaitForPortUpAsync()
    {
        int waited = 0;
        while (!PortOpen(cfg.Port) && waited < PortWaitMs)
        {
            await Task.Delay(PortPollStepMs).ConfigureAwait(false);
            waited += PortPollStepMs;
        }
        Logging.Log("WaitForPortUpAsync: waited=" + waited + "ms up=" + PortOpen(cfg.Port));
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
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = "--elevated-kill " + pid,
                UseShellExecute = true,
                Verb = "runas"
            };
            var p = Process.Start(psi);
            if (p != null) { p.WaitForExit(ElevatedKillWaitMs); Logging.Log("elevated kill helper: exit=" + p.ExitCode); }
            else Logging.Log("elevated kill helper: Process.Start returned null");
        }
        catch (Exception ex)
        {
            Logging.Log("elevated kill launch failed (UAC declined?): " + ex.Message);
        }
    }

    // ---- runs as elevated helper: kill one pid + its tree ----
    public void RunElevatedKillDirect(int pid)
    {
        Logging.Log("=== elevated kill start: pid=" + pid + " myIntegrity=" + selfIntegrity + " ===");
        Taskkill(pid);
        TryProcessKill(pid);
        Thread.Sleep(KillSleepMs);
        Logging.Log("elevated kill: pid=" + pid + " alive=" + IsAlive(pid));
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

    // IsDshUp runs from the 3-second poll: log each distinct warning only once per
    // episode (until the verdict turns healthy again) so the log is not flooded
    void LogDshUpWarnOnce(string msg)
    {
        if (msg != lastDshUpWarn)
        {
            lastDshUpWarn = msg;
            Logging.Log("IsDshUp: " + msg);
        }
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
