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
    RadioButton radioAuto;
    RadioButton radioZh;
    RadioButton radioEn;
    CheckBox chkAutoRestart;
    CheckBox chkAutostart;
    Button btnCheck;
    Label lblResult;
    LinkLabel lnkDownload;
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
        ClientSize = new Size(560, 392);
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
        radioAuto = new RadioButton { Left = 120, Top = 44, AutoSize = true };
        radioAuto.CheckedChanged += delegate { OnLangChanged(radioAuto); };
        radioZh = new RadioButton { Left = 220, Top = 44, AutoSize = true };
        radioZh.CheckedChanged += delegate { OnLangChanged(radioZh); };
        radioEn = new RadioButton { Left = 292, Top = 44, AutoSize = true };
        radioEn.CheckedChanged += delegate { OnLangChanged(radioEn); };
        chkAutoRestart = new CheckBox { Left = 16, Top = 76, Width = 528, Height = 22 };
        chkAutoRestart.Checked = dp.AutoRestartEnabled;
        chkAutoRestart.CheckedChanged += delegate { OnAutoRestartChanged(); };
        chkAutostart = new CheckBox { Left = 16, Top = 108, Width = 528, Height = 22 };
        chkAutostart.Checked = Config.IsAutostartEnabled();
        chkAutostart.CheckedChanged += delegate { OnAutostartChanged(); };

        // ---- section 2: about / updates ----
        lblSecAbout = new Label { Left = 16, Top = 152, AutoSize = true };
        lblSecAbout.Font = new Font(lblSecAbout.Font, FontStyle.Bold);
        lineAbout = new Panel { Left = 16, Top = 174, Width = 528, Height = 1 };
        lblVersion = new Label { Left = 16, Top = 184, Width = 528, Height = 20 };
        lblVersion.AutoSize = false;
        lblVersion.TextAlign = ContentAlignment.MiddleLeft;
        lblCurrentUrl = new Label { Left = 16, Top = 206, Width = 528, Height = 22 };
        lblCurrentUrl.AutoSize = false;
        lblCurrentUrl.TextAlign = ContentAlignment.MiddleLeft;
        lnkRepo = new LinkLabel { Left = 16, Top = 232, Width = 528, Height = 20 };
        lnkRepo.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };
        btnCheck = new Button { Left = 16, Top = 264, Width = 150, Height = 28 };
        btnCheck.Click += delegate { OnCheckUpdate(); };
        lblResult = new Label { Left = 174, Top = 268, Width = 260, Height = 22 };
        lblResult.AutoSize = false;
        lblResult.TextAlign = ContentAlignment.MiddleLeft;
        lnkDownload = new LinkLabel { Left = 182, Top = 267, AutoSize = true, Visible = false };
        lnkDownload.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };
        btnOpenConfig = new Button { Left = 16, Top = 296, Width = 256, Height = 28 };
        btnOpenConfig.Click += delegate { OpenConfig(); };
        btnOpenLogs = new Button { Left = 288, Top = 296, Width = 256, Height = 28 };
        btnOpenLogs.Click += delegate { OpenLogsFolder(); };

        // ---- close button (bottom-right) ----
        btnClose = new Button { Left = 454, Top = 342, Width = 90, Height = 30 };
        btnClose.Click += delegate { Close(); };

        Controls.Add(lblSecGeneral);
        Controls.Add(lineGeneral);
        Controls.Add(lblLanguage);
        Controls.Add(radioAuto);
        Controls.Add(radioZh);
        Controls.Add(radioEn);
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
        Controls.Add(btnOpenConfig);
        Controls.Add(btnOpenLogs);
        Controls.Add(btnClose);

        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    // full light/dark adaptation across form + separators + every control
    void ApplyTheme()
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

        lblSecGeneral.ForeColor = fore;
        lblSecAbout.ForeColor = fore;
        lblLanguage.ForeColor = fore;
        lblResult.ForeColor = fore;
        lblVersion.ForeColor = dim;
        lblCurrentUrl.ForeColor = dim;

        StyleRadio(radioAuto, fore);
        StyleRadio(radioZh, fore);
        StyleRadio(radioEn, fore);
        StyleCheckBox(chkAutoRestart, fore);
        StyleCheckBox(chkAutostart, fore);

        StyleButton(btnCheck, btnBack, fore, btnBorder, btnHover);
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
        chkAutoRestart.Text = Lang.T("settings.autoRestart");
        chkAutostart.Text = Lang.T("settings.autostart");
        btnCheck.Text = Lang.T("settings.checkUpdate");
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
                    }
                    else
                    {
                        lblResult.Text = Lang.T("settings.upToDate");
                        lnkDownload.Visible = false;
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
        catch (Exception ex) { Logging.Log("SettingsForm open config failed: " + ex.Message); }
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
        catch (Exception ex) { Logging.Log("SettingsForm open logs failed: " + ex.Message); }
    }
}
