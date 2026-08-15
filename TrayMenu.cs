using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

// Tray icon + native context menu + status/theme UI. Static (single-process). Depends on
// DshProcess and WindowMgr (received via Init), plus Config/Win32/Logging/Lang.
static class TrayMenu
{
    const int PollIntervalMs = 3000;       // status-poll timer cadence
    const int DoubleClickSwallowMs = 300;  // left-click dedupe window

    static NotifyIcon tray;
    static Icon whiteIcon;
    static Icon blueIcon;
    static Icon darkIcon;
    static bool darkMode;
    static bool menuShowing;
    static Form menuOwner;
    static int lastLeftClickTick = -1000;
    static System.Windows.Forms.Timer pollTimer;
    static System.Windows.Forms.Timer flashTimer;
    static bool flashing;
    static DshProcess dp;
    static string appVersion;
    static bool lastUpState;
    static bool lastDarkState;

    // theme flag exposed so Program can log it on startup (set during Init before the tray builds)
    public static bool DarkMode { get { return darkMode; } }

    // dependency injection: Program creates the DshProcess instance and hands it in
    public static void Init(DshProcess process, string version)
    {
        dp = process;
        appVersion = version;
        darkMode = Config.IsDarkMode();
        Win32.ApplyAppTheme(darkMode);
        // seed the change-detection cache with a forced mismatch so the first UpdateStatus
        // always applies the real icon/text (BuildTray only sets a provisional white icon)
        lastDarkState = !darkMode;
        lastUpState = false;
        Logging.Log("=== dsh-tray v" + version + " started (integrity=" + dp.SelfIntegrity +
            ", autoRestart=" + dp.AutoRestartEnabled + ", darkMode=" + darkMode + ") ===");
        BuildTray();
        dp.UserStopped = false;
        if (!dp.IsUp) dp.StartDsh();
        UpdateStatus();
        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = PollIntervalMs;
        pollTimer.Tick += delegate { PollTick(); };
        pollTimer.Start();
        flashTimer = new System.Windows.Forms.Timer();
        flashTimer.Interval = 500;
        flashTimer.Tick += delegate { FlashTick(); };
        // silent one-shot GitHub update check; result is read on the next menu build
        UpdateCheck.CheckOnce(appVersion);
    }

    public static void Dispose()
    {
        if (pollTimer != null) { pollTimer.Stop(); pollTimer.Dispose(); }
        if (flashTimer != null) { flashTimer.Stop(); flashTimer.Dispose(); }
        if (whiteIcon != null) whiteIcon.Dispose();
        if (blueIcon != null) blueIcon.Dispose();
        if (darkIcon != null) darkIcon.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        if (menuOwner != null) { menuOwner.Dispose(); menuOwner = null; }
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
        dp.PollAutoRestart();
        UpdateStatus();
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
    static async void StartAndOpen()
    {
        try
        {
            if (dp.IsStarting)
            {
                WindowMgr.OpenWindow(); // already starting: just open, don't start twice
                return;
            }
            if (!dp.IsUp)
            {
                dp.UserStopped = false;
                await dp.StartAndWaitAsync();
                UpdateStatus();
            }
            WindowMgr.OpenWindow();
        }
        catch (Exception ex) { Logging.Log("StartAndOpen failed: " + ex.Message); }
    }

    static void ShowTrayMenu()
    {
        if (menuShowing) return;
        menuShowing = true;
        try
        {
            bool up = dp.IsUp;
            var defs = new List<MenuDef>();
            defs.Add(new MenuDef(Lang.T("menu.open"), WindowMgr.OpenWindow, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.start"), async delegate
            {
                try { if (!dp.IsUp && !dp.IsStarting) await dp.StartAndWaitAsync(); UpdateStatus(); }
                catch (Exception ex) { Logging.Log("start failed: " + ex.Message); UpdateStatus(); }
            }, !up, false));
            defs.Add(new MenuDef(Lang.T("menu.restart"), async delegate
            {
                try { await dp.RestartAsync(); WindowMgr.ReloadAppWindow(); UpdateStatus(); }
                catch (Exception ex) { Logging.Log("restart failed: " + ex.Message); UpdateStatus(); }
            }, up, false));
            defs.Add(new MenuDef(Lang.T("menu.stop"), async delegate
            {
                try
                {
                    if (dp.IsUp) { dp.UserStopped = true; UpdateStatus(); await dp.StopAsync(); }
                    UpdateStatus();
                }
                catch (Exception ex) { Logging.Log("stop failed: " + ex.Message); UpdateStatus(); }
            }, up, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            defs.Add(new MenuDef(Lang.T("menu.settings"), delegate { new SettingsForm(dp, appVersion).ShowDialog(); }, true, false));
            defs.Add(new MenuDef(null, null, true, false) { Separator = true });
            if (UpdateCheck.IsNewerAvailable)
            {
                defs.Add(new MenuDef(string.Format(Lang.T("menu.downloadUpdate"), UpdateCheck.LatestVersion),
                    delegate { OpenUpdatePage(); }, true, false));
            }
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

    public static List<Action> BuildNativeMenu(List<MenuDef> defs, out IntPtr hmenu)
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

    public class MenuDef
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

    // white <-> blue alternation while starting (startup-in-progress flash)
    static void FlashTick()
    {
        if (tray != null)
            tray.Icon = (tray.Icon == whiteIcon) ? blueIcon : whiteIcon;
    }

    static void UpdateStatus()
    {
        if (tray == null) return;
        if (dp.IsStarting)
        {
            // keep flashing; don't overwrite the icon with a static status
            if (!flashing)
            {
                flashing = true;
                if (flashTimer != null) flashTimer.Start();
            }
            return;
        }
        // not starting: stop the flash and settle on the real icon
        if (flashing)
        {
            flashing = false;
            if (flashTimer != null) flashTimer.Stop();
        }
        bool up = dp.IsUp;
        // skip when nothing changed (avoid churning the icon/text every poll tick)
        if (up == lastUpState && darkMode == lastDarkState)
            return;
        lastUpState = up;
        lastDarkState = darkMode;
        Icon use = null;
        if (up) use = blueIcon;
        else use = darkMode ? whiteIcon : darkIcon;
        if (use != null) tray.Icon = use;
        tray.Text = up ? Lang.T("tray.running") : Lang.T("tray.stopped");
    }

    // open the GitHub releases page for the "download update" menu item
    static void OpenUpdatePage()
    {
        try { Process.Start(UpdateCheck.ReleasesPageUrl); }
        catch (Exception ex) { Logging.Log("OpenUpdatePage failed: " + ex.Message); }
    }

    static void ExitApp()
    {
        // tray only: harness keeps running (stop it via the Stop menu item)
        Logging.Log("=== ExitApp (tray only, harness kept running) ===");
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Application.Exit();
    }
}
