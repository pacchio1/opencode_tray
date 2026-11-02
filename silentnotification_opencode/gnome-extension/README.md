# Linux / GNOME port of opencode-tray

## What it does
Linux replica (GNOME Shell extension) of all behaviors extracted from
`src/OpencodeTray.cs` without touching opencode app logic.

## Architettura
- **Plugin** (`plugin/opencode-tray.js`): **unchanged**. Continues to POST to `127.0.0.1:47110`.
- **Listener** (`listener/opencode-tray-listener.py`): listens on port, writes `/tmp/opencode-tray-state.json`.
- **Extension** (`extension.js`): indicator in panel (left of clock), menu with statuses, costs, projects, toggle, exit.

## Comportamenti estratti da OpencodeTray.cs
- Icona colorata: nero (idle), verde (working), giallo (attention)
- Menu: stato + numero istanze, stima costi, elenco progetti (attivi/fermi), azzera costi, info costi
- Toggle: popup, suono, flash, avvia con sessione
- Apri cartelle: configurazione opencode, installazione, configurazione tray
- Exit / disattivazione
- Lettura costi da `~/.cache/opencode/models.json` (stesso path Linux)
- File di stato atomico (`/tmp/...`) per non perdere i totali al riavvio

## Installazione rapida
```bash
# 1. Copia l'estensione
mkdir -p ~/.local/share/gnome-shell/extensions/opencode-tray@local
cp -r gnome-extension/* ~/.local/share/gnome-shell/extensions/opencode-tray@local/
chmod +x ~/.local/share/gnome-shell/extensions/opencode-tray@local/listener/*.py

# 2. Avvia listener (in background)
python3 ~/.local/share/gnome-shell/extensions/opencode-tray@local/listener/opencode-tray-listener.py &

# 3. Reload GNOME Shell (Alt+F2 -> r on X11; on Wayland disconnect and reconnect)
# 4. Activate extension with GNOME Tweaks / Extensions or via gsettings
```

## Note
- To place the indicator exactly left of the clock: `Main.panel.addToStatusArea(..., 0, "right")`; if the clock is in the center a different index may be needed depending on GNOME version.
- Full cost estimation (tokens, weekly/monthly projections) requires parsing the `models` field sent by the plugin; `cost-info-window.py` shows current state as a first step.
- No files have been added to `opencode` logic or the JS plugin (as requested).
