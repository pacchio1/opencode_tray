#!/bin/bash
# start-opencode-tray-linux.sh
# Avvia listener + abilita estensione GNOME (se sessione attiva)

set -euo pipefail

EXT_DIR="$HOME/.local/share/gnome-shell/extensions/opencode-tray@local"
LISTENER="$EXT_DIR/listener/opencode-tray-listener.py"

# 1. Listener (se non già attivo)
if ! ss -tlnp 2>/dev/null | grep -q ':47110'; then
    echo "[start] Avvio listener su 47110..."
    nohup python3 "$LISTENER" > /tmp/listener_start.log 2>&1 &
    sleep 1
else
    echo "[start] Listener già su 47110"
fi

# 2. Abilita estensione GNOME (se disponibile)
if command -v gnome-extensions &>/dev/null; then
    echo "[start] Abilito estensione..."
    gnome-extensions enable opencode-tray@local || true
fi

# 3. Se vuoi forzare colore visibile (verde) per test:
# sed -i 's/let fileName = "icon-idle"/let fileName = "icon-working"/' "$EXT_DIR/extension.js" || true

echo "[start] Fatto. Verifica indicatore a sinistra dell'orologio."
echo "        Se non compare: il pannello potrebbe aver bisogno di Alt+F2 r"
