using System;
using System.IO;
using System.Text;

// Log writing + rotation. Leaf layer: no dependencies. Logging before InitLog() is a silent
// no-op (logPath is null and we return early) so early code can never throw or write.
static class Logging
{
    const int LogMaxBytes = 5 * 1024 * 1024; // rotate the log once it exceeds this size

    static readonly object logLock = new object();
    static string logPath;

    // exposed for callers that need the log directory (harness.log sibling, "open logs")
    public static string LogPath { get { return logPath; } }

    public static void InitLog()
    {
        logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-tray", "tray.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)); } catch (Exception ex) { Log("InitLog mkdir failed: " + ex.Message); }
        // one-time migration from the old log directory name
        try
        {
            string oldDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHTray");
            string newDir = Path.GetDirectoryName(logPath);
            if (Directory.Exists(oldDir) && oldDir != newDir)
            {
                string oldTray = Path.Combine(oldDir, "dshtray.log");
                string oldHarness = Path.Combine(oldDir, "dsh.log");
                if (File.Exists(oldTray) && !File.Exists(logPath)) File.Copy(oldTray, logPath);
                string newHarness = Path.Combine(newDir, "harness.log");
                if (File.Exists(oldHarness) && !File.Exists(newHarness)) File.Copy(oldHarness, newHarness);
            }
        }
        catch (Exception ex) { Log("InitLog migration failed: " + ex.Message); }
    }

    public static void Log(string msg)
    {
        // InitLog() has not run yet: never write nor throw (keeps pre-config paths safe)
        if (logPath == null) return;
        try
        {
            lock (logLock)
            {
                try
                {
                    FileInfo fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > LogMaxBytes)
                    {
                        try { File.Copy(logPath, logPath + ".old", true); } catch { }
                        File.WriteAllText(logPath, "", Encoding.UTF8);
                    }
                }
                catch { }
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}
