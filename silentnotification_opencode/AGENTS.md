# Agent Instructions

## Orientation

This repository contains **OpenCode Tray**, a status and cost indicator linked to OpenCode. It is not the OpenCode application and does not install or configure its models.

- `plugin/opencode-tray.js`: bridge between OpenCode events and the local listener. The v2 API must export a definition with `id` and `setup(ctx)`; `server()` maintains compatibility with v1 plugins. The plugin uses port `47110` to send state.
- `src/OpencodeTray.cs`: Tray application for Windows.
- `install.ps1`, `uninstall.ps1`, `build.ps1`: installation, removal, and compilation for Windows.
- `gnome-extension/`: GNOME extension and Python listener for Linux.
- `install-linux.sh`, `linux-start.sh`: installation and startup of the Linux variant.
- `readme.md`: requirements, installation, and usage; `gnome-extension/README.md`: GNOME variant details.

## Work Rules

- Before changing a component, read the relevant README and identify source files and installation scripts.
- Keep the shared bridge (`plugin/`) separate from the two platform indicators (Windows and GNOME). Do not change HTTP protocol or state format without updating both listeners.
- If you touch the plugin, verify compatibility with the actually required OpenCode API: v2 registers events and hooks via `setup(ctx)`; just adding `id` to v1 export is not enough.
- Do not modify or remove OpenCode, providers, models, credentials, or user data when the task concerns OpenCode Tray. Global installations in `~/.config/opencode/` and `~/.local/share/` are outside the repository: act there only if explicitly requested.
- Do not copy or print credentials and authentication file contents. In shared logs include only relevant lines, redacting any secrets.
- Do not delete backups, untracked files, or existing modifications without first identifying their origin and necessity.
- For plugin modifications, distinguish plugin loading errors from authentication or model availability problems: check server logs and model catalog before attributing the cause.

## Architecture (operational details)

- **Single bridge**: `plugin/opencode-tray.js`. Exports v2 (`id`, `setup(ctx)`) with `ctx.event.subscribe()` and `ctx.tool.hook("execute.before")`; keeps `server()` for v1. Uses POST JSON on `127.0.0.1:47110` (env `OPENCODE_TRAY_PORT`). If you change protocol/format, update both listeners (Windows + GNOME).
- **Windows tray**: `src/OpencodeTray.cs` (.NET Framework 4.x, zero external dependencies). Compiled by `build.ps1` -> `dist/`; installed by `install.ps1` in `%LOCALAPPDATA%\Programs\opencode-tray`. Reads prices from `~/.cache/opencode/models.json` (same path on Linux).
- **Linux GNOME**: Python listener (`gnome-extension/listener/opencode-tray-listener.py`) listens on the same port, writes atomic `/tmp/opencode-tray-state.json` (rename). Extension `extension.js` reads the file; `cost-info-window.py` opens cost window. The plugin does NOT change between platforms.
- **Extension — "Exit" (menu)**: label updated to `Esci` in `extension.js`; calls `this._ext.disable()`. Does not log out or remove global files.
- **Extension — configurable index on left**: `POSITIONS.left` now uses `section: 'right', index: 0` (as per README). Index can be overridden via `settings.json` (`"index": N`) and is read by `_moveTo`; thus left position is configurable even on index. After changes to `extension.js`: reload GNOME Shell (`Alt+F2 r` or disable/enable).
- **Extension — fixed color (no blink)**: `_startFlash()` commented out; icon stays solid color (black/green/yellow) even in attention.
- **Session state**: plugin keeps in memory `sessionID` -> `{state, reason, pending, cwd, project, activity, lastModel}`. Groups by project folder (`cwd`). `pending` forces `attention` even at end of turn until satisfied; yellow does not turn off by itself.
- **Cost estimation**: only token providers `opencode` / `opencode-go`; `openrouter` ones excluded. Free models (`-free`) evaluated with paid twin (e.g. `minimax-m3.1-flash-free` -> `minimax-m3`). Prices reloaded when `~/.cache/opencode/models.json` updates.
- **Installation**: requires opencode already installed (`https://opencode.ai/v2/install`). Plugin copied to `~/.config/opencode/plugins/`; **after installing restart opencode** (loaded once at startup). No admin required (`HKCU`, `%LOCALAPPDATA%`, `~/.local/share/`).
- **Key Windows commands**: `powershell -ExecutionPolicy Bypass -File build.ps1`; `install.ps1 -Port 47110 -NoStartWithWindows -NoPlugin -Force`. **Linux commands**: `bash install-linux.sh` (copies extension, installs plugin to `~/.config/opencode/plugins/`, creates settings); `bash linux-start.sh` (starts listener on 47110 if not active, enables GNOME extension). For manual reload: `Alt+F2 r` on X11; on Wayland disconnect/reconnect.
- **Plugin backup**: `plugin/opencode-tray.js.bak` exists; do not delete without verifying origin.
- **Port/heartbeat**: plugin sends POST every `HEARTBEAT_MS` (4000); Windows listener handles timeout with `heartbeatTimeoutSec` (default 25s). If tray is not running the POST fails and is ignored.

## Verification

- For documentation or script changes, check diff and use `git diff --check`.
- For plugin changes, verify syntax, loading in OpenCode server, and event reception before declaring a runtime error resolved.
- For Linux variant changes, check consistency between installer, listener, and GNOME extension directory.
- Do not install or remove global software as part of a normal change to this repository.

## Restart

- To restart the shared OpenCode server (downloads and reloads plugins): `opencode service restart`.
- To restart the OpenCode Tray GNOME extension: `gnome-extensions disable opencode-tray@local` then `gnome-extensions enable opencode-tray@local`.
- If GNOME does not update the indicator, check state with `gnome-extensions info opencode-tray@local`. On Wayland, if status is active but indicator does not update, disconnect and reconnect to the GNOME session.
