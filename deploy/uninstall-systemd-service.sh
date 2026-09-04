#!/usr/bin/env bash
# Stops and removes the context-platform systemd service created by install-systemd-service.sh.
#
# Usage: sudo ./uninstall-systemd-service.sh
set -euo pipefail

systemctl disable --now context-platform.service 2>/dev/null || true
rm -f /etc/systemd/system/context-platform.service
rm -rf /etc/context-platform
systemctl daemon-reload

echo "Removed context-platform.service"
