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

// Configuration: ini parsing, path detection, registry persistence. Leaf layer (depends on
// Lang and Logging only, both leaves). Never depends on Program.
static class Config
{
    public static readonly AppConfig Current = new AppConfig();

    public static void InitConfig()
    {
        LoadIniConfig();
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

    // optional dshtray.ini next to the exe; keys: node, dshentry, dshworkdir, chrome, url, lang.
    // url is the only explicit port setting: the port is derived from it (default 3080). Any
    // legacy `port=` line is ignored by the switch below (no compat needed).
    static void LoadIniConfig()
    {
        try
        {
            string ini = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dshtray.ini");
            if (!File.Exists(ini)) return;
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "node": Current.NodePath = val; break;
                    case "dshentry": Current.DshEntry = val; break;
                    case "dshworkdir": Current.DshWorkDir = val; break;
                    case "chrome": Current.ChromePath = val; break;
                    case "url":
                        Current.WebUrl = val;
                        try { Current.Port = new Uri(val).Port; }
                        catch (Exception ex)
                        {
                            // roll back to the default so WebUrl and Port stay consistent
                            Current.WebUrl = "http://127.0.0.1:3080";
                            Logging.Log("ini url parse failed, using default: " + ex.Message);
                        }
                        break;
                    case "lang":
                        Current.IniLang = val;
                        break;
                }
            }
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

    public static bool IsAutostartEnabled()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                if (k == null) return false;
                return k.GetValue("dsh-tray") != null;
            }
        }
        catch { return false; }
    }

    public static void ToggleAutostart()
    {
        bool want = !IsAutostartEnabled();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k == null) return;
                if (want) k.SetValue("dsh-tray", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("dsh-tray", false);
            }
            Logging.Log("autostart = " + want);
        }
        catch (Exception ex) { Logging.Log("autostart toggle failed: " + ex.Message); }
    }

    public static bool LoadAutoRestart()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\dsh-tray", false))
            {
                if (k == null)
                {
                    // one-time migration from the old key name
                    using (var old = Registry.CurrentUser.OpenSubKey(@"Software\DSHTray", false))
                    {
                        if (old == null) return false;
                        object ov = old.GetValue("AutoRestart");
                        bool val = ov != null && Convert.ToInt32(ov) == 1;
                        if (val) SaveAutoRestart(val); // copy into the new key
                        return val;
                    }
                }
                object v = k.GetValue("AutoRestart");
                return v != null && Convert.ToInt32(v) == 1;
            }
        }
        catch { return false; }
    }

    public static void SaveAutoRestart(bool enabled)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\dsh-tray"))
            {
                k.SetValue("AutoRestart", enabled ? 1 : 0);
            }
        }
        catch (Exception ex) { Logging.Log("save autoRestart failed: " + ex.Message); }
    }

    public static bool IsDarkMode()
    {
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
            string ini = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dshtray.ini");
            var lines = new List<string>();
            if (File.Exists(ini))
            {
                lines.AddRange(File.ReadAllLines(ini));
            }
            else
            {
                // no ini yet: seed it from the embedded commented template first, so a bare
                // lang-only file is never produced and the template is always visible
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dshtray.ini.example"))
                    if (s != null) using (var r = new StreamReader(s))
                        lines.AddRange(r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            }
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                string key = trimmed.Substring(0, eq).Trim().ToLowerInvariant();
                if (key == "lang")
                {
                    lines[i] = "lang=" + lang;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) lines.Add("lang=" + lang);
            File.WriteAllLines(ini, lines.ToArray(), Encoding.UTF8);
        }
        catch (Exception ex) { Logging.Log("SaveLang failed: " + ex.Message); }
    }
}
