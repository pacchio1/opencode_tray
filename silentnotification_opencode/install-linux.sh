#!/bin/bash
# install-linux.sh - Linux installer for opencode-tray (GNOME extension)
# Usa il proprio percorso come base (funziona da qualsiasi cwd)

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

EXT_UUID="opencode-tray@local"
EXT_DIR="$HOME/.local/share/gnome-shell/extensions/$EXT_UUID"
PLUGIN_SRC="$SCRIPT_DIR/plugin/opencode-tray.js"
PLUGIN_DST="$HOME/.config/opencode/plugins/opencode-tray.js"
SETTINGS_DIR="$HOME/.config/opencode-tray"
SETTINGS_FILE="$SETTINGS_DIR/settings.json"
LISTENER_PY="$EXT_DIR/listener/opencode-tray-listener.py"

echo -e "${GREEN}=== Opencode Tray - Install Linux ===${NC}"

# 1. Prerequisiti
if [ -z "${XDG_SESSION_TYPE:-}" ]; then
    echo -e "${RED}Attenzione: non in sessione desktop (XDG_SESSION_TYPE vuoto)${NC}"
fi
if ! command -v python3 &>/dev/null; then
    echo -e "${RED}python3 mancante${NC}"; exit 1
fi

# 2. Copy extension (clean)
mkdir -p "$EXT_DIR"
cp -r "$SCRIPT_DIR/gnome-extension/"* "$EXT_DIR/"
# Rimuovi manifest se presente, assicurati metadata.json
if [ -f "$EXT_DIR/manifest.json" ]; then
    mv "$EXT_DIR/manifest.json" "$EXT_DIR/metadata.json" || true
fi
chmod +x "$LISTENER_PY" 2>/dev/null || true
echo "[install] Extension installed to $EXT_DIR"

# 3. Impostazioni (default come Windows)
mkdir -p "$SETTINGS_DIR"
cat > "$SETTINGS_FILE" << 'EOF'
{
  "port": 47110,
  "balloon": false,
  "sound": false,
  "flash": false,
  "heartbeatTimeoutSec": 25,
  "startWithSession": true
}
EOF
echo "[install] Impostazioni: $SETTINGS_FILE"

# 4. Plugin opencode (se presente nella repo)
if [ -f "$PLUGIN_SRC" ]; then
    mkdir -p "$HOME/.config/opencode/plugins"
    cp "$PLUGIN_SRC" "$PLUGIN_DST"
    echo "[install] Plugin opencode: $PLUGIN_DST"
else
    echo "[install] Plugin source not found (optional, restart opencode to reload)"
fi

# 5. Autostart (equivalente startWithWindows)
mkdir -p "$HOME/.config/autostart"
cat > "$HOME/.config/autostart/opencode-tray.desktop" << EOF
[Desktop Entry]
Type=Application
Name=OpenCode Tray
Exec=bash -c "python3 $LISTENER_PY &"
Hidden=false
X-GNOME-Autostart-enabled=true
EOF
echo "[install] Autostart: $HOME/.config/autostart/opencode-tray.desktop"

# 6. Start listener now
if ! ss -tlnp 2>/dev/null | grep -q ':47110'; then
    nohup python3 "$LISTENER_PY" > /tmp/opencode-tray-listener.log 2>&1 &
    echo "[install] Listener started (PID $!)"
else
    echo "[install] Listener already active on 47110"
fi

# 7. Enable extension (if GNOME session active)
if command -v gnome-extensions &>/dev/null; then
    gnome-extensions enable "$EXT_UUID" 2>/dev/null || true
    echo "[install] Extension enabled (reload with Alt+F2 r if not visible)"
fi

echo -e "${GREEN}[install] Done.${NC}"
echo "    Estensione: $EXT_DIR"
echo "    Listener:   $LISTENER_PY"
echo "    Plugin:     $PLUGIN_DST"
echo "    Settings:   $SETTINGS_FILE"
echo "    Autostart:  $HOME/.config/autostart/opencode-tray.desktop"
echo ""
echo "To uninstall: run install-linux.sh with -uninstall or remove manually."
