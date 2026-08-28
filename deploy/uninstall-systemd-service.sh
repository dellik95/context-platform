#!/usr/bin/env bash
# Stops and removes a Context.Host systemd instance created by install-systemd-service.sh.
#
# Usage: sudo ./uninstall-systemd-service.sh --name myproject
set -euo pipefail

NAME=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --name) NAME="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$NAME" ]]; then
  echo "Usage: $0 --name <instance-name>" >&2
  exit 1
fi

systemctl disable --now "context-platform@${NAME}.service" 2>/dev/null || true
rm -f "/etc/context-platform/${NAME}.env"
systemctl daemon-reload

echo "Removed context-platform@${NAME}.service"
