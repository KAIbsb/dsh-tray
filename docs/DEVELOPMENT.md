# 开发者文档

本仓库的结构与开发指南,面向想编译、修改、贡献 dsh-tray 的开发者。

## 环境要求

- Windows 10/11(自带 .NET Framework 4.8 与编译器 `csc.exe`)
- Node.js + DeepSeek Harness(运行与调试对象)
- 可选:Chromium 系浏览器(Chrome / Edge 等,用于浏览器 APP 模式窗口)

## 项目结构

```
Program.cs        入口:Main + headless 分支(--smoke / --menu-test / --find-window / --ui-preview / --elevated-kill)
Config.cs         配置单一来源 dshtray.ini:解析、自动探测、注册表镜像
IniFile.cs        ini 读写小工具(注释行保留,键值就地更新)
DshProcess.cs     harness 进程状态机:启动/停止/重启/自愈轮询/判活/提权杀
WindowMgr.cs      浏览器 APP 窗口:打开、刷新(Ctrl+R)、枚举
TrayMenu.cs       托盘图标、原生菜单、主题、轮询
SettingsForm.cs   设置窗口(语言/主题热切换/开关/检查更新与自动更新/关于)
UpdateCheck.cs    GitHub Releases 版本检查 + 自动更新下载与 sha256 校验(后台静默,TLS 1.2)
UiFeedback.cs     操作失败 / 信息气泡反馈通道(叶子,事件触发)
Win32.cs          P/Invoke 声明与暗色主题封装
Logging.cs        日志写入/轮转(5MB)
Lang.cs           界面语言表(zh / en)
app.manifest      DPI 感知 + asInvoker 权限清单
assets/           whale-white.ico(exe 图标)、whale-blue.png / whale-dark.png(状态图标,内嵌资源)
.github/workflows/ Release 自动化
docs/             README 英文版、本文档
```

依赖方向单向:`Program → TrayMenu → {DshProcess, WindowMgr} → {Config, IniFile, Win32, Logging, Lang}`;`SettingsForm` / `UpdateCheck` 分别由 TrayMenu / 后台按需使用,不反向依赖上层。

## 构建

本地一键构建(仓库根目录运行):

```bat
build.bat
```

等价于直接调用编译器响应文件:

```bat
csc @dsh-tray.rsp
```

编译参数与源文件清单统一收敛在仓库根的 `dsh-tray.rsp`(当前 13 个源文件 + 图标/配置模板内嵌资源),`build.bat` 与 CI(`.github/workflows/release.yml`)均以它作为单一来源,避免多处命令漂移。`csc.exe` 位于 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`(`build.bat` 自动定位)。产物为单文件 exe(图标、状态图标与配置模板均内嵌),无需安装任何运行时。

开发期若托盘正在运行(exe 被占用),可用本地脚本编译到临时名并自动跑 smoke:

```bat
cmd /c .devtools\build-dev.bat
```

## 发布流程

1. 更新版本号:`Program.cs` 顶部的 `AssemblyVersion` / `AssemblyFileVersion` 特性(当前 `1.1.3.0`),与 git tag 保持一致;`AppVersion` 运行时自动从程序集读取,无需单独维护
2. `git tag vX.Y.Z` 并 `git push --tags`
3. GitHub Actions 自动编译 → 生成 SHA256 → 创建 Release 并附上 exe 与校验和

## 内部机制(修改前必读)

- **harness 启动方式**:通过 `cmd /c node <dsh入口> web >> harness.log 2>&1` 启动,输出重定向到**文件**而非管道。原因:托盘退出时若管道断裂,node 会在 ~1 秒内因 EPIPE 崩溃(已实测),文件重定向让 harness 完全独立于托盘生命周期
- **异步生命周期**:启动 / 停止 / 重启走 `Task` 异步执行,不阻塞 UI 线程(菜单、左键、轮询始终可响应);图标二态:蓝=运行,白/暗=停止(无闪动),状态变化经变化检测后更新;自愈轮询在异步启动进行中不会重复拉起(防双实例)
- **判活**:TCP 探测 `127.0.0.1:Port`(默认 3080),且端口占用者必须是 node 进程才判定为运行中(防误判他人进程);PID 解析用 `netstat -ano`(只认 LISTENING 行、本地回环/any 地址)
- **停止 / 重启**:`taskkill /T /F` 杀进程树;若目标进程完整性级别高于自身(如管理员启动的 harness),以管理员身份重跑自身(`--elevated-kill <pid>`)执行杀进程(UAC 为「从不通知」时静默完成)
- **原生菜单**:`CreatePopupMenu` + `AppendMenuW` + `TrackPopupMenuEx`。深色模式靠 `uxtheme.dll` 的 `SetPreferredAppMode(#135)` + `FlushMenuThemes(#136)` 跟随系统;弹菜单前 owner 窗口必须置前台(`SetForegroundWindow` + ALT 键技巧),否则菜单无法通过点击外部 / Esc 关闭
- **窗口自动刷新**:枚举配置的浏览器顶层窗口(进程名取配置的浏览器 + chrome/msedge 兜底),对标题含 "DeepSeek Harness" 的窗口发送 Ctrl+R(先置前台,抢不到焦点则跳过)
- **配置**:`dshtray.ini` 是**唯一配置源**(见 README「配置」)——自动重启 / 开机自启也存于此文件;开机自启的 ini 值在启动时镜像到注册表 Run 键;历史注册表值(`Software\dsh-tray\AutoRestart`)启动时自动迁移一次。node / dsh / chrome 路径留空自动探测(PATH、常见安装路径、npm 全局目录)。`theme` 键(light/dark/空=跟随系统)为手动主题覆盖,优先于注册表
- **更新检查 / 自动更新**:启动时后台静默请求一次 GitHub Releases API(失败静默,仅日志),发现新版在菜单与设置窗展示;设置窗「自动更新」一键 `UpdateCheck.DownloadAndVerify`(下载 exe + sha256 校验),运行中 exe 被锁时保留已校验的 `.new` 并提示手动替换
- **操作反馈**:`UiFeedback` 事件通道(`Fail` 失败 / `Info` 信息),TrayMenu 订阅后弹 4 秒气泡(Error / Info 图标);仅「用户主动操作失败 / 更新就绪」使用,启动失败、提权失败等被动路径不弹
- **界面语言**:`Lang.cs`;优先级 ini 的 `lang` 覆盖 > 系统 UI 语言;设置窗可热切换并写回 ini
- **手动主题**:设置窗「主题」行(跟随系统/亮/暗)写 ini `theme` 键;`Config.IsDarkMode` 先读覆盖、空则回退注册表;切换即时生效——`TrayMenu.ApplyThemeNow()` 重刷托盘图标、uxtheme 与打开中的设置窗

## 测试与诊断

| 参数 | 作用 |
| --- | --- |
| `--smoke` | 自检:路径探测、端口、图标资源、语言;结果写 `smoke-result.txt` |
| `--menu-test` | 构建原生菜单验证(不显示);结果写 `menu-test.txt` |
| `--find-window` | 列出所有浏览器顶层窗口(只读);结果写 `find-window-result.txt` |
| `--ui-preview` | 渲染设置窗口亮/暗两张截图(开发用),输出 `settings-preview-*.png`;可用临时 `dshtray.ini` 的 `lang` 控制语言 |
| `--elevated-kill <pid>` | 以管理员身份杀进程树(由主程序按需自动调用) |

日志:`%LOCALAPPDATA%\dsh-tray\tray.log`(托盘操作)超 5MB 自动轮转;`harness.log`(harness 输出)独立于托盘生命周期,每次启动 harness 前若超 5MB 会轮转为 `harness.log.old`(harness 运行中不强制轮转)。

## 图标与资源

鲸鱼图标取自 DeepSeek Harness 前端包内的 `favicon.svg`(`dsh-web-frontend/dist/favicon.svg`);生成/校验工具源码在 `.devtools/`(本地,不入库)。

## 已知约定

- 单实例:互斥体名 `dsh-tray_SingleInstance`(上次实例崩溃后可自动接管)
- 自动重启:ini 的 `autorestart` 键(历史版本存注册表 `Software\dsh-tray\AutoRestart`,启动时自动迁移一次)
- 开机自启:ini 的 `autostart` 键为唯一来源,镜像写入注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`(值名 `dsh-tray`)
- 退出托盘不影响 harness;停止 harness 用菜单「停止」
