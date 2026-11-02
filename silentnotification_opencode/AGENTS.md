# Istruzioni per gli agenti

## Orientamento

Questo repository contiene **OpenCode Tray**, un indicatore di stato e costi collegato a OpenCode. Non è l'applicazione OpenCode e non ne installa o configura i modelli.

- `plugin/opencode-tray.js`: ponte tra gli eventi OpenCode e il listener locale. L'API v2 deve esportare una definizione con `id` e `setup(ctx)`; `server()` mantiene la compatibilità con plugin v1. Il plugin usa la porta `47110` per inviare lo stato.
- `src/OpencodeTray.cs`: applicazione Tray per Windows.
- `install.ps1`, `uninstall.ps1`, `build.ps1`: installazione, rimozione e compilazione per Windows.
- `gnome-extension/`: estensione GNOME e listener Python per Linux.
- `install-linux.sh`, `linux-start.sh`: installazione e avvio della variante Linux.
- `readme.md`: requisiti, installazione e uso; `gnome-extension/README.md`: dettagli della variante GNOME.

## Regole di lavoro

- Prima di cambiare un componente, leggi il relativo README e individua i file sorgente e gli script che lo installano.
- Mantieni distinto il ponte condiviso (`plugin/`) dai due indicatori di piattaforma (Windows e GNOME). Non cambiare il protocollo HTTP o il formato dello stato senza aggiornare entrambi i listener.
- Se tocchi il plugin, verifica la compatibilità con l'API OpenCode effettivamente richiesta: la v2 registra eventi e hook tramite `setup(ctx)`; non basta aggiungere `id` all'export v1.
- Non modificare né rimuovere OpenCode, i provider, i modelli, le credenziali o i dati utente quando l'attività riguarda OpenCode Tray. Le installazioni globali in `~/.config/opencode/` e `~/.local/share/` sono fuori dal repository: agisci lì solo se richiesto esplicitamente.
- Non copiare o stampare credenziali e contenuti di file di autenticazione. Nei log condividi solo le righe pertinenti, oscurando eventuali segreti.
- Non eliminare backup, file non tracciati o modifiche già presenti senza prima identificarne la provenienza e la necessità.
- Per le modifiche al plugin, distingui l'errore di caricamento del plugin dai problemi di autenticazione o disponibilità dei modelli: controlla i log del server e il catalogo modelli prima di attribuire la causa.

## Architettura (dettagli operativi)

- **Ponte unico**: `plugin/opencode-tray.js`. Esporta v2 (`id`, `setup(ctx)`) con `ctx.event.subscribe()` e `ctx.tool.hook("execute.before")`; mantiene `server()` per v1. Usa POST JSON su `127.0.0.1:47110` (env `OPENCODE_TRAY_PORT`). Se cambi protocollo/formato, aggiorna entrambi i listener (Windows + GNOME).
- **Windows tray**: `src/OpencodeTray.cs` (.NET Framework 4.x, zero dipendenze esterne). Compilato da `build.ps1` -> `dist/`; installato da `install.ps1` in `%LOCALAPPDATA%\Programs\opencode-tray`. Legge prezzi da `~/.cache/opencode/models.json` (stesso path Linux).
- **Linux GNOME**: listener Python (`gnome-extension/listener/opencode-tray-listener.py`) ascolta la stessa porta, scrive atomico `/tmp/opencode-tray-state.json` (rename). Estensione `extension.js` legge il file; `cost-info-window.py` apre finestra costi. Il plugin NON cambia tra piattaforme.
- **Estensione — "Esci" (menu)**: etichetta aggiornata a `Esci` in `extension.js`; chiama `this._ext.disable()`. Non fa logout né rimuove file globali.
- **Estensione — indice configurabile su sinistra**: `POSITIONS.left` ora usa `section: 'right', index: 0` (come README). L'indice si può sovrascrivere via `settings.json` (`"index": N`) e viene letto da `_moveTo`; così la posizione sinistra è configurabile anche sull'indice. Dopo modifiche a `extension.js`: ricarica GNOME Shell (`Alt+F2 r` o disable/enable).
- **Estensione — colore fisso (no lampeggio)**: `_startFlash()` commentato; l'icona resta a colore solido (nero/verde/giallo) anche in attenzione.
- **Stato sessioni**: il plugin tiene in memoria `sessionID` -> `{state, reason, pending, cwd, project, activity, lastModel}`. Raggruppa per cartella progetto (`cwd`). `pending` forza `attention` anche a fine turno finché non soddisfatta; il giallo non si spegne da solo.
- **Stima costi**: solo token provider `opencode` / `opencode-go`; esclusi quelli `openrouter`. Modelli gratuiti (`-free`) valutati con gemello a pagamento (es. `minimax-m3.1-flash-free` -> `minimax-m3`). Prezzi ricaricati quando `~/.cache/opencode/models.json` si aggiorna.
- **Installazione**: richiede opencode già installato (`https://opencode.ai/v2/install`). Plugin copiato in `~/.config/opencode/plugins/`; **dopo installare riavvia opencode** (caricamento una sola volta all'avvio). Nessun admin richiesto (`HKCU`, `%LOCALAPPDATA%`, `~/.local/share/`).
- **Comandi chiave Windows**: `powershell -ExecutionPolicy Bypass -File build.ps1`; `install.ps1 -Port 47110 -NoStartWithWindows -NoPlugin -Force`. **Comandi Linux**: `bash install-linux.sh` (copia estensione, installa plugin in `~/.config/opencode/plugins/`, crea settings); `bash linux-start.sh` (avvia listener su 47110 se non attivo, abilita estensione GNOME). Per ricarica manuale: `Alt+F2 r` su X11; su Wayland disconnetti/rientra.
- **Backup plugin**: `plugin/opencode-tray.js.bak` esiste; non cancellare senza verificare origine.
- **Porta/heartbeat**: plugin invia POST ogni `HEARTBEAT_MS` (4000); listener Windows gestisce timeout con `heartbeatTimeoutSec` (default 25s). Se il tray non è in esecuzione il POST fallisce e viene ignorato.

## Verifica

- Per modifiche alla documentazione o agli script, controlla il diff e usa `git diff --check`.
- Per modifiche al plugin, verifica sintassi, caricamento nel server OpenCode e ricezione degli eventi prima di dichiarare risolto un errore runtime.
- Per modifiche alla variante Linux, controlla coerenza tra installer, listener e directory dell'estensione GNOME.
- Non installare o rimuovere software globale come parte di una normale modifica a questo repository.

## Riavvio

- Per riavviare il server condiviso di OpenCode (scarica e ricarica i plugin): `opencode service restart`.
- Per riavviare l'estensione GNOME OpenCode Tray: `gnome-extensions disable opencode-tray@local` e poi `gnome-extensions enable opencode-tray@local`.
- Se GNOME non aggiorna l'indicatore, verifica lo stato con `gnome-extensions info opencode-tray@local`. Su Wayland, se lo stato è attivo ma l'indicatore non si aggiorna, disconnettersi e rientrare nella sessione GNOME.
