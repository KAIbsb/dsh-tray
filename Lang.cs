using System;
using System.Collections.Generic;
using System.Globalization;

// UI language table: zh / en, resolved from dshtray.ini "lang" override or the system UI language
static class Lang
{
    static Dictionary<string, string> zh = new Dictionary<string, string>();
    static Dictionary<string, string> en = new Dictionary<string, string>();
    static Dictionary<string, string> cur;
    public static string Code = "zh";

    public static void Init(string iniLang)
    {
        zh["menu.open"] = "打开窗口";
        zh["menu.start"] = "启动";
        zh["menu.restart"] = "重启";
        zh["menu.stop"] = "停止";
        zh["menu.autoRestart"] = "崩溃自动重启";
        zh["menu.autostart"] = "开机自启";
        zh["menu.openLogs"] = "打开日志";
        zh["menu.exit"] = "退出";
        zh["tray.title"] = "DSH Harness 托盘管家";
        zh["tray.running"] = "DSH Harness — 运行中";
        zh["tray.stopped"] = "DSH Harness — 已停止";

        en["menu.open"] = "Open Window";
        en["menu.start"] = "Start";
        en["menu.restart"] = "Restart";
        en["menu.stop"] = "Stop";
        en["menu.autoRestart"] = "Auto-restart on Crash";
        en["menu.autostart"] = "Start with Windows";
        en["menu.openLogs"] = "Open Logs";
        en["menu.exit"] = "Exit";
        en["tray.title"] = "DSH Harness Tray Manager";
        en["tray.running"] = "DSH Harness — Running";
        en["tray.stopped"] = "DSH Harness — Stopped";

        if (!string.IsNullOrEmpty(iniLang))
            Code = (iniLang.Trim().ToLowerInvariant() == "en") ? "en" : "zh";
        else
            Code = CultureInfo.CurrentUICulture.Name.ToLowerInvariant().StartsWith("zh") ? "zh" : "en";
        cur = (Code == "en") ? en : zh;
    }

    public static string T(string key)
    {
        string v;
        if (cur != null && cur.TryGetValue(key, out v)) return v;
        if (en.TryGetValue(key, out v)) return v;
        return key;
    }
}
