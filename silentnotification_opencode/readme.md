# opencode-tray

Icon in the **Windows notification area** (the hidden icon tray, the arrow `^`
at the bottom right) that shows at a glance what opencode is doing.

| OpenCode status | Icon | Color |
| --- | --- | --- |
| working (model or tool running) | `O` with broken square | **green** |
| needs your response (question, permission, error, retry) | `O` with broken square | **yellow** |
| finished writing, waiting for your next message | `O` with broken square | **black** |

The "strange" component inside the `O` is a square with interrupted outline.

**Stealth by default**: no popup, no sound, no animation, no window.
The signal is **only the icon color**. Other options are activated from the menu.

---

## Requirements

- Windows 10 / 11 (uses only .NET Framework 4.x, already present on every Windows)
- opencode installed with the official script:
  ```sh
  curl -fsSL https://opencode.ai/v2/install | bash
  ```
  (the plugin is copied to `~/.config/opencode/plugins/`)
- No administrator rights required: everything in `HKCU` and `%LOCALAPPDATA%`
- **Linux (GNOME)**: Shell extension + Python listener; requires `python3`, `gnome-extensions` and active GNOME session

## Installation

Double-click `Install.cmd`, or:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

Useful options:

```powershell
# change port or disable auto-start
powershell -ExecutionPolicy Bypass -File install.ps1 -Port 47110 -NoStartWithWindows

# install only the app, without the opencode plugin
powershell -ExecutionPolicy Bypass -File install.ps1 -NoPlugin

# reset settings to defaults
powershell -ExecutionPolicy Bypass -File install.ps1 -Force
```

The installer:

1. compiles `OpencodeTray.exe` (source in `src/`, no external dependency);
2. installs it in `%LOCALAPPDATA%\Programs\opencode-tray`;
3. writes settings to `%APPDATA%\opencode-tray\settings.json`;
4. creates the Start menu shortcut and (optional) the auto-start shortcut;
5. registers the entry in **Programs and Features** (with uninstaller);
6. installs plugin `opencode-tray.js` to `~/.config/opencode/plugins/`;
7. starts the app.

> **After installation restart opencode**: configuration, like all plugins, is loaded
> once at startup.

## Linux Installation (GNOME)

The tray component on Linux is a **GNOME Shell extension** (indicator in the panel) + a
**Python listener** that listens to the same HTTP POST from plugin `opencode-tray.js`.
The JS plugin does not change: it is the same `plugin/opencode-tray.js` used on Windows.

### Quick commands

```bash
# from this folder
bash install-linux.sh
```

The script:

1. copies `gnome-extension/` to `~/.local/share/gnome-shell/extensions/opencode-tray@local/`;
2. copies `plugin/opencode-tray.js` to `~/.config/opencode/plugins/` (if present);
3. writes `~/.config/opencode-tray/settings.json` (port `47110`, default like Windows);
4. creates autostart file `~/.config/autostart/opencode-tray.desktop`;
5. starts listener in background (`python3 listener/opencode-tray-listener.py`);
6. enables the extension (if `gnome-extensions` is available).

Then **reload GNOME Shell** (`Alt+F2` → `r` on X11; on Wayland disconnect / reconnect) and activate
the extension from **GNOME Tweaks / Extensions** (or `gsettings`).

### Restart / manual start

```bash
bash linux-start.sh
```

This checks if the listener is already on `47110`; if not it starts it (`nohup`) and enables
the extension. To disable instead:

```bash
gnome-extensions disable opencode-tray@local
# or remove the extension folder:
rm -rf ~/.local/share/gnome-shell/extensions/opencode-tray@local
```

### Linux paths

| Component | Path |
| --- | --- |
| GNOME Extension | `~/.local/share/gnome-shell/extensions/opencode-tray@local/` |
| Listener | `.../listener/opencode-tray-listener.py` |
| Atomic state (tmp) | `/tmp/opencode-tray-state.json` |
| opencode Plugin | `~/.config/opencode/plugins/opencode-tray.js` |
| Settings | `~/.config/opencode-tray/settings.json` |
| Listener log | `/tmp/opencode-tray-listener.log` |

For details on indicator index (left of clock) or cost window,
see `gnome-extension/README.md`.

## Usage

**Windows**: the icon is in the hidden icon tray. If you don't see it, drag it out from
the `^` arrow at the bottom right to pin it in the bar (Windows remembers).

**Linux**: the indicator is in the panel (left of clock, default `index: 0` in
`settings.json`). If it doesn't appear after `Alt+F2 r`, check that the extension is active
(GNOME Tweaks / Extensions) and that the listener is running (`ps aux | grep listener`).
- **Left click** on the icon = menu (status, costs, projects, toggle, exit).
- The menu and states are the same as Windows (black / green / yellow); the cost window
opens with `cost-info-window.py` (see `gnome-extension/README.md`).
- **Double click** on the icon = menu:

| Item | Effect |
| --- | --- |
| status: … | current status + number of active opencode instances |
| opencode: ca. …$ | cost estimate (how much you would pay if the model were not free) |
| OpenCode instances (N) | list of projects: which is working, which is blocked waiting |
| Reset cost count | resets cost counting (estimate restarts from zero) |
| Cost info (model - token) | window with estimate per project/model and weekly projection |
| Notification popup | **disabled** by default; when active a "attention" balloon appears |
| Warning sound | **disabled** by default |
| Blinking icon when waiting | **disabled** by default; when active blinks yellow for 7 seconds |
| Start with Windows | creates/removes shortcut in Startup folder |
| Open opencode config / installation / tray config | opens folders |
| Exit | closes app |

### Cost estimation

- The estimate answers a single question: **how much would I pay if the model were not free**.
- Free models (e.g. `minimax-m3.1-flash-free`) are evaluated with the price list of the
  closest paid twin (e.g. `minimax-m3`): the window always shows
  `price of <model>` when the estimate does not use the model's own price list.
- Prices are read from opencode's local registry (`~/.cache/opencode/models.json`)
  and reloaded when opencode updates it.
- **Only opencode provider tokens enter the estimate** (`opencode`, `opencode-go`):
  tokens passed by **openrouter** (or other providers) are excluded, because they have a
  different price list.
- The "Cost info" window shows, for each project, tokens and cost per model, today's/7-day total
  and a **weekly and monthly projection** calculated on the observed daily average.
- Costs are attributed to **session** (not process): each opencode session is followed separately
  and tokens are summed per project, so two opencode on the same project are not counted twice
  and a closed opencode does not erase the total.

Settings can also be written manually in
`%APPDATA%\opencode-tray\settings.json` (Windows) or `~/.config/opencode-tray/settings.json` (Linux):

```json
{
  "port": 47110,
  "balloon": false,
  "sound": false,
  "flash": false,
  "heartbeatTimeoutSec": 25,
  "startWithWindows": true
}
```

`heartbeatTimeoutSec` is how long the icon stays colored after the last opencode signal:
when timeout expires (opencode closed) the icon turns black.

## Uninstallation

Double-click `Uninstall.cmd`, or:

- **Programs and Features** → *Opencode Tray* → *Uninstall*
- or from the installation folder:

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

Process, shortcuts (Start and Startup), registry entry, installation folder, settings and
opencode plugin are removed. To keep something:

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1 -KeepPlugin -KeepSettings
```

After uninstallation **restart opencode**, otherwise the plugin stays loaded in the
current session (and sends only failed POSTs, ignored).

### Linux Uninstallation

```bash
# disable extension and remove listener/autostart
rm -rf ~/.local/share/gnome-shell/extensions/opencode-tray@local
rm -rf ~/.config/autostart/opencode-tray.desktop
# option: also remove plugin and settings
rm -rf ~/.config/opencode/plugins/opencode-tray.js
rm -rf ~/.config/opencode-tray
# kill listener if still active
kill $(pgrep -f 'opencode-tray-listener.py') 2>/dev/null || true
```

Then reload GNOME Shell (`Alt+F2 r`) if you want to remove the indicator immediately.

## How it works

```
opencode  --(events)-->  opencode-tray.js plugin
                             |
                             |  HTTP POST http://127.0.0.1:47110/state
                             |  { state, reason, cost, models, sessions, pid, project, cwd, ts }
                             v
                      OpencodeTray.exe  --(icon + color)-->  notification area
```

- The plugin listens to session events (`session.status`, `session.idle`,
  `message.updated`, `message.part.updated`, `permission.updated`, `permission.replied`,
  `session.error`, `session.created/deleted`) and hooks `tool.execute.*` / `permission.ask`.
  It keeps a state per session and sends the most "urgent" one.
- The plugin sends one line per **session** (`sessions`): id, project, folder, status,
  activity (tool running), 24h/7d cost and tokens per model. The tray groups by
  project and calculates totals as difference from last received value: if opencode restarts,
  total never goes backward.
- **Yellow is an open request, not a "I finished"**: it stays on until the question, permission
  or error has been satisfied. If opencode has simply finished writing and is waiting for the
  next message, the icon stays **black**; yellow turns on only when your action is really needed.
- Every 4 seconds the plugin sends a heartbeat: it tells the tray app that opencode is alive.
  If heartbeats disappear (opencode closed) for `heartbeatTimeoutSec`, the icon turns black.
- The tray app is a minimal TCP server on `127.0.0.1` (no `HttpListener`, so no URL ACL and no admin)
  and a single instance (mutex).
- If there are multiple opencode open, the icon shows the worst state and the menu shows how
  many instances are connected.

### Project structure

```
silentnotification_opencode/
├── Install.cmd / Uninstall.cmd     convenient wrappers
├── install.ps1                     installer
├── uninstall.ps1                   uninstaller (also copied to install folder)
├── build.ps1                       build (csc.exe of .NET Framework)
├── install-linux.sh                 Linux installer (GNOME)
├── linux-start.sh                   manual listener + extension start
├── plugin/opencode-tray.js         opencode plugin (copied to ~/.config/opencode/plugins/; unchanged on Linux)
├── src/OpencodeTray.cs             Windows tray app: icon, states, menu, server
├── gnome-extension/                GNOME Shell extension + Python listener + cost-info-window
├── dist/                           compiled executable (build.ps1)
└── readme.md                       this file
```

Log: `%APPDATA%\opencode-tray\tray.log` (state changes and connections/disconnections).

### State files (why costs don't get lost)

| File | Content | Pruning |
| --- | --- | --- |
| `%APPDATA%\opencode-tray\plugin-state.txt` | opencode sessions, tokens and costs per model, hourly baskets, already counted steps | sessions > 45 days, baskets > 31 days, steps > 14 days |
| `%APPDATA%\opencode-tray\cost-base.txt` | reset bases for "Reset cost count" | manual (rewritten on each reset) |

The plugin saves `plugin-state.txt` every 20 seconds (atomic write) and reads it back
at startup: **restarting opencode does NOT reset the total to zero** and already counted
steps are not counted twice. The tray app saves reset bases: if restarted, counting resumes
from where it was reset.

## Diagnostics

```powershell
# process and port
Get-Process OpencodeTray
Get-NetTCPConnection -LocalPort 47110 -State Listen

# current state
Get-Content $env:APPDATA\opencode-tray\tray.log -Tail 20

# test icons without opencode (attention for 15 seconds)
& "$env:LOCALAPPDATA\Programs\opencode-tray\OpencodeTray.exe" --test-attention
```

If the port is occupied by another instance the app reports it in log and tooltip
(`port 47110 occupied`): to change it, modify `port` in `settings.json` **and** the
`OPENCODE_TRAY_PORT` environment variable of the process running opencode.

## Notes / known issues

- **Plugin must be reloaded**: after every change to `opencode-tray.js` restart opencode.
- **Antivirus**: some products (e.g. Sophos Intercept X) block the executable right after
  compilation. For this `build.ps1` does not use `/optimize+` and checks that the file is
  readable; if it stays blocked, add the installation folder to exclusions.
- **Windows popup**: if in future you activate the balloon and it doesn't appear, check
  *Settings → System → Notifications*.
- **Multiple instances**: the tray app allows only one (mutex `Local\OpencodeTray.SingleInstance`).

## Quick diagnostics

```powershell
# open cost window without going through menu
& "$env:LOCALAPPDATA\Programs\opencode-tray\OpencodeTray.exe" --show-costs

# open icon menu
& "$env:LOCALAPPDATA\Programs\opencode-tray\OpencodeTray.exe" --show-menu
```

Done: menu without test items, cost estimate reformatted with weekly projection,
costs attributed per session/project, project list in menu and openrouter tokens excluded from estimate.
