using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("dsh-tray")]
[assembly: AssemblyDescription("DeepSeek Harness tray lifecycle manager")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyProduct("dsh-tray")]
[assembly: AssemblyCopyright("Copyright (c) 2026 KAIbsb")]

static class Program
{
    // version is single-sourced from the assembly version attribute (see AssemblyVersion below);
    // no hardcoded copy here to avoid drift
    static readonly string AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

    // ---- timing constants (named to make intent explicit; values unchanged). These stay in
    // Program for this phase; they move with the process state machine in a later phase ----
    const int PollIntervalMs = 3000;             // status-poll timer cadence
    const int AutoRestartStartCooldownMs = 10000; // min age of a start attempt before auto-restart
    const int AutoRestartRetryCooldownMs = 30000; // min gap between two auto-restart attempts
    const int PortWaitMs = 30000;                 // max time to wait for the port to come up
    const int PortFreeWaitMs = 8000;              // max time to wait for the port to be released
    const int PortPollStepMs = 200;               // sleep step while polling port open/free
    const int PortProbeTimeoutMs = 300;           // TCP connect timeout in PortOpen
    const int KillSleepMs = 300;                  // pause after a kill before checking liveness
    const int DoubleClickSwallowMs = 300;         // left-click dedupe window
    const int ProcessWaitExitMs = 3000;           // WaitForExit timeout for a killed process
    const int TaskkillWaitMs = 8000;              // taskkill subprocess wait timeout
    const int NetstatWaitMs = 5000;               // netstat subprocess wait timeout
    const int ElevatedKillWaitMs = 30000;         // elevated kill helper wait timeout

    static NotifyIcon tray;
    static Icon whiteIcon;
    static Icon blueIcon;
    static Icon darkIcon;
    static bool darkMode;
    static Process dshProc;
    static System.Windows.Forms.Timer pollTimer;
    static Mutex mutex;
    static Win32.IntegrityLevel selfIntegrity = Win32.IntegrityLevel.Unknown;
    static bool userStopped;
    static int lastStartTick;
    static int lastAutoRestartTick;
    static bool menuShowing;
    static Form menuOwner;
    // all tick-delay checks below use Environment.TickCount (int). The elapsed-time
    // expressions are plain `int - int` in C#'s default unchecked context, so a 24.8-day
    // TickCount wraparound stays well-defined (mod 2^32) and never yields a spuriously
    // negative elapsed value. .NET Framework 4.8 has no Environment.TickCount64, so we
    // keep TickCount and rely on this wraparound-safe int subtraction.
    static int lastLeftClickTick = -1000;
    static string lastDshUpWarn;

    [STAThread]
    static void Main()
    {
        string[] args = Environment.GetCommandLineArgs();

        // headless helper modes are dispatched before the single-instance mutex: the
        // --elevated-kill helper is a separate process spawned by a live instance (that holds
        // the mutex), so gating it would make it exit and the elevated kill would never run.
        if (args.Length > 1)
        {
            // elevated kill helper: only needs logging + our own integrity level
            if (args[1] == "--elevated-kill" && args.Length > 2)
            {
                Logging.InitLog();
                selfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
                int pid;
                if (int.TryParse(args[2], out pid)) RunElevatedKillDirect(pid);
                return;
            }

            // diagnostic one-shot modes: need full config detection, still early-return
            if (args[1] == "--smoke" || args[1] == "--find-window" || args[1] == "--menu-test")
            {
                Logging.InitLog();
                Config.InitConfig();
                selfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
                Config.AutoRestartEnabled = Config.LoadAutoRestart();
                if (args[1] == "--smoke") { RunSmoke(); return; }
                if (args[1] == "--find-window") { RunFindWindow(); return; }
                if (args[1] == "--menu-test") { RunMenuTest(); return; }
            }
        }

        // single-instance guard: reject a second (no-arg) instance before doing any
        // initialization, so it performs no config detection / logging / registry reads
        bool createdNew;
        mutex = new Mutex(false, "dsh-tray_SingleInstance", out createdNew);
        bool acquired;
        try { acquired = mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { acquired = true; } // previous instance crashed; take over
        if (!acquired) return; // another live instance

        // only the single primary instance reaches here
        Logging.InitLog();
        Config.InitConfig();
        selfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
        Config.AutoRestartEnabled = Config.LoadAutoRestart();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
        {
            if (e.Exception != null) Logging.Log("ThreadException: " + e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject != null) Logging.Log("UnhandledException: " + e.ExceptionObject);
        };

        darkMode = Config.IsDarkMode();
        Win32.ApplyAppTheme(darkMode);
        Logging.Log("=== dsh-tray v" + AppVersion + " started (integrity=" + selfIntegrity + ", autoRestart=" + Config.AutoRestartEnabled +
            ", darkMode=" + darkMode + ") ===");

        BuildTray();

        userStopped = false;
        if (!IsDshUp()) StartDsh();
        UpdateStatus();

        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = PollIntervalMs;
        pollTimer.Tick += delegate { PollTick(); };
        pollTimer.Start();

        Application.Run();

        if (pollTimer != null) pollTimer.Stop();
        if (whiteIcon != null) whiteIcon.Dispose();
        if (blueIcon != null) blueIcon.Dispose();
        if (darkIcon != null) darkIcon.Dispose();
        DisposeDshProc();
        dshProc = null;
        if (mutex != null) mutex.ReleaseMutex();
    }

    static void PollTick()
    {
        bool d = Config.IsDarkMode();
        if (d != darkMode)
        {
            darkMode = d;
            Win32.ApplyAppTheme(darkMode);
            Logging.Log("theme changed to " + (d ? "dark" : "light"));
        }
        UpdateStatus();
        if (Config.AutoRestartEnabled && !userStopped && !IsDshUp() &&
            Environment.TickCount - lastStartTick > AutoRestartStartCooldownMs &&
            Environment.TickCount - lastAutoRestartTick > AutoRestartRetryCooldownMs)
        {
            lastAutoRestartTick = Environment.TickCount;
            Logging.Log("AutoRestart: harness is down, restarting");
            StartDsh();
        }
    }

    // ---- headless self-check, writes result next to exe ----
    static void RunSmoke()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string report = Path.Combine(dir, "smoke-result.txt");
        var sb = new StringBuilder();
        sb.AppendLine("node exists=" + File.Exists(Config.Current.NodePath));
        sb.AppendLine("dsh entry exists=" + File.Exists(Config.Current.DshEntry));
        sb.AppendLine("chrome exists=" + File.Exists(Config.Current.ChromePath));
        sb.AppendLine("self integrity=" + selfIntegrity);
        sb.AppendLine("autoRestart=" + Config.AutoRestartEnabled);
        sb.AppendLine("ui lang=" + Lang.Code);
        using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale-blue.png"))
            sb.AppendLine("blue icon resource=" + (rs != null));
        using (Stream rs2 = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale-dark.png"))
            sb.AppendLine("dark icon resource=" + (rs2 != null));
        sb.AppendLine("port" + Config.Current.Port + " open=" + PortOpen(Config.Current.Port));
        int p3080 = FindPidOnPort(Config.Current.Port);
        sb.AppendLine("pid on port=" + p3080);
        if (p3080 > 0) sb.AppendLine("pid integrity=" + Win32.GetIntegrity(p3080));
        sb.AppendLine("SMOKE OK");
        try { File.WriteAllText(report, sb.ToString(), Encoding.UTF8); } catch (Exception ex) { Logging.Log("RunSmoke write report failed: " + ex.Message); }
    }

    // ---- headless: list Chrome top-level windows (read-only) ----
    static void RunFindWindow()
    {
        var sb = new StringBuilder();
        Win32.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!Win32.IsWindowVisible(hWnd)) return true;
            uint pid;
            Win32.GetWindowThreadProcessId(hWnd, out pid);
            try
            {
                var p = Process.GetProcessById((int)pid);
                if (Config.Current.BrowserNames.Contains(p.ProcessName.ToLowerInvariant()))
                {
                    var t = new StringBuilder(256);
                    Win32.GetWindowText(hWnd, t, 256);
                    sb.AppendLine("hwnd=" + hWnd + " pid=" + pid + " title=[" + t.ToString() + "]");
                }
            }
            catch (Exception ex) { Logging.Log("RunFindWindow GetProcessById failed: " + ex.Message); }
            return true;
        }, IntPtr.Zero);
        string report = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "find-window-result.txt");
        try { File.WriteAllText(report, sb.ToString(), Encoding.UTF8); } catch (Exception ex) { Logging.Log("RunFindWindow write failed: " + ex.Message); }
    }

    // ---- headless: build the native menu without showing it ----
    static void RunMenuTest()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        try
        {
            var defs = new List<MenuDef>();
            defs.Add(new MenuDef(Lang.T("menu.open"), delegate { }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.start"), delegate { }, false, false));
            defs.Add(new MenuDef(Lang.T("menu.restart"), delegate { }, true, false));
            defs.Add(new MenuDef(Lang.T("menu.stop"), delegate { }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.autoRestart"), delegate { }, true, true));
            defs.Add(new MenuDef(Lang.T("menu.autostart"), delegate { }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.openLogs"), delegate { }, true, false));
            defs.Add(new MenuDef(Lang.T("menu.exit"), delegate { }, true, false));
            List<Action> actions;
            IntPtr hmenu;
            actions = BuildNativeMenu(defs, out hmenu);
            bool ok = hmenu != IntPtr.Zero && actions.Count == 8;
            if (hmenu != IntPtr.Zero) Win32.DestroyMenu(hmenu);
            File.WriteAllText(Path.Combine(dir, "menu-test.txt"),
                ok ? "menu-test OK (items=" + actions.Count + ")" : "menu-test FAIL", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "menu-test.txt"), "menu-test FAIL: " + ex.Message, Encoding.UTF8); } catch (Exception ex2) { Logging.Log("RunMenuTest write failed: " + ex2.Message); }
        }
    }

    // ---- runs as elevated helper: kill one pid + its tree ----
    static void RunElevatedKillDirect(int pid)
    {
        Logging.Log("=== elevated kill start: pid=" + pid + " myIntegrity=" + selfIntegrity + " ===");
        Taskkill(pid);
        TryProcessKill(pid);
        Thread.Sleep(KillSleepMs);
        Logging.Log("elevated kill: pid=" + pid + " alive=" + IsAlive(pid));
    }

    static void BuildTray()
    {
        tray = new NotifyIcon();
        tray.Text = Lang.T("tray.title");
        tray.Visible = true;
        try { whiteIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception ex) { Logging.Log("BuildTray extract icon failed: " + ex.Message); }
        blueIcon = BuildIconFromResource("whale-blue.png");
        darkIcon = BuildIconFromResource("whale-dark.png");
        tray.Icon = whiteIcon != null ? whiteIcon : SystemIcons.Application;
        tray.MouseUp += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { ShowTrayMenu(); return; }
            if (e.Button == MouseButtons.Left)
            {
                // single-click action only: swallow a second click within the dedupe window
                // so an accidental double-click never opens two windows
                int now = Environment.TickCount;
                if (now - lastLeftClickTick < DoubleClickSwallowMs) { lastLeftClickTick = now; return; }
                lastLeftClickTick = now;
                StartAndOpen();
            }
        };
    }

    // left click: ensure the harness is up, then open the window
    static void StartAndOpen()
    {
        if (!IsDshUp())
        {
            userStopped = false;
            StartDsh();
            WaitForPortUp(PortWaitMs);
            UpdateStatus();
        }
        OpenWindow();
    }

    static void ShowTrayMenu()
    {
        if (menuShowing) return;
        menuShowing = true;
        try
        {
            bool up = IsDshUp();
            var defs = new List<MenuDef>();
            defs.Add(new MenuDef(Lang.T("menu.open"), OpenWindow, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.start"), delegate { if (!IsDshUp()) StartDsh(); }, !up, false));
            defs.Add(new MenuDef(Lang.T("menu.restart"), RestartDsh, up, false));
            defs.Add(new MenuDef(Lang.T("menu.stop"), delegate { if (IsDshUp()) { userStopped = true; StopDsh(); UpdateStatus(); } }, up, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.autoRestart"), Config.ToggleAutoRestart, true, Config.AutoRestartEnabled));
            defs.Add(new MenuDef(Lang.T("menu.autostart"), Config.ToggleAutostart, true, Config.IsAutostartEnabled()));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.openLogs"), OpenLog, true, false));
            defs.Add(new MenuDef(Lang.T("menu.exit"), ExitApp, true, false));

            List<Action> actions;
            IntPtr hmenu;
            actions = BuildNativeMenu(defs, out hmenu);
            try
            {
                if (hmenu != IntPtr.Zero)
                {
                    IntPtr owner = GetMenuOwnerHwnd();
                    Win32.ApplyMenuTheme(owner, darkMode); // dark/light per system theme
                    Win32.ApplyAppTheme(darkMode);         // process-wide menu theme (uxtheme)
                    Point p = Cursor.Position;
                    // owner window must be foreground, else menu won't dismiss on outside click / Esc
                    Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
                    Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Win32.SetForegroundWindow(owner);
                    uint cmd = Win32.TrackPopupMenuEx(hmenu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
                        p.X, p.Y, owner, IntPtr.Zero);
                    if (cmd >= 1 && cmd <= (uint)actions.Count)
                    {
                        Action act = actions[(int)cmd - 1];
                        if (act != null) act();
                    }
                }
            }
            finally { if (hmenu != IntPtr.Zero) Win32.DestroyMenu(hmenu); }
        }
        finally { menuShowing = false; }
    }

    static List<Action> BuildNativeMenu(List<MenuDef> defs, out IntPtr hmenu)
    {
        var actions = new List<Action>();
        hmenu = Win32.CreatePopupMenu();
        if (hmenu == IntPtr.Zero) return actions;
        uint id = 1;
        foreach (MenuDef def in defs)
        {
            if (def.Separator)
            {
                Win32.AppendMenuW(hmenu, Win32.MF_SEPARATOR, 0, null);
                continue;
            }
            uint flags = Win32.MF_STRING;
            if (!def.Enabled) flags |= Win32.MF_GRAYED;
            if (def.Checked) flags |= Win32.MF_CHECKED;
            Win32.AppendMenuW(hmenu, flags, id, def.Text);
            actions.Add(def.Action);
            id++;
        }
        return actions;
    }

    static IntPtr GetMenuOwnerHwnd()
    {
        if (menuOwner == null)
        {
            menuOwner = new Form();
            menuOwner.ShowInTaskbar = false;
            menuOwner.FormBorderStyle = FormBorderStyle.None;
            menuOwner.Opacity = 0;
            menuOwner.StartPosition = FormStartPosition.Manual;
            menuOwner.Location = new Point(-32000, -32000);
            menuOwner.Size = new Size(1, 1);
            menuOwner.CreateControl();
        }
        return menuOwner.Handle;
    }

    class MenuDef
    {
        public string Text;
        public Action Action;
        public bool Enabled = true;
        public bool Checked;
        public bool Separator;

        public MenuDef(string text, Action action, bool enabled, bool check)
        {
            Text = text;
            Action = action;
            Enabled = enabled;
            Checked = check;
        }
    }

    static void OpenWindow()
    {
        try
        {
            if (Config.Current.ChromePath != null && File.Exists(Config.Current.ChromePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Config.Current.ChromePath,
                    Arguments = "--app=" + Config.Current.WebUrl,
                    UseShellExecute = false
                });
            }
            else
            {
                // no Chrome/Edge found: open in the default browser
                Process.Start(Config.Current.WebUrl);
                Logging.Log("OpenWindow: no chrome/edge found, opened in default browser");
            }
        }
        catch (Exception ex) { Logging.Log("OpenWindow failed: " + ex.Message); }
    }

    static void RestartDsh()
    {
        Logging.Log("=== RestartDsh ===");
        StopDsh();
        UpdateStatus();   // harness is down now -> show stopped (white) icon
        StartDsh();
        WaitForPortUp(PortWaitMs);
        ReloadAppWindow();
        UpdateStatus();   // harness is up -> show running (blue) icon
    }

    static void StartDsh()
    {
        userStopped = false;
        lastStartTick = Environment.TickCount;
        if (Config.Current.NodePath == null || !File.Exists(Config.Current.NodePath)) { Logging.Log("StartDsh failed: node.exe not found (set 'node' in dshtray.ini)"); return; }
        if (Config.Current.DshEntry == null || !File.Exists(Config.Current.DshEntry)) { Logging.Log("StartDsh failed: dsh entry not found (set 'dshentry' in dshtray.ini)"); return; }
        if (IsDshUp()) { Logging.Log("StartDsh: already up, skip"); return; }
        try
        {
            // spawn via cmd with stdout/stderr redirected to a FILE: the harness must not
            // depend on the tray's lifetime (a broken pipe EPIPE kills node in ~1s)
            string dshLog = Path.Combine(Path.GetDirectoryName(Logging.LogPath), "harness.log");
            string cmdArgs = "/c \"\"" + Config.Current.NodePath + "\" \"" + Config.Current.DshEntry + "\" web >> \"" + dshLog + "\" 2>&1\"";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                WorkingDirectory = Config.Current.DshWorkDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
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
    static void dshProcExited(object sender, EventArgs e)
    {
        try
        {
            var p = sender as Process;
            Logging.Log("dsh process exited pid=" + (p != null ? p.Id : -1));
        }
        catch { }
    }

    static void StopDsh()
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

        if (PortOpen(Config.Current.Port))
        {
            int pid = FindPidOnPort(Config.Current.Port);
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
                    Logging.Log("StopDsh: pid=" + pid + " on port " + Config.Current.Port + " is not node, refusing to kill");
                }
            }
        }
        WaitForPortFree(PortFreeWaitMs);
        Logging.Log("StopDsh: done, port open=" + PortOpen(Config.Current.Port));
    }

    // detach the Exited handler then dispose the process object; safe to call when null
    static void DisposeDshProc()
    {
        if (dshProc != null)
        {
            try { dshProc.Exited -= dshProcExited; } catch { }
            try { dshProc.Dispose(); } catch { }
        }
    }

    // kill a pid + its tree; elevate if the target runs at higher integrity
    static void KillTree(int pid)
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

    static void RunElevatedKill(int pid)
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

    static string Taskkill(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/PID " + pid + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(TaskkillWaitMs);
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

    static bool TryProcessKill(int pid)
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

    static bool IsAlive(int pid)
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

    static void WaitForPortFree(int timeoutMs)
    {
        int waited = 0;
        while (PortOpen(Config.Current.Port) && waited < timeoutMs)
        {
            Thread.Sleep(PortPollStepMs);
            waited += PortPollStepMs;
        }
        if (waited >= timeoutMs && PortOpen(Config.Current.Port)) Logging.Log("WaitForPortFree: timed out, port still open");
    }

    static void WaitForPortUp(int timeoutMs)
    {
        int waited = 0;
        while (!PortOpen(Config.Current.Port) && waited < timeoutMs)
        {
            Thread.Sleep(PortPollStepMs);
            waited += PortPollStepMs;
        }
        Logging.Log("WaitForPortUp: waited=" + waited + "ms up=" + PortOpen(Config.Current.Port));
    }

    // find Chrome top-level windows whose title matches the DSH webui and send Ctrl+R
    static void ReloadAppWindow()
    {
        try
        {
            const string title = "DeepSeek Harness";
            var targets = new List<IntPtr>();
            Win32.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Win32.IsWindowVisible(hWnd)) return true;
                uint pid;
                Win32.GetWindowThreadProcessId(hWnd, out pid);
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    if (Config.Current.BrowserNames.Contains(p.ProcessName.ToLowerInvariant()))
                    {
                        var sb = new StringBuilder(256);
                        Win32.GetWindowText(hWnd, sb, 256);
                        if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                            targets.Add(hWnd);
                    }
                }
                catch (Exception ex) { Logging.Log("ReloadAppWindow GetProcessById failed: " + ex.Message); }
                return true;
            }, IntPtr.Zero);

            if (targets.Count == 0) { Logging.Log("ReloadAppWindow: no matching window"); return; }

            // dummy ALT press unlocks Windows foreground-switch restrictions
            Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);

            int sent = 0;
            foreach (IntPtr h in targets)
            {
                Win32.SetForegroundWindow(h);
                Thread.Sleep(80);
                if (Win32.GetForegroundWindow() != h)
                {
                    Logging.Log("ReloadAppWindow: cannot focus window, skip");
                    continue;
                }
                Win32.keybd_event(Win32.VK_CONTROL, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_R, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_R, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_CONTROL, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                sent++;
                Thread.Sleep(150);
            }
            Logging.Log("ReloadAppWindow: reloaded " + sent + "/" + targets.Count + " window(s)");
        }
        catch (Exception ex) { Logging.Log("ReloadAppWindow failed: " + ex.Message); }
    }

    // Is the process owning `pid` a node.exe? Used to verify a port listener is really our harness.
    static bool IsNodeProcess(int pid)
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

    static bool IsDshUp()
    {
        if (dshProc != null && !dshProc.HasExited) { lastDshUpWarn = null; return true; }
        if (!PortOpen(Config.Current.Port)) { lastDshUpWarn = null; return false; }
        // port is open but we don't own the listener: only treat it as "up" if the owner is node
        int pid = FindPidOnPort(Config.Current.Port);
        if (pid <= 0) { LogDshUpWarnOnce("port " + Config.Current.Port + " open but no listener pid found"); return false; }
        if (!IsNodeProcess(pid)) { LogDshUpWarnOnce("port " + Config.Current.Port + " owned by non-node pid=" + pid + "; treating as down"); return false; }
        lastDshUpWarn = null;
        return true;
    }

    // IsDshUp runs from the 3-second poll: log each distinct warning only once per
    // episode (until the verdict turns healthy again) so the log is not flooded
    static void LogDshUpWarnOnce(string msg)
    {
        if (msg != lastDshUpWarn)
        {
            lastDshUpWarn = msg;
            Logging.Log("IsDshUp: " + msg);
        }
    }

    static bool PortOpen(int port)
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

    // Is the netstat local-address HOST (port suffix already stripped) a loopback/any
    // listener? Only these can be ours; anything else (e.g. the remote address on an
    // ESTABLISHED line) is never a local port owner.
    static bool IsLocalListenAddress(string localAddr)
    {
        return localAddr == "127.0.0.1" || localAddr == "0.0.0.0" ||
               localAddr == "[::1]" || localAddr == "[::]";
    }

    static int FindPidOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(NetstatWaitMs);
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

    static Icon BuildIconFromResource(string resName)
    {
        try
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (s == null) return null;
                using (Bitmap src = new Bitmap(s))
                {
                    int size = 32;
                    using (Bitmap bmp = new Bitmap(size, size))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, size, size);
                        }
                        IntPtr h = bmp.GetHicon();
                        Icon icon = (Icon)Icon.FromHandle(h).Clone();
                        Win32.DestroyIcon(h);
                        return icon;
                    }
                }
            }
        }
        catch (Exception ex) { Logging.Log("BuildIconFromResource(" + resName + ") failed: " + ex.Message); return null; }
    }

    static void UpdateStatus()
    {
        if (tray == null) return;
        bool up = IsDshUp();
        Icon use = null;
        if (up) use = blueIcon;
        else use = darkMode ? whiteIcon : darkIcon;
        if (use != null) tray.Icon = use;
        tray.Text = up ? Lang.T("tray.running") : Lang.T("tray.stopped");
    }

    static void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + Logging.LogPath + "\"",
                UseShellExecute = false
            });
            string dshLog = Path.Combine(Path.GetDirectoryName(Logging.LogPath), "harness.log");
            if (File.Exists(dshLog))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + dshLog + "\"",
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex) { Logging.Log("OpenLog failed: " + ex.Message); }
    }

    static void ExitApp()
    {
        // tray only: harness keeps running (stop it via the Stop menu item)
        Logging.Log("=== ExitApp (tray only, harness kept running) ===");
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Application.Exit();
    }
}
