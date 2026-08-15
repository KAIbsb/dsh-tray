# Developer Documentation

Repository structure and development guide for dsh-tray — for developers who want to build, modify or contribute.

## Requirements

- Windows 10/11 (ships .NET Framework 4.8 and the `csc.exe` compiler)
- Node.js + DeepSeek Harness (the thing this tool manages)
- Optional: a Chromium-based browser (Chrome / Edge etc., for the browser app-mode window)

## Repository layout

```
Program.cs        entry: Main + headless modes (--smoke / --menu-test / --find-window / --ui-preview / --elevated-kill)
Config.cs         single config source dshtray.ini: parsing, auto-detection, registry mirror
IniFile.cs        minimal ini reader/writer (comments preserved, keys updated in place)
DshProcess.cs     harness process state machine: start/stop/restart/self-heal poll/liveness/elevated kill
WindowMgr.cs      browser app window: open, reload (Ctrl+R), enumerate
TrayMenu.cs       tray icon, native menu, theme, poll, starting-flash
SettingsForm.cs   settings window (language hot-switch / toggles / update check / about)
UpdateCheck.cs    GitHub Releases check (background silent, 8s timeout, TLS 1.2)
Win32.cs          P/Invoke declarations and dark-theme helpers
Logging.cs        log writing / rotation (5 MB)
Lang.cs           UI language table (zh / en)
app.manifest      DPI awareness + asInvoker manifest
assets/           whale-white.ico (exe icon), whale-blue.png / whale-dark.png (status icons, embedded)
.github/workflows/ release automation
docs/             English README, this document
```

Dependencies flow one way: `Program → TrayMenu → {DshProcess, WindowMgr} → {Config, IniFile, Win32, Logging, Lang}`; `SettingsForm` / `UpdateCheck` are used by TrayMenu / the background on demand and never depend upward.

## Build

One-shot local build (run from the repo root):

```bat
build.bat
```

Equivalent to invoking the compiler response file directly:

```bat
csc @dsh-tray.rsp
```

Compiler flags and the source-file list are consolidated into `dsh-tray.rsp` at the repo root (currently 11 source files plus embedded icon/config-template resources), which `build.bat` and CI (`.github/workflows/release.yml`) both use as the single source of truth, so the command copies can't drift apart. `csc.exe` lives at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` (`build.bat` locates it automatically). The output is a single exe (icon, status icons and the config template all embedded) with no runtime to install.

During development, when the tray is running and the exe is locked, use the local helper that builds to a temporary name and runs smoke:

```bat
cmd /c .devtools\build-dev.bat
```

## Release process

1. Bump the version: the `AssemblyVersion` / `AssemblyFileVersion` attributes at the top of `Program.cs` (currently `1.1.0.0`), keeping them in sync with the git tag; `AppVersion` is read from the assembly at runtime, so nothing else needs updating
2. `git tag vX.Y.Z` and `git push --tags`
3. GitHub Actions compiles, generates the SHA256, and creates a Release with the exe and checksum attached

## Internals (read before modifying)

- **How the harness is launched**: via `cmd /c node <dsh entry> web >> harness.log 2>&1`, with output redirected to a **file** instead of a pipe. Reason: if the tray exits and the pipe breaks, node crashes from EPIPE within ~1 second (verified empirically); file redirection makes the harness fully independent of the tray's lifetime
- **Async lifecycle**: start / stop / restart run on `Task`s and never block the UI thread (menu, left-click and the poll stay responsive); while starting, the tray icon flashes white↔blue every 500 ms and settles on the real status when done; the self-heal poll won't double-start while an async start/restart is in flight
- **Liveness check**: TCP probe to `127.0.0.1:Port` (default 3080), and the port owner must be a node process for the harness to count as up (avoids mistaking other processes); PIDs are resolved by parsing `netstat -ano` (LISTENING rows with loopback/any local addresses only)
- **Stop / restart**: `taskkill /T /F` kills the process tree; if the target runs at a higher integrity level (e.g. an admin-started harness), the tray re-launches itself elevated (`--elevated-kill <pid>`) to kill it (silent when UAC is "never notify")
- **Native menu**: `CreatePopupMenu` + `AppendMenuW` + `TrackPopupMenuEx`. Dark mode follows the system via `uxtheme.dll` `SetPreferredAppMode(#135)` + `FlushMenuThemes(#136)`; the owner window must be brought to the foreground before showing the menu (`SetForegroundWindow` + ALT-key trick), otherwise the menu won't dismiss on outside clicks / Esc
- **Auto-refresh**: enumerates top-level windows of the configured browser (process names from the config plus chrome/msedge fallbacks) and sends Ctrl+R to windows whose title contains "DeepSeek Harness" (foreground first; skipped if focus can't be taken)
- **Configuration**: `dshtray.ini` is the **single config source** (see README "Configuration") — auto-restart and autostart live in this file too; the autostart ini value is mirrored to the registry Run key at startup; a legacy registry value (`Software\dsh-tray\AutoRestart`) is migrated once at startup. node / dsh / chrome paths are auto-detected when left empty (PATH, common install locations, npm global directory)
- **Update check**: one silent background request to the GitHub Releases API at startup (failures are logged only); results surface via the "Download Update" menu item and the settings window, never as popups
- **UI language**: `Lang.cs`; precedence: `dshtray.ini` `lang` override > system UI language; the settings window can hot-switch and writes back to the ini

## Testing & diagnostics

| Flag | Purpose |
| --- | --- |
| `--smoke` | Self-check: path detection, port, icon resources, language; writes `smoke-result.txt` |
| `--menu-test` | Builds the native menu for validation (not shown); writes `menu-test.txt` |
| `--find-window` | Lists all browser top-level windows (read-only); writes `find-window-result.txt` |
| `--ui-preview` | Renders light/dark screenshots of the settings window (dev use), writes `settings-preview-*.png`; a temporary `dshtray.ini` `lang` key controls the language |
| `--elevated-kill <pid>` | Kills a process tree as administrator (invoked automatically on demand) |

Logs: `%LOCALAPPDATA%\dsh-tray\tray.log` (tray operations) + `harness.log` (harness output), both auto-rotated past 5 MB.

## Icons & assets

The whale icon comes from `favicon.svg` inside the DeepSeek Harness frontend package (`dsh-web-frontend/dist/favicon.svg`); the generator/checker tool sources live in `.devtools/` (local only, not committed).

## Conventions

- Single instance: mutex `dsh-tray_SingleInstance` (automatically takes over after a crashed instance)
- Auto-restart: the `autorestart` ini key (older versions stored it in the registry under `Software\dsh-tray\AutoRestart`; migrated once at startup)
- Autostart: the `autostart` ini key is the single source, mirrored to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (value name `dsh-tray`)
- Exiting the tray does not stop the harness; use the "Stop" menu item for that
