# Developer Documentation

Repository structure and development guide for dsh-tray — for developers who want to build, modify or contribute.

## Requirements

- Windows 10/11 (ships .NET Framework 4.8 and the `csc.exe` compiler)
- Node.js + DeepSeek Harness (the thing this tool manages)
- Optional: Google Chrome (application-mode window)

## Repository layout

```
Program.cs              main program (all logic)
Lang.cs                 UI language table (zh / en)
app.manifest            DPI awareness + asInvoker manifest
assets/DSHTray.ico      exe icon (win32icon)
assets/whale-blue.png   running-state icon (embedded resource)
assets/whale-dark.png   light-theme stopped icon (embedded resource)
.github/workflows/      release automation
docs/                   English README, this document
```

## Build

```bat
csc.exe /nologo /t:winexe /platform:anycpu /optimize+ ^
  /win32icon:assets\DSHTray.ico ^
  /win32manifest:app.manifest ^
  /resource:assets\whale-blue.png,DSHTray.blue.png ^
  /resource:assets\whale-dark.png,DSHTray.dark.png ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:dsh-tray.exe Program.cs Lang.cs
```

`csc.exe` lives at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`. The output is a single exe (icon and status icons embedded) with no runtime to install.

## Release process

1. Bump the version: `AppVersion` constant and the three Assembly attributes (`AssemblyVersion` / `AssemblyFileVersion` / `AssemblyTitle` etc.) at the top of `Program.cs`, keeping them in sync with the git tag
2. `git tag vX.Y.Z` and `git push --tags`
3. GitHub Actions (`.github/workflows/release.yml`) compiles, generates the SHA256, and creates a Release with the exe and checksum attached

## Internals (read before modifying)

- **How the harness is launched**: via `cmd /c node <dsh entry> web >> harness.log 2>&1`, with output redirected to a **file** instead of a pipe. Reason: if the tray exits and the pipe breaks, node crashes from EPIPE within ~1 second (verified empirically); file redirection makes the harness fully independent of the tray's lifetime
- **Liveness check**: TCP probe to `127.0.0.1:Port` (default 3080); PIDs are resolved by parsing `netstat -ano`
- **Stop / restart**: `taskkill /T /F` kills the process tree; if the target runs at a higher integrity level (e.g. an admin-started harness), the tray re-launches itself elevated (`--elevated-kill <pid>`) to kill it (silent when UAC is "never notify")
- **Native menu**: `CreatePopupMenu` + `AppendMenuW` + `TrackPopupMenuEx`. Dark mode follows the system via `uxtheme.dll` `SetPreferredAppMode(#135)` + `FlushMenuThemes(#136)`; the owner window must be brought to the foreground before showing the menu (`SetForegroundWindow` + ALT-key trick), otherwise the menu won't dismiss on outside clicks / Esc
- **Auto-refresh**: enumerates Chrome top-level windows and sends Ctrl+R to windows whose title contains "DeepSeek Harness" (foreground first; skipped if focus can't be taken)
- **Configuration**: `dshtray.ini` (see README "Configuration"); node / dsh / chrome paths are auto-detected (PATH, common install locations, npm global directory)
- **UI language**: `Lang.cs`; precedence: `dshtray.ini` `lang` override > system UI language

## Testing & diagnostics

| Flag | Purpose |
| --- | --- |
| `--smoke` | Self-check: path detection, port, icon resources, language; writes `smoke-result.txt` |
| `--menu-test` | Builds the native menu for validation (not shown); writes `menu-test.txt` |
| `--find-window` | Lists all Chrome top-level windows (read-only); writes `find-window-result.txt` |
| `--elevated-kill <pid>` | Kills a process tree as administrator (invoked automatically on demand) |

Logs: `%LOCALAPPDATA%\dsh-tray\tray.log` (tray operations) + `harness.log` (harness output), both auto-rotated past 5 MB.

## Icons & assets

The whale icon comes from `favicon.svg` inside the DeepSeek Harness frontend package (path: `dsh-web-frontend/dist/favicon.svg` under the npm install directory). A local set of icon-generation tools is kept out of the repo (see `.gitignore`):

- `make_whale_svg.js` — extracts the SVG path data and emits a new SVG with a forced fill color
- `IconBuilder.cs` — parses the SVG path (M/C/Z commands only) into a `GraphicsPath`, renders multi-size ICO / PNG with a configurable fill color

Regenerating a status icon:

```bat
IconBuilder.exe assets\whale-white.svg out.ico out.png <fill-hex>
```

## Conventions

- Single instance: mutex `dsh-tray_SingleInstance` (automatically takes over after a crashed instance)
- Autostart: registry value `dsh-tray` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- Exiting the tray does not stop the harness; use the "Stop" menu item for that
