#!/usr/bin/env bash
# Installs Context.Dashboard.Server as a single, always-on systemd service. The host is
# multi-tenant (projects are added at runtime via POST /api/projects, each with its own root path
# and own file watcher -- see ProjectFileWatcherManager) -- there is no per-project install step any
# more, just one shared instance.
#
# Usage:
#   sudo ./install-systemd-service.sh [--port 47923] [--data-dir /path/to/data]
#
# Prerequisite: a self-contained publish of Context.Dashboard.Server at /opt/context-platform, e.g.:
#   dotnet publish apps/host/Context.Dashboard/Context.Dashboard.Server -c Release -r linux-x64 \
#     --self-contained -p:PublishSingleFile=true -o /opt/context-platform
set -euo pipefail

PORT=""
DATA_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="$2"; shift 2 ;;
    --data-dir) DATA_DIR="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

if [[ ! -x /opt/context-platform/Context.Dashboard.Server ]]; then
  echo "error: /opt/context-platform/Context.Dashboard.Server not found or not executable." >&2
  echo "Publish it first -- see deploy/README.md." >&2
  exit 1
fi

install -d -m 0755 /etc/context-platform
ENV_FILE="/etc/context-platform/context-platform.env"
: > "$ENV_FILE"
[[ -n "$PORT" ]] && echo "CONTEXT_PLATFORM_PORT=${PORT}" >> "$ENV_FILE"
[[ -n "$DATA_DIR" ]] && echo "CONTEXT_PLATFORM_DATA_DIR=${DATA_DIR}" >> "$ENV_FILE"
chmod 0640 "$ENV_FILE"

cp "$(dirname "$0")/context-platform.service" /etc/systemd/system/context-platform.service

systemctl daemon-reload
systemctl enable --now context-platform.service

echo "Installed and started context-platform.service"
echo "Logs: journalctl -u context-platform.service -f"
echo "Add projects via the Dashboard (or POST /api/projects) once it's up."
