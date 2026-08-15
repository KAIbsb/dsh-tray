# dsh-tray

**[简体中文](../README.md) | English**

A Windows tray manager for [DeepSeek Harness](https://github.com/deepseek-ai/DeepSeek-Harness): start / restart / stop / auto-restart on crash, all from the tray's right-click menu. No terminal needed, no risk of accidentally closing the window — works best paired with a browser app-mode window.

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-blue.svg)]()
[![Language: C#](https://img.shields.io/badge/language-C%23-239120.svg)]()

> **Disclaimer**: This project is the product of pure "vibe coding" — no rigorous testing or code review, and unknown bugs may exist. Use at your own risk. If you run into issues, please report them on [Issues](https://github.com/KAIbsb/dsh-tray/issues).

## Features

- **Lifecycle management**: start / restart / stop / exit, all from the tray menu
- **Single-click tray icon**: starts the harness and opens the window if it's not running; opens the window directly if it is
- **Status icon**: blue whale while running; black/white whale when stopped, switching with the system light/dark theme in real time
- **Auto-restart on crash** (toggleable): brings the harness back up after an unexpected exit, with cooldowns to prevent restart loops
- **Start with Windows** (toggleable): writes `HKCU\...\Run`, no admin rights needed
- **Native system menu**: Windows 11 rounded theme, follows the dark mode automatically
- **No terminal window**: launches `node dsh web` hidden, output redirected to a dedicated `harness.log` independent of the tray's lifetime
- **Auto-refresh on restart**: refreshes the browser app-mode window when a restart finishes
- **On-demand elevation**: if the harness runs as administrator, the tray elevates itself to kill it (silent when UAC is set to "never notify")
- **Manual theme**: follow system / light / dark (settings window; ini `theme` key)
- **Auto-update**: one-click download + sha256 verification + deploy hint from the settings window when a new version is found
- **Logs**: `%LOCALAPPDATA%\dsh-tray\tray.log`, auto-rotated past 5 MB

## Download & Install

- Download the latest `dsh-tray.exe` from [Releases](https://github.com/KAIbsb/dsh-tray/releases)
- **Single file, zero dependencies**: no runtime to install (Windows 10/11 ships .NET Framework 4.8), just double-click to run
- **First run**: as an unsigned tool, SmartScreen may show "Unknown publisher" — click "More info" → "Run anyway" (see FAQ)
- **Upgrading**: overwrite the old exe with the new one; your settings (autostart, auto-restart, `dshtray.ini`) are untouched. When the settings window detects a new version you can also auto-update in one click (download + verification + deploy hint)
- Want to build it yourself or contribute? See the [developer documentation](DEVELOPMENT.md)

## Quick Start

### 1. Dependencies

| Dependency | Notes |
| --- | --- |
| Windows 10/11 | .NET Framework 4.8, built in |
| Node.js | required to run the harness |
| DeepSeek Harness | install it per the [DeepSeek-Harness repo](https://github.com/deepseek-ai/DeepSeek-Harness) |
| Browser (Chromium-based: Chrome / Edge) | optional, for the browser app-mode window |

### 2. Create a browser app-mode window (optional but recommended)

The harness Web UI lives at `http://127.0.0.1:3080`. To keep it out of your browser tabs, turn it into a standalone browser app-mode window (how to create one is left to you — search "browser app mode").

The tray's "Open Window" menu item will then launch this window. Closing it does not affect the harness — click the tray icon to reopen it anytime.

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
Settings…
────────
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

## Configuration

`dshtray.ini` is the tray's single configuration file. It is auto-generated next to the exe on first run (with a commented template), and every toggle in the settings window ("Settings…") writes this same file. Changes apply on the next start.

```ini
# dshtray.ini
url = http://127.0.0.1:3080   # default; port derived from the URL (change it for a custom port)
lang =                        # UI language zh/en; empty = follow system
autorestart = true            # auto-restart on crash: true/false
autostart = false             # start with Windows: true/false (also written to the startup key)
theme =                       # theme: light/dark, empty = follow system
node =                        # path to node.exe; empty = auto-detect
dshentry =                    # path to the dsh entry script; empty = auto-detect
dshworkdir =                  # dsh working directory; empty = inferred
chrome =                      # Chromium-family browser path; empty = auto-detect Chrome/Edge
```

An empty line means the default / auto-detection for that item (node / dsh entry / browser resolve via PATH, common install paths, npm global directory); deleting or commenting a line works the same. `url` is the only port setting (the port is derived from it). `autostart` is the single source for start-with-Windows; the Windows startup key is only a mirror (synced from the file at startup). The `theme` manual theme (light/dark) takes precedence over the system setting; empty = follow the system.

## Logs

- `%LOCALAPPDATA%\dsh-tray\tray.log` — tray operations (start / stop / restart / elevation / auto-restart etc.), auto-rotated to `tray.log.old` past 5 MB
- `%LOCALAPPDATA%\dsh-tray\harness.log` — harness output, independent of the tray's lifetime (keeps writing after the tray exits)
- The tray menu no longer has "Open Logs"; open the logs folder from the settings window ("Settings…")

## FAQ

> Full FAQ: [docs/FAQ.en.md](FAQ.en.md)(完整 FAQ 见 [docs/FAQ.md](FAQ.md))。A few of the most frequently asked:

**Does dsh-tray access the network?**

On startup it silently checks GitHub for the latest version once in the background (only once; works offline, fails silently, no popup); otherwise it does not access the network. Beyond that it only works locally: starting/stopping local processes, reading/writing the registry and logs, and launching your local browser when you choose "Open Window".

**The harness is still running after I exit the tray?**

By design: "Exit" only quits the tray; the harness keeps running. Use the "Stop" menu item to fully stop it.

**Why does a UAC prompt sometimes appear?**

When the harness was started as administrator, stopping/restarting it requires admin rights, which triggers UAC. If your UAC is set to "never notify", it completes silently without a prompt.

## License

[MIT License](../LICENSE) — free to use, modify, distribute and use commercially; just keep the copyright notice.
