# dsh-tray

**[简体中文](../README.md) | English**

A Windows tray manager for [DeepSeek Harness](https://github.com/deepseek-ai/DeepSeek-Harness): start / restart / stop / auto-restart on crash, all from the tray's right-click menu. No terminal needed, no risk of accidentally closing the window — works best paired with an application-mode window.

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../LICENSE)

## Features

- **Lifecycle management**: start / restart / stop / exit, all from the tray menu
- **Single-click tray icon**: starts the harness and opens the window if it's not running; opens the window directly if it is
- **Status icon**: blue whale while running; black/white whale when stopped, switching with the system light/dark theme in real time
- **Auto-restart on crash** (toggleable): brings the harness back up after an unexpected exit, with cooldowns to prevent restart loops
- **Start with Windows** (toggleable): writes `HKCU\...\Run`, no admin rights needed
- **Native system menu**: Windows 11 rounded theme, follows the dark mode automatically
- **No terminal window**: launches `node dsh web` hidden, output redirected to a dedicated `dsh.log` independent of the tray's lifetime
- **Auto-refresh on restart**: refreshes the application-mode window when a restart finishes
- **On-demand elevation**: if the harness runs as administrator, the tray elevates itself to kill it (silent when UAC is set to "never notify")
- **Logs**: `%LOCALAPPDATA%\DSHTray\dshtray.log`, auto-rotated past 5 MB

## Download & Install

<!-- TODO: replace owner/repo in the links below and delete this line once the repository exists -->

- Download the latest `dsh-tray.exe` from [Releases](https://github.com/<owner>/<repo>/releases)
- **Single file, zero dependencies**: no runtime to install (Windows 10/11 ships .NET Framework 4.8), just double-click to run
- **First run**: as an unsigned tool, SmartScreen may show "Unknown publisher" — click "More info" → "Run anyway" (see FAQ)
- **Upgrading**: overwrite the old exe with the new one; your settings (autostart, auto-restart, `dshtray.ini`) are untouched
- Want to build it yourself? See [Build from source](#build-from-source)

## Quick Start

### 1. Dependencies

| Dependency | Notes |
| --- | --- |
| Windows 10/11 | .NET Framework 4.8, built in |
| Node.js | required to run the harness |
| DeepSeek Harness | install it per the [DeepSeek-Harness repo](https://github.com/deepseek-ai/DeepSeek-Harness) |
| Google Chrome | optional, for the application-mode window |

### 2. Create an application-mode window (optional but recommended)

The harness Web UI lives at `http://127.0.0.1:3080`. To keep it out of your browser tabs, turn it into a Chrome application-mode window:

Open the URL in Chrome → `⋮` (top right) → **More tools** → **Create shortcut** → tick **"Open as window"** → **Create**

The tray's "Open Window" menu item will then launch this application-mode window. Closing it does not affect the harness — click the tray icon to reopen it anytime.

### 3. Run dsh-tray

Double-click `dsh-tray.exe` → the whale icon appears in the tray → the harness starts automatically (no terminal window). **No more typing `dsh web` by hand.** Start/restart/stop and more live in the tray's right-click menu — see [Usage](#usage).

## Usage

### Tray menu

Right-click the tray icon (menu language follows the system UI language, or `lang` in `dshtray.ini`):

```
Open Window
────────
Start           ← available when the harness is stopped
Restart         ← available when running
Stop            ← stops the harness only; the tray stays
────────
☑ Auto-restart on Crash
☐ Start with Windows
────────
Open Logs
Exit            ← exits the tray only; the harness keeps running (use Stop to stop it)
```

### Click behavior

| Action | Behavior |
| --- | --- |
| Left click on the tray icon | Not running: start and open the window; running: open the window |
| Right click | Shows the menu only |

### Status icon

| State | Icon |
| --- | --- |
| Running | Blue whale |
| Stopped | Black/white whale, follows the system light/dark theme |

## Build from source

Requires the .NET Framework compiler that ships with Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`):

```bat
csc.exe /nologo /t:winexe /platform:anycpu /optimize+ ^
  /win32icon:assets\DSHTray.ico ^
  /win32manifest:app.manifest ^
  /resource:assets\whale-blue.png,DSHTray.blue.png ^
  /resource:assets\whale-dark.png,DSHTray.dark.png ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:dsh-tray.exe Program.cs Lang.cs
```

Releases: pushing a `v*` tag makes GitHub Actions compile the exe and create a Release automatically (see `.github/workflows/release.yml`).

## Configuration

No configuration is needed — node, dsh and Chrome are auto-detected. For special environments, create a `dshtray.ini` next to the exe to override (all keys optional):

```ini
# dshtray.ini (optional)
node = C:\path\to\node.exe
dshentry = C:\path\to\dsh\lib\bin.js
dshworkdir = C:\path\to\dsh
chrome = C:\path\to\chrome.exe
port = 3080
lang = en        # UI language zh/en; defaults to the system UI language
```

Precedence: explicit ini values > auto-detection (PATH / common install locations / npm global directory).

## Logs

- `%LOCALAPPDATA%\DSHTray\dshtray.log` — tray operations (start / stop / restart / elevation / auto-restart etc.), auto-rotated to `dshtray.log.old` past 5 MB
- `%LOCALAPPDATA%\DSHTray\dsh.log` — harness output, independent of the tray's lifetime (keeps writing after the tray exits)
- The tray menu's "Open Logs" item opens both files

## FAQ

**SmartScreen shows "Unknown publisher"?**

dsh-tray is an unsigned tool, so Windows SmartScreen may block the first run. Click "More info" → "Run anyway". If it bothers you, build from source yourself (see [Build from source](#build-from-source)).

**The harness is still running after I exit the tray?**

By design: "Exit" only quits the tray; the harness keeps running. Use the "Stop" menu item to fully stop it.

**Why does a UAC prompt sometimes appear?**

When the harness was started as administrator, stopping/restarting it requires admin rights, which triggers UAC. If your UAC is set to "never notify", it completes silently without a prompt.

**The tray whale icon doesn't follow the light/dark theme?**

The theme is checked every 3 seconds, so the icon updates within 3 seconds of switching. If it still doesn't change, look for a `theme changed` entry in the log (tray menu → Open Logs).

**How do I change the listening port?**

Create a `dshtray.ini` next to the exe with `port = <your port>` (see [Configuration](#configuration)).

**Autostart stopped working after I moved the exe?**

Autostart records the exe's path at the time it was enabled. After moving the exe, tick "Start with Windows" in the tray menu again.

**Does dsh-tray access the network?**

No. It only works locally: starting/stopping local processes and reading/writing the registry and logs. The only external call is launching your local browser when you choose "Open Window".

## License

[MIT License](../LICENSE) — free to use, modify, distribute and use commercially; just keep the copyright notice.
