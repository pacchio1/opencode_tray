# AGENTS.md — OpenCode Tray (repo root)

Project lives in `silentnotification_opencode/`. Full agent instruction file (rules, verification, architecture) is `silentnotification_opencode/AGENTS.md`.

## Architecture (operational details — added on request)

- **Shared bridge**: `silentnotification_opencode/plugin/opencode-tray.js`. The only contact point with OpenCode. Exports v2 (`id`, `setup(ctx)` with `ctx.event.subscribe()` + `ctx.tool.hook("execute.before")`) and keeps `server()` for v1. Sends POST JSON to `127.0.0.1:47110` (env `OPENCODE_TRAY_PORT`). If you change format/protocol, update both listeners (Windows + GNOME).
- **Windows tray**: `silentnotification_opencode/src/OpencodeTray.cs` (.NET Framework 4.x, zero external dependencies). Compiled by `build.ps1` -> `dist/`; installed by `install.ps1` in `%LOCALAPPDATA%\Programs\opencode-tray`. Reads prices from `~/.cache/opencode/models.json` (same path on Linux).
- **Linux GNOME**: Python listener (`gnome-extension/listener/opencode-tray-listener.py`) listens on the same port, writes atomic `/tmp/opencode-tray-state.json` (rename). Extension `extension.js` reads file; `cost-info-window.py` opens cost window. The plugin does NOT change between platforms.
- **Extension — "Exit" (menu)**: label updated to `Esci` in `extension.js`; calls `this._ext.disable()`. Does not log out or remove global files.
- **Extension — configurable index on left**: `POSITIONS.left` now uses `section: 'right', index: 0`; index can be overridden in `settings.json` (`"index": N`) via `_moveTo`.
- **Extension — fixed color (no blink)**: `_startFlash()` disabled; solid icon, no blink even in attention; `settings.json` `flash` no longer has any effect.
- **Session state**: plugin keeps in memory `sessionID` -> `{state, reason, pending, cwd, project, activity, lastModel}`. Groups by project folder (`cwd`). `pending` forces `attention` even at end of turn until satisfied; yellow does not turn off by itself.
- **Cost estimation**: only token providers `opencode` / `opencode-go`; `openrouter` ones excluded. Free models (`-free`) evaluated with paid twin (e.g. `minimax-m3.1-flash-free` -> `minimax-m3`). Prices reloaded when `~/.cache/opencode/models.json` updates.
- **Installation**: requires opencode already installed (`https://opencode.ai/v2/install`). Plugin copied to `~/.config/opencode/plugins/`; **after installing restart opencode** (loaded once at startup). No admin required (`HKCU`, `%LOCALAPPDATA%`, `~/.local/share/`).
- **Key Windows commands**: `powershell -ExecutionPolicy Bypass -File build.ps1`; `install.ps1 -Port 47110 -NoStartWithWindows -NoPlugin -Force`. **Linux commands**: `bash silentnotification_opencode/install-linux.sh` (installs extension + plugin + settings); `bash silentnotification_opencode/linux-start.sh` (starts listener, enables extension). Reload GNOME Shell with `Alt+F2 r` (X11) or disconnect/reconnect (Wayland).
- **Plugin backup**: `plugin/opencode-tray.js.bak` exists; do not delete without verifying origin.
- **Port/heartbeat**: plugin sends POST every `HEARTBEAT_MS` (4000); Windows listener handles timeout with `heartbeatTimeoutSec` (default 25s). If tray is not running the POST fails and is ignored.

For rules, verification steps, and full context see `silentnotification_opencode/AGENTS.md`.
