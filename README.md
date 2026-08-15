# dsh-tray

**简体中文 | [English](docs/README.en.md)**

DeepSeek Harness 的 Windows 托盘管家:启动 / 重启 / 停止 / 崩溃自动拉起,全部在托盘右键完成。
不用开终端、不怕误关窗口，配合浏览器 APP 模式效果更佳！

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-blue.svg)]()
[![Language: C#](https://img.shields.io/badge/language-C%23-239120.svg)]()

> **免责声明**:本项目为纯「Vibe Coding」产物,未经严格测试与代码审查,可能存在未知 Bug。请自行评估风险后使用;如遇问题,欢迎到 [Issues](https://github.com/KAIbsb/dsh-tray/issues) 反馈。

## 功能特性

- **生命周期管理**:启动 / 重启 / 停止 / 退出,全部在托盘右键菜单完成
- **单击托盘图标**:未运行时自动启动并打开窗口,运行中直接打开窗口
- **状态图标**:运行中 = 蓝色;停止 = 黑 / 白鲸,随系统深浅色实时切换
- **崩溃自动重启**(可开关):harness 意外退出自动拉起,带冷却防死循环
- **开机自启**(可开关):写注册表 `HKCU\...\Run`,免管理员
- **原生系统右键菜单**:Win11 圆角主题样式,深色模式自动跟随
- **无终端窗口**:隐藏拉起 `node dsh web`,输出重定向到独立日志文件 `harness.log`,与托盘生命周期解耦
- **重启后自动刷新窗口**:重启完成自动刷新浏览器 APP 模式窗口
- **按需提权**:harness 若以管理员身份运行,托盘自动以管理员身份执行 kill(UAC 为「从不通知」时静默)
- **日志**:`%LOCALAPPDATA%\dsh-tray\tray.log`,超过 5MB 自动轮转

## 下载与安装

- 从 [Releases](https://github.com/KAIbsb/dsh-tray/releases) 下载最新的 `dsh-tray.exe`
- **单文件,零依赖**:无需安装任何运行时(Windows 10/11 自带 .NET Framework 4.8),双击即用
- **首次运行提示**:未签名的小工具会被 SmartScreen 提示「未知发布者」→ 点「更多信息」→「仍要运行」(详见 FAQ)
- **升级**:下载新 exe 直接覆盖旧文件即可,设置(开机自启、崩溃自动重启、`dshtray.ini`)不受影响
- 想自己编译或贡献代码?见 [开发者文档](docs/DEVELOPMENT.md)

## 快速开始

### 1. 安装依赖

| 依赖 | 说明 |
| --- | --- |
| Windows 10/11 | .NET Framework 4.8 系统自带 |
| Node.js | 运行 harness 所需 |
| DeepSeek Harness | 安装方法见 [DeepSeek-Harness 仓库](https://github.com/deepseek-ai/DeepSeek-Harness) |
| 浏览器(Chrome / Edge 等 Chromium 系) | 可选,用于浏览器 APP 模式窗口显示 |

### 2. 创建浏览器 APP 模式窗口(可选但推荐)

harness 的 Web UI 在 `http://127.0.0.1:3080`。不想让它混进浏览器标签页的话,可以把它做成独立的浏览器 APP 模式窗口(具体创建方法请自行查阅,搜索「浏览器 app mode」即可)。

之后托盘菜单「打开窗口」会自动拉起这个窗口;窗口关了不影响 harness,想再开点一下托盘即可。

### 3. 运行 dsh-tray

双击 `dsh-tray.exe` → 托盘出现鲸鱼图标 → harness 自动启动(无终端窗口)。**以后不用再手动敲 `dsh web` 了。** 启动、重启、停止等操作都在托盘右键菜单里,详见「使用说明」。

## 使用说明

### 托盘菜单

右键托盘图标弹出:

```
打开窗口
────────
启动        ← harness 停止时可用
重启        ← harness 运行时可用
停止        ← 只停 harness,托盘不退
────────
设置…
────────
退出        ← 仅退出托盘,harness 保持运行(停止用「停止」)
```

### 交互约定

| 操作 | 行为 |
| --- | --- |
| 左键单击托盘图标 | 未运行:启动并打开窗口;运行中:打开窗口 |
| 右键托盘图标 | 仅弹出菜单 |

### 状态图标

| 状态 | 图标 |
| --- | --- |
| 运行中 | 蓝色鲸鱼 |
| 已停止 | 黑 / 白鲸,随系统深浅色切换 |

## 配置

`dshtray.ini` 是托盘的唯一配置文件。首次运行会自动在 exe 同目录生成(带注释模板),设置窗口(菜单「设置…」)的所有开关/选项也直接改这个文件。修改保存后,下次启动托盘生效。

```ini
# dshtray.ini
url = http://127.0.0.1:3080   # 默认值;端口由 url 推导,改端口直接改这里
lang =                        # 界面语言 zh/en,留空 = 跟随系统
autorestart = true            # 崩溃自动重启 true/false
autostart = false             # 开机自启 true/false(同时写入 Windows 启动项)
node =                        # Node.js 路径,留空 = 自动检测
dshentry =                    # dsh 入口脚本路径,留空 = 自动检测
dshworkdir =                  # dsh 工作目录,留空 = 自动推断
chrome =                      # Chromium 系浏览器路径,留空 = 自动查找 Chrome/Edge
```

留空某行 = 该项使用默认值 / 自动检测(Node/Dsh 入口/浏览器走 PATH、常见安装路径、npm 全局目录);删除或注释某行同样生效。`url` 是唯一的端口配置项(端口由其自动推导)。`autostart` 是开机自启的唯一来源,Windows 启动项只是它的镜像(启动时按文件同步)。

## 日志

- `%LOCALAPPDATA%\dsh-tray\tray.log` —— 托盘自身操作记录(启动 / 停止 / 重启 / 提权 / 自动重启等),超 5MB 自动轮转为 `tray.log.old`
- `%LOCALAPPDATA%\dsh-tray\harness.log` —— harness 输出,独立于托盘生命周期(托盘退出后仍在写入)
- 托盘菜单已无「打开日志」;可在设置窗口(菜单「设置…」)里打开日志文件夹

## FAQ

> 完整 FAQ 见 [docs/FAQ.md](docs/FAQ.md)(Full FAQ: [docs/FAQ.en.md](docs/FAQ.en.md))。以下是几个最常问的:

**dsh-tray 会访问网络吗?**

启动时会后台静默检查一次 GitHub 最新版本(仅此一次;可离线,失败静默,不弹窗);其余时候不访问网络。除此之外只在本机工作:启动/停止本地进程、读写注册表与日志,以及「打开窗口」时唤起本机浏览器。

**退出托盘后 harness 还在运行?**

这是设计行为:「退出」只退出托盘,harness 保持运行;需要完全停止请用菜单里的「停止」。

**为什么有时会弹出 UAC 提权?**

当 harness 以管理员身份启动时,停止 / 重启 / 退出需要管理员权限才能结束它,此时会弹 UAC。若系统 UAC 设置为「从不通知」则静默完成,不弹窗。

## 许可证

[MIT License](LICENSE) —— 可自由使用、修改、商用、再分发,仅需保留版权声明。
