using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// Settings dialog: language hot-switch, auto-restart / autostart toggles, update check,
// about info (version / repo / log dir), and open-config/open-logs entries. Created by TrayMenu;
// depends on DshProcess / Config / Lang / UpdateCheck (fields injected via constructor).
class SettingsForm : Form
{
    readonly DshProcess dp;
    readonly string appVersion;

    Label lblSecGeneral;
    Panel lineGeneral;
    Label lblSecAbout;
    Panel lineAbout;
    Label lblLanguage;
    Panel langPanel;
    RadioButton radioAuto;
    RadioButton radioZh;
    RadioButton radioEn;
    Label lblTheme;
    Panel themePanel;
    RadioButton radioThemeAuto;
    RadioButton radioThemeLight;
    RadioButton radioThemeDark;
    CheckBox chkAutoRestart;
    CheckBox chkAutostart;
    Button btnCheck;
    Label lblResult;
    LinkLabel lnkDownload;
    Button btnAutoUpdate;
    Label lblVersion;
    Label lblCurrentUrl;
    LinkLabel lnkRepo;
    Button btnOpenConfig;
    Button btnOpenLogs;
    Button btnClose;

    // current persisted language: "" = follow system, "zh", "en"
    string langCode;
    bool applyingLang;
    readonly bool? themeOverride;
    Icon ownedIcon;

    public SettingsForm(DshProcess process, string version, bool? themeOverride = null)
    {
        dp = process;
        appVersion = version;
        this.themeOverride = themeOverride;
        langCode = Config.Current.IniLang ?? "";

        Text = "dsh-tray";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 426);
        AutoScaleMode = AutoScaleMode.Dpi;
        try { ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (ownedIcon != null) Icon = ownedIcon;

        BuildControls();
        ApplyTheme();
        ApplyLang();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
        base.Dispose(disposing);
    }

    void BuildControls()
    {
        // ---- section 1: general (bold heading + separator line) ----
        lblSecGeneral = new Label { Left = 16, Top = 12, AutoSize = true };
        lblSecGeneral.Font = new Font(lblSecGeneral.Font, FontStyle.Bold);
        lineGeneral = new Panel { Left = 16, Top = 34, Width = 528, Height = 1 };
        lblLanguage = new Label { Left = 16, Top = 44, Width = 90, Height = 22 };
        lblLanguage.AutoSize = false;
        lblLanguage.TextAlign = ContentAlignment.MiddleLeft;
        // each radio group gets its OWN Panel parent: WinForms RadioButtons group by parent
        // container, so language (3) and theme (3) radios must NOT share the Form directly or
        // they form ONE mutual-exclusion group (the bug). Radio positions are panel-relative.
        langPanel = new Panel { Left = 120, Top = 44, Width = 400, Height = 26, BorderStyle = BorderStyle.None };
        radioAuto = new RadioButton { Left = 0, Top = 0, AutoSize = true };
        radioAuto.CheckedChanged += delegate { OnLangChanged(radioAuto); };
        radioZh = new RadioButton { Left = 0, Top = 0, AutoSize = true };
        radioZh.CheckedChanged += delegate { OnLangChanged(radioZh); };
        radioEn = new RadioButton { Left = 0, Top = 0, AutoSize = true };
        radioEn.CheckedChanged += delegate { OnLangChanged(radioEn); };
        langPanel.Controls.Add(radioAuto);
        langPanel.Controls.Add(radioZh);
        langPanel.Controls.Add(radioEn);
        lblTheme = new Label { Left = 16, Top = 76, Width = 90, Height = 22 };
        lblTheme.AutoSize = false;
        lblTheme.TextAlign = ContentAlignment.MiddleLeft;
        themePanel = new Panel { Left = 120, Top = 76, Width = 400, Height = 26, BorderStyle = BorderStyle.None };
        radioThemeAuto = new RadioButton { Left = 0, Top = 0, AutoSize = true };
        radioThemeAuto.CheckedChanged += delegate { OnThemeChanged(radioThemeAuto); };
        radioThemeLight = new RadioButton { Left = 0, Top = 0, AutoSize = true };
        radioThemeLight.CheckedChanged += delegate { OnThemeChanged(radioThemeLight); };
        radioThemeDark = new RadioButton { Left = 0, Top = 0, AutoSize = true };
        radioThemeDark.CheckedChanged += delegate { OnThemeChanged(radioThemeDark); };
        themePanel.Controls.Add(radioThemeAuto);
        themePanel.Controls.Add(radioThemeLight);
        themePanel.Controls.Add(radioThemeDark);
        chkAutoRestart = new CheckBox { Left = 16, Top = 108, Width = 528, Height = 22 };
        chkAutoRestart.Checked = dp.AutoRestartEnabled;
        chkAutoRestart.CheckedChanged += delegate { OnAutoRestartChanged(); };
        chkAutostart = new CheckBox { Left = 16, Top = 140, Width = 528, Height = 22 };
        chkAutostart.Checked = Config.IsAutostartEnabled();
        chkAutostart.CheckedChanged += delegate { OnAutostartChanged(); };

        // ---- section 2: about / updates ----
        lblSecAbout = new Label { Left = 16, Top = 184, AutoSize = true };
        lblSecAbout.Font = new Font(lblSecAbout.Font, FontStyle.Bold);
        lineAbout = new Panel { Left = 16, Top = 206, Width = 528, Height = 1 };
        lblVersion = new Label { Left = 16, Top = 216, Width = 528, Height = 20 };
        lblVersion.AutoSize = false;
        lblVersion.TextAlign = ContentAlignment.MiddleLeft;
        lblCurrentUrl = new Label { Left = 16, Top = 238, Width = 528, Height = 22 };
        lblCurrentUrl.AutoSize = false;
        lblCurrentUrl.TextAlign = ContentAlignment.MiddleLeft;
        lnkRepo = new LinkLabel { Left = 16, Top = 264, Width = 528, Height = 20 };
        lnkRepo.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };
        btnCheck = new Button { Left = 16, Top = 296, Width = 150, Height = 28 };
        btnCheck.Click += delegate { OnCheckUpdate(); };
        lblResult = new Label { Left = 178, Top = 300, Width = 180, Height = 22 };
        lblResult.AutoSize = false;
        lblResult.TextAlign = ContentAlignment.MiddleLeft;
        lnkDownload = new LinkLabel { Left = 186, Top = 299, AutoSize = true, Visible = false };
        lnkDownload.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };
        btnAutoUpdate = new Button { Left = 400, Top = 296, Width = 132, Height = 28, Visible = false };
        btnAutoUpdate.Click += delegate { OnAutoUpdate(); };
        btnOpenConfig = new Button { Left = 16, Top = 328, Width = 256, Height = 28 };
        btnOpenConfig.Click += delegate { OpenConfig(); };
        btnOpenLogs = new Button { Left = 288, Top = 328, Width = 256, Height = 28 };
        btnOpenLogs.Click += delegate { OpenLogsFolder(); };

        // ---- close button (bottom-right) ----
        btnClose = new Button { Left = 454, Top = 374, Width = 90, Height = 30 };
        btnClose.Click += delegate { Close(); };

        Controls.Add(lblSecGeneral);
        Controls.Add(lineGeneral);
        Controls.Add(lblLanguage);
        Controls.Add(langPanel);
        Controls.Add(lblTheme);
        Controls.Add(themePanel);
        Controls.Add(chkAutoRestart);
        Controls.Add(chkAutostart);
        Controls.Add(lblSecAbout);
        Controls.Add(lineAbout);
        Controls.Add(lblVersion);
        Controls.Add(lblCurrentUrl);
        Controls.Add(lnkRepo);
        Controls.Add(btnCheck);
        Controls.Add(lblResult);
        Controls.Add(lnkDownload);
        Controls.Add(btnAutoUpdate);
        Controls.Add(btnOpenConfig);
        Controls.Add(btnOpenLogs);
        Controls.Add(btnClose);

        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    // full light/dark adaptation across form + separators + every control
    // public: TrayMenu.ApplyThemeNow() calls it to re-theme the open dialog on a theme change
    public void ApplyTheme()
    {
        bool dark = themeOverride ?? Config.IsDarkMode();
        Color back = dark ? Color.FromArgb(0x20, 0x20, 0x20) : SystemColors.Control;       // form background
        Color fore = dark ? Color.FromArgb(0xF0, 0xF0, 0xF0) : SystemColors.ControlText;   // primary text
        Color line = dark ? Color.FromArgb(0x3F, 0x3F, 0x3F) : Color.FromArgb(0xC8, 0xC8, 0xC8); // separator line
        Color btnBack = dark ? Color.FromArgb(0x45, 0x45, 0x45) : SystemColors.Control;    // normal button bg
        Color btnBorder = dark ? Color.FromArgb(0x54, 0x54, 0x54) : Color.FromArgb(0xB0, 0xB0, 0xB0);
        Color btnHover = dark ? Color.FromArgb(0x56, 0x56, 0x56) : Color.FromArgb(0xE8, 0xF0, 0xFE);
        Color link = dark ? Color.FromArgb(0x8F, 0xC3, 0xFF) : Color.Blue;
        Color dim = dark ? Color.FromArgb(0xAA, 0xAA, 0xAA) : Color.FromArgb(0x66, 0x66, 0x66);   // version (dim)

        BackColor = back;
        ForeColor = fore;

        lineGeneral.BackColor = line;
        lineAbout.BackColor = line;

        // panels host the radio groups; paint them to match the form so no default light panel
        // shows through in dark mode
        langPanel.BackColor = back;
        langPanel.ForeColor = fore;
        themePanel.BackColor = back;
        themePanel.ForeColor = fore;

        lblSecGeneral.ForeColor = fore;
        lblSecAbout.ForeColor = fore;
        lblLanguage.ForeColor = fore;
        lblTheme.ForeColor = fore;
        lblResult.ForeColor = fore;
        lblVersion.ForeColor = dim;
        lblCurrentUrl.ForeColor = dim;

        StyleRadio(radioAuto, fore);
        StyleRadio(radioZh, fore);
        StyleRadio(radioEn, fore);
        StyleRadio(radioThemeAuto, fore);
        StyleRadio(radioThemeLight, fore);
        StyleRadio(radioThemeDark, fore);
        StyleCheckBox(chkAutoRestart, fore);
        StyleCheckBox(chkAutostart, fore);

        StyleButton(btnCheck, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnAutoUpdate, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnOpenConfig, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnOpenLogs, btnBack, fore, btnBorder, btnHover);
        // brand-blue primary button, identical in both themes
        StyleButton(btnClose, Color.FromArgb(0x0F, 0x6C, 0xBD), Color.White,
            Color.FromArgb(0x0F, 0x6C, 0xBD), Color.FromArgb(0x17, 0x72, 0xC9));

        lnkRepo.LinkColor = link;
        lnkRepo.LinkBehavior = LinkBehavior.HoverUnderline;
        lnkDownload.LinkColor = link;
        lnkDownload.LinkBehavior = LinkBehavior.HoverUnderline;
    }

    static void StyleRadio(RadioButton rb, Color fore)
    {
        rb.ForeColor = fore;
        rb.UseVisualStyleBackColor = false;
        // BackColor left inherited; the radio dot/ring stays system-drawn (accepted)
    }

    static void StyleCheckBox(CheckBox cb, Color fore)
    {
        cb.ForeColor = fore;
        cb.UseVisualStyleBackColor = false;
        // BackColor left inherited; the checkbox square stays system-drawn (accepted)
    }

    static void StyleButton(Button b, Color back, Color fore, Color border, Color hover)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = back;
        b.ForeColor = fore;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = border;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = hover;
        b.UseVisualStyleBackColor = false;
    }

    void ApplyLang()
    {
        applyingLang = true;
        Text = Lang.T("settings.title");
        lblSecGeneral.Text = Lang.T("settings.groupGeneral");
        lblSecAbout.Text = Lang.T("settings.groupAbout");
        lblLanguage.Text = Lang.T("settings.language");
        radioAuto.Text = Lang.T("settings.langAuto");
        radioZh.Text = Lang.T("settings.langZh");
        radioEn.Text = Lang.T("settings.langEn");
        // dynamic equal spacing: recompute Left after text widths settle (AutoSize)
        radioZh.Left = radioAuto.Right + 16;
        radioEn.Left = radioZh.Right + 16;
        radioAuto.Checked = (langCode == "");
        radioZh.Checked = (langCode == "zh");
        radioEn.Checked = (langCode == "en");
        lblTheme.Text = Lang.T("settings.theme");
        radioThemeAuto.Text = Lang.T("settings.themeAuto");
        radioThemeLight.Text = Lang.T("settings.themeLight");
        radioThemeDark.Text = Lang.T("settings.themeDark");
        // dynamic equal spacing: recompute Left after text widths settle (AutoSize)
        radioThemeLight.Left = radioThemeAuto.Right + 16;
        radioThemeDark.Left = radioThemeLight.Right + 16;
        string theme = (Config.ThemeOverride ?? "").Trim().ToLowerInvariant();
        radioThemeAuto.Checked = (theme.Length == 0);
        radioThemeLight.Checked = (theme == "light");
        radioThemeDark.Checked = (theme == "dark");
        chkAutoRestart.Text = Lang.T("settings.autoRestart");
        chkAutostart.Text = Lang.T("settings.autostart");
        btnCheck.Text = Lang.T("settings.checkUpdate");
        btnAutoUpdate.Text = Lang.T("settings.autoUpdate");
        lblVersion.Text = string.Format(Lang.T("settings.version"), appVersion);
        lblCurrentUrl.Text = string.Format(Lang.T("settings.currentUrl"), Config.Current.WebUrl);
        lnkRepo.Text = Lang.T("settings.repo");
        lnkDownload.Text = Lang.T("settings.download");
        btnOpenConfig.Text = Lang.T("settings.openConfig");
        btnOpenLogs.Text = Lang.T("settings.openLogs");
        btnClose.Text = Lang.T("settings.close");
        applyingLang = false;
    }

    // only fire on a radio becoming checked (radio groups emit both an uncheck and a check)
    void OnLangChanged(RadioButton rb)
    {
        if (applyingLang) return;
        if (!rb.Checked) return;
        if (rb == radioAuto) langCode = "";
        else if (rb == radioZh) langCode = "zh";
        else if (rb == radioEn) langCode = "en";
        Lang.Switch(langCode);
        ApplyLang();
    }

    // only fire on a radio becoming checked (radio groups emit both an uncheck and a check)
    void OnThemeChanged(RadioButton rb)
    {
        if (applyingLang) return;
        if (!rb.Checked) return;
        string val;
        if (rb == radioThemeAuto) val = "";
        else if (rb == radioThemeLight) val = "light";
        else val = "dark";
        Config.SetTheme(val);
        // apply immediately: tray sees the new effective theme (icon + uxtheme) and this dialog
        // re-themes itself (TrayMenu.ApplyThemeNow also re-themes the open dialog via ApplyTheme)
        TrayMenu.ApplyThemeNow();
    }

    void OnAutoRestartChanged()
    {
        dp.AutoRestartEnabled = chkAutoRestart.Checked;
        Config.SaveAutoRestart(dp.AutoRestartEnabled);
    }

    void OnAutostartChanged()
    {
        if (chkAutostart.Checked != Config.IsAutostartEnabled())
        {
            Config.ToggleAutostart();
        }
    }

    // download the new exe to a temp path, verify its checksum, then deploy it over the running
    // exe (rename the running exe aside when possible). No process restart: the user restarts the
    // tray manually to apply. UI is disabled while running; result arrives via balloons (Info on
    // success/partial, Fail on error) and the button is re-enabled.
    void OnAutoUpdate()
    {
        btnAutoUpdate.Enabled = false;
        btnAutoUpdate.Text = Lang.T("settings.updating");
        string destPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-tray", "update", "dsh-tray.exe.new");
        Task.Run(() =>
        {
            bool downloaded = UpdateCheck.DownloadAndVerify(destPath);
            if (!downloaded)
            {
                TryCleanup(destPath);
                BeginInvokeSafe(delegate
                {
                    btnAutoUpdate.Enabled = true;
                    btnAutoUpdate.Text = Lang.T("settings.autoUpdate");
                });
                UiFeedback.Fail(Lang.T("settings.autoUpdateFailed"));
                return;
            }
            // deploy: rename running exe aside (may fail if locked / read-only) then move new in
            string exePath = Application.ExecutablePath;
            string oldPath = exePath + ".old.tmp.exe";
            try
            {
                bool swapped = false;
                if (File.Exists(oldPath)) TryDeleteFile(oldPath);
                try { File.Move(exePath, oldPath); swapped = true; } catch { swapped = false; }
                if (swapped)
                {
                    File.Move(destPath, exePath);
                    UiFeedback.Info(Lang.T("settings.updateReady"));
                }
                else
                {
                    // running exe is locked (a tray instance holds it): a running process cannot
                    // overwrite its own binary, so swap is normally unavailable here. Keep the
                    // verified download in place for manual deployment after exiting the tray, and
                    // say so clearly.
                    Logging.Log("auto-update: running exe is locked, verified binary left at " + destPath);
                    UiFeedback.Fail(Lang.T("settings.updateDeployFailed"));
                }
            }
            catch (Exception ex)
            {
                Logging.Log("auto-update deploy failed: " + ex.Message);
                UiFeedback.Fail(Lang.T("settings.autoUpdateFailed"));
                TryCleanup(destPath);
            }
            BeginInvokeSafe(delegate
            {
                btnAutoUpdate.Enabled = true;
                btnAutoUpdate.Text = Lang.T("settings.autoUpdate");
            });
        });
    }

    // thread-safe BeginInvoke that survives disposal races (the dialog may close mid-download)
    void BeginInvokeSafe(Action a)
    {
        try { if (!IsDisposed) BeginInvoke(a); } catch { }
    }

    static void TryCleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    void OnCheckUpdate()
    {
        btnCheck.Enabled = false;
        lblResult.Text = Lang.T("settings.checking");
        Task.Run(() =>
        {
            bool newer = UpdateCheck.Check(appVersion);
            try
            {
                BeginInvoke((Action)delegate
                {
                    btnCheck.Enabled = true;
                    if (newer)
                    {
                        lblResult.Text = string.Format(Lang.T("settings.updateAvailable"), UpdateCheck.LatestVersion);
                        // re-position the download link right after the result text (which may
                        // have grown) so they never overlap or clip the link's text
                        lnkDownload.Left = lblResult.Right + 8;
                        lnkDownload.Visible = true;
                        btnAutoUpdate.Visible = true;
                    }
                    else
                    {
                        lblResult.Text = Lang.T("settings.upToDate");
                        lnkDownload.Visible = false;
                        btnAutoUpdate.Visible = false;
                    }
                });
            }
            catch { }
        });
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(url); }
        catch (Exception ex) { Logging.Log("SettingsForm open url failed: " + ex.Message); }
    }

    void OpenConfig()
    {
        try
        {
            string ini = Config.IniPath;
            Config.EnsureIni();
            Process.Start(new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "notepad.exe"), Arguments = "\"" + ini + "\"", UseShellExecute = false });
        }
        catch (Exception ex) { Logging.Log("SettingsForm open config failed: " + ex.Message); UiFeedback.Fail(Lang.T("feedback.openConfigFailed")); }
    }

    void OpenLogsFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(Logging.LogPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                // bare name, not the System32 full path: ShellExecute on the full explorer.exe
                // path fails with "file not found" (verified); explorer resolves via App Paths
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
        }
        catch (Exception ex) { Logging.Log("SettingsForm open logs failed: " + ex.Message); UiFeedback.Fail(Lang.T("feedback.openLogsFailed")); }
    }
}
