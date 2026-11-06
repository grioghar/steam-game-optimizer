# Configuration & presets

Everything the app does is driven by one JSON settings file. You can change it three ways:

1. **The UI** (Apps / Profiles / Settings tabs) — saved automatically.
2. **Drop a preset into the presets folder** — picked up within a few seconds.
3. **Import a preset from the Settings tab** (*Presets (JSON)* → *Import preset…*). *Export current settings…* writes your live settings out as a file you can share or keep as a backup.

A **preset** is just a JSON file containing *some or all* of the settings below. Anything you leave out is left alone, so a preset can be as small as one line.

## Where things live

| What | Path |
| --- | --- |
| Live settings | `%APPDATA%\SteamOptimizer\config.json` |
| **Drop folder** | `%APPDATA%\SteamOptimizer\presets\` |
| Bundled examples (copy from here) | `%APPDATA%\SteamOptimizer\presets\examples\` |
| Already-applied presets | `%APPDATA%\SteamOptimizer\presets\applied\` |
| Rejected presets (+ a `.txt` saying why) | `%APPDATA%\SteamOptimizer\presets\rejected\` |
| Log / history / active-session journal | `log.txt`, `history.json`, `session.json` in `%APPDATA%\SteamOptimizer\` |

*Settings → Open presets folder* jumps straight to the drop folder. The same example files also live in this repository under [`examples/`](../examples).

## How a preset is applied

- Files dropped in the presets folder are applied automatically while the app is running (and on its next start), then moved to `applied\` — or to `rejected\` if they were invalid.
- **Merge by default.** Single values (`true`, `12`, `"Close"`) overwrite; **rules are merged** key by key; **lists are merged** without duplicates.
- **All-or-nothing.** If anything in the file is invalid (an error, not a warning), *nothing* is applied and the reason is written to the log and to a `.txt` next to the rejected file.
- **Unknown keys** only produce a warning and are ignored (so typos don't silently destroy your setup — check the log for `warning:`).
- Names are case-insensitive and `.exe` is optional: `"Discord.exe"`, `"discord"` and `"DISCORD"` are the same app.
- Comments aren't allowed in JSON, but any key starting with `_` (for example `"_comment"`) is ignored, as are `Name`, `Description` and `Version`. `Name` is shown in the log and in the notification.
- Applying a preset while a game session is active is safe: the new settings are used from the next session.

### Changing the merge behaviour

| You write | Effect |
| --- | --- |
| `"Rules": { "discord": "Keep" }` | Adds/overwrites just that rule. |
| `"Rules": { "discord": "auto" }` (or `null`) | **Removes** the rule — the app falls back to the defaults. |
| `"RemoveRules": ["discord", "obs64"]` | Removes several rules at once. |
| `"Rules!": { ... }` | A trailing **`!`** means *replace*: all existing rules are discarded first. Works on `Rules` and on every list setting (`"ExtraTriggers!"`, `"StreamIndicators!"`, `"StopServices!"` inside a profile). |
| `"ImportMode": "replace"` | Reset **everything** to the built-in defaults before applying the file. Use it for a complete, self-contained configuration. Default is `"merge"`. |

## Settings reference

### Top level

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Enabled` | bool | `true` | Master switch for automation. |
| `Rules` | object | see below | `"appname": "Keep" \| "Suspend" \| "Close"` (or `"auto"` to remove). |
| `DefaultWindowed` | `Keep`/`Suspend`/`Close` | `Close` | What happens to an app that has a window and **no rule**. |
| `DefaultBackground` | `Keep`/`Suspend`/`Close` | `Keep` | What happens to windowless/tray apps with **no rule**. |
| `Local` | profile | see below | Settings used for local play. |
| `Streaming` | profile | see below | Settings used while Remote Play streaming. |
| `ExtraTriggers` | list | `[]` | Non-Steam game executables (`"eldenring.exe"`) that should also trigger optimization. |
| `StreamIndicators` | list | `["streaming_client"]` | Processes whose presence means "a stream is active". |
| `OptimizeForStreamingClient` | bool | `true` | Also optimize when this PC is the Remote Play *client*. |
| `StartWithWindows` | bool | `false` | Start in the tray at sign-in. (Only touched if the preset contains this key.) |
| `StartMinimized` | bool | `true` | Reserved; the tray start is controlled by the `--tray` shortcut argument. |
| `CloseToTray` | bool | `true` | The window's X hides to the tray instead of exiting. |
| `ShowToasts` | bool | `true` | Tray balloon notifications from this app. |
| `DryRun` | bool | `false` | Log what *would* happen; change nothing. |
| `ForceKillAfterTimeout` | bool | `false` | Kill apps that don't close in time (can lose unsaved work). |
| `RelaunchMinimized` | bool | `false` | Relaunch restored apps minimized. |
| `CloseTimeoutSec` | int | `8` | How long to wait for an app to close. |
| `StartDelaySec` | int | `5` | Wait this long after a game starts before optimizing. |
| `EndGraceSec` | int | `8` | Wait this long after the game exits before restoring. |
| `RelaunchStaggerMs` | int | `400` | Pause between relaunching apps. |

Default `Rules` (all `Keep`): `claude`, `tailscale-ipn`, `tailscale`, `sunshine`, `parsecd`, `anydesk`, `teamviewer`, `rustdesk`.

### Rule actions

| Action | What happens during the game | After the game |
| --- | --- | --- |
| `Keep` | Never touched. | — |
| `Suspend` | Frozen (no CPU), memory trimmed. | Resumed exactly where it was. |
| `Close` | Asked to quit gracefully (save prompts are respected). | Relaunched with its original command line. |

### Profiles (`Local` and `Streaming`)

| Key | Type | Local default | Streaming default | Meaning |
| --- | --- | --- | --- | --- |
| `Enabled` | bool | `true` | `true` | If `false`, do nothing in this mode. |
| `CloseApps` | bool | `true` | `true` | Apply the Keep/Suspend/Close rules. |
| `PowerPlan` | text | `"high"` | `"high"` | `""` = don't change, `"high"` = Ultimate/High performance, or a power-scheme GUID. |
| `HighPriority` | bool | `true` | `true` | Raise the game's CPU priority to High. |
| `SuppressToasts` | bool | `true` | `true` | Turn off Windows notification pop-ups for the session. |
| `KeepAwake` | bool | `false` | `true` | Block sleep and screen-off. |
| `TrimMemory` | bool | `true` | `true` | Trim RAM of suspended apps. |
| `StopServices` | list | `[]` | `[]` | Windows service names to stop and later restart (**needs administrator**). |

### Always protected

Core Windows processes (`explorer`, `dwm`, `svchost`, `lsass`, …), Steam and its helpers, GPU driver containers, anything running from the Windows folder (unless you give it an explicit rule), the game itself, and this app and the program that launched it can never be closed or suspended, whatever a preset says.

## Examples

All of these are in [`examples/`](../examples) and are unpacked into `presets\examples\` for you.

### 1. "Only Claude should be open" — a clean taskbar

[`examples/strict-only-claude.json`](../examples/strict-only-claude.json)

```json
{
  "Name": "Strict - only Claude",
  "DefaultWindowed": "Close",
  "DefaultBackground": "Keep",
  "Rules!": {
    "claude": "Keep",
    "tailscale-ipn": "Keep",
    "tailscale": "Keep",
    "sunshine": "Keep",
    "parsecd": "Keep",
    "anydesk": "Keep",
    "teamviewer": "Keep",
    "rustdesk": "Keep"
  }
}
```

During a game every app with a window — except Claude and the remote-access tools — is closed; anything that was open is relaunched afterwards. Tray-only helpers and Windows' own support processes keep running. (`Rules!` replaces any previous rules; drop the `!` to merge instead.)

### 2. Keep one more app

```json
{ "Rules": { "discord": "Keep" } }
```

### 3. Freeze instead of close

[`examples/suspend-heavy-apps.json`](../examples/suspend-heavy-apps.json)

```json
{
  "Rules": {
    "firefox": "Suspend",
    "chrome": "Suspend",
    "spotify": "Close",
    "notepad": "auto"
  }
}
```

Browsers come back instantly with every tab exactly as they were; Spotify is quit and relaunched; the `notepad` rule is removed.

### 4. Tune streaming only

[`examples/streaming-focus.json`](../examples/streaming-focus.json)

```json
{
  "Streaming": {
    "PowerPlan": "high",
    "KeepAwake": true,
    "StopServices": ["SysMain", "WSearch"]
  },
  "StreamIndicators": ["streaming_client", "sunshine"]
}
```

Only the keys you list change; `Local` is untouched. Stopping services requires running the app as administrator (*Settings → Restart as administrator*).

### 5. Non-Steam games

```json
{ "ExtraTriggers": ["eldenring.exe", "Cyberpunk2077.exe"] }
```

### 6. Try it safely first

```json
{ "DryRun": true, "StartDelaySec": 3, "EndGraceSec": 5 }
```

Run a game, read the Dashboard log to see exactly what *would* have been closed, then import `{ "DryRun": false }`.

### 7. A complete, self-contained configuration

```json
{
  "Name": "My whole setup",
  "ImportMode": "replace",
  "DefaultWindowed": "Close",
  "Rules": { "claude": "Keep", "firefox": "Suspend", "discord": "Keep" },
  "Local": { "KeepAwake": false },
  "Streaming": { "KeepAwake": true, "StopServices": [] }
}
```

`ImportMode: "replace"` starts from the built-in defaults, so the result is exactly what's in the file — handy for moving a setup to another PC (use *Export current settings…* to generate one).

## Troubleshooting

- **Nothing happened after dropping a file** — it must end in `.json` and sit directly in `presets\` (not a subfolder). Check `applied\` and `rejected\`, and the Dashboard log.
- **"REJECTED"** — open the matching `.txt` in `rejected\`. Typical causes: a trailing comma, comments in the JSON, or an action other than `Keep`/`Suspend`/`Close`.
- **"warning: unknown setting"** — a key is misspelled; the rest of the file was still applied.
- Edit `config.json` by hand only while the app is closed — the app rewrites it on every change.
