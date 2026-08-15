# FAQ

Full FAQ list. The main README ([../README.md](../README.md) / [README.en.md](README.en.md)) keeps only the most frequently asked items.

## SmartScreen shows "Unknown publisher"?

dsh-tray is an unsigned tool, so Windows SmartScreen may block the first run. Click "More info" → "Run anyway". If it bothers you, build from source yourself (see the [developer documentation](DEVELOPMENT.md)).

## The harness is still running after I exit the tray?

By design: "Exit" only quits the tray; the harness keeps running. Use the "Stop" menu item to fully stop it.

## Why does a UAC prompt sometimes appear?

When the harness was started as administrator, stopping/restarting it requires admin rights, which triggers UAC. If your UAC is set to "never notify", it completes silently without a prompt.

## The tray whale icon doesn't follow the light/dark theme?

The theme is checked every 3 seconds, so the icon updates within 3 seconds of switching. If it still doesn't change, look for a `theme changed` entry in the log (settings window → open logs folder).

## How do I change the listening port?

Create a `dshtray.ini` next to the exe with `url = http://127.0.0.1:<your port>` (the port is derived from the URL; see [Configuration](../README.en.md#configuration)).

## Autostart stopped working after I moved the exe?

Autostart records the exe's path at the time it was enabled. After moving the exe, tick "Start with Windows" in the tray menu again.

## Does dsh-tray access the network?

On startup it silently checks GitHub for the latest version once in the background (only once; works offline, fails silently, no popup); otherwise it does not access the network. Beyond that it only works locally: starting/stopping local processes, reading/writing the registry and logs, and launching your local browser when you choose "Open Window".

## How do I switch the light/dark theme?

In the settings window, the "Theme" row lets you pick "Follow system / Light / Dark" and it applies immediately. Alternatively edit `dshtray.ini` next to the exe directly: write `theme = light` (light) or `theme = dark` (dark); leave it empty (or delete the line) to follow the system — this takes effect after the tray restarts.
