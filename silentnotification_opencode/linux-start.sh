#!/bin/bash
# start-opencode-tray-linux.sh
# Starts listener + enables GNOME extension (if session active)

set -euo pipefail

EXT_DIR="$HOME/.local/share/gnome-shell/extensions/opencode-tray@local"
LISTENER="$EXT_DIR/listener/opencode-tray-listener.py"

# 1. Listener (if not already active)
if ! ss -tlnp 2>/dev/null | grep -q ':47110'; then
    echo "[start] Starting listener on 47110..."
    nohup python3 "$LISTENER" > /tmp/listener_start.log 2>&1 &
    sleep 1
else
    echo "[start] Listener already on 47110"
fi

# 2. Enable GNOME extension (if available)
if command -v gnome-extensions &>/dev/null; then
    echo "[start] Enabling extension..."
    gnome-extensions enable opencode-tray@local || true
fi

# 3. If you want to force visible color (green) for testing:
# sed -i 's/let fileName = "icon-idle"/let fileName = "icon-working"/' "$EXT_DIR/extension.js" || true

echo "[start] Done. Check indicator left of clock."
echo "        If not visible: panel may need Alt+F2 r"
