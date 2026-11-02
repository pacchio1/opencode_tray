# AGENTS.md — OpenCode Tray (repo root)

Project live in `silentnotification_opencode/`. Full agent instruction file (rules, verification, architecture) is `silentnotification_opencode/AGENTS.md`.

## Architettura (dettagli operativi — aggiunti per richiesta)

- **Ponte condiviso**: `silentnotification_opencode/plugin/opencode-tray.js`. Unico punto di contatto con OpenCode. Esporta v2 (`id`, `setup(ctx)` con `ctx.event.subscribe()` + `ctx.tool.hook("execute.before")`) e mantiene `server()` per v1. Invia POST JSON a `127.0.0.1:47110` (env `OPENCODE_TRAY_PORT`). Se cambi formato/al protocollo, aggiorna entrambi i listener (Windows + GNOME).
- **Windows tray**: `silentnotification_opencode/src/OpencodeTray.cs` (.NET Framework 4.x, zero dipendenze esterne). Compilato da `build.ps1` -> `dist/`; installato da `install.ps1` in `%LOCALAPPDATA%\Programs\opencode-tray`. Legge prezzi da `~/.cache/opencode/models.json` (stesso path su Linux).
- **Linux GNOME**: listener Python (`gnome-extension/listener/opencode-tray-listener.py`) ascolta stessa porta, scrive atomico `/tmp/opencode-tray-state.json` (rename). Estensione `extension.js` legge file; `cost-info-window.py` apre finestra costi. Il plugin NON cambia tra piattaforme.
- **Estensione — "Esci" (menu)**: etichetta aggiornata a `Esci` in `extension.js`; chiama `this._ext.disable()`. Non fa logout né rimuove file globali.
- **Estensione — indice configurabile su sinistra**: `POSITIONS.left` ora usa `section: 'right', index: 0`; l'indice può essere sovrascritto in `settings.json` (`"index": N`) tramite `_moveTo`.
- **Estensione — colore fisso (no lampeggio)**: `_startFlash()` disattivato; icona solida, senza lampeggio anche in attenzione; `settings.json` `flash` non ha più effetto.
- **Stato sessioni**: plugin tiene in memoria `sessionID` -> `{state, reason, pending, cwd, project, activity, lastModel}`. Raggruppa per cartella progetto (`cwd`). `pending` forza `attention` anche a fine turno finché non soddisfatta; il giallo non si spegne da solo.
- **Stima costi**: solo token provider `opencode` / `opencode-go`; esclusi quelli `openrouter`. Modelli gratuiti (`-free`) valutati con gemello a pagamento (es. `minimax-m3.1-flash-free` -> `minimax-m3`). Prezzi ricaricati quando `~/.cache/opencode/models.json` si aggiorna.
- **Installazione**: richiede opencode già installato (`https://opencode.ai/v2/install`). Plugin copiato in `~/.config/opencode/plugins/`; **dopo installare riavvia opencode** (caricamento una sola volta all'avvio). Nessun admin richiesto (`HKCU`, `%LOCALAPPDATA%`, `~/.local/share/`).
- **Comandi chiave Windows**: `powershell -ExecutionPolicy Bypass -File build.ps1`; `install.ps1 -Port 47110 -NoStartWithWindows -NoPlugin -Force`. **Comandi Linux**: `bash silentnotification_opencode/install-linux.sh` (installa estensione + plugin + settings); `bash silentnotification_opencode/linux-start.sh` (avvia listener, abilita estensione). Ricarica GNOME Shell con `Alt+F2 r` (X11) o disconnetti/rientra (Wayland).
- **Backup plugin**: `plugin/opencode-tray.js.bak` esiste; non cancellare senza verificare origine.
- **Porta/heartbeat**: plugin invia POST ogni `HEARTBEAT_MS` (4000); listener Windows gestisce timeout con `heartbeatTimeoutSec` (default 25s). Se il tray non è in esecuzione il POST fallisce e viene ignorato.

For rules, verification steps, and full context see `silentnotification_opencode/AGENTS.md`.
