#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    echo "Run this installer as root." >&2
    exit 1
fi

SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_USER="staffmessenger"
SERVICE_GROUP="staffmessenger"

if ! getent group "$SERVICE_GROUP" >/dev/null; then
    groupadd --system "$SERVICE_GROUP"
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    NOLOGIN_SHELL="$(command -v nologin || printf '/sbin/nologin')"
    useradd \
        --system \
        --gid "$SERVICE_GROUP" \
        --home-dir /var/lib/staffmessenger \
        --shell "$NOLOGIN_SHELL" \
        "$SERVICE_USER"
fi

install -d -m 0755 /opt/staffmessenger
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" /var/lib/staffmessenger
install -d -m 0750 -o root -g "$SERVICE_GROUP" /etc/staffmessenger
install -m 0755 "$SOURCE_DIR/StaffMessenger.Server" /opt/staffmessenger/StaffMessenger.Server
install -m 0644 "$SOURCE_DIR/staffmessenger.service" /etc/systemd/system/staffmessenger.service

if [[ ! -f /etc/staffmessenger/appsettings.Production.json ]]; then
    install -m 0640 -o root -g "$SERVICE_GROUP" \
        "$SOURCE_DIR/appsettings.Production.json" \
        /etc/staffmessenger/appsettings.Production.json
fi

systemctl daemon-reload
systemctl enable staffmessenger.service

echo "Edit /etc/staffmessenger/appsettings.Production.json, then run:"
echo "  systemctl start staffmessenger"
echo "  curl http://127.0.0.1:5072/health"
