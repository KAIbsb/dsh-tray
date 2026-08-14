# dsh-tray

**简体中文 | [English](docs/README.en.md)**

DeepSeek Harness 的 Windows 托盘管家:启动 / 重启 / 停止 / 崩溃自动拉起,全部在托盘右键完成。
不用开终端、不怕误关窗口，配合应用模式窗口效果更佳！

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## 功能特性

- **生命周期管理**:启动 / 重启 / 停止 / 退出,全部在托盘右键菜单完成
- **单击托盘图标**:未运行时自动启动并打开窗口,运行中直接打开窗口
- **状态图标**:运行中 = 蓝色;停止 = 黑 / 白鲸,随系统深浅色实时切换
- **崩溃自动重启**(可开关):harness 意外退出自动拉起,带冷却防死循环
- **开机自启**(可开关):写注册表 `HKCU\...\Run`,免管理员
- **原生系统右键菜单**:Win11 圆角主题样式,深色模式自动跟随
- **无终端窗口**:隐藏拉起 `node dsh web`,输出重定向到独立日志文件 `harness.log`,与托盘生命周期解耦
- **重启后自动刷新窗口**:重启完成自动刷新应用模式窗口
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
| Google Chrome | 可选,用于应用模式窗口显示 |

### 2. 创建应用模式窗口(可选但推荐)

harness 的 Web UI 在 `http://127.0.0.1:3080`。不想让它混进浏览器标签页的话,可以做成 Chrome 应用模式窗口:

浏览器打开该地址 → Chrome 右上角 `⋮` → **更多工具** → **创建快捷方式** → 勾选 **「作为窗口打开」** → 创建

之后托盘菜单「打开窗口」会自动拉起这个应用模式窗口;窗口关了不影响 harness,想再开点一下托盘即可。

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
☑ 崩溃自动重启
☐ 开机自启
────────
打开日志
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

无需任何配置即可运行 —— node、dsh、Chrome 会自动探测。特殊环境可在 exe 同目录建 `dshtray.ini` 覆盖(全部项均可省略):

```ini
# dshtray.ini(可选)
node = C:\path\to\node.exe
dshentry = C:\path\to\dsh\lib\bin.js
dshworkdir = C:\path\to\dsh
chrome = C:\path\to\chrome.exe
port = 3080
lang = zh        # 界面语言 zh/en,缺省跟随系统
```

优先级:ini 显式值 > 自动探测(PATH / 常见安装路径 / npm 全局目录)。

## 日志

- `%LOCALAPPDATA%\dsh-tray\tray.log` —— 托盘自身操作记录(启动 / 停止 / 重启 / 提权 / 自动重启等),超 5MB 自动轮转为 `tray.log.old`
- `%LOCALAPPDATA%\dsh-tray\harness.log` —— harness 输出,独立于托盘生命周期(托盘退出后仍在写入)
- 托盘菜单「打开日志」会同时打开这两个文件

## FAQ

**SmartScreen 提示「未知发布者」怎么办?**

dsh-tray 是未签名的小工具,首次运行会被 Windows SmartScreen 拦截。点「更多信息」→「仍要运行」即可。在意的话可以自己从源码构建(见[开发者文档](docs/DEVELOPMENT.md))。

**退出托盘后 harness 还在运行?**

这是设计行为:「退出」只退出托盘,harness 保持运行;需要完全停止请用菜单里的「停止」。

**为什么有时会弹出 UAC 提权?**

当 harness 以管理员身份启动时,停止 / 重启 / 退出需要管理员权限才能结束它,此时会弹 UAC。若系统 UAC 设置为「从不通知」则静默完成,不弹窗。

**托盘的鲸鱼图标不随系统深浅色变化?**

图标每 3 秒检查一次主题,切换后最多 3 秒更新。仍不变的话,看日志(菜单「打开日志」)里是否有 `theme changed` 记录。

**如何修改监听端口?**

在 exe 同目录创建 `dshtray.ini`,写入 `port = 你的端口`(见「配置」)。

**移动了 exe 位置后开机自启失效?**

开机自启记录的是 exe 当时的路径。移动后重新在托盘菜单勾选一次「开机自启」即可。

**dsh-tray 会访问网络吗?**

不会。它只在本机工作:启动/停止本地进程、读写注册表与日志。唯一的外部调用是「打开窗口」时唤起本机浏览器。

## 许可证

[MIT License](LICENSE) —— 可自由使用、修改、商用、再分发,仅需保留版权声明。
