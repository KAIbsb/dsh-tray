using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Chrome/Edge window helpers: open the app window, enumerate/focus existing ones. Leaf-ish
// layer: depends only on Config / Win32 / Logging (never on Program, TrayMenu, or DshProcess).
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
                // openmode=browser skips app mode entirely (plain tab in the default browser)
                if (Config.Current.OpenMode != "browser" &&
                    Config.Current.ChromePath != null && File.Exists(Config.Current.ChromePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Config.Current.ChromePath,
                        // Chrome's default app-window size is awkward: fit 16:9 to the work area
                        Arguments = "--app=" + url + " " + WindowGeometryArgs(),
                        UseShellExecute = false
                    });
                }
                else
                {
                    // openmode=browser or no Chromium found: open in the default browser
                    Process.Start(url);
                    Logging.Log("OpenWindow: opened in default browser");
                }
                Logging.Log("OpenWindow: url=" + url);
                if (authPending) UiFeedback.Info(Lang.T("feedback.webAuthPending"));
            }
            catch (Exception ex) { Logging.Log("OpenWindow failed: " + ex.Message); UiFeedback.Fail(Lang.T("feedback.openWindowFailed")); }
        }));
    }

    // --window-size/--window-position for the app window: 90% of the primary work area height at
    // 16:9 (width-capped), centered. Physical pixels — the app manifest is DPI-aware. Returns an
    // empty string when the work-area probe fails, letting Chrome use its own default.
    static string WindowGeometryArgs()
    {
        var wa = new Win32.RECT();
        if (!Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref wa, 0)) return "";
        int ww = wa.Right - wa.Left, wh = wa.Bottom - wa.Top;
        if (ww <= 0 || wh <= 0) return "";
        int h = wh * 9 / 10, w = h * 16 / 9;
        int maxW = ww * 19 / 20;
        if (w > maxW) { w = maxW; h = w * 9 / 16; }
        return "--window-size=" + w + "," + h +
               " --window-position=" + (wa.Left + (ww - w) / 2) + "," + (wa.Top + (wh - h) / 2);
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
    // hwnd+title pairs. Shared by FocusHarnessWindow and FindWindows, so the EnumWindows+
    // visibility+pid+browser-filter boilerplate lives in exactly one place.
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

    // Focus an existing harness window instead of stacking a duplicate: matches app-mode windows
    // AND plain browser tabs showing the UI (both carry "DeepSeek Harness" in the title; a tab is
    // only detectable while it is the active tab). Uses the ALT-foreground dance Windows requires
    // before focus stealing. Returns false when no harness window exists.
    public static bool FocusHarnessWindow()
    {
        foreach (var w in EnumerateAppWindows())
        {
            if (w.Value == null ||
                w.Value.IndexOf("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Win32.SetForegroundWindow(w.Key);
            Thread.Sleep(80);
            return Win32.GetForegroundWindow() == w.Key;
        }
        return false;
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
