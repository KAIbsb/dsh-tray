using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("dsh-tray")]
[assembly: AssemblyDescription("DeepSeek Harness tray lifecycle manager")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]
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
            // elevated kill helper: a separate elevated process spawned via runas. It verifies
            // a one-time nonce + target identity before killing (fail-closed). Loads config so
            // it can compare the dsh entry against the token + target command line.
            if (args[1] == "--elevated-kill" && args.Length > 2)
            {
                Logging.InitLog();
                Config.InitConfig();
                var killDp = new DshProcess(Config.Current);
                killDp.SelfIntegrity = Win32.GetIntegrity(Process.GetCurrentProcess().Id);
                int pid;
                if (int.TryParse(args[2], out pid))
                {
                    string nonce = args.Length > 3 ? args[3] : null;
                    bool ok = killDp.RunElevatedKillDirect(pid, nonce);
                    Environment.ExitCode = ok ? 0 : 1;
                }
                else Environment.ExitCode = 1;
                return;
            }

            // diagnostic one-shot modes: need full config detection, still early-return
            if (args[1] == "--smoke" || args[1] == "--find-window" || args[1] == "--menu-test" || args[1] == "--ui-preview")
            {
                Logging.InitLog();
                Config.InitConfig();
                if (args[1] == "--smoke") { RunSmoke(); return; }
                if (args[1] == "--find-window") { RunFindWindow(); return; }
                if (args[1] == "--menu-test") { RunMenuTest(); return; }
                if (args[1] == "--ui-preview") { RunUiPreview(); return; }
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
        sb.AppendLine("themeOverride=" + (Config.ThemeOverride ?? "") + " isDark=" + Config.IsDarkMode());
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
            // build the REAL menu via the shared factory (same defs ShowTrayMenu uses), then
            // count only the executable items (separators carry no action) — asserted from the
            // real defs, never a hardcoded number.
            var defs = TrayMenu.BuildMenuDefs(DshState.Stopped);
            var execCount = 0;
            foreach (var def in defs) if (!def.Separator) execCount++;
            List<Action> actions;
            IntPtr hmenu;
            actions = TrayMenu.BuildNativeMenu(defs, out hmenu);
            bool ok = hmenu != IntPtr.Zero && actions.Count == execCount && execCount > 0;
            if (hmenu != IntPtr.Zero) Win32.DestroyMenu(hmenu);
            File.WriteAllText(Path.Combine(dir, "menu-test.txt"),
                ok ? "menu-test OK (items=" + actions.Count + ", defs=" + defs.Count + ")" : "menu-test FAIL", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "menu-test.txt"), "menu-test FAIL: " + ex.Message, Encoding.UTF8); } catch (Exception ex2) { Logging.Log("RunMenuTest write failed: " + ex2.Message); }
        }
    }

    // ---- headless, dev-only: render the settings dialog (light + dark) to 1.5x PNGs ----
    static void RunUiPreview()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var dp = new DshProcess(Config.Current);
        RenderSettingsPreview(dp, false, "settings-preview-light.png");
        RenderSettingsPreview(dp, true, "settings-preview-dark.png");
    }

    static void RenderSettingsPreview(DshProcess dp, bool dark, string fileName)
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), fileName);
        SettingsForm form = null;
        try
        {
            form = new SettingsForm(dp, AppVersion, dark);
            // show offscreen so every child control handle is created and paint state is
            // fully initialized, then WM_PRINT renders nonclient+client+children into the HDC
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            using (var bmp = new Bitmap(form.Width, form.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                Win32.SendMessage(form.Handle, Win32.WM_PRINT, hdc, (IntPtr)Win32.PRF_ALL);
                g.ReleaseHdc(hdc);
                int w = (int)Math.Round(form.Width * 1.5);
                int h = (int)Math.Round(form.Height * 1.5);
                using (var scaled = new Bitmap(w, h))
                using (var g2 = Graphics.FromImage(scaled))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.DrawImage(bmp, 0, 0, w, h);
                    scaled.Save(path, ImageFormat.Png);
                }
            }
            form.Close();
            Logging.Log("ui-preview written to " + path);
        }
        catch (Exception ex) { Logging.Log("ui-preview (" + fileName + ") failed: " + ex.Message); }
        finally { if (form != null) form.Dispose(); }
    }
}
