using System;
using System.Collections.Generic;
using System.Globalization;

// UI language table: zh / en, resolved from dshtray.ini "lang" override or the system UI language
static class Lang
{
    static Dictionary<string, string> zh = new Dictionary<string, string>();
    static Dictionary<string, string> en = new Dictionary<string, string>();
    static Dictionary<string, string> cur;
    static string code = "zh";
    // private set: language state may only be changed through Init/Switch inside this class
    public static string Code { get { return code; } private set { code = value; } }

    public static void Init(string iniLang)
    {
        zh["menu.open"] = "打开窗口";
        zh["menu.start"] = "启动";
        zh["menu.restart"] = "重启";
        zh["menu.stop"] = "停止";
        zh["menu.settings"] = "设置…";
        zh["menu.downloadUpdate"] = "下载更新 v{0}";
        zh["menu.exit"] = "退出";
        zh["tray.title"] = "dsh-tray";
        zh["tray.running"] = "DSH Harness — 运行中";
        zh["tray.stopped"] = "DSH Harness — 已停止";
        zh["settings.title"] = "设置";
        zh["settings.language"] = "语言";
        zh["settings.langAuto"] = "跟随系统";
        zh["settings.langZh"] = "中文";
        zh["settings.langEn"] = "English";
        zh["settings.autoRestart"] = "崩溃自动重启";
        zh["settings.autostart"] = "开机自启";
        zh["settings.checkUpdate"] = "检查更新";
        zh["settings.checking"] = "检查中…";
        zh["settings.upToDate"] = "已是最新";
        zh["settings.updateAvailable"] = "发现新版 v{0}";
        zh["settings.download"] = "下载";
        zh["settings.version"] = "版本 {0}";
        zh["settings.currentUrl"] = "当前地址 {0}";
        zh["settings.repo"] = "仓库";
        zh["settings.openConfig"] = "打开配置文件";
        zh["settings.openLogs"] = "打开日志文件夹";
        zh["settings.close"] = "关闭";
        zh["settings.groupGeneral"] = "通用";
        zh["settings.groupAbout"] = "关于 / 更新";
        zh["feedback.openWindowFailed"] = "打开窗口失败,详情见日志";
        zh["feedback.openLogsFailed"] = "打开日志文件夹失败,详情见日志";
        zh["feedback.openConfigFailed"] = "打开配置文件失败,详情见日志";
        zh["settings.autoUpdate"] = "自动更新";
        zh["settings.updating"] = "更新中…";
        zh["settings.updateReady"] = "更新已就绪,重启托盘生效";
        zh["settings.autoUpdateFailed"] = "自动更新失败,详情见日志";
        zh["settings.updateDeployFailed"] = "下载完成但部署失败,请退出托盘后运行新版或手动替换";
        zh["settings.theme"] = "主题";
        zh["settings.themeAuto"] = "跟随系统";
        zh["settings.themeLight"] = "亮";
        zh["settings.themeDark"] = "暗";

        en["menu.open"] = "Open Window";
        en["menu.start"] = "Start";
        en["menu.restart"] = "Restart";
        en["menu.stop"] = "Stop";
        en["menu.settings"] = "Settings…";
        en["menu.downloadUpdate"] = "Download Update v{0}";
        en["menu.exit"] = "Exit";
        en["tray.title"] = "dsh-tray";
        en["tray.running"] = "DSH Harness — Running";
        en["tray.stopped"] = "DSH Harness — Stopped";
        en["settings.title"] = "Settings";
        en["settings.language"] = "Language";
        en["settings.langAuto"] = "Follow system";
        en["settings.langZh"] = "Chinese";
        en["settings.langEn"] = "English";
        en["settings.autoRestart"] = "Auto-restart on crash";
        en["settings.autostart"] = "Start with Windows";
        en["settings.checkUpdate"] = "Check for updates";
        en["settings.checking"] = "Checking…";
        en["settings.upToDate"] = "Up to date";
        en["settings.updateAvailable"] = "Update available: v{0}";
        en["settings.download"] = "Download";
        en["settings.version"] = "Version {0}";
        en["settings.currentUrl"] = "Current URL {0}";
        en["settings.repo"] = "Repository";
        en["settings.openConfig"] = "Open config file";
        en["settings.openLogs"] = "Open logs folder";
        en["settings.close"] = "Close";
        en["settings.groupGeneral"] = "General";
        en["settings.groupAbout"] = "About / Updates";
        en["feedback.openWindowFailed"] = "Failed to open the window; see the log";
        en["feedback.openLogsFailed"] = "Failed to open the logs folder; see the log";
        en["feedback.openConfigFailed"] = "Failed to open the config file; see the log";
        en["settings.autoUpdate"] = "Auto-update";
        en["settings.updating"] = "Updating…";
        en["settings.updateReady"] = "Update ready; restart the tray to apply";
        en["settings.autoUpdateFailed"] = "Auto-update failed; see the log";
        en["settings.updateDeployFailed"] = "Download finished but deployment failed; exit the tray and run the new build or replace it manually";
        en["settings.theme"] = "Theme";
        en["settings.themeAuto"] = "Follow system";
        en["settings.themeLight"] = "Light";
        en["settings.themeDark"] = "Dark";

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

    // hot language switch: "auto"/"" = follow system, "zh" = Chinese, "en" = English.
    // Persists the override via Config.SaveLang (empty clears it). Persist failures are logged
    // and swallowed; the in-process language still takes effect this session.
    public static void Switch(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Trim().ToLowerInvariant() == "auto")
        {
            Code = CultureInfo.CurrentUICulture.Name.ToLowerInvariant().StartsWith("zh") ? "zh" : "en";
            Config.SaveLang("");
        }
        else
        {
            Code = (code.Trim().ToLowerInvariant() == "en") ? "en" : "zh";
            Config.SaveLang(Code);
        }
        cur = (Code == "en") ? en : zh;
    }
}
