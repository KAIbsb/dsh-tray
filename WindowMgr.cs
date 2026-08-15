using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// Chrome/Edge window helpers: open the app window, enumerate/reload it. Leaf-ish layer:
// depends only on Config / Win32 / Logging (never on Program, TrayMenu, or DshProcess).
static class WindowMgr
{
    public static void OpenWindow()
    {
        try
        {
            if (Config.Current.ChromePath != null && File.Exists(Config.Current.ChromePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Config.Current.ChromePath,
                    Arguments = "--app=" + Config.Current.WebUrl,
                    UseShellExecute = false
                });
            }
            else
            {
                // no Chrome/Edge found: open in the default browser
                Process.Start(Config.Current.WebUrl);
                Logging.Log("OpenWindow: no chrome/edge found, opened in default browser");
            }
        }
        catch (Exception ex) { Logging.Log("OpenWindow failed: " + ex.Message); }
    }

    // find Chrome top-level windows whose title matches the DSH webui and send Ctrl+R
    public static void ReloadAppWindow()
    {
        try
        {
            const string title = "DeepSeek Harness";
            var targets = new List<IntPtr>();
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
                        if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                            targets.Add(hWnd);
                    }
                }
                catch (Exception ex) { Logging.Log("ReloadAppWindow GetProcessById failed: " + ex.Message); }
                return true;
            }, IntPtr.Zero);

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
    // The enumeration logic is unchanged; only the report writing moved to the caller.
    public static string FindWindows()
    {
        var sb = new StringBuilder();
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
                    var t = new StringBuilder(256);
                    Win32.GetWindowText(hWnd, t, 256);
                    sb.AppendLine("hwnd=" + hWnd + " pid=" + pid + " title=[" + t.ToString() + "]");
                }
            }
            catch (Exception ex) { Logging.Log("RunFindWindow GetProcessById failed: " + ex.Message); }
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }
}
