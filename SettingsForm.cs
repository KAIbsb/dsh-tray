using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// Settings dialog: language hot-switch, auto-restart / autostart toggles, update check,
// about info (version / repo / log dir), and open-config/open-logs entries. Created by TrayMenu;
// depends on DshProcess / Config / Lang / UpdateCheck (fields injected via constructor).
//
// Layout architecture: every row is declared once in `Rows` (kind, height, control factories).
// BuildControls consumes that table, so row numbers, row heights and the form height are derived
// instead of maintained as parallel magic numbers. DPI scaling is centralized in Ui (manual only;
// AutoScaleMode.None) so --ui-preview and real high-DPI desktops follow the same scaling path.
class SettingsForm : Form
{
    readonly DshProcess dp;
    readonly string appVersion;

    Label lblSecGeneral;
    Panel lineGeneral;
    Label lblSecAbout;
    Panel lineAbout;
    Label lblLanguage;
    FlowLayoutPanel langPanel;
    RadioButton radioAuto;
    RadioButton radioZh;
    RadioButton radioEn;
    Label lblTheme;
    FlowLayoutPanel themePanel;
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
    bool applyingTheme;
    // last update-check result (null = no result yet, true = newer available, false = up to date)
    bool? checkResult;
    bool checkingUpdate;
    bool autoUpdating;
    // language updaters registered once in CreateControls; ApplyLang just runs them all
    readonly List<Action> langUpdaters = new List<Action>();
    // raised when the user changes the theme override; TrayMenu subscribes so the tray icon and
    // process-wide uxtheme are refreshed without SettingsForm depending on TrayMenu
    public event Action ThemeChanged;
    readonly bool? themeOverride;
    readonly float? dpiOverride;
    Icon ownedIcon;
    float dpi;

    TableLayoutPanel root;
    TableLayoutPanel updateRow;
    TableLayoutPanel fileRow;
    TableLayoutPanel closeRow;

    // ---- row table: single source for row order, kind and height ----
    enum RowKind { Section, Separator, Labeled, Span }

    sealed class RowDef
    {
        public RowKind Kind;
        public int BaseH;
        public Func<SettingsForm, Control> Build;
        public Func<SettingsForm, Label> BuildLabel; // only for Kind == Labeled

        public RowDef(RowKind kind, int baseH, Func<SettingsForm, Control> build, Func<SettingsForm, Label> buildLabel = null)
        {
            Kind = kind;
            BaseH = baseH;
            Build = build;
            BuildLabel = buildLabel;
        }
    }

    static readonly RowDef[] Rows = BuildRows();

    static RowDef[] BuildRows()
    {
        return new RowDef[]
        {
            new RowDef(RowKind.Section, 28, f => f.lblSecGeneral),
            new RowDef(RowKind.Separator, 16, f => f.lineGeneral),
            new RowDef(RowKind.Labeled, 28, f => f.langPanel, f => f.lblLanguage),
            new RowDef(RowKind.Labeled, 28, f => f.themePanel, f => f.lblTheme),
            new RowDef(RowKind.Span, 28, f => f.chkAutoRestart),
            new RowDef(RowKind.Span, 28, f => f.chkAutostart),
            new RowDef(RowKind.Section, 28, f => f.lblSecAbout),
            new RowDef(RowKind.Separator, 16, f => f.lineAbout),
            new RowDef(RowKind.Span, 34, f => f.lblVersion),
            new RowDef(RowKind.Span, 34, f => f.lblCurrentUrl),
            new RowDef(RowKind.Span, 34, f => f.lnkRepo),
            new RowDef(RowKind.Span, 44, f => f.updateRow),
            new RowDef(RowKind.Span, 36, f => f.fileRow),
            new RowDef(RowKind.Span, 40, f => f.closeRow),
        };
    }

    // headless smoke check: the layout table is the single source for rows; if it is ever
    // malformed (missing row / non-positive height) this catches it before the dialog opens.
    public static string ValidateRows()
    {
        if (Rows == null || Rows.Length == 0) return "SettingsForm rows missing";
        for (int i = 0; i < Rows.Length; i++)
        {
            if (Rows[i] == null) return "SettingsForm row null at index " + i;
            if (Rows[i].BaseH <= 0) return "SettingsForm row height <=0 at index " + i;
            if (Rows[i].Kind == RowKind.Labeled && Rows[i].BuildLabel == null)
                return "SettingsForm Labeled row missing label factory at index " + i;
            if (Rows[i].Build == null) return "SettingsForm row missing control factory at index " + i;
        }
        return "SettingsForm rows OK (" + Rows.Length + ")";
    }

    public SettingsForm(DshProcess process, string version, bool? themeOverride = null, float? dpiOverride = null)
    {
        dp = process;
        appVersion = version;
        this.themeOverride = themeOverride;
        this.dpiOverride = dpiOverride;
        langCode = Config.Current.IniLang ?? "";
        dpi = dpiOverride ?? ((float)DeviceDpi / 96f);
        if (dpiOverride.HasValue) Ui.SetScale(dpiOverride.Value); else Ui.SetScale(dpi);

        Text = "dsh-tray";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        // All layout pixels are scaled manually via Ui. AutoScaleMode.None avoids double-scaling
        // explicit control sizes on real high-DPI desktops; fonts still scale naturally with DPI.
        AutoScaleMode = AutoScaleMode.None;
        // in the simulated-DPI preview the device is still 100%, so grow the font ourselves to
        // faithfully simulate a scaled desktop (real desktops scale the font naturally).
        if (dpiOverride.HasValue)
        {
            Font = new Font(Font.FontFamily, Font.Size * dpiOverride.Value, Font.Style);
        }
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        try { ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (ownedIcon != null) Icon = ownedIcon;

        // PerMonitorV2: when the dialog is moved to a monitor with a different DPI, rebuild the
        // hand-scaled layout so row/button sizes match the new scale (AutoScaleMode.None does not
        // re-run automatically). Preview mode keeps its fixed dpiOverride and never moves monitors.
        DpiChanged += OnDpiChanged;

        BuildControls();
        ApplyTheme();
        ApplyLang();
    }

    void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (dpiOverride.HasValue) return;
        dpi = (float)e.DeviceDpiNew / 96f;
        Ui.SetScale(dpi);
        RebuildLayout();
    }

    void RebuildLayout()
    {
        if (root != null)
        {
            Controls.Remove(root);
            DisposeControlTree(root);
            root = null;
        }
        BuildControls();
        ApplyTheme();
        ApplyLang();
    }

    static void DisposeControlTree(Control c)
    {
        if (c == null) return;
        while (c.Controls.Count > 0)
        {
            Control child = c.Controls[0];
            c.Controls.Remove(child);
            DisposeControlTree(child);
        }
        c.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
        base.Dispose(disposing);
    }

    void BuildControls()
    {
        // ---- main vertical stack: label column + content column, one row per RowDef ----
        root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Ui.Gap(16)),
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(104))); // label column
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));        // content column

        CreateControls();

        int row = 0;
        foreach (var rd in Rows)
        {
            Control c = rd.Build(this);
            switch (rd.Kind)
            {
                case RowKind.Section:   AddSpan(root, c, row); break;
                case RowKind.Separator: AddSeparatorRow(root, c, row); break;
                case RowKind.Labeled:   AddRow(root, rd.BuildLabel(this), c, row); break;
                default:                AddSpan(root, c, row); break;
            }
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,
                rd.BaseH > 0 ? Ui.RowH(rd.BaseH) : c.PreferredSize.Height));
            row++;
        }

        Controls.Add(root);
        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    void CreateControls()
    {
        // ---- section 1: general (bold heading + separator line) ----
        lblSecGeneral = new Label { AutoSize = true };
        lblSecGeneral.Font = new Font(lblSecGeneral.Font.FontFamily, 10f, FontStyle.Bold);
        lineGeneral = new Panel { Height = Ui.Line(), Dock = DockStyle.Fill };

        lblLanguage = new Label { AutoSize = false, Width = Ui.Px(90), Height = Ui.Px(22), TextAlign = ContentAlignment.MiddleLeft };
        // each radio group gets its OWN parent container: WinForms RadioButtons group by parent,
        // so language and theme radios must NOT share the Form directly or they form ONE
        // mutual-exclusion group. FlowLayoutPanel arranges the radios automatically (no manual
        // Left recomputation in ApplyLang).
        langPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = Ui.Px(28),
            Margin = new Padding(0),
            BorderStyle = BorderStyle.None,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        radioAuto = new RadioButton { AutoSize = true, Margin = new Padding(0, Ui.Gap(2), Ui.Gap(12), 0) };
        radioAuto.CheckedChanged += delegate { OnLangChanged(radioAuto); };
        radioZh = new RadioButton { AutoSize = true, Margin = new Padding(0, Ui.Gap(2), Ui.Gap(12), 0) };
        radioZh.CheckedChanged += delegate { OnLangChanged(radioZh); };
        radioEn = new RadioButton { AutoSize = true, Margin = new Padding(0, Ui.Gap(2), 0, 0) };
        radioEn.CheckedChanged += delegate { OnLangChanged(radioEn); };
        langPanel.Controls.Add(radioAuto);
        langPanel.Controls.Add(radioZh);
        langPanel.Controls.Add(radioEn);

        lblTheme = new Label { AutoSize = false, Width = Ui.Px(90), Height = Ui.Px(22), TextAlign = ContentAlignment.MiddleLeft };
        themePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = Ui.Px(28),
            Margin = new Padding(0),
            BorderStyle = BorderStyle.None,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        radioThemeAuto = new RadioButton { AutoSize = true, Margin = new Padding(0, Ui.Gap(2), Ui.Gap(12), 0) };
        radioThemeAuto.CheckedChanged += delegate { OnThemeChanged(radioThemeAuto); };
        radioThemeLight = new RadioButton { AutoSize = true, Margin = new Padding(0, Ui.Gap(2), Ui.Gap(12), 0) };
        radioThemeLight.CheckedChanged += delegate { OnThemeChanged(radioThemeLight); };
        radioThemeDark = new RadioButton { AutoSize = true, Margin = new Padding(0, Ui.Gap(2), 0, 0) };
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
        lblSecAbout.Font = new Font(lblSecAbout.Font.FontFamily, 10f, FontStyle.Bold);
        lineAbout = new Panel { Height = Ui.Line(), Dock = DockStyle.Fill };
        lblVersion = new Label { AutoSize = false, Width = Ui.Px(500), Height = Ui.Px(25), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        lblCurrentUrl = new Label { AutoSize = false, Width = Ui.Px(500), Height = Ui.Px(26), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        lnkRepo = new LinkLabel { AutoSize = false, Width = Ui.Px(500), Height = Ui.Px(25), AutoEllipsis = true };
        lnkRepo.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };

        // check-update row: check / result / download-link / auto-update all on ONE grid row so
        // they never collide when "new version found" reveals the last two. The nested panel stays
        // AutoSize (so it doesn't inflate its Absolute parent row); its internal row is a FIXED
        // scaled height so btnCheck (Dock=Fill) stretches tall enough that "Check for updates" text
        // never clips at high DPI.
        updateRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoSize = false
        };
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Px(190))); // btnCheck (wide enough for en "Check for updates")
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // lblResult
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // lnkDownload
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // btnAutoUpdate
        btnCheck = new Button { Dock = DockStyle.Fill, Margin = new Padding(0, Ui.Gap(4), 0, Ui.Gap(4)) };
        btnCheck.Click += delegate { OnCheckUpdate(); };
        lblResult = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        lnkDownload = new LinkLabel { AutoSize = true, Visible = false, Margin = new Padding(0) };
        lnkDownload.LinkClicked += delegate { OpenUrl(UpdateCheck.ReleasesPageUrl); };
        btnAutoUpdate = new Button { AutoSize = true, Height = Ui.Px(32), Margin = new Padding(0), Visible = false };
        btnAutoUpdate.Click += delegate { OnAutoUpdate(); };
        updateRow.Controls.Add(btnCheck, 0, 0);
        updateRow.Controls.Add(lblResult, 1, 0);
        updateRow.Controls.Add(lnkDownload, 2, 0);
        updateRow.Controls.Add(btnAutoUpdate, 3, 0);
        // row fills the root cell; Dock=Fill children get the full height (>= textH at every DPI)
        updateRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // config / logs buttons on one row
        fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = false };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnOpenConfig = new Button { AutoSize = false, Dock = DockStyle.Fill, Height = Ui.Px(32), Margin = new Padding(0) };
        btnOpenConfig.Click += delegate { OpenConfig(); };
        btnOpenLogs = new Button { AutoSize = false, Dock = DockStyle.Fill, Height = Ui.Px(32), Margin = new Padding(0) };
        btnOpenLogs.Click += delegate { OpenLogsFolder(); };
        fileRow.Controls.Add(btnOpenConfig, 0, 0);
        fileRow.Controls.Add(btnOpenLogs, 1, 0);
        fileRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // bottom row: right-aligned close button
        closeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = false };
        closeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btnClose = new Button { AutoSize = false, Width = Ui.Px(90), Height = Ui.Px(32), Anchor = AnchorStyles.Right };
        btnClose.Margin = new Padding(0, Ui.Gap(2), 0, 0);
        btnClose.Click += delegate { Close(); };
        closeRow.Controls.Add(btnClose, 0, 0);
        closeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        RegisterLangUpdaters();
    }

    // add a 2-column-wide (label+content) row
    void AddRow(TableLayoutPanel grid, Control label, Control content, int row)
    {
        label.Margin = new Padding(0, Ui.Gap(4), Ui.Gap(6), Ui.Gap(4));
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(content, 1, row);
    }

    // add a row that spans BOTH columns
    void AddSpan(TableLayoutPanel grid, Control c, int row, int colSpan = 2, int rowSpan = 1)
    {
        c.Margin = new Padding(0, Ui.Gap(2), 0, Ui.Gap(2));
        grid.Controls.Add(c, 0, row);
        grid.SetColumnSpan(c, colSpan);
        grid.SetRowSpan(c, rowSpan);
    }

    // separator row: 1px line set to fill its cell
    void AddSeparatorRow(TableLayoutPanel grid, Control sep, int row)
    {
        sep.Margin = new Padding(0, Ui.Gap(6), 0, Ui.Gap(6));
        grid.Controls.Add(sep, 0, row);
        grid.SetColumnSpan(sep, 2);
    }

    // Central light/dark palette so theme colors are reviewable in one place.
    sealed class ThemePalette
    {
        public Color Back, Fore, Line, BtnBack, BtnBorder, BtnHover, Link, Dim;
        public Color Primary, PrimaryFore, PrimaryHover;

        public static readonly ThemePalette Light = new ThemePalette
        {
            Back = SystemColors.Control,
            Fore = SystemColors.ControlText,
            Line = Color.FromArgb(0xC8, 0xC8, 0xC8),
            BtnBack = SystemColors.Control,
            BtnBorder = Color.FromArgb(0xB0, 0xB0, 0xB0),
            BtnHover = Color.FromArgb(0xE8, 0xF0, 0xFE),
            Link = Color.Blue,
            Dim = Color.FromArgb(0x66, 0x66, 0x66),
            Primary = Color.FromArgb(0x0F, 0x6C, 0xBD),
            PrimaryFore = Color.White,
            PrimaryHover = Color.FromArgb(0x17, 0x72, 0xC9)
        };

        public static readonly ThemePalette Dark = new ThemePalette
        {
            Back = Color.FromArgb(0x20, 0x20, 0x20),
            Fore = Color.FromArgb(0xF0, 0xF0, 0xF0),
            Line = Color.FromArgb(0x3F, 0x3F, 0x3F),
            BtnBack = Color.FromArgb(0x45, 0x45, 0x45),
            BtnBorder = Color.FromArgb(0x54, 0x54, 0x54),
            BtnHover = Color.FromArgb(0x56, 0x56, 0x56),
            Link = Color.FromArgb(0x8F, 0xC3, 0xFF),
            Dim = Color.FromArgb(0xAA, 0xAA, 0xAA),
            Primary = Color.FromArgb(0x0F, 0x6C, 0xBD),
            PrimaryFore = Color.White,
            PrimaryHover = Color.FromArgb(0x17, 0x72, 0xC9)
        };
    }

    // full light/dark adaptation across form + separators + every control
    // public: TrayMenu.ApplyThemeNow() calls it to re-theme the open dialog on a theme change
    public void ApplyTheme()
    {
        bool dark = themeOverride ?? Config.IsDarkMode();
        ThemePalette p = dark ? ThemePalette.Dark : ThemePalette.Light;
        Color back = p.Back, fore = p.Fore, line = p.Line;
        Color btnBack = p.BtnBack, btnBorder = p.BtnBorder, btnHover = p.BtnHover;
        Color link = p.Link, dim = p.Dim;

        BackColor = back;
        ForeColor = fore;

        lineGeneral.BackColor = line;
        lineAbout.BackColor = line;

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

        StyleRadio(radioAuto, fore, back);
        StyleRadio(radioZh, fore, back);
        StyleRadio(radioEn, fore, back);
        StyleRadio(radioThemeAuto, fore, back);
        StyleRadio(radioThemeLight, fore, back);
        StyleRadio(radioThemeDark, fore, back);
        StyleCheckBox(chkAutoRestart, fore, back);
        StyleCheckBox(chkAutostart, fore, back);

        StyleButton(btnCheck, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnAutoUpdate, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnOpenConfig, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnOpenLogs, btnBack, fore, btnBorder, btnHover);
        StyleButton(btnClose, p.Primary, p.PrimaryFore, p.Primary, p.PrimaryHover);

        lnkRepo.LinkColor = link;
        lnkRepo.LinkBehavior = LinkBehavior.HoverUnderline;
        lnkDownload.LinkColor = link;
        lnkDownload.LinkBehavior = LinkBehavior.HoverUnderline;
    }

    static void StyleRadio(RadioButton rb, Color fore, Color back)
    {
        rb.ForeColor = fore;
        rb.BackColor = back;
        rb.FlatStyle = FlatStyle.Flat;
        rb.UseVisualStyleBackColor = false;
    }

    static void StyleCheckBox(CheckBox cb, Color fore, Color back)
    {
        cb.ForeColor = fore;
        cb.BackColor = back;
        cb.FlatStyle = FlatStyle.Flat;
        cb.UseVisualStyleBackColor = false;
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

    // Register one language updater per text-bearing control. ApplyLang simply runs this list, so
    // adding a new text control only requires adding one updater here instead of editing ApplyLang.
    void RegisterLangUpdaters()
    {
        langUpdaters.Clear();
        langUpdaters.Add(delegate { Text = Lang.T("settings.title"); });
        langUpdaters.Add(delegate { lblSecGeneral.Text = Lang.T("settings.groupGeneral"); });
        langUpdaters.Add(delegate { lblSecAbout.Text = Lang.T("settings.groupAbout"); });
        langUpdaters.Add(delegate { lblLanguage.Text = Lang.T("settings.language"); });
        langUpdaters.Add(delegate { radioAuto.Text = Lang.T("settings.langAuto"); });
        langUpdaters.Add(delegate { radioZh.Text = Lang.T("settings.langZh"); });
        langUpdaters.Add(delegate { radioEn.Text = Lang.T("settings.langEn"); });
        langUpdaters.Add(delegate
        {
            radioAuto.Checked = (langCode == "");
            radioZh.Checked = (langCode == "zh");
            radioEn.Checked = (langCode == "en");
        });
        langUpdaters.Add(delegate { lblTheme.Text = Lang.T("settings.theme"); });
        langUpdaters.Add(delegate { radioThemeAuto.Text = Lang.T("settings.themeAuto"); });
        langUpdaters.Add(delegate { radioThemeLight.Text = Lang.T("settings.themeLight"); });
        langUpdaters.Add(delegate { radioThemeDark.Text = Lang.T("settings.themeDark"); });
        langUpdaters.Add(delegate
        {
            string theme = (Config.ThemeOverride ?? "").Trim().ToLowerInvariant();
            radioThemeAuto.Checked = (theme.Length == 0);
            radioThemeLight.Checked = (theme == "light");
            radioThemeDark.Checked = (theme == "dark");
        });
        langUpdaters.Add(delegate { chkAutoRestart.Text = Lang.T("settings.autoRestart"); });
        langUpdaters.Add(delegate { chkAutostart.Text = Lang.T("settings.autostart"); });
        langUpdaters.Add(delegate { btnCheck.Text = Lang.T("settings.checkUpdate"); });
        langUpdaters.Add(delegate { btnAutoUpdate.Text = Lang.T("settings.autoUpdate"); });
        langUpdaters.Add(delegate { lblVersion.Text = string.Format(Lang.T("settings.version"), appVersion); });
        langUpdaters.Add(delegate { lblCurrentUrl.Text = string.Format(Lang.T("settings.currentUrl"), Config.Current.WebUrl); });
        langUpdaters.Add(delegate { lnkRepo.Text = Lang.T("settings.repo"); });
        langUpdaters.Add(delegate { lnkDownload.Text = Lang.T("settings.download"); });
        langUpdaters.Add(delegate { btnOpenConfig.Text = Lang.T("settings.openConfig"); });
        langUpdaters.Add(delegate { btnOpenLogs.Text = Lang.T("settings.openLogs"); });
        langUpdaters.Add(delegate { btnClose.Text = Lang.T("settings.close"); });
    }

    void ApplyLang()
    {
        applyingLang = true;
        applyingTheme = true; // ApplyLang also initializes theme radios; keep both guards on
        foreach (var u in langUpdaters) u();
        RestoreDynamicUiState();
        applyingLang = false;
        applyingTheme = false;
    }

    // Restore update-check / auto-update dynamic UI state. Called after language switches and after
    // a DPI rebuild so a visible "new version / up to date / updating" state is not lost.
    void RestoreDynamicUiState()
    {
        if (checkingUpdate)
        {
            btnCheck.Enabled = false;
            lblResult.Text = Lang.T("settings.checking");
        }
        else
        {
            btnCheck.Enabled = true;
            if (checkResult == true)
            {
                lblResult.Text = string.Format(Lang.T("settings.updateAvailable"), UpdateCheck.LatestVersion);
                lnkDownload.Visible = true;
                btnAutoUpdate.Visible = true;
            }
            else if (checkResult == false)
            {
                lblResult.Text = Lang.T("settings.upToDate");
                lnkDownload.Visible = false;
                btnAutoUpdate.Visible = false;
            }
        }

        if (autoUpdating)
        {
            btnAutoUpdate.Enabled = false;
            btnAutoUpdate.Text = Lang.T("settings.updating");
        }
        else
        {
            btnAutoUpdate.Enabled = true;
        }
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
        if (applyingTheme) return;
        if (!rb.Checked) return;
        string val;
        if (rb == radioThemeAuto) val = "";
        else if (rb == radioThemeLight) val = "light";
        else val = "dark";
        Config.SetTheme(val);
        // apply immediately: tray sees the new effective theme (icon + uxtheme) and this dialog
        // re-themes itself; TrayMenu subscribes to ThemeChanged for the tray side
        if (ThemeChanged != null) ThemeChanged();
        ApplyTheme();
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
        autoUpdating = true;
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
                    autoUpdating = false;
                    btnAutoUpdate.Enabled = true;
                    btnAutoUpdate.Text = Lang.T("settings.autoUpdate");
                });
                BeginInvokeSafe(delegate { UiFeedback.Fail(Lang.T("settings.autoUpdateFailed")); });
                return;
            }
            string exePath = Application.ExecutablePath;
            string oldPath = exePath + ".old.tmp.exe";
            try
            {
                bool swapped = false;
                if (File.Exists(oldPath)) TryDeleteFile(oldPath);
                try { File.Move(exePath, oldPath); swapped = true; } catch { swapped = false; }
                if (swapped)
                {
                    bool moved = false;
                    try
                    {
                        File.Move(destPath, exePath);
                        moved = true;
                    }
                    catch (Exception ex)
                    {
                        // Move failed after the running exe was renamed aside: restore the old exe
                        // and keep the verified .new for manual deployment. Never delete destPath here.
                        Logging.Log("auto-update deploy move failed: " + ex.Message);
                        try { if (File.Exists(oldPath)) File.Move(oldPath, exePath); }
                        catch (Exception ex2) { Logging.Log("auto-update rollback failed: " + ex2.Message); }
                    }
                    if (moved)
                        BeginInvokeSafe(delegate { UiFeedback.Info(Lang.T("settings.updateReady")); });
                    else
                        BeginInvokeSafe(delegate { UiFeedback.Fail(Lang.T("settings.updateDeployFailed")); });
                }
                else
                {
                    Logging.Log("auto-update: running exe is locked, verified binary left at " + destPath);
                    BeginInvokeSafe(delegate { UiFeedback.Fail(Lang.T("settings.updateDeployFailed")); });
                }
            }
            catch (Exception ex)
            {
                Logging.Log("auto-update deploy failed: " + ex.Message);
                BeginInvokeSafe(delegate { UiFeedback.Fail(Lang.T("settings.autoUpdateFailed")); });
            }
            BeginInvokeSafe(delegate
            {
                autoUpdating = false;
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
        checkingUpdate = true;
        checkResult = null;
        lblResult.Text = Lang.T("settings.checking");
        Task.Run(() =>
        {
            bool newer = UpdateCheck.Check(appVersion);
            BeginInvokeSafe(delegate
            {
                btnCheck.Enabled = true;
                checkingUpdate = false;
                checkResult = newer;
                if (newer)
                {
                    lblResult.Text = string.Format(Lang.T("settings.updateAvailable"), UpdateCheck.LatestVersion);
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
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
        }
        catch (Exception ex) { Logging.Log("SettingsForm open logs failed: " + ex.Message); UiFeedback.Fail(Lang.T("feedback.openLogsFailed")); }
    }
}
