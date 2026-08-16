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
    readonly float dpi;

    // fixed 100%-DPI row heights (general / sep / lang / theme / restart / autostart / aboutheader
    // / sep / version / url / repo / update / file / close). Absolute so text never outgrows a row
    // on high-DPI once scaled; used both for the root RowStyles and the explicit ClientSize height.
    static readonly int[] BaseRowH = { 24, 14, 26, 26, 26, 26, 24, 14, 32, 32, 32, 42, 34, 38 };

    public SettingsForm(DshProcess process, string version, bool? themeOverride = null, float? dpiOverride = null)
    {
        dp = process;
        appVersion = version;
        this.themeOverride = themeOverride;
        langCode = Config.Current.IniLang ?? "";
        // TableLayoutPanel Absolute row/column sizes do NOT follow AutoScaleMode.Dpi, so on
        // 125%/150% the fixed pixel heights are too small and text clips vertically. We scale every
        // layout pixel by the device-DPI factor ourselves (dpiOverride lets --ui-preview simulate a
        // scaled monitor at 100%).
        dpi = dpiOverride ?? ((float)DeviceDpi / 96f);

        Text = "dsh-tray";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        // in the simulated-DPI preview the device is still 100%, so AutoScaleMode.Dpi won't grow the
        // font like a real high-DPI screen would — scale it ourselves so the 125% image is faithful
        // (font and layout both grow by dpi). On a real device dpiOverride is null and AutoScaleMode
        // handles the font.
        if (dpiOverride.HasValue)
        {
            Font = new Font(Font.FontFamily, Font.Size * dpiOverride.Value, Font.Style);
        }
        ClientSize = new Size(Sp(560), Sp(426));
        // size to the grid content (rows are fixed-height); width ends up ~560 via the percent
        // column. Keep a sane floor for the FixedDialog on very small scales.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (ownedIcon != null) Icon = ownedIcon;

        BuildControls();
        ApplyTheme();
        ApplyLang();
    }

    // scale a design-time (100% DPI) pixel value to the current device DPI
    int Sp(int px) { return Math.Max(1, (int)Math.Round(px * dpi)); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
        base.Dispose(disposing);
    }

    void BuildControls()
    {
        // ---- main vertical stack: label column + content column, one row per control row ----
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Sp(14)),
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Sp(104))); // label column
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // content column
        this.root = root;

        // ---- section 1: general (bold heading + separator line) ----
        lblSecGeneral = new Label { AutoSize = true };
        lblSecGeneral.Font = new Font(lblSecGeneral.Font, FontStyle.Bold);
        lineGeneral = new Panel { Height = 1, Dock = DockStyle.Fill };

        lblLanguage = new Label { AutoSize = false, Width = Sp(90), Height = Sp(22), TextAlign = ContentAlignment.MiddleLeft };
        // each radio group gets its OWN Panel parent: WinForms RadioButtons group by parent
        // container, so language (3) and theme (3) radios must NOT share the Form directly or
        // they form ONE mutual-exclusion group (the bug). Radio positions are panel-relative.
        langPanel = new Panel { Dock = DockStyle.Fill, Height = Sp(26), BorderStyle = BorderStyle.None };
        radioAuto = new RadioButton { Left = 0, Top = Sp(2), AutoSize = true };
        radioAuto.CheckedChanged += delegate { OnLangChanged(radioAuto); };
        radioZh = new RadioButton { Left = 0, Top = Sp(2), AutoSize = true };
        radioZh.CheckedChanged += delegate { OnLangChanged(radioZh); };
        radioEn = new RadioButton { Left = 0, Top = Sp(2), AutoSize = true };
        radioEn.CheckedChanged += delegate { OnLangChanged(radioEn); };
        langPanel.Controls.Add(radioAuto);
        langPanel.Controls.Add(radioZh);
        langPanel.Controls.Add(radioEn);

        lblTheme = new Label { AutoSize = false, Width = Sp(90), Height = Sp(22), TextAlign = ContentAlignment.MiddleLeft };
        themePanel = new Panel { Dock = DockStyle.Fill, Height = Sp(26), BorderStyle = BorderStyle.None };
        radioThemeAuto = new RadioButton { Left = 0, Top = Sp(2), AutoSize = true };
        radioThemeAuto.CheckedChanged += delegate { OnThemeChanged(radioThemeAuto); };
        radioThemeLight = new RadioButton { Left = 0, Top = Sp(2), AutoSize = true };
        radioThemeLight.CheckedChanged += delegate { OnThemeChanged(radioThemeLight); };
        radioThemeDark = new RadioButton { Left = 0, Top = Sp(2), AutoSize = true };
        radioThemeDark.CheckedChanged += delegate { OnThemeChanged(radioThemeDark); };
        themePanel.Controls.Add(radioThemeAuto);
        themePanel.Controls.Add(radioThemeLight);
        themePanel.Controls.Add(radioThemeDark);

        chkAutoRestart = new CheckBox { AutoSize = true };
        chkAutoRestart.Checked = dp.AutoRestartEnabled;
        chkAutoRestart.CheckedChanged += delegate { OnAutoRestartChanged(); };
        chkAutostart = new CheckBox { AutoSize = true };
        chkAutostart.Checked = Config.IsAutostartEnabled();
        chkAutostart.CheckedChanged += delegate { OnAutostartChanged(); };

        // ---- section 2: about / updates ----
        lblSecAbout = new Label { AutoSize = true };
        lblSecAbout.Font = new Font(lblSecAbout.Font, FontStyle.Bold);
        lineAbout = new Panel { Height = 1, Dock = DockStyle.Fill };
        lblVersion = new Label { AutoSize = false, Width = Sp(500), Height = Sp(25), TextAlign = ContentAlignment.MiddleLeft };
        lblCurrentUrl = new Label { AutoSize = false, Width = Sp(500), Height = Sp(26), TextAlign = ContentAlignment.MiddleLeft };
        lnkRepo = new LinkLabel { AutoSize = false, Width = Sp(500), Height = Sp(25) };
        lnkRepo.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };

        // check-update row: check / result / download-link / auto-update all on ONE grid row so
        // they never collide when "new version found" reveals the last two. The nested panel stays
        // AutoSize (so it doesn't inflate its Absolute parent row); its internal row is a FIXED
        // scaled height so btnCheck (Dock=Fill) stretches tall enough that "Check for updates" text
        // never clips at high DPI.
        var updateRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Sp(190))); // btnCheck (wide enough for en "Check for updates")
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // lblResult
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // lnkDownload
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // btnAutoUpdate
        btnCheck = new Button { Dock = DockStyle.Fill };
        btnCheck.Click += delegate { OnCheckUpdate(); };
        lblResult = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        lnkDownload = new LinkLabel { AutoSize = true, Visible = false };
        lnkDownload.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };
        btnAutoUpdate = new Button { AutoSize = true, Height = Sp(28), Visible = false };
        btnAutoUpdate.Click += delegate { OnAutoUpdate(); };
        updateRow.Controls.Add(btnCheck, 0, 0);
        updateRow.Controls.Add(lblResult, 1, 0);
        updateRow.Controls.Add(lnkDownload, 2, 0);
        updateRow.Controls.Add(btnAutoUpdate, 3, 0);
        // fixed internal row height drives btnCheck's fill height (>= textH at every DPI)
        updateRow.RowStyles.Add(new RowStyle(SizeType.Absolute, Sp(34)));
        this.updateRow = updateRow;

        // config / logs buttons on one row
        var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnOpenConfig = new Button { AutoSize = false, Dock = DockStyle.Fill, Height = Sp(28) }; // Dock=Fill spans its cell width
        btnOpenConfig.Click += delegate { OpenConfig(); };
        btnOpenLogs = new Button { AutoSize = false, Dock = DockStyle.Fill, Height = Sp(28) };
        btnOpenLogs.Click += delegate { OpenLogsFolder(); };
        fileRow.Controls.Add(btnOpenConfig, 0, 0);
        fileRow.Controls.Add(btnOpenLogs, 1, 0);
        fileRow.RowStyles.Add(new RowStyle(SizeType.Absolute, Sp(30)));
        this.fileRow = fileRow;

        // bottom row: right-aligned close button
        var closeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        closeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btnClose = new Button { AutoSize = false, Width = Sp(90), Height = Sp(30), Anchor = AnchorStyles.Right };
        // anchor Right inside the cell keeps it flush right; add right margin via cell padding
        btnClose.Margin = new Padding(0, Sp(4), 0, 0);
        btnClose.Click += delegate { Close(); };
        closeRow.Controls.Add(btnClose, 0, 0);
        closeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        this.closeRow = closeRow;

        // ---- assemble rows ----
        AddSpan(root, lblSecGeneral, 0);
        AddSeparatorRow(root, lineGeneral, 1);
        AddRow(root, lblLanguage, langPanel, 2);
        AddRow(root, lblTheme, themePanel, 3);
        AddSpan(root, chkAutoRestart, 4);
        AddSpan(root, chkAutostart, 5);
        AddSpan(root, lblSecAbout, 6);
        AddSeparatorRow(root, lineAbout, 7);
        AddSpan(root, lblVersion, 8);
        AddSpan(root, lblCurrentUrl, 9);
        AddSpan(root, lnkRepo, 10);
        AddSpan(root, updateRow, 11);
        AddSpan(root, fileRow, 12);
        AddSpan(root, closeRow, 13);

        // explicit fixed row heights: content rows get their intended control height (auto-sizing
        // them is fragile here — both the fixed-size labels and the Dock=Fill panels report no
        // preferred size, which would collapse the whole row). Dock=Fill on children then fills each
        // row predictably. All heights are DPI-scaled so taller text at 125%/150% is not clipped.
        for (int i = 0; i < root.RowCount; i++)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Sp(BaseRowH[i])));

        Controls.Add(root);

        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    TableLayoutPanel root;
    TableLayoutPanel updateRow;
    TableLayoutPanel fileRow;
    TableLayoutPanel closeRow;

    // add a 2-column-wide (label+content) row
    void AddRow(TableLayoutPanel grid, Control label, Control content, int row)
    {
        label.Margin = new Padding(0, Sp(4), Sp(6), Sp(4));
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(content, 1, row);
    }

    // add a row that spans BOTH columns
    void AddSpan(TableLayoutPanel grid, Control c, int row, int colSpan = 2, int rowSpan = 1)
    {
        c.Margin = new Padding(0, Sp(4), 0, Sp(4));
        grid.Controls.Add(c, 0, row);
        grid.SetColumnSpan(c, colSpan);
        grid.SetRowSpan(c, rowSpan);
    }

    // separator row: 1px line set to fill its cell
    void AddSeparatorRow(TableLayoutPanel grid, Control sep, int row)
    {
        sep.Margin = new Padding(0, Sp(6), 0, Sp(6));
        grid.Controls.Add(sep, 0, row);
        grid.SetColumnSpan(sep, 2);
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

        // paint every container to the form background so no default light panel shows through
        // in dark mode (root grid + radio-group panels + nested update/file/close rows)
        if (root != null) { root.BackColor = back; root.ForeColor = fore; }
        if (updateRow != null) { updateRow.BackColor = back; updateRow.ForeColor = fore; }
        if (fileRow != null) { fileRow.BackColor = back; fileRow.ForeColor = fore; }
        if (closeRow != null) { closeRow.BackColor = back; closeRow.ForeColor = fore; }
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
        radioZh.Left = radioAuto.Right + Sp(16);
        radioEn.Left = radioZh.Right + Sp(16);
        radioAuto.Checked = (langCode == "");
        radioZh.Checked = (langCode == "zh");
        radioEn.Checked = (langCode == "en");
        lblTheme.Text = Lang.T("settings.theme");
        radioThemeAuto.Text = Lang.T("settings.themeAuto");
        radioThemeLight.Text = Lang.T("settings.themeLight");
        radioThemeDark.Text = Lang.T("settings.themeDark");
        // dynamic equal spacing: recompute Left after text widths settle (AutoSize)
        radioThemeLight.Left = radioThemeAuto.Right + Sp(16);
        radioThemeDark.Left = radioThemeLight.Right + Sp(16);
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
                        // lnkDownload lives in its own grid column, so the table provides the
                        // spacing (previous absolute Left repositioning is superseded by the grid)
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
