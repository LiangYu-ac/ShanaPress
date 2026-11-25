#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$HOME/.local/opt/shanapress"
BIN_DIR="$HOME/.local/bin"
APPS_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"

mkdir -p "$APP_DIR" "$BIN_DIR" "$APPS_DIR" "$ICON_DIR"
cp -a "$SCRIPT_DIR/." "$APP_DIR/"
chmod +x "$APP_DIR/ShanaPress"
ln -sf "$APP_DIR/ShanaPress" "$BIN_DIR/shanapress"
cp "$SCRIPT_DIR/shanapress.png" "$ICON_DIR/shanapress.png"
sed "s|Exec=shanapress %F|Exec=$BIN_DIR/shanapress %F|" "$SCRIPT_DIR/shanapress.desktop" > "$APPS_DIR/shanapress.desktop"
update-desktop-database "$APPS_DIR" 2>/dev/null || true

echo "Installed ShanaPress to $APP_DIR"
echo "Launcher created at $APPS_DIR/shanapress.desktop"
