# Steam Game Optimizer

A small native Windows tray app that notices when you start a Steam game — to play locally **or to stream with Steam Remote Play** — and clears the decks: it closes or freezes the apps you don't need, switches to a performance power plan, quiets notifications and keeps the PC awake. When the game exits, everything goes back the way it was.

> Status: **v0.1.0**, first release. Tested end to end on a real Remote Play session (host: Windows 11 PC, client: Steam Deck). See [CHANGELOG](CHANGELOG.md) and [TODO](TODO.md).

## Features

- **Automatic detection** of Steam games (via Steam's own `RunningAppID`), non-Steam games you list, and Remote Play streaming (host or client).
- **Per-app rules** — *Keep*, *Suspend* (freeze instantly, resume exactly where it was, RAM trimmed) or *Close* (ask the app to quit, then relaunch it later with its original command line).
- **Safe by default** — core Windows processes, Steam and the app itself are always protected; apps that refuse to close (unsaved-work prompts) are left alone unless you opt into force-kill.
- **Full restore** — apps are resumed/relaunched, power plan, notifications and stopped services are put back. A journal is written to disk, so a crash or reboot mid-game is recovered on the next launch.
- **Separate Local and Streaming profiles**: power plan (Ultimate/High performance), game priority High, notification suppression, keep-awake, optional Windows services to stop.
- **Config UI** with a live process list, History tab (what was released per session) and a dry-run mode.
- Per-user install, no administrator rights needed, no dependencies beyond the .NET Framework 4.x that ships with Windows 10/11.

## Install

1. Download `SteamOptimizer-Setup-0.1.0.exe` from the [latest release](../../releases/latest).
2. Run it and choose your options. Windows SmartScreen may warn that the file is unsigned — see [Security](#security-notes).
3. Look for the bolt icon in the system tray. Double-click it to open the settings.

Prefer no installer? `SteamOptimizer.exe` on the release page is a portable build. Silent install: `Setup.exe /S [/autostart]`; silent uninstall: `Uninstall.exe /uninstall /S`.

## Using it

| Tab | What it's for |
| --- | --- |
| **Dashboard** | Current state, what's being held, live log, *Optimize now* / *Restore now*, pause, and a "treat next session as streaming" override. |
| **Apps** | Every running app with a rule: *(auto)*, Keep, Suspend, Close. Set what unlisted windowed and background apps do by default. |
| **Profiles** | Separate settings for Local play and Streaming. |
| **Settings** | Start with Windows, timings, force-kill, dry run, non-Steam triggers, stream indicators, restart as administrator. |
| **History** | Past sessions and totals. |

Defaults keep Discord, Tailscale, Parsec, Sunshine, AnyDesk, TeamViewer, OBS, overlay tools, Claude and terminals. **Turn on *Dry run* in Settings for your first session** to see what it would do without changing anything.

## Presets: configure with a JSON file

Drop a `.json` file into `%APPDATA%\SteamOptimizer\presets\` (Settings → *Open presets folder*) or use Settings → *Import preset…*, and it's applied automatically. Presets can be tiny — they only change what they mention — and *Export current settings…* gives you a file to back up or move to another PC. For example, to keep only Claude (plus remote-access tools) open during games:

```json
{
  "Name": "Strict - only Claude",
  "DefaultWindowed": "Close",
  "Rules!": { "claude": "Keep", "tailscale-ipn": "Keep", "sunshine": "Keep" }
}
```

Full reference, merge/replace rules and more examples: **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)**. Ready-made presets are in [`examples/`](examples).

## How it works

- Polls `HKCU\Software\Valve\Steam\RunningAppID` every 1.5 s; looks up the game name/install folder from the `appmanifest_*.acf` files in your Steam libraries.
- Streaming is inferred from Steam's `streaming_log.txt` being written recently or `streaming_client.exe` running (best-effort, configurable, with a manual override).
- Suspend uses `NtSuspendProcess`/`NtResumeProcess`; Close posts `WM_CLOSE` to the app's windows and waits, then relaunches the root process from its recorded path and arguments.
- State is stored in `%APPDATA%\SteamOptimizer\` (`config.json`, `history.json`, `log.txt`, and `session.json` while a session is active).

## Build from source

No SDK needed — it uses the C# compiler that ships with Windows.

```powershell
.\build.ps1
```

Output goes to `dist\`: `SteamOptimizer.exe` and `SteamOptimizer-Setup-<version>.exe`. Headless checks: `SteamOptimizer.exe --dry-run` (list what would be handled) and `--selftest` (close/relaunch/suspend/resume against Notepad).

## Security notes

- Binaries are **not code-signed**, so SmartScreen will warn on first run. Verify the SHA-256 on the release page, or build from source.
- The app only acts on processes in your own Windows session and never touches processes it can't open.
- Running it as administrator is only needed to stop Windows services; apps relaunched from an elevated session come back elevated.

## Known limitations

Microsoft Store apps aren't detected, notification suppression is unverified on all Windows builds, and Remote Play host detection is heuristic. Details in [TODO](TODO.md).

## License

[MIT](LICENSE)
