#!/bin/sh
# Adds RLT Recorder to the desktop menu for the current user. No sudo needed;
# run it again after moving the folder, since the entry points at this copy.
set -e

HERE=$(cd "$(dirname "$0")" && pwd)
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

mkdir -p "$APPS"
cat > "$APPS/rlt-recorder.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=RLT Recorder
Comment=Record F1 telemetry for Racing League Tools
Exec="$HERE/RltUdpClient"
Icon=$HERE/rlt-recorder.png
Terminal=false
Categories=Game;Utility;
EOF

echo "Added RLT Recorder to the menu: $APPS/rlt-recorder.desktop"
