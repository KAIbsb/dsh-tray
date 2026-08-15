using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("dsh-tray")]
[assembly: AssemblyDescription("DeepSeek Harness tray lifecycle manager")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyProduct("dsh-tray")]
[assembly: AssemblyCopyright("Copyright (c) 2026 KAIbsb")]

static class Program
{
    // version is single-sourced from the assembly version attribute (see AssemblyVersion below);
    // no hardcoded copy here to avoid drift
    static readonly string AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

    static Mutex mutex;

    [STAThread]
    static void Main()
    {
        string[] args = Environment.GetCommandLineArgs();

        // headless helper modes are dispatched before the single-instance mutex: the
        // --elevated-kill helper is a separate process spawned by a live instance (that holds
        // the mutex), so gating it would make it exit and the elevated kill would never run.
        if (args.Length > 1)
        {
            // elevated kill helper: only needs logging + our own integrity level. A bare
            // DshProcess is enough here (RunElevatedKillDirect uses no config).
            if (args[1] == "--elevated-kill" && args.Length > 2)
            {
                Logging.InitLog();
                var killDp = new DshProcess(Config.Current);
                killDp.SelfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
                int pid;
                if (int.TryParse(args[2], out pid)) killDp.RunElevatedKillDirect(pid);
                return;
            }

            // diagnostic one-shot modes: need full config detection, still early-return
            if (args[1] == "--smoke" || args[1] == "--find-window" || args[1] == "--menu-test")
            {
                Logging.InitLog();
                Config.InitConfig();
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
        var dp = new DshProcess(Config.Current);
        dp.SelfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
        dp.AutoRestartEnabled = Config.LoadAutoRestart();

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

        TrayMenu.Init(dp, AppVersion);

        Application.Run();

        TrayMenu.Dispose();
        dp.Dispose();
        if (mutex != null) mutex.ReleaseMutex();
    }

    // ---- headless self-check, writes result next to exe ----
    static void RunSmoke()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string report = Path.Combine(dir, "smoke-result.txt");
        var dp = new DshProcess(Config.Current);
        dp.SelfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
        dp.AutoRestartEnabled = Config.LoadAutoRestart();
        var sb = new StringBuilder();
        sb.AppendLine("node exists=" + File.Exists(Config.Current.NodePath));
        sb.AppendLine("dsh entry exists=" + File.Exists(Config.Current.DshEntry));
        sb.AppendLine("chrome exists=" + File.Exists(Config.Current.ChromePath));
        sb.AppendLine("self integrity=" + dp.SelfIntegrity);
        sb.AppendLine("autoRestart=" + dp.AutoRestartEnabled);
        sb.AppendLine("ui lang=" + Lang.Code);
        using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale-blue.png"))
            sb.AppendLine("blue icon resource=" + (rs != null));
        using (Stream rs2 = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale-dark.png"))
            sb.AppendLine("dark icon resource=" + (rs2 != null));
        sb.AppendLine("port" + Config.Current.Port + " open=" + dp.PortOpen(Config.Current.Port));
        int p3080 = dp.FindPidOnPort(Config.Current.Port);
        sb.AppendLine("pid on port=" + p3080);
        if (p3080 > 0) sb.AppendLine("pid integrity=" + Win32.GetIntegrity(p3080));
        sb.AppendLine("SMOKE OK");
        try { File.WriteAllText(report, sb.ToString(), Encoding.UTF8); } catch (Exception ex) { Logging.Log("RunSmoke write report failed: " + ex.Message); }
    }

    // ---- headless: list Chrome top-level windows (read-only) ----
    static void RunFindWindow()
    {
        string report = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "find-window-result.txt");
        string content = WindowMgr.FindWindows();
        try { File.WriteAllText(report, content, Encoding.UTF8); } catch (Exception ex) { Logging.Log("RunFindWindow write failed: " + ex.Message); }
    }

    // ---- headless: build the native menu without showing it ----
    static void RunMenuTest()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        try
        {
            var defs = new List<TrayMenu.MenuDef>();
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.open"), delegate { }, true, false));
            defs.Add(new TrayMenu.MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.start"), delegate { }, false, false));
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.restart"), delegate { }, true, false));
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.stop"), delegate { }, true, false));
            defs.Add(new TrayMenu.MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.openLogs"), delegate { }, true, false));
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.settings"), delegate { }, true, false));
            defs.Add(new TrayMenu.MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new TrayMenu.MenuDef(Lang.T("menu.exit"), delegate { }, true, false));
            List<Action> actions;
            IntPtr hmenu;
            actions = TrayMenu.BuildNativeMenu(defs, out hmenu);
            bool ok = hmenu != IntPtr.Zero && actions.Count == 7;
            if (hmenu != IntPtr.Zero) Win32.DestroyMenu(hmenu);
            File.WriteAllText(Path.Combine(dir, "menu-test.txt"),
                ok ? "menu-test OK (items=" + actions.Count + ")" : "menu-test FAIL", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "menu-test.txt"), "menu-test FAIL: " + ex.Message, Encoding.UTF8); } catch (Exception ex2) { Logging.Log("RunMenuTest write failed: " + ex2.Message); }
        }
    }
}
