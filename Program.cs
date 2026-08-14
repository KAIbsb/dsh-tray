using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    // ---- runtime configuration: resolved from dshtray.ini (next to exe) or auto-detected ----
    static string NodePath;
    static string DshEntry;
    static string DshWorkDir;
    static string ChromePath;
    static string WebUrl = "http://127.0.0.1:3080";
    static int Port = 3080;

    static void InitConfig()
    {
        LoadIniConfig();
        if (string.IsNullOrEmpty(NodePath) || !File.Exists(NodePath)) NodePath = DetectNode();
        if (string.IsNullOrEmpty(DshEntry) || !File.Exists(DshEntry)) DshEntry = DetectDshEntry();
        if (string.IsNullOrEmpty(DshWorkDir) && !string.IsNullOrEmpty(DshEntry))
            DshWorkDir = Path.GetDirectoryName(Path.GetDirectoryName(DshEntry));
        if (string.IsNullOrEmpty(ChromePath) || !File.Exists(ChromePath)) ChromePath = DetectChrome();
        Log("Config: node=" + (NodePath ?? "NOT FOUND") +
            " | dshEntry=" + (DshEntry ?? "NOT FOUND") +
            " | chrome=" + (ChromePath ?? "NOT FOUND") +
            " | url=" + WebUrl);
    }

    // optional dshtray.ini next to the exe; keys: node, dshentry, dshworkdir, chrome, url, port
    static void LoadIniConfig()
    {
        try
        {
            string ini = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dshtray.ini");
            if (!File.Exists(ini)) return;
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "node": NodePath = val; break;
                    case "dshentry": DshEntry = val; break;
                    case "dshworkdir": DshWorkDir = val; break;
                    case "chrome": ChromePath = val; break;
                    case "url":
                        WebUrl = val;
                        try { Port = new Uri(val).Port; } catch { }
                        break;
                    case "port":
                        int p;
                        if (int.TryParse(val, out p) && p > 0)
                        {
                            Port = p;
                            WebUrl = "http://127.0.0.1:" + p;
                        }
                        break;
                }
            }
        }
        catch { }
    }

    static string FindOnPath(string exe)
    {
        try
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar == null) return null;
            foreach (string dir in pathVar.Split(';'))
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                string candidate = Path.Combine(d, exe);
                if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    static string DetectNode()
    {
        string onPath = FindOnPath("node.exe");
        if (onPath != null) return onPath;
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(pf) ? pf : null;
    }

    static string DetectDshEntry()
    {
        // 1. dsh shim on PATH -> sibling node_modules\@deepseek-ai\dsh\lib\bin.js
        string shim = FindOnPath("dsh.cmd");
        if (shim == null) shim = FindOnPath("dsh");
        if (shim != null)
        {
            string entry = Path.Combine(Path.GetDirectoryName(shim), "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(entry)) return entry;
        }
        // 2. default npm global location (%APPDATA%\npm)
        string npmGlobal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        string entry2 = Path.Combine(npmGlobal, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        if (File.Exists(entry2)) return entry2;
        // 3. npm root -g
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c npm root -g",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string root = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (root.Length > 0 && Directory.Exists(root))
                {
                    string entry3 = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(entry3)) return entry3;
                }
            }
        }
        catch { }
        return null;
    }

    static string DetectChrome()
    {
        string[] candidates = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        foreach (string c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    static NotifyIcon tray;
    static Icon whiteIcon;
    static Icon blueIcon;
    static Icon darkIcon;
    static bool darkMode;
    static Process dshProc;
    static System.Windows.Forms.Timer pollTimer;
    static Mutex mutex;
    static readonly object logLock = new object();
    static string logPath;
    static IntegrityLevel selfIntegrity = IntegrityLevel.Unknown;
    static bool autoRestartEnabled;
    static bool userStopped;
    static int lastStartTick;
    static int lastAutoRestartTick;
    static bool menuShowing;
    static Form menuOwner;

    // ---- integrity (elevation) helpers ----
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr info, uint len, out uint retLen);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint TOKEN_QUERY = 0x0008;
    const int TokenIntegrityLevel = 25;

    // ---- window reload (refresh the Chrome app window) ----
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    // ---- native system menu ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    const uint MF_STRING = 0x0000;
    const uint MF_SEPARATOR = 0x0800;
    const uint MF_GRAYED = 0x0001;
    const uint MF_CHECKED = 0x0008;
    const uint TPM_RIGHTBUTTON = 0x0002;
    const uint TPM_RETURNCMD = 0x0100;

    // ---- immersive dark mode for native menus ----
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 2004+
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809/1903

    static void ApplyMenuTheme(IntPtr hwnd)
    {
        try
        {
            int useDark = darkMode ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, 4) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, 4);
        }
        catch { }
    }

    // ---- process-wide menu theme (uxtheme ordinals, same as Chromium/Firefox) ----
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
    static extern void FlushMenuThemes();

    const int PAM_DEFAULT = 0;
    const int PAM_ALLOW_DARK = 1;
    const int PAM_FORCE_DARK = 2;

    static void ApplyAppTheme()
    {
        try
        {
            // AllowDark when system is dark, Default otherwise; FlushMenuThemes drops cached menu themes
            SetPreferredAppMode(darkMode ? PAM_ALLOW_DARK : PAM_DEFAULT);
            FlushMenuThemes();
        }
        catch { }
    }

    const byte VK_CONTROL = 0x11;
    const byte VK_MENU = 0x12;
    const byte VK_R = 0x52;
    const uint KEYEVENTF_KEYUP = 0x0002;

    enum IntegrityLevel { Unknown = 0, Low = 4096, Medium = 8192, High = 12288, System = 16384 }

    [STAThread]
    static void Main()
    {
        InitLog();
        InitConfig();
        selfIntegrity = GetIntegrity(Process.GetCurrentProcess().Id);
        autoRestartEnabled = LoadAutoRestart();

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            if (args[1] == "--smoke") { RunSmoke(); return; }
            if (args[1] == "--find-window") { RunFindWindow(); return; }
            if (args[1] == "--menu-test") { RunMenuTest(); return; }
            if (args[1] == "--elevated-kill" && args.Length > 2)
            {
                int pid;
                if (int.TryParse(args[2], out pid)) RunElevatedKillDirect(pid);
                return;
            }
        }

        bool createdNew;
        mutex = new Mutex(false, "dsh-tray_SingleInstance", out createdNew);
        bool acquired;
        try { acquired = mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { acquired = true; } // previous instance crashed; take over
        if (!acquired) return; // another live instance

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
        {
            if (e.Exception != null) Log("ThreadException: " + e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject != null) Log("UnhandledException: " + e.ExceptionObject);
        };

        darkMode = IsDarkMode();
        ApplyAppTheme();
        Log("=== DSHTray started (integrity=" + selfIntegrity + ", autoRestart=" + autoRestartEnabled +
            ", darkMode=" + darkMode + ") ===");

        BuildTray();

        userStopped = false;
        if (!IsDshUp()) StartDsh();
        UpdateStatus();

        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = 3000;
        pollTimer.Tick += delegate { PollTick(); };
        pollTimer.Start();

        Application.Run();

        if (pollTimer != null) pollTimer.Stop();
        if (whiteIcon != null) whiteIcon.Dispose();
        if (blueIcon != null) blueIcon.Dispose();
        if (darkIcon != null) darkIcon.Dispose();
        if (mutex != null) mutex.ReleaseMutex();
    }

    static void InitLog()
    {
        logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DSHTray", "dshtray.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)); } catch { }
    }

    static void PollTick()
    {
        bool d = IsDarkMode();
        if (d != darkMode)
        {
            darkMode = d;
            ApplyAppTheme();
            Log("theme changed to " + (d ? "dark" : "light"));
        }
        UpdateStatus();
        if (autoRestartEnabled && !userStopped && !IsDshUp() &&
            Environment.TickCount - lastStartTick > 10000 &&
            Environment.TickCount - lastAutoRestartTick > 30000)
        {
            lastAutoRestartTick = Environment.TickCount;
            Log("AutoRestart: harness is down, restarting");
            StartDsh();
        }
    }

    // ---- headless self-check, writes result next to exe ----
    static void RunSmoke()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string report = Path.Combine(dir, "smoke-result.txt");
        var sb = new StringBuilder();
        sb.AppendLine("node exists=" + File.Exists(NodePath));
        sb.AppendLine("dsh entry exists=" + File.Exists(DshEntry));
        sb.AppendLine("chrome exists=" + File.Exists(ChromePath));
        sb.AppendLine("self integrity=" + selfIntegrity);
        sb.AppendLine("autoRestart=" + autoRestartEnabled);
        using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("DSHTray.blue.png"))
            sb.AppendLine("blue icon resource=" + (rs != null));
        using (Stream rs2 = Assembly.GetExecutingAssembly().GetManifestResourceStream("DSHTray.dark.png"))
            sb.AppendLine("dark icon resource=" + (rs2 != null));
        sb.AppendLine("port" + Port + " open=" + PortOpen(Port));
        int p3080 = FindPidOnPort(Port);
        sb.AppendLine("pid on port=" + p3080);
        if (p3080 > 0) sb.AppendLine("pid integrity=" + GetIntegrity(p3080));
        sb.AppendLine("SMOKE OK");
        try { File.WriteAllText(report, sb.ToString(), Encoding.UTF8); } catch { }
    }

    // ---- headless: list Chrome top-level windows (read-only) ----
    static void RunFindWindow()
    {
        var sb = new StringBuilder();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            try
            {
                var p = Process.GetProcessById((int)pid);
                if (p.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                {
                    var t = new StringBuilder(256);
                    GetWindowText(hWnd, t, 256);
                    sb.AppendLine("hwnd=" + hWnd + " pid=" + pid + " title=[" + t.ToString() + "]");
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        string report = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "find-window-result.txt");
        try { File.WriteAllText(report, sb.ToString(), Encoding.UTF8); } catch { }
    }

    // ---- headless: build the native menu without showing it ----
    static void RunMenuTest()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        try
        {
            var defs = new List<MenuDef>();
            defs.Add(new MenuDef("打开窗口", delegate { }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef("启动", delegate { }, false, false));
            defs.Add(new MenuDef("重启", delegate { }, true, false));
            defs.Add(new MenuDef("停止", delegate { }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef("崩溃自动重启", delegate { }, true, true));
            defs.Add(new MenuDef("开机自启", delegate { }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef("打开日志", delegate { }, true, false));
            defs.Add(new MenuDef("退出", delegate { }, true, false));
            List<Action> actions;
            IntPtr hmenu;
            actions = BuildNativeMenu(defs, out hmenu);
            bool ok = hmenu != IntPtr.Zero && actions.Count == 8;
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            File.WriteAllText(Path.Combine(dir, "menu-test.txt"),
                ok ? "menu-test OK (items=" + actions.Count + ")" : "menu-test FAIL", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "menu-test.txt"), "menu-test FAIL: " + ex.Message, Encoding.UTF8); } catch { }
        }
    }

    // ---- runs as elevated helper: kill one pid + its tree ----
    static void RunElevatedKillDirect(int pid)
    {
        Log("=== elevated kill start: pid=" + pid + " myIntegrity=" + selfIntegrity + " ===");
        Taskkill(pid);
        TryProcessKill(pid);
        Thread.Sleep(300);
        Log("elevated kill: pid=" + pid + " alive=" + IsAlive(pid));
    }

    static void BuildTray()
    {
        tray = new NotifyIcon();
        tray.Text = "DSH Harness 托盘管家";
        tray.Visible = true;
        try { whiteIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        blueIcon = BuildIconFromResource("DSHTray.blue.png");
        darkIcon = BuildIconFromResource("DSHTray.dark.png");
        tray.Icon = whiteIcon != null ? whiteIcon : SystemIcons.Application;
        tray.MouseUp += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) ShowTrayMenu();
        };
        tray.DoubleClick += delegate
        {
            if (Control.MouseButtons != MouseButtons.Left) return; // left double-click only
            if (!IsDshUp())
            {
                userStopped = false;
                StartDsh();
                WaitForPortUp(30000);
                UpdateStatus();
            }
            OpenWindow();
        };
    }

    static void ShowTrayMenu()
    {
        if (menuShowing) return;
        menuShowing = true;
        try
        {
            bool up = IsDshUp();
            var defs = new List<MenuDef>();
            defs.Add(new MenuDef("打开窗口", OpenWindow, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef("启动", delegate { if (!IsDshUp()) StartDsh(); }, !up, false));
            defs.Add(new MenuDef("重启", RestartDsh, up, false));
            defs.Add(new MenuDef("停止", delegate { if (IsDshUp()) { userStopped = true; StopDsh(); UpdateStatus(); } }, up, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef("崩溃自动重启", ToggleAutoRestart, true, autoRestartEnabled));
            defs.Add(new MenuDef("开机自启", ToggleAutostart, true, IsAutostartEnabled()));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef("打开日志", OpenLog, true, false));
            defs.Add(new MenuDef("退出", ExitApp, true, false));

            List<Action> actions;
            IntPtr hmenu;
            actions = BuildNativeMenu(defs, out hmenu);
            try
            {
                if (hmenu != IntPtr.Zero)
                {
                    IntPtr owner = GetMenuOwnerHwnd();
                    ApplyMenuTheme(owner); // dark/light per system theme
                    ApplyAppTheme();       // process-wide menu theme (uxtheme)
                    Point p = Cursor.Position;
                    // owner window must be foreground, else menu won't dismiss on outside click / Esc
                    keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    SetForegroundWindow(owner);
                    uint cmd = TrackPopupMenuEx(hmenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                        p.X, p.Y, owner, IntPtr.Zero);
                    if (cmd >= 1 && cmd <= (uint)actions.Count)
                    {
                        Action act = actions[(int)cmd - 1];
                        if (act != null) act();
                    }
                }
            }
            finally { if (hmenu != IntPtr.Zero) DestroyMenu(hmenu); }
        }
        finally { menuShowing = false; }
    }

    static List<Action> BuildNativeMenu(List<MenuDef> defs, out IntPtr hmenu)
    {
        var actions = new List<Action>();
        hmenu = CreatePopupMenu();
        if (hmenu == IntPtr.Zero) return actions;
        uint id = 1;
        foreach (MenuDef def in defs)
        {
            if (def.Separator)
            {
                AppendMenuW(hmenu, MF_SEPARATOR, 0, null);
                continue;
            }
            uint flags = MF_STRING;
            if (!def.Enabled) flags |= MF_GRAYED;
            if (def.Checked) flags |= MF_CHECKED;
            AppendMenuW(hmenu, flags, id, def.Text);
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
            if (ChromePath != null && File.Exists(ChromePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ChromePath,
                    Arguments = "--app=" + WebUrl,
                    UseShellExecute = false
                });
            }
            else
            {
                // no Chrome/Edge found: open in the default browser
                Process.Start(WebUrl);
                Log("OpenWindow: no chrome/edge found, opened in default browser");
            }
        }
        catch (Exception ex) { Log("OpenWindow failed: " + ex.Message); }
    }

    static void RestartDsh()
    {
        Log("=== RestartDsh ===");
        StopDsh();
        UpdateStatus();   // harness is down now -> show stopped (white) icon
        StartDsh();
        WaitForPortUp(30000);
        ReloadAppWindow();
        UpdateStatus();   // harness is up -> show running (blue) icon
    }

    static void StartDsh()
    {
        userStopped = false;
        lastStartTick = Environment.TickCount;
        if (NodePath == null || !File.Exists(NodePath)) { Log("StartDsh failed: node.exe not found (set 'node' in dshtray.ini)"); return; }
        if (DshEntry == null || !File.Exists(DshEntry)) { Log("StartDsh failed: dsh entry not found (set 'dshentry' in dshtray.ini)"); return; }
        if (IsDshUp()) { Log("StartDsh: already up, skip"); return; }
        try
        {
            // spawn via cmd with stdout/stderr redirected to a FILE: the harness must not
            // depend on the tray's lifetime (a broken pipe EPIPE kills node in ~1s)
            string dshLog = Path.Combine(Path.GetDirectoryName(logPath), "dsh.log");
            string cmdArgs = "/c \"\"" + NodePath + "\" \"" + DshEntry + "\" web >> \"" + dshLog + "\" 2>&1\"";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                WorkingDirectory = DshWorkDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += delegate { Log("dsh process exited pid=" + proc.Id); };
            proc.Start();
            dshProc = proc;
            Log("StartDsh: launched pid=" + dshProc.Id + " (log=" + dshLog + ")");
        }
        catch (Exception ex) { Log("StartDsh failed: " + ex.Message); }
    }

    static void StopDsh()
    {
        bool owned = (dshProc != null && !dshProc.HasExited);
        if (owned)
        {
            Log("StopDsh: killing owned pid=" + dshProc.Id);
            KillTree(dshProc.Id);
            try { dshProc.WaitForExit(3000); } catch { }
        }
        dshProc = null;

        if (PortOpen(Port))
        {
            int pid = FindPidOnPort(Port);
            if (pid > 0)
            {
                Log("StopDsh: killing external pid=" + pid);
                KillTree(pid);
            }
        }
        WaitForPortFree(8000);
        Log("StopDsh: done, port open=" + PortOpen(Port));
    }

    // kill a pid + its tree; elevate if the target runs at higher integrity
    static void KillTree(int pid)
    {
        IntegrityLevel target = GetIntegrity(pid);
        Log("KillTree: pid=" + pid + " targetIntegrity=" + target + " selfIntegrity=" + selfIntegrity);

        bool needElevate = (target != IntegrityLevel.Unknown) && (target > selfIntegrity);
        if (needElevate)
        {
            Log("KillTree: elevating to kill higher-integrity pid=" + pid);
            RunElevatedKill(pid);
            return;
        }

        Taskkill(pid);
        TryProcessKill(pid);

        Thread.Sleep(300);
        if (IsAlive(pid))
        {
            Log("KillTree: pid=" + pid + " still alive after normal kill, elevating");
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
            if (p != null) { p.WaitForExit(30000); Log("elevated kill helper: exit=" + p.ExitCode); }
            else Log("elevated kill helper: Process.Start returned null");
        }
        catch (Exception ex)
        {
            Log("elevated kill launch failed (UAC declined?): " + ex.Message);
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
                p.WaitForExit(8000);
                string msg = "taskkill pid=" + pid + " exit=" + p.ExitCode +
                    " out=" + outp.Trim() + " err=" + err.Trim();
                Log(msg);
                return msg;
            }
        }
        catch (Exception ex)
        {
            string msg = "taskkill pid=" + pid + " exception: " + ex.Message;
            Log(msg);
            return msg;
        }
    }

    static bool TryProcessKill(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            p.Kill();
            p.WaitForExit(3000);
            Log("Process.Kill pid=" + pid + " ok");
            return true;
        }
        catch (Exception ex)
        {
            Log("Process.Kill pid=" + pid + " failed: " + ex.Message);
            return false;
        }
    }

    static bool IsAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    static void WaitForPortFree(int timeoutMs)
    {
        int waited = 0;
        while (PortOpen(Port) && waited < timeoutMs)
        {
            Thread.Sleep(200);
            waited += 200;
        }
        if (waited >= timeoutMs && PortOpen(Port)) Log("WaitForPortFree: timed out, port still open");
    }

    static void WaitForPortUp(int timeoutMs)
    {
        int waited = 0;
        while (!PortOpen(Port) && waited < timeoutMs)
        {
            Thread.Sleep(200);
            waited += 200;
        }
        Log("WaitForPortUp: waited=" + waited + "ms up=" + PortOpen(Port));
    }

    // find Chrome top-level windows whose title matches the DSH webui and send Ctrl+R
    static void ReloadAppWindow()
    {
        try
        {
            const string title = "DeepSeek Harness";
            var targets = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    if (p.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                    {
                        var sb = new StringBuilder(256);
                        GetWindowText(hWnd, sb, 256);
                        if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                            targets.Add(hWnd);
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            if (targets.Count == 0) { Log("ReloadAppWindow: no matching window"); return; }

            // dummy ALT press unlocks Windows foreground-switch restrictions
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            int sent = 0;
            foreach (IntPtr h in targets)
            {
                SetForegroundWindow(h);
                Thread.Sleep(80);
                if (GetForegroundWindow() != h)
                {
                    Log("ReloadAppWindow: cannot focus window, skip");
                    continue;
                }
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_R, 0, 0, UIntPtr.Zero);
                keybd_event(VK_R, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                sent++;
                Thread.Sleep(150);
            }
            Log("ReloadAppWindow: reloaded " + sent + "/" + targets.Count + " window(s)");
        }
        catch (Exception ex) { Log("ReloadAppWindow failed: " + ex.Message); }
    }

    static bool IsDshUp()
    {
        if (dshProc != null && !dshProc.HasExited) return true;
        return PortOpen(Port);
    }

    static bool PortOpen(int port)
    {
        using (var c = new TcpClient())
        {
            try
            {
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(300, false);
                if (!ok) return false;
                c.EndConnect(ar);
                return true;
            }
            catch { return false; }
        }
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
                p.WaitForExit(5000);
                string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.IndexOf("LISTENING", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf(":" + port + " ", StringComparison.Ordinal) < 0) continue;
                    string[] cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length >= 5)
                    {
                        int pid;
                        if (int.TryParse(cols[cols.Length - 1], out pid)) return pid;
                    }
                }
            }
        }
        catch (Exception ex) { Log("FindPidOnPort failed: " + ex.Message); }
        return 0;
    }

    static IntegrityLevel GetIntegrity(int pid)
    {
        try
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return IntegrityLevel.Unknown;
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(h, TOKEN_QUERY, out tok)) return IntegrityLevel.Unknown;
                try
                {
                    uint retLen;
                    GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out retLen);
                    IntPtr buf = Marshal.AllocHGlobal((int)retLen);
                    try
                    {
                        if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, retLen, out retLen))
                            return IntegrityLevel.Unknown;
                        IntPtr sid = Marshal.ReadIntPtr(buf);
                        string s = new SecurityIdentifier(sid).Value;
                        int dash = s.LastIndexOf('-');
                        int rid;
                        if (dash < 0 || !int.TryParse(s.Substring(dash + 1), out rid)) return IntegrityLevel.Unknown;
                        return (IntegrityLevel)rid;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(tok); }
            }
            finally { CloseHandle(h); }
        }
        catch { return IntegrityLevel.Unknown; }
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
                        DestroyIcon(h);
                        return icon;
                    }
                }
            }
        }
        catch (Exception ex) { Log("BuildIconFromResource(" + resName + ") failed: " + ex.Message); return null; }
    }

    static void UpdateStatus()
    {
        if (tray == null) return;
        bool up = IsDshUp();
        Icon use = null;
        if (up) use = blueIcon;
        else use = darkMode ? whiteIcon : darkIcon;
        if (use != null) tray.Icon = use;
        tray.Text = up ? "DSH Harness — 运行中" : "DSH Harness — 已停止";
    }

    static bool IsDarkMode()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
            {
                if (k == null) return false;
                object v = k.GetValue("AppsUseLightTheme");
                return v != null && Convert.ToInt32(v) == 0;
            }
        }
        catch { return false; }
    }

    static bool IsAutostartEnabled()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                if (k == null) return false;
                return k.GetValue("dsh-tray") != null;
            }
        }
        catch { return false; }
    }

    static void ToggleAutostart()
    {
        bool want = !IsAutostartEnabled();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k == null) return;
                if (want) k.SetValue("dsh-tray", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("dsh-tray", false);
            }
            Log("autostart = " + want);
        }
        catch (Exception ex) { Log("autostart toggle failed: " + ex.Message); }
    }

    static bool LoadAutoRestart()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\DSHTray", false))
            {
                if (k == null) return false;
                object v = k.GetValue("AutoRestart");
                return v != null && Convert.ToInt32(v) == 1;
            }
        }
        catch { return false; }
    }

    static void SaveAutoRestart()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\DSHTray"))
            {
                k.SetValue("AutoRestart", autoRestartEnabled ? 1 : 0);
            }
        }
        catch (Exception ex) { Log("save autoRestart failed: " + ex.Message); }
    }

    static void ToggleAutoRestart()
    {
        autoRestartEnabled = !autoRestartEnabled;
        SaveAutoRestart();
        Log("autoRestart = " + autoRestartEnabled);
    }

    static void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + logPath + "\"",
                UseShellExecute = false
            });
            string dshLog = Path.Combine(Path.GetDirectoryName(logPath), "dsh.log");
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
        catch (Exception ex) { Log("OpenLog failed: " + ex.Message); }
    }

    static void ExitApp()
    {
        // tray only: harness keeps running (stop it via the 停止 menu item)
        Log("=== ExitApp (tray only, harness kept running) ===");
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Application.Exit();
    }

    static void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                try
                {
                    FileInfo fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    {
                        try { File.Copy(logPath, logPath + ".old", true); } catch { }
                        File.WriteAllText(logPath, "", Encoding.UTF8);
                    }
                }
                catch { }
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}
