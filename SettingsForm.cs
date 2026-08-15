using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

// Settings dialog: language hot-switch, auto-restart / autostart toggles, update check,
// about info (version / repo / log dir), and open-config/open-logs entries. Created by TrayMenu;
// depends on DshProcess / Config / Lang / UpdateCheck (fields injected via constructor).
class SettingsForm : Form
{
    readonly DshProcess dp;
    readonly string appVersion;

    ComboBox cmbLang;
    Label lblLanguage;
    CheckBox chkAutoRestart;
    CheckBox chkAutostart;
    Button btnCheck;
    Label lblResult;
    LinkLabel lnkDownload;
    Label lblVersion;
    LinkLabel lnkRepo;
    Button btnOpenConfig;
    Button btnOpenLogs;
    Button btnClose;

    // current persisted language: "" = follow system, "zh", "en"
    string langCode;
    bool applyingLang;

    public SettingsForm(DshProcess process, string version)
    {
        dp = process;
        appVersion = version;
        langCode = Config.Current.IniLang ?? "";

        Text = "dsh-tray";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 360);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildControls();
        ApplyTheme();
        ApplyLang();
    }

    void BuildControls()
    {
        lblLanguage = new Label { Left = 20, Top = 24, Width = 110 };
        lblLanguage.AutoSize = false;

        cmbLang = new ComboBox { Left = 150, Top = 20, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbLang.SelectedIndexChanged += delegate { OnLangChanged(); };

        chkAutoRestart = new CheckBox { Left = 20, Top = 64, Width = 380 };
        chkAutoRestart.Checked = dp.AutoRestartEnabled;
        chkAutoRestart.CheckedChanged += delegate { OnAutoRestartChanged(); };

        chkAutostart = new CheckBox { Left = 20, Top = 92, Width = 380 };
        chkAutostart.Checked = Config.IsAutostartEnabled();
        chkAutostart.CheckedChanged += delegate { OnAutostartChanged(); };

        btnCheck = new Button { Left = 20, Top = 128, Width = 120 };
        btnCheck.Click += delegate { OnCheckUpdate(); };

        lblResult = new Label { Left = 150, Top = 132, Width = 250 };
        lblResult.AutoSize = false;

        lnkDownload = new LinkLabel { Left = 20, Top = 160, Width = 380, Visible = false };
        lnkDownload.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };

        lblVersion = new Label { Left = 20, Top = 192, Width = 380 };
        lblVersion.AutoSize = false;

        lnkRepo = new LinkLabel { Left = 20, Top = 220, Width = 380 };
        lnkRepo.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };

        btnOpenConfig = new Button { Left = 20, Top = 260, Width = 180 };
        btnOpenConfig.Click += delegate { OpenConfig(); };

        btnOpenLogs = new Button { Left = 210, Top = 260, Width = 190 };
        btnOpenLogs.Click += delegate { OpenLogsFolder(); };

        btnClose = new Button { Left = 320, Top = 320, Width = 80 };
        btnClose.Click += delegate { Close(); };

        Controls.Add(lblLanguage);
        Controls.Add(cmbLang);
        Controls.Add(chkAutoRestart);
        Controls.Add(chkAutostart);
        Controls.Add(btnCheck);
        Controls.Add(lblResult);
        Controls.Add(lnkDownload);
        Controls.Add(lblVersion);
        Controls.Add(lnkRepo);
        Controls.Add(btnOpenConfig);
        Controls.Add(btnOpenLogs);
        Controls.Add(btnClose);

        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    // simple dark adaptation: form + control colors only, no elaborate theming
    void ApplyTheme()
    {
        bool dark = Config.IsDarkMode();
        Color back = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
        Color fore = dark ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText;
        BackColor = back;
        ForeColor = fore;
        foreach (Control c in Controls)
        {
            if (c is ComboBox || c is CheckBox || c is Label)
            {
                c.BackColor = back;
                c.ForeColor = fore;
            }
        }
        lnkRepo.LinkColor = dark ? Color.FromArgb(120, 180, 255) : Color.Blue;
        lnkDownload.LinkColor = dark ? Color.FromArgb(120, 180, 255) : Color.Blue;
    }

    void ApplyLang()
    {
        applyingLang = true;
        Text = Lang.T("settings.title");
        lblLanguage.Text = Lang.T("settings.language");
        cmbLang.Items.Clear();
        cmbLang.Items.Add(Lang.T("settings.langAuto"));
        cmbLang.Items.Add(Lang.T("settings.langZh"));
        cmbLang.Items.Add(Lang.T("settings.langEn"));
        cmbLang.SelectedIndex = IndexOfLang(langCode);
        chkAutoRestart.Text = Lang.T("settings.autoRestart");
        chkAutostart.Text = Lang.T("settings.autostart");
        btnCheck.Text = Lang.T("settings.checkUpdate");
        lblVersion.Text = string.Format(Lang.T("settings.version"), appVersion);
        lnkRepo.Text = Lang.T("settings.repo");
        lnkDownload.Text = Lang.T("settings.download");
        btnOpenConfig.Text = Lang.T("settings.openConfig");
        btnOpenLogs.Text = Lang.T("settings.openLogs");
        btnClose.Text = Lang.T("settings.close");
        applyingLang = false;
    }

    static int IndexOfLang(string code)
    {
        if (code == "en") return 2;
        if (code == "zh") return 1;
        return 0; // "" / auto
    }

    void OnLangChanged()
    {
        if (applyingLang) return;
        int idx = cmbLang.SelectedIndex;
        if (idx == 1) langCode = "zh";
        else if (idx == 2) langCode = "en";
        else langCode = "";
        Lang.Switch(langCode == "" ? "" : langCode);
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
            string ini = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dshtray.ini");
            if (!File.Exists(ini)) File.WriteAllText(ini, "", System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = "\"" + ini + "\"", UseShellExecute = false });
        }
        catch (Exception ex) { Logging.Log("SettingsForm open config failed: " + ex.Message); }
    }

    void OpenLogsFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(Logging.LogPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", "\"" + dir + "\"");
        }
        catch (Exception ex) { Logging.Log("SettingsForm open logs failed: " + ex.Message); }
    }
}
