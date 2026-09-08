using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Chrome/Edge window helpers: open the app window, enumerate/reload it. Leaf-ish layer:
// depends only on Config / Win32 / Logging (never on Program, TrayMenu, or DshProcess).
static class WindowMgr
{
    public static void OpenWindow()
    {
        // URL resolution probes the harness over HTTP and may poll for a fresh token line for a
        // couple of seconds: run it on a worker thread so the tray menu never freezes. The
        // browser launch, Logging and the balloon events are all safe off the UI thread.
        Task.Run((Action)(() =>
        {
            bool authPending;
            string url = ResolveWebUrl(out authPending);
            try
            {
                if (Config.Current.ChromePath != null && File.Exists(Config.Current.ChromePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Config.Current.ChromePath,
                        Arguments = "--app=" + url,
                        UseShellExecute = false
                    });
                }
                else
                {
                    // no Chrome/Edge found: open in the default browser
                    Process.Start(url);
                    Logging.Log("OpenWindow: no chrome/edge found, opened in default browser");
                }
                Logging.Log("OpenWindow: url=" + url);
                if (authPending) UiFeedback.Info(Lang.T("feedback.webAuthPending"));
            }
            catch (Exception ex) { Logging.Log("OpenWindow failed: " + ex.Message); UiFeedback.Fail(Lang.T("feedback.openWindowFailed")); }
        }));
    }

    // ---- web URL resolution ----------------------------------------------------------

    // Resolve the URL OpenWindow should launch. dsh >= 0.1.2 authenticates the web UI with a
    // per-process launch token: it prints "dsh web: <url>?token=..." to its stdout (harness.log)
    // and serves 401 to anything else until a browser exchanges the token once for a signed
    // 30-day cookie (every tokenized open re-issues the cookie, so active users never see a
    // login page). Older versions print the bare URL and need no auth. One parse rule covers
    // both: the child appends to harness.log, and a second instance dies on the port conflict
    // before it could print its own line, so the LAST "dsh web:" line in the file belongs to
    // the harness that most recently came up — no per-session bookkeeping needed.
    //
    // The candidate is probed before use (GET, no redirect): 303 = live token on a new dsh
    // (open the token URL), 200 = no auth needed (old dsh). 401 means the line belongs to a
    // dead harness (the running one was started elsewhere without the redirect): poll briefly
    // for a fresher line — a just-started harness may not have flushed its line yet — then
    // fall back to the configured bare URL, which reproduces the pre-auth behavior. Falling
    // back on a 401 sets authPending: the browser will show the 401 page, and the caller
    // should hint at the one-click fix (tray Restart re-prints a fresh token line).
    public static string ResolveWebUrl(out bool authPending)
    {
        string fallback = Config.Current.WebUrl;
        authPending = false;
        try
        {
            string log = Path.Combine(Path.GetDirectoryName(Logging.LogPath), "harness.log");
            if (!File.Exists(log)) return fallback;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(2500);
            int lastStatus = -1;
            while (true)
            {
                string candidate = LastPrintedWebUrl(log);
                if (candidate != null)
                {
                    lastStatus = ProbeStatus(candidate);
                    if (lastStatus == 303 || lastStatus == 200) return candidate;
                    // transport error (harness down/booting): no point polling for a line
                    if (lastStatus != 401) break;
                }
                else
                {
                    // no usable line right now (rotated away / not yet flushed): a fresh start
                    // is imminent or in flight, so drop any stale probe verdict
                    lastStatus = -1;
                }
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(300);
            }
            if (lastStatus == 401) authPending = true;
        }
        catch (Exception ex) { Logging.Log("ResolveWebUrl failed: " + ex.Message); }
        return fallback;
    }

    // the URL from the last usable "dsh web:" line in harness.log, or null. The banner can carry
    // a trailing " (LAN: ...)" suffix and — on versions where browser auto-open is active — be
    // followed by a second non-URL "dsh web:" hint line, so take the last line that actually
    // starts with a URL, truncated at the first whitespace. The WHOLE file is scanned (bounded
    // by the 5MB rotation): the banner prints only at harness start, so a long-running session
    // pushes it arbitrarily far from the end of the file. Opens the file with
    // FileShare.ReadWrite because the harness child keeps it open for appending.
    static string LastPrintedWebUrl(string log)
    {
        using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            string line, found = null;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("dsh web:", StringComparison.OrdinalIgnoreCase)) continue;
                string rest = line.Substring("dsh web:".Length).Trim();
                if (!rest.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                int sp = rest.IndexOf(' ');
                found = sp > 0 ? rest.Substring(0, sp) : rest;
            }
            return found;
        }
    }

    // HTTP GET status without following redirects or reading the body (the token exchange is a
    // GET-only 303 that sets the session cookie). Returns -1 on transport failure.
    static int ProbeStatus(string url)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = false;
            req.Proxy = null;
            req.Timeout = 1500;
            req.ReadWriteTimeout = 1500;
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse()) return (int)res.StatusCode;
            }
            catch (WebException ex)
            {
                var res = ex.Response as HttpWebResponse;
                return res != null ? (int)res.StatusCode : -1;
            }
        }
        catch (Exception ex) { Logging.Log("ProbeStatus failed: " + ex.Message); return -1; }
    }

    // enumerate top-level windows owned by a configured browser (Chrome/Edge/etc.), returning
    // hwnd+title pairs. Shared by ReloadAppWindow (title match) and FindWindows (report), so the
    // EnumWindows+visibility+pid+browser-filter boilerplate lives in exactly one place.
    static List<KeyValuePair<IntPtr, string>> EnumerateAppWindows()
    {
        var windows = new List<KeyValuePair<IntPtr, string>>();
        try
        {
            Win32.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Win32.IsWindowVisible(hWnd)) return true;
                uint pid;
                Win32.GetWindowThreadProcessId(hWnd, out pid);
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    if (Config.Current.BrowserNames.Contains(p.ProcessName.ToLowerInvariant()))
                    {
                        var sb = new StringBuilder(256);
                        Win32.GetWindowText(hWnd, sb, 256);
                        windows.Add(new KeyValuePair<IntPtr, string>(hWnd, sb.ToString()));
                    }
                }
                catch (Exception ex) { Logging.Log("EnumerateAppWindows GetProcessById failed: " + ex.Message); }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Logging.Log("EnumerateAppWindows failed: " + ex.Message); }
        return windows;
    }

    // A browser tab appends " - Google Chrome" / " - Microsoft Edge" to its title; the dedicated
    // app-mode harness window does not. Matching on the marker alone is too loose and would also
    // reload unrelated tabs whose title happens to mention "deepseek harness".
    static bool LooksLikeHarnessWindow(string title)
    {
        if (title == null ||
            title.IndexOf("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return !title.EndsWith(" - Google Chrome", StringComparison.OrdinalIgnoreCase) &&
               !title.EndsWith(" - Microsoft Edge", StringComparison.OrdinalIgnoreCase) &&
               !title.EndsWith(" - Chromium", StringComparison.OrdinalIgnoreCase);
    }

    // find Chrome top-level windows whose title matches the DSH webui and send Ctrl+R
    public static void ReloadAppWindow()
    {
        try
        {
            var targets = new List<IntPtr>();
            foreach (var w in EnumerateAppWindows())
            {
                if (LooksLikeHarnessWindow(w.Value))
                    targets.Add(w.Key);
            }

            if (targets.Count == 0) { Logging.Log("ReloadAppWindow: no matching window"); return; }

            // dummy ALT press unlocks Windows foreground-switch restrictions
            Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);

            int sent = 0;
            foreach (IntPtr h in targets)
            {
                Win32.SetForegroundWindow(h);
                Thread.Sleep(80);
                if (Win32.GetForegroundWindow() != h)
                {
                    Logging.Log("ReloadAppWindow: cannot focus window, skip");
                    continue;
                }
                Win32.keybd_event(Win32.VK_CONTROL, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_R, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_R, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_CONTROL, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                sent++;
                Thread.Sleep(150);
            }
            Logging.Log("ReloadAppWindow: reloaded " + sent + "/" + targets.Count + " window(s)");
        }
        catch (Exception ex) { Logging.Log("ReloadAppWindow failed: " + ex.Message); }
    }

    // headless: list Chrome top-level windows (read-only), returned as newline-joined text.
    // The output format (hwnd + pid + title) is unchanged; the pid is re-derived from each hwnd
    // only at report time so the shared enumerator can stay hwnd+title pairs.
    public static string FindWindows()
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var w in EnumerateAppWindows())
            {
                uint pid;
                IntPtr hwnd = w.Key;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                sb.AppendLine("hwnd=" + hwnd + " pid=" + pid + " title=[" + w.Value + "]");
            }
        }
        catch (Exception ex) { Logging.Log("FindWindows failed: " + ex.Message); }
        return sb.ToString();
    }
}
