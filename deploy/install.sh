#!/bin/sh
# Installs rlt-udp-record as a system service.
#
# Run from the extracted release directory:
#   sudo ./install.sh
#
# Leaves an existing config.json alone, so upgrading never loses your settings.

set -eu

SERVICE_USER=rlt-record
INSTALL_DIR=/opt/rlt-udp-record
CONFIG_DIR=/etc/rlt-udp-record
DATA_DIR=/var/lib/rlt-udp-record
HTTP_PORT=20780

die() {
    echo "error: $*" >&2
    exit 1
}

[ "$(id -u)" -eq 0 ] || die "run this with sudo"
command -v systemctl >/dev/null 2>&1 || die "this installer needs systemd"

SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# Pick the binary matching this machine, so one release archive serves both a
# 64-bit Pi and an ordinary x86 box.
case "$(uname -m)" in
    x86_64|amd64)   BINARY=rlt-udp-record-linux-x64 ;;
    aarch64|arm64)  BINARY=rlt-udp-record-linux-arm64 ;;
    *)              die "unsupported architecture: $(uname -m)" ;;
esac

# Allow a single-binary layout too, for people who built it themselves.
if [ ! -f "$SOURCE_DIR/$BINARY" ]; then
    if [ -f "$SOURCE_DIR/rlt-udp-record" ]; then
        BINARY=rlt-udp-record
    else
        die "$BINARY not found in $SOURCE_DIR"
    fi
fi

echo "Installing $BINARY"

id "$SERVICE_USER" >/dev/null 2>&1 || \
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"

mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$DATA_DIR/dumps"

install -m 0755 "$SOURCE_DIR/$BINARY" "$INSTALL_DIR/rlt-udp-record"

# The service runs with a read-only /etc, so the config has to exist up front
# rather than being created by the recorder on first start.
if [ ! -f "$CONFIG_DIR/config.json" ]; then
    cat > "$CONFIG_DIR/config.json" <<EOF
{
  "port": 20777,
  "output_directory": "$DATA_DIR/dumps",
  "file_prefix": "dump",
  "session_timeout_seconds": 120,
  "receive_buffer_bytes": 4194304,
  "http_enabled": true,
  "http_port": $HTTP_PORT,
  "http_port_fallback": false,
  "mdns_enabled": true,
  "mdns_name": ""
}
EOF
    echo "Wrote $CONFIG_DIR/config.json"
else
    echo "Keeping existing $CONFIG_DIR/config.json"
fi

chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
chmod 0644 "$CONFIG_DIR/config.json"

install -m 0644 "$SOURCE_DIR/rlt-udp-record.service" /etc/systemd/system/rlt-udp-record.service

# Optional: makes the box findable as <hostname>.local and listed in Bonjour
# browsers. Raspberry Pi OS ships avahi already.
if [ -d /etc/avahi/services ]; then
    install -m 0644 "$SOURCE_DIR/rlt-recorder.avahi-service" \
        /etc/avahi/services/rlt-recorder.service
    echo "Registered the mDNS service with avahi"
else
    echo "avahi not installed - the .local address will not resolve, IP still works"
fi

systemctl daemon-reload
systemctl enable rlt-udp-record.service >/dev/null
systemctl restart rlt-udp-record.service

sleep 1
if ! systemctl is-active --quiet rlt-udp-record.service; then
    echo
    echo "The service did not come up. Recent log:" >&2
    journalctl -u rlt-udp-record.service -n 20 --no-pager >&2
    exit 1
fi

IP=$(hostname -I 2>/dev/null | awk '{print $1}')

cat <<EOF

Done. The recorder starts automatically on boot.

  Point the game's telemetry at   ${IP:-this machine}  port 20777
  Recorded files and live status  http://$(hostname).local:$HTTP_PORT/
                                  http://${IP:-127.0.0.1}:$HTTP_PORT/

  Status   systemctl status rlt-udp-record
  Log      journalctl -u rlt-udp-record -f
  Config   $CONFIG_DIR/config.json  (restart the service after editing)
EOF
