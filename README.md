# DSHTray — DeepSeek Harness 托盘管家

Windows 托盘小程序,管理 [DeepSeek Harness](https://github.com/deepseek-ai/dsh) 的生命周期。不用开终端、不怕误关窗口:启动、重启、停止、崩溃自动拉起,全部在托盘里一键完成。

## 功能特性

- **生命周期管理**:启动 / 重启 / 停止 / 退出,全部在托盘右键菜单完成
- **双击托盘图标**:未运行时「启动并打开窗口」,运行中「直接打开窗口」
- **状态图标**:运行中 = 蓝色鲸鱼 `#5686fe`;停止 = 深色主题白鲸 / 浅色主题 `#151517`,随系统深浅色实时切换
- **崩溃自动重启**(可开关):harness 意外退出自动拉起,带冷却防死循环
- **开机自启**(可开关):写注册表 `HKCU\...\Run`,免管理员
- **原生系统右键菜单**:Win11 圆角主题样式,深色模式自动跟随
- **无终端窗口**:隐藏拉起 `node dsh web`,输出重定向到独立日志文件 `dsh.log`,与托盘生命周期解耦
- **重启后自动刷新窗口**:重启完成自动给 Chrome app 窗口发刷新
- **按需提权**:harness 若以管理员身份运行,托盘自动以管理员身份执行 kill(UAC 为「从不通知」时静默)
- **日志**:`%LOCALAPPDATA%\DSHTray\dshtray.log`,超过 5MB 自动轮转

## 快速开始

### 1. 环境要求

| 依赖 | 说明 |
| --- | --- |
| Windows 10/11 | .NET Framework 4.8 系统自带,无需安装 |
| Node.js | 运行 harness |
| DeepSeek Harness | `npm install -g @deepseek-ai/dsh` |
| Google Chrome | 可选,用于独立窗口显示 |

### 2. 安装 Harness

```bash
npm install -g @deepseek-ai/dsh
dsh --version   # 验证
```

### 3. 创建 Chrome App 窗口(推荐)

harness 的 Web UI 默认运行在 `http://127.0.0.1:3080`。为了不混进浏览器的一堆标签页,建议创建独立窗口:

**方式 A(图形界面)**
1. 浏览器打开 `http://127.0.0.1:3080`
2. Chrome 右上角 `⋮` → **更多工具** → **创建快捷方式**
3. 勾选 **「作为窗口打开」** → **创建**

**方式 B(命令行)**

```bash
chrome.exe --app=http://127.0.0.1:3080
```

之后托盘菜单「打开窗口」会自动拉起这个独立窗口(等价于 `chrome --app`),与标签页互不干扰。窗口关了不影响 harness,想再开点一下托盘即可。

### 4. 运行 DSHTray

```bash
# 直接双击即可,无需命令行
DSHTray.exe
```

托盘出现鲸鱼图标,自动启动 harness(无终端窗口、无闪窗)。右键图标弹出菜单:

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

### 5. 开机自启

托盘右键 → 勾选「开机自启」,下次登录自动拉起。

## 交互约定

| 操作 | 行为 |
| --- | --- |
| 左键双击托盘图标 | 未运行:启动并打开窗口;运行中:打开窗口 |
| 右键托盘图标 | 仅弹出菜单 |
| 单击左键 | 无动作 |

## 构建(从源码)

依赖:系统自带 .NET Framework 编译器 `csc.exe`(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`)

```bat
csc.exe /nologo /t:winexe /platform:anycpu /optimize+ ^
  /win32icon:assets\DSHTray.ico ^
  /win32manifest:app.manifest ^
  /resource:assets\whale-blue.png,DSHTray.blue.png ^
  /resource:assets\whale-dark.png,DSHTray.dark.png ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:DSHTray.exe Program.cs
```

## 目录结构

```
DSHTray.exe           托盘程序(单文件,图标内嵌)
Program.cs            全部源码
app.manifest          DPI 感知 + asInvoker 权限清单
assets/
  DSHTray.ico         exe 图标(白鲸 #f9fafb)
  whale-blue.png      运行状态图标(#5686fe)
  whale-dark.png      浅色主题停止图标(#151517)
```

## 配置说明

当前版本路径为硬编码(`Program.cs` 顶部常量):node.exe 路径、dsh 入口 `lib/bin.js`、Chrome 路径、端口 3080。路线图将改为配置文件支持。

## 日志

`%LOCALAPPDATA%\DSHTray\dshtray.log` —— 记录托盘自身的全部操作(启动/停止/重启/提权/自动重启等)。超过 5MB 自动轮转为 `dshtray.log.old`。

`%LOCALAPPDATA%\DSHTray\dsh.log` —— harness 的 stdout/stderr 输出(由 `cmd /c` 重定向写入,与托盘生命周期无关)。「打开日志」会同时打开这两个文件。

## 已知限制与 Roadmap

- 退出托盘不影响 harness(保持运行);停止 harness 请用「停止」菜单项
- 若 harness 在管理员终端启动,托盘执行停止/重启/退出时需要提权;UAC 设置为「从不通知」时静默完成,否则弹一次确认
- 硬编码路径,待配置化
- 打包为免 .NET 依赖的单文件(Self-contained)
- WebView2 独立壳,彻底脱离 Chrome

## 许可证

[MIT License](LICENSE) —— 可自由使用、修改、商用、再分发,仅需保留版权声明。
