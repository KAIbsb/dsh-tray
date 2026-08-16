using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

// Resolved runtime configuration snapshot: filled by InitConfig() from dshtray.ini and/or
// auto-detection. Plain mutable data object; owned statically by Config.
class AppConfig
{
    public string NodePath;
    public string DshEntry;
    public string DshWorkDir;
    public string ChromePath;
    public string WebUrl = "http://127.0.0.1:3080";
    public int Port = 3080;
    public string IniLang;
    public List<string> BrowserNames = new List<string>();
}

// Configuration: dshtray.ini is the single source of truth (parsed via IniFile). Auto-detection
// fills any key left empty/unset. Depends on Lang / Logging / IniFile (all leaves). Never depends
// on Program. The Windows startup registry key is only a mirror of the `autostart` ini key.
static class Config
{
    public static readonly AppConfig Current = new AppConfig();

    // in-memory mirror of dshtray.ini (set once in InitConfig after the ini is ensured). All
    // readers prefer it over re-reading the file each call; every writer refreshes it after
    // IniFile.Save so the live value and the file stay in sync for the process lifetime.
    static List<string> iniLines;

    public static string IniPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dshtray.ini"); }
    }

    public static void InitConfig()
    {
        EnsureIni();
        iniLines = IniFile.Load(IniPath);   // single read; everything below parses from this mirror
        LoadIniConfig();
        SyncAutostartFromIni();
        Lang.Init(Current.IniLang);
        Logging.Log("UI language: " + Lang.Code);
        if (string.IsNullOrEmpty(Current.NodePath) || !File.Exists(Current.NodePath)) Current.NodePath = DetectNode();
        if (string.IsNullOrEmpty(Current.DshEntry) || !File.Exists(Current.DshEntry)) Current.DshEntry = DetectDshEntry();
        if (string.IsNullOrEmpty(Current.DshWorkDir) && !string.IsNullOrEmpty(Current.DshEntry))
            Current.DshWorkDir = Path.GetDirectoryName(Path.GetDirectoryName(Current.DshEntry));
        if (string.IsNullOrEmpty(Current.ChromePath) || !File.Exists(Current.ChromePath)) Current.ChromePath = DetectChrome();
        InitBrowserNames();
        Logging.Log("Config: node=" + (Current.NodePath ?? "NOT FOUND") +
            " | dshEntry=" + (Current.DshEntry ?? "NOT FOUND") +
            " | chrome=" + (Current.ChromePath ?? "NOT FOUND") +
            " | url=" + Current.WebUrl);
    }

    // Minimal config for the elevated-kill helper. Unlike InitConfig, this must NOT create the ini
    // or write the autostart registry mirror: the helper runs with an elevated token and should only
    // resolve the dsh entry it needs for token comparison.
    public static void InitElevatedKillConfig()
    {
        iniLines = IniFile.Load(IniPath);
        LoadIniConfig();
        if (string.IsNullOrEmpty(Current.DshEntry) || !File.Exists(Current.DshEntry))
            Current.DshEntry = DetectDshEntry();
        Logging.Log("ElevatedKill config: dshEntry=" + (Current.DshEntry ?? "NOT FOUND"));
    }

    // create the ini from the embedded template when missing; failure is logged and swallowed
    public static void EnsureIni()
    {
        try
        {
            if (File.Exists(IniPath)) return;
            string template = null;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dshtray.ini.example"))
                if (s != null) using (var r = new StreamReader(s)) template = r.ReadToEnd();
            File.WriteAllText(IniPath, template ?? "", Encoding.UTF8);
            Logging.Log("Config: created " + IniPath);
        }
        catch (Exception ex) { Logging.Log("EnsureIni failed: " + ex.Message); }
    }

    // process names whose windows we refresh on restart: the configured browser + chrome/msedge fallbacks
    static void InitBrowserNames()
    {
        Current.BrowserNames.Clear();
        if (!string.IsNullOrEmpty(Current.ChromePath))
        {
            string n = Path.GetFileNameWithoutExtension(Current.ChromePath);
            if (!string.IsNullOrEmpty(n)) Current.BrowserNames.Add(n.ToLowerInvariant());
        }
        if (!Current.BrowserNames.Contains("chrome")) Current.BrowserNames.Add("chrome");
        if (!Current.BrowserNames.Contains("msedge")) Current.BrowserNames.Add("msedge");
    }

    // dshtray.ini is the single config source. url is the only explicit port setting (the port
    // is derived from it, default 3080). Unset keys fall through to auto-detection.
    static void LoadIniConfig()
    {
        try
        {
            var lines = iniLines;

            string url = IniFile.Get(lines, "url");
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    var uri = new Uri(url);
                    Current.WebUrl = url;
                    Current.Port = uri.Port;
                    // a URL without an explicit port silently uses the scheme default (80/443),
                    // which almost never matches the harness port; warn so the user can fix it
                    if (uri.IsDefaultPort)
                        Logging.Log("ini url has no explicit port, using scheme default " + uri.Port +
                            "; if the harness uses 3080, add :3080 to the url");
                }
                catch (Exception ex)
                {
                    // roll back to the default so WebUrl and Port stay consistent
                    Current.WebUrl = "http://127.0.0.1:3080";
                    Logging.Log("ini url parse failed, using default: " + ex.Message);
                }
            }

            Current.IniLang = IniFile.Get(lines, "lang");

            Current.NodePath = IniFile.Get(lines, "node");
            Current.DshEntry = IniFile.Get(lines, "dshentry");
            Current.DshWorkDir = IniFile.Get(lines, "dshworkdir");
            Current.ChromePath = IniFile.Get(lines, "chrome");
        }
        catch (Exception ex) { Logging.Log("LoadIniConfig failed: " + ex.Message); }
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
        catch (Exception ex) { Logging.Log("FindOnPath failed: " + ex.Message); }
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
        // wait timeout for the `npm root -g` discovery subprocess (config-layer detection,
        // distinct from the process-kill wait kept in Program)
        const int NpmRootWaitMs = 3000;
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
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                // npm has no fixed System32 location; it must be resolved on PATH, so keep the
                // bare name and let cmd.exe locate it
                Arguments = "/c npm root -g",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                // async read first to avoid a full-pipe deadlock; then wait (or kill on timeout)
                var readOut = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(NpmRootWaitMs))
                {
                    try { p.Kill(); } catch { }
                    Logging.Log("DetectDshEntry npm root -g timed out, killed");
                }
                string root = readOut.Result.Trim();
                if (root.Length > 0 && Directory.Exists(root))
                {
                    string entry3 = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(entry3)) return entry3;
                }
            }
        }
        catch (Exception ex) { Logging.Log("DetectDshEntry npm root -g failed: " + ex.Message); }
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

    // Parse a "true"/"false" string; null/empty/other -> null (caller decides fallback).
    static bool? ParseBool(string s)
    {
        if (s == null) return null;
        string t = s.Trim().ToLowerInvariant();
        if (t == "true" || t == "1" || t == "yes" || t == "on") return true;
        if (t == "false" || t == "0" || t == "no" || t == "off") return false;
        return null;
    }

    // ---- auto-restart: ini is authoritative; registry is only a legacy migration source ----

    // the cached ini mirror when present, else a fresh file read (covers callers that run before
    // InitConfig, e.g. headless probes). Writers call SyncIniLines after Save to keep the same set.
    static List<string> IniLines()
    {
        if (iniLines == null) iniLines = IniFile.Load(IniPath);
        return iniLines;
    }

    // re-read the file into the mirror so subsequent reads see what was just written
    static void SyncIniLines()
    {
        iniLines = IniFile.Load(IniPath);
    }

    public static bool LoadAutoRestart()
    {
        try
        {
            var lines = IniLines();
            string v = IniFile.Get(lines, "autorestart");
            if (v != null)
            {
                bool? b = ParseBool(v);
                if (b != null) return b.Value;
            }
            // no (valid) ini value: migrate from the legacy registry key once
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\dsh-tray", false))
            {
                object rv = k != null ? k.GetValue("AutoRestart") : null;
                if (rv != null)
                {
                    bool val = Convert.ToInt32(rv) == 1;
                    IniFile.Set(lines, "autorestart", val ? "true" : "false");
                    IniFile.Save(IniPath, lines);
                    SyncIniLines();
                    return val;
                }
            }
            return true; // template default
        }
        catch (Exception ex) { Logging.Log("LoadAutoRestart failed: " + ex.Message); return true; }
    }

    public static void SaveAutoRestart(bool enabled)
    {
        try
        {
            var lines = IniLines();
            IniFile.Set(lines, "autorestart", enabled ? "true" : "false");
            IniFile.Save(IniPath, lines);
            SyncIniLines();
        }
        catch (Exception ex) { Logging.Log("save autoRestart failed: " + ex.Message); }
    }

    // ---- autostart: ini is authoritative; the Windows Run key is only a mirror ----

    public static bool IsAutostartEnabled()
    {
        try
        {
            var lines = IniLines();
            string v = IniFile.Get(lines, "autostart");
            if (v != null)
            {
                bool? b = ParseBool(v);
                if (b != null) return b.Value;
            }
            // fallback: legacy registry Run key
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                return k != null && k.GetValue("dsh-tray") != null;
            }
        }
        catch { return false; }
    }

    public static void SetAutostart(bool want)
    {
        try
        {
            var lines = IniLines();
            IniFile.Set(lines, "autostart", want ? "true" : "false");
            IniFile.Save(IniPath, lines);
            SyncIniLines();
            WriteRunKey(want);
            Logging.Log("autostart = " + want);
        }
        catch (Exception ex) { Logging.Log("set autostart failed: " + ex.Message); }
    }

    public static void ToggleAutostart()
    {
        SetAutostart(!IsAutostartEnabled());
    }

    // ensure the Windows Run key mirrors the ini `autostart` value at startup
    public static void SyncAutostartFromIni()
    {
        try
        {
            var lines = IniLines();
            string v = IniFile.Get(lines, "autostart");
            if (v == null) return;
            bool? b = ParseBool(v);
            if (b == null) return;
            WriteRunKey(b.Value);
        }
        catch (Exception ex) { Logging.Log("SyncAutostartFromIni failed: " + ex.Message); }
    }

    static void WriteRunKey(bool want)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k == null) return;
                if (want) k.SetValue("dsh-tray", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("dsh-tray", false);
            }
        }
        catch (Exception ex) { Logging.Log("write run key failed: " + ex.Message); }
    }

    // manual theme override from the ini `theme` key: "dark" / "light"; anything else (empty,
    // unknown) means follow the system registry. Null when the key is absent.
    public static string ThemeOverride
    {
        get { return IniFile.Get(IniLines(), "theme"); }
    }

    // persist the `theme` ini key ("" / "light" / "dark"); follows the standard write-then-resync
    // pattern so the in-memory iniLines mirror stays consistent with the file.
    public static void SetTheme(string theme)
    {
        try
        {
            var lines = IniLines();
            IniFile.Set(lines, "theme", theme ?? "");
            IniFile.Save(IniPath, lines);
            SyncIniLines();
            Logging.Log("theme override = " + (string.IsNullOrEmpty(theme) ? "(follow system)" : theme));
        }
        catch (Exception ex) { Logging.Log("set theme failed: " + ex.Message); }
    }

    public static bool IsDarkMode()
    {
        // manual override wins; else fall back to the system Registry theme
        string overrideTheme = ThemeOverride;
        if (!string.IsNullOrEmpty(overrideTheme))
        {
            string t = overrideTheme.Trim().ToLowerInvariant();
            if (t == "dark") return true;
            if (t == "light") return false;
        }
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

    // write (or clear) the "lang" key in dshtray.ini. Empty lang clears the override so
    // language falls back to the system default on next launch. Failure is logged and swallowed.
    public static void SaveLang(string lang)
    {
        try
        {
            EnsureIni();
            if (iniLines == null) iniLines = IniFile.Load(IniPath);
            var lines = iniLines;
            IniFile.Set(lines, "lang", lang);
            IniFile.Save(IniPath, lines);
            SyncIniLines();
            // keep the runtime snapshot in sync so re-opening the settings dialog reflects the
            // current ini value instead of the process-start value
            Current.IniLang = lang;
        }
        catch (Exception ex) { Logging.Log("SaveLang failed: " + ex.Message); }
    }
}
