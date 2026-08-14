# 开发者文档

本仓库的结构与开发指南,面向想编译、修改、贡献 dsh-tray 的开发者。

## 环境要求

- Windows 10/11(自带 .NET Framework 4.8 与编译器 `csc.exe`)
- Node.js + DeepSeek Harness(运行与调试对象)
- 可选:Google Chrome(浏览器 APP 模式窗口)

## 项目结构

```
Program.cs              主程序(全部逻辑)
Lang.cs                 界面语言表(zh / en)
app.manifest            DPI 感知 + asInvoker 权限清单
assets/DSHTray.ico      exe 图标(win32icon)
assets/whale-blue.png   运行状态图标(内嵌资源)
assets/whale-dark.png   浅色主题停止图标(内嵌资源)
.github/workflows/      Release 自动化
docs/                   README 英文版、本文档
```

## 构建

```bat
csc.exe /nologo /t:winexe /platform:anycpu /optimize+ ^
  /win32icon:assets\DSHTray.ico ^
  /win32manifest:app.manifest ^
  /resource:assets\whale-blue.png,DSHTray.blue.png ^
  /resource:assets\whale-dark.png,DSHTray.dark.png ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:dsh-tray.exe Program.cs Lang.cs
```

`csc.exe` 位于 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`。产物为单文件 exe(图标与状态图标均内嵌),无需安装任何运行时。

## 发布流程

1. 更新版本号:`Program.cs` 顶部的 `AppVersion` 常量与三个 Assembly 特性(`AssemblyVersion` / `AssemblyFileVersion` / `AssemblyTitle` 等),与 git tag 保持一致
2. `git tag vX.Y.Z` 并 `git push --tags`
3. GitHub Actions(`.github/workflows/release.yml`)自动编译 → 生成 SHA256 → 创建 Release 并附上 exe 与校验和

## 内部机制(修改前必读)

- **harness 启动方式**:通过 `cmd /c node <dsh入口> web >> harness.log 2>&1` 启动,输出重定向到**文件**而非管道。原因:托盘退出时若管道断裂,node 会在 ~1 秒内因 EPIPE 崩溃(已实测),文件重定向让 harness 完全独立于托盘生命周期
- **判活**:TCP 探测 `127.0.0.1:Port`(默认 3080);找 PID 用 `netstat -ano` 解析
- **停止 / 重启**:`taskkill /T /F` 杀进程树;若目标进程完整性级别高于自身(如管理员启动的 harness),以管理员身份重跑自身(`--elevated-kill <pid>`)执行杀进程(UAC 为「从不通知」时静默完成)
- **原生菜单**:`CreatePopupMenu` + `AppendMenuW` + `TrackPopupMenuEx`。深色模式靠 `uxtheme.dll` 的 `SetPreferredAppMode(#135)` + `FlushMenuThemes(#136)` 跟随系统;弹菜单前 owner 窗口必须置前台(`SetForegroundWindow` + ALT 键技巧),否则菜单无法通过点击外部 / Esc 关闭
- **窗口自动刷新**:枚举 Chrome 顶层窗口,对标题含 "DeepSeek Harness" 的窗口发送 Ctrl+R(先置前台,抢不到焦点则跳过)
- **配置**:`dshtray.ini`(见 README「配置」);node / dsh / chrome 路径自动探测(PATH、常见安装路径、npm 全局目录)
- **界面语言**:`Lang.cs`;优先级 `dshtray.ini` 的 `lang` 覆盖 > 系统 UI 语言

## 测试与诊断

| 参数 | 作用 |
| --- | --- |
| `--smoke` | 自检:路径探测、端口、图标资源、语言;结果写 `smoke-result.txt` |
| `--menu-test` | 构建原生菜单验证(不显示);结果写 `menu-test.txt` |
| `--find-window` | 列出所有 Chrome 顶层窗口(只读);结果写 `find-window-result.txt` |
| `--elevated-kill <pid>` | 以管理员身份杀进程树(由主程序按需自动调用) |

日志:`%LOCALAPPDATA%\dsh-tray\tray.log`(托盘操作)+ `harness.log`(harness 输出),均超 5MB 自动轮转。

## 图标与资源

鲸鱼图标取自 DeepSeek Harness 前端包内的 `favicon.svg`(路径:`dsh-web-frontend/dist/favicon.svg`,位于 npm 安装目录)。本地保留一套图标生成工具(不入库,见 `.gitignore`):

- `make_whale_svg.js` — 提取 SVG 路径数据,强制填充色输出为新的 SVG
- `IconBuilder.cs` — 将 SVG 路径(仅 M/C/Z 命令)解析为 `GraphicsPath`,渲染多尺寸 ICO / PNG,填充色可指定

重新生成状态图标示例:

```bat
IconBuilder.exe assets\whale-white.svg out.ico out.png <fill-hex>
```

## 已知约定

- 单实例:互斥体名 `dsh-tray_SingleInstance`(上次实例崩溃后可自动接管)
- 开机自启:注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值名 `dsh-tray`
- 退出托盘不影响 harness;停止 harness 用菜单「停止」
