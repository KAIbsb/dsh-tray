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
    static DshProcess dp;
    static string appVersion;
    static SettingsForm openSettings;
    static bool lastUpState;             // change-detection: only re-set the icon when the state flips
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
        // operation-failure feedback: the tray owns the NotifyIcon, so it is the balloon owner
        UiFeedback.BalloonRequested += OnBalloon;
        UiFeedback.InfoRequested += OnInfo;
        if (dp.State == DshState.Stopped)
        {
#pragma warning disable 4014 // fire-and-forget initial start; the status icon settles via UpdateStatus
            dp.StartAsync();
#pragma warning restore 4014
        }
        UpdateStatus();
        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = PollIntervalMs;
        pollTimer.Tick += delegate { PollTick(); };
        pollTimer.Start();
        // silent one-shot GitHub update check; result is read on the next menu build
        UpdateCheck.CheckOnce(appVersion);
    }

    public static void Dispose()
    {
        // static events: unsubscribe so a reload / re-Init cannot double-fire the balloon handlers
        UiFeedback.BalloonRequested -= OnBalloon;
        UiFeedback.InfoRequested -= OnInfo;
        if (pollTimer != null) { pollTimer.Stop(); pollTimer.Dispose(); }
        if (whiteIcon != null) whiteIcon.Dispose();
        if (blueIcon != null) blueIcon.Dispose();
        if (darkIcon != null) darkIcon.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        if (menuOwner != null) { menuOwner.Dispose(); menuOwner = null; }
    }

    // UiFeedback subscriber: shows a non-intrusive failure balloon on the tray icon
    static void OnBalloon(string msg)
    {
        if (tray != null) tray.ShowBalloonTip(4000, "dsh-tray", msg, ToolTipIcon.Error);
    }

    // UiFeedback subscriber: shows a neutral/informational balloon (e.g. auto-update ready)
    static void OnInfo(string msg)
    {
        if (tray != null) tray.ShowBalloonTip(4000, "dsh-tray", msg, ToolTipIcon.Info);
    }

    // Apply the current effective theme (ini override or system) immediately: refresh the tray icon
    // + process uxtheme and re-theme an open settings dialog. Called by SettingsForm after the user
    // changes the theme override. The 3s PollTick remains as a fallback (it no-ops when unchanged).
    public static void ApplyThemeNow()
    {
        bool d = Config.IsDarkMode();
        if (d != darkMode)
        {
            darkMode = d;
            Win32.ApplyAppTheme(darkMode);
            Logging.Log("theme applied " + (d ? "dark" : "light"));
        }
        if (openSettings != null && !openSettings.IsDisposed) openSettings.ApplyTheme();
        UpdateStatus();
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

    // left click: ensure the harness is up, then open the window (RunAsync handles logging/status)
    static void StartAndOpen()
    {
        RunAsync(async () =>
        {
            DshState st = dp.State;
            if (st == DshState.Starting || st == DshState.Stopping)
                return; // transition in flight: don't open a window against a half-started state
            if (st == DshState.Stopped)
                await dp.StartAsync();
            WindowMgr.OpenWindow();
        }, "start");
    }

    static void ShowTrayMenu()
    {
        if (menuShowing) return;
        menuShowing = true;
        try
        {
            var defs = BuildMenuDefs(dp.State);

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

    // the tray's real menu definition, built against the current state. Shared with the headless
    // --menu-test so the test always asserts against the actual menu (no hand-copied list to drift).
    // The enabled flag per item reflects the current state; dynamic items (update download)
    // survive because each call re-reads UpdateCheck.IsNewerAvailable.
    public static List<MenuDef> BuildMenuDefs(DshState st)
    {
        var defs = new List<MenuDef>();
        defs.Add(new MenuDef(Lang.T("menu.open"), WindowMgr.OpenWindow, true, false));
        defs.Add(new MenuDef(null, null, true, false) { Separator = true });
        defs.Add(new MenuDef(Lang.T("menu.start"), delegate { RunAsync(async () => { await dp.StartAsync(); }, "start"); }, st == DshState.Stopped, false));
        defs.Add(new MenuDef(Lang.T("menu.restart"), delegate { RunAsync(async () => { await dp.RestartAsync(); WindowMgr.ReloadAppWindow(); }, "restart"); }, st == DshState.Running, false));
        defs.Add(new MenuDef(Lang.T("menu.stop"), delegate { RunAsync(async () => { await dp.StopAsync(); }, "stop"); }, st == DshState.Running || st == DshState.Starting, false));
        defs.Add(new MenuDef(null, null, true, false) { Separator = true });
        defs.Add(new MenuDef(Lang.T("menu.settings"), delegate { OpenSettings(); }, true, false));
        defs.Add(new MenuDef(null, null, true, false) { Separator = true });
        if (UpdateCheck.IsNewerAvailable)
        {
            defs.Add(new MenuDef(string.Format(Lang.T("menu.downloadUpdate"), UpdateCheck.LatestVersion),
                delegate { OpenUpdatePage(); }, true, false));
        }
        defs.Add(new MenuDef(Lang.T("menu.exit"), ExitApp, true, false));
        return defs;
    }

    // single wrapper for every fire-and-forget async menu/click action: catch + log + settle the
    // tray status. Removes the duplicated bare-async-void try/catch from each handler. The action
    // must not touch the UI thread except through UpdateStatus (invoked here on every exit path).
    static async void RunAsync(Func<Task> action, string name)
    {
        try
        {
            await action();
        }
        catch (Exception ex) { Logging.Log(name + " failed: " + ex.Message); }
        finally { UpdateStatus(); }
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

    static void UpdateStatus()
    {
        if (tray == null) return;
        // simple two-state icon: blue = running, white/dark = stopped (no flashing)
        bool up = dp.State == DshState.Running;
        if (up == lastUpState && darkMode == lastDarkState)
            return; // nothing changed
        lastUpState = up;
        lastDarkState = darkMode;
        Icon use = up ? blueIcon : (darkMode ? whiteIcon : darkIcon);
        if (use != null) tray.Icon = use;
        tray.Text = up ? Lang.T("tray.running") : Lang.T("tray.stopped");
    }

    // open the GitHub releases page for the "download update" menu item
    static void OpenUpdatePage()
    {
        try { Process.Start(UpdateCheck.ReleasesPageUrl); }
        catch (Exception ex) { Logging.Log("OpenUpdatePage failed: " + ex.Message); }
    }

    // theme change from the settings dialog: refresh tray icon + process-wide uxtheme
    static void OnSettingsThemeChanged()
    {
        ApplyThemeNow();
    }

    // single settings instance: re-focus an open dialog instead of stacking nested modals
    static void OpenSettings()
    {
        try
        {
            if (openSettings == null || openSettings.IsDisposed)
            {
                openSettings = new SettingsForm(dp, appVersion);
                openSettings.ThemeChanged += OnSettingsThemeChanged;
                openSettings.FormClosed += delegate
                {
                    openSettings.ThemeChanged -= OnSettingsThemeChanged;
                    openSettings = null;
                };
                openSettings.ShowDialog();
            }
            else
            {
                openSettings.Activate();
            }
        }
        catch (Exception ex) { Logging.Log("OpenSettings failed: " + ex.Message); }
    }

    static void ExitApp()
    {
        // tray only: harness keeps running (stop it via the Stop menu item)
        Logging.Log("=== ExitApp (tray only, harness kept running) ===");
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Application.Exit();
    }
}
