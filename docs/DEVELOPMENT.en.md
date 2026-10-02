# Developer Documentation

Repository structure and development guide for dsh-tray — for developers who want to build, modify or contribute.

## Requirements

- Windows 10/11 (ships .NET Framework 4.8 and the `csc.exe` compiler)
- Node.js + DeepSeek Harness (the thing this tool manages)
- Optional: a Chromium-based browser (Chrome / Edge etc., for the browser app-mode window)

## Repository layout

```
src/Program.cs        entry: Main + headless modes (--smoke / --menu-test / --find-window / --resolve-url / --ui-preview / --liveness-test / --elevated-kill / --restart-helper)
src/Config.cs         single config source dshtray.ini: parsing, auto-detection, registry mirror
src/IniFile.cs        minimal ini reader/writer (comments preserved, keys updated in place)
src/DshProcess.cs     harness process state machine: start/stop/restart/self-heal poll/liveness/elevated kill
src/WindowMgr.cs      browser app window: open (16:9 fit), enumerate, focus
src/TrayMenu.cs       tray icon, native menu, theme, poll
src/SettingsForm.cs   settings window (language/theme hot-switch / toggles / check & auto-update / about)
build/            build.bat, dsh-tray.rsp, app.manifest, dshtray.ini.example (build inputs)
src/UpdateCheck.cs    GitHub Releases check + auto-update download with sha256 verification (background silent, TLS 1.2)
src/UiFeedback.cs     operation-failure / info balloon channel (leaf, event-driven)
src/Win32.cs          P/Invoke declarations and dark-theme helpers
src/Logging.cs        log writing / rotation (5 MB)
src/Lang.cs           UI language table (zh / en)
build/app.manifest   DPI awareness + asInvoker + Win10/11 supportedOS manifest
assets/           whale-white.ico (exe icon), whale-blue.png / whale-dark.png (status icons, embedded)
.github/workflows/ release automation
docs/             English README, this document
```

Dependencies flow one way: `Program → TrayMenu → {DshProcess, WindowMgr} → {Config, IniFile, Win32, Logging, Lang}`; `SettingsForm` / `UpdateCheck` are used by TrayMenu / the background on demand and never depend upward.

## Build

One-shot local build (run from the repo root):

```bat
build\build.bat
```

Equivalent to invoking the compiler response file directly:

```bat
csc @build\dsh-tray.rsp
```

Compiler flags and the source-file list are consolidated into `build/dsh-tray.rsp` (currently 13 source files plus embedded icon/config-template resources), which `build.bat` and CI (`.github/workflows/release.yml`) both use as the single source of truth, so the command copies can't drift apart. `csc.exe` lives at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` (`build/build.bat` locates it automatically). The output is a single exe (icon, status icons and the config template all embedded) with no runtime to install.

During development, when the tray is running and the exe is locked, use the local helper that builds to a temporary name and runs smoke:

```bat
cmd /c .devtools\build-dev.bat
```

## Release process

1. Bump the version: the `AssemblyVersion` / `AssemblyFileVersion` attributes at the top of `src/Program.cs` (currently `1.6.0.0`), keeping them in sync with the git tag; `AppVersion` is read from the assembly at runtime, so nothing else needs updating
2. `git tag vX.Y.Z` and `git push --tags`
3. GitHub Actions compiles, generates the SHA256, and creates a Release with the exe and checksum attached

## dsh version compatibility boundary

Everything the tray depends on in dsh converges to seven touchpoints; after upgrading dsh, checking them one by one decides compatibility (2026-09-12: the full 0.1.5 line alpha.1/alpha.2/rc.1/rc.2 audited + live-verified; 2026-09-18: 0.1.6-alpha.1/alpha.2 unpack-audited; 2026-09-24: 0.1.7-alpha.1/alpha.2/rc.1/rc.2 unpack-audited + 0.1.7-alpha.2/rc.2 live-verified; 2026-10-02: backfill audit of 0.1.0-rc.8 / 0.1.1-rc.2 / 0.1.2-rc.1 / 0.1.3-alpha.2 — the last prerelease of each minor (0.1.4 was never published) — completing coverage of the whole 0.1.x line. Every round concluded zero-change compatibility; evidence lives in the workspace dirs `fixes/compat-check-20260912/`, `fixes/compat-check-20260918/`, `fixes/compat-check-20260924/`, `fixes/compat-check-20261002/`):

1. **Entry file**: the global package's `lib/bin.js` (0.1.5 turned it into a hash-chunked bundle, entry name unchanged; auto-detection follows this path)
2. **`web` subcommand**: up to rc.2 a commander subcommand of the launcher; from 0.1.6-alpha.1 a first-argument expansion (`dsh web` ≡ `dsh --profile web`, the tray's launch line unchanged); `--no-open` is parsed by web-startup, gated by `VersionSupportsNoOpen` (introduced in 0.1.0-rc.8)
3. **Startup banner**: `dsh web: <URL>` (possibly with a ` (LAN: …)` suffix; a non-URL line appears additionally when auto-opening the browser) — the parser only accepts lines starting with http and takes the last one in the file
4. **Token authentication**: the 303 (token→cookie) / 401 (unauthenticated) semantics of dsh ≥ 0.1.2, the `?token=` query parameter — `ResolveWebUrl`'s three-way probe depends on them
5. **Default port 3080**: used only for liveness/port-ownership checks; the actually opened address follows the banner URL
6. **Process identity**: node + command-line markers (`@deepseek-ai` / `bin.js` / `\dsh\` / `/dsh/`, the last covering forward-slash paths produced by npm shims or manual terminal launches)
7. **Frontend window title**: `DeepSeek Harness` (the match target for single-click focus of an existing window; works for both app windows and web tabs)

dsh 0.0.1-rc.1/rc.2 depend on the unpublished `dsh-frontend` package and cannot be installed from npm — declared unsupported; 0.0.1-rc.5 through 0.1.0-rc.7 were historically compatible but are no longer covered as of v1.6.0; **0.2.x is not supported** — the 2026-10-02 unpack reconnaissance of 0.2.0-rc.2 found the seven touchpoints substantively identical to 0.1.7-rc.2 (web-startup byte-identical), i.e. zero-change compatible at the unpack level, but the 0.2 line reserves a `desktop` profile for the official Electron desktop distribution; with this project archived, 0.2+ users are redirected to the official desktop app (see the README archive banner) and no adaptation is made.

## Internals (read before modifying)

- **How the harness is launched**: via `cmd /c node <dsh entry> web >> harness.log 2>&1`, with output redirected to a **file** instead of a pipe. Reason: if the tray exits and the pipe breaks, node crashes from EPIPE within ~1 second (verified empirically); file redirection makes the harness fully independent of the tray's lifetime
- **Async lifecycle**: start / stop / restart run on `Task`s and never block the UI thread (menu, left-click and the poll stay responsive); the icon is two-state — blue=running, white/dark=stopped (no flashing) — and updates only when the state changes; the self-heal poll won't double-start while an async start/restart is in flight
- **Liveness check**: TCP probe to `127.0.0.1:Port` (default 3080), and the port owner must be a node process for the harness to count as up (avoids mistaking other processes); PIDs are resolved by parsing `netstat -ano` (LISTENING rows with loopback/any local addresses only). While Running, the poll additionally re-verifies the port every 3s: an adopted host the tray cannot track (e.g. an elevated harness) has no Exited watcher, so 3 consecutive dead probes (≈9s) demote the state to Stopped for the auto-restart to take over; a false demotion is harmless (StartCore re-adopts). Verified deterministically by `--liveness-test`
- **Stop / restart**: stop still uses `taskkill /T /F` on the process tree; if the target runs at a higher integrity level (e.g. an admin-started harness), the tray re-launches itself elevated (`--elevated-kill <pid>`) to kill it (silent when UAC is "never notify"). **Restart prefers a soft restart**: the target PID is the real NODE process on the port (not the cmd wrapper, otherwise the soft path would always degrade to hard restart) → rebuild the exact boot invocation from that process's WMI command line → write `restart-<nonce>.spec` → spawn a detached `dsh-tray.exe --restart-helper <spec>` → **hard-stop the OLD process tree first** (on Windows, node's `process.kill(SIGTERM)` is only TerminateProcess and never reaches a JS handler / graceful dispose, so graceful shutdown cannot be relied on); the helper polls the port for release ≤30s → once free, replays the launch through `cmd /c` hidden (keeping the original log semantics; PowerShell `-WindowStyle Hidden` is only the fallback when an arg contains a double quote or %); the helper writes OK only when the port is bound by a NEW pid (≠ old pid) and kills the wrapper tree it spawned on failure; the tray poll equally requires port pid ≠ old pid before declaring success and re-binds `dshProc` so crash auto-restart/stop still work; a failed/timed-out/unpreparable soft restart automatically falls back to the hard restart. `--restart-helper` accepts only `%LOCALAPPDATA%\dsh-tray` `restart-*.spec` files and is dispatched before the single-instance mutex, like `--elevated-kill`
- **Native menu**: `CreatePopupMenu` + `AppendMenuW` + `TrackPopupMenuEx`. Dark mode follows the system via `uxtheme.dll` `SetPreferredAppMode(#135)` + `FlushMenuThemes(#136)`; the owner window must be brought to the foreground before showing the menu (`SetForegroundWindow` + ALT-key trick), otherwise the menu won't dismiss on outside clicks / Esc
- **Window open & focus**: "Open Window" launches an app-mode window via `chrome --app=<url>` with `--window-size/--window-position` geometry — 16:9 fitting 90% of the primary work area from `SPI_GETWORKAREA`, centered (the manifest is DPI-aware; values are physical pixels; a failed probe falls back to Chrome's default). `openmode=browser` in the ini skips app mode and opens a plain tab in the default browser. **Pages are never force-reloaded**: the dsh frontend has shipped auto-reconnect since 0.0.1-rc.5 (connection state machine), so after a restart the page recovers on its own — a forced reload only causes the theme flash; users press Ctrl+R once after a plugin frontend update. **Single-click focus**: `FocusHarnessWindow` enumerates browser top-level windows and brings the first titled "DeepSeek Harness" to the foreground (matches app windows and active tabs; background tabs are undetectable) — a new window opens only when none exists
- **Configuration**: `dshtray.ini` is the **single config source** (see README "Configuration") — auto-restart and autostart live in this file too; the autostart ini value is mirrored to the registry Run key at startup; a legacy registry value (`Software\dsh-tray\AutoRestart`) is migrated once at startup. node / dsh / chrome paths are auto-detected when left empty (PATH, common install locations, npm global directory). The `theme` key (light/dark/empty = follow system) is a manual theme override that takes precedence over the registry
- **Update check / auto-update**: one silent background request to the GitHub Releases API at startup (failures are logged only); when a new version is found it surfaces in the menu and the settings window. The settings window's "Auto-update" runs `UpdateCheck.DownloadAndVerify` (downloads the exe + sha256 verification); when the running exe is locked it keeps the verified `.new` and prompts for a manual replace
- **Operation feedback**: `UiFeedback` is an event channel (`Fail` for failures / `Info` for informational); TrayMenu subscribes and shows a 4-second balloon (Error / Info icon). It is used only for "a user-initiated action failed" and "update ready" — passive paths like start/elevation failures never pop up
- **UI language**: `Lang.cs`; precedence: `dshtray.ini` `lang` override > system UI language; the settings window can hot-switch and writes back to the ini
- **Manual theme**: the settings window "Theme" row (follow system / light / dark) writes the ini `theme` key; `Config.IsDarkMode` reads the override first and falls back to the registry when empty; it applies immediately — `TrayMenu.ApplyThemeNow()` refreshes the tray icon, uxtheme and the open settings dialog

## Testing & diagnostics

| Flag | Purpose |
| --- | --- |
| `--smoke` | Self-check: path detection, port, icon resources, language; writes `smoke-result.txt` |
| `--menu-test` | Builds the native menu for validation (not shown); writes `menu-test.txt` |
| `--find-window` | Lists all browser top-level windows (read-only); writes `find-window-result.txt` |
| `--resolve-url` | Reproduces the URL resolution of "Open Window": reads the last `dsh web:` banner from the harness log + HTTP probe; writes `resolve-url-result.txt` (with an `authPending` field) |
| `--liveness-test` | Self-check of the liveness blind spot: drives the Running-state re-verification state machine against real loopback ports (live port never degrades / 3 consecutive dead probes degrade / the counter resets across non-Running states); writes `liveness-test.txt` |
| `--ui-preview` | Renders light/dark screenshots of the settings window (dev use), writes `settings-preview-*.png`; a temporary `dshtray.ini` `lang` key controls the language |
| `--elevated-kill <pid>` | Kills a process tree as administrator (invoked automatically on demand) |

Logs: `%LOCALAPPDATA%\dsh-tray\tray.log` (tray operations) is auto-rotated past 5 MB; `harness.log` (harness output) is independent of the tray lifetime and is rotated to `harness.log.old` before each harness start when it exceeds 5 MB (no forced rotation while the harness is running).

## Icons & assets

The whale icon comes from `favicon.svg` inside the DeepSeek Harness frontend package (`dsh-web-frontend/dist/favicon.svg`); the generator/checker tool sources live in `.devtools/` (local only, not committed).

## Conventions

- Platform: Windows 10/11 only (`app.manifest` supportedOS declares only the Win10 GUID, which Win11 shares); runs on the system-built-in .NET Framework 4.8, compiled with the in-box csc (C# 5), zero external dependencies
- Single instance: mutex `dsh-tray_SingleInstance` (automatically takes over after a crashed instance)
- Auto-restart: the `autorestart` ini key (older versions stored it in the registry under `Software\dsh-tray\AutoRestart`; migrated once at startup)
- Autostart: the `autostart` ini key is the single source, mirrored to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (value name `dsh-tray`)
- Exiting the tray does not stop the harness; use the "Stop" menu item for that
