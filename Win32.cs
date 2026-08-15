using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

// Native interop + theme helpers. Leaf layer: depends only on Logging (for error messages),
// never on Program/Config. All declarations are public so Program can call them directly.
static class Win32
{
    // ---- integrity (elevation) helpers ----
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr info, uint len, out uint retLen);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenIntegrityLevel = 25;

    // ---- WM_PRINT off-screen rendering (renders nonclient+client+children into a DC) ----
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    public const int WM_PRINT = 0x0317;
    // NONCLIENT(0x02) | CLIENT(0x04) | ERASEBKGND(0x08) | CHILDREN(0x10)
    public const int PRF_ALL = 0x1E;

    // ---- window reload (refresh the Chrome app window) ----
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- native system menu ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    // ---- immersive dark mode for native menus ----
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 2004+
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809/1903

    public static void ApplyMenuTheme(IntPtr hwnd, bool darkMode)
    {
        try
        {
            int useDark = darkMode ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, 4) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, 4);
        }
        catch (Exception ex) { Logging.Log("ApplyMenuTheme failed: " + ex.Message); }
    }

    // ---- process-wide menu theme (uxtheme ordinals, same as Chromium/Firefox) ----
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
    public static extern void FlushMenuThemes();

    public const int PAM_DEFAULT = 0;
    public const int PAM_ALLOW_DARK = 1;
    public const int PAM_FORCE_DARK = 2;

    public static void ApplyAppTheme(bool darkMode)
    {
        try
        {
            // AllowDark when system is dark, Default otherwise; FlushMenuThemes drops cached menu themes
            SetPreferredAppMode(darkMode ? PAM_ALLOW_DARK : PAM_DEFAULT);
            FlushMenuThemes();
        }
        catch (Exception ex) { Logging.Log("ApplyAppTheme failed: " + ex.Message); }
    }

    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const byte VK_R = 0x52;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public enum IntegrityLevel { Unknown = 0, Low = 4096, Medium = 8192, High = 12288, System = 16384 }

    public static IntegrityLevel GetIntegrity(int pid)
    {
        try
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return IntegrityLevel.Unknown;
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(h, TOKEN_QUERY, out tok)) return IntegrityLevel.Unknown;
                try
                {
                    uint retLen;
                    GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out retLen);
                    IntPtr buf = Marshal.AllocHGlobal((int)retLen);
                    try
                    {
                        if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, retLen, out retLen))
                            return IntegrityLevel.Unknown;
                        IntPtr sid = Marshal.ReadIntPtr(buf);
                        string s = new SecurityIdentifier(sid).Value;
                        int dash = s.LastIndexOf('-');
                        int rid;
                        if (dash < 0 || !int.TryParse(s.Substring(dash + 1), out rid)) return IntegrityLevel.Unknown;
                        return (IntegrityLevel)rid;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(tok); }
            }
            finally { CloseHandle(h); }
        }
        catch { return IntegrityLevel.Unknown; }
    }
}
