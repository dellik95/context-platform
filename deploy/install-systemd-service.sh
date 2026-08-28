#!/usr/bin/env bash
# Installs Context.Host as a systemd service instance for one project.
#
# Usage:
#   sudo ./install-systemd-service.sh --project-root /path/to/repo --name myproject [--port 47923]
#
# Prerequisite: a self-contained publish of Context.Host at /opt/context-platform, e.g.:
#   dotnet publish apps/host/Context.Host -c Release -r linux-x64 --self-contained \
#     -p:PublishSingleFile=true -o /opt/context-platform
set -euo pipefail

PORT=47923
NAME=""
PROJECT_ROOT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --project-root) PROJECT_ROOT="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$PROJECT_ROOT" || -z "$NAME" ]]; then
  echo "Usage: $0 --project-root <path> --name <instance-name> [--port <port>]" >&2
  exit 1
fi

if [[ ! -x /opt/context-platform/Context.Host ]]; then
  echo "error: /opt/context-platform/Context.Host not found or not executable." >&2
  echo "Publish it first: dotnet publish apps/host/Context.Host -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /opt/context-platform" >&2
  exit 1
fi

install -d -m 0755 /etc/context-platform
ENV_FILE="/etc/context-platform/${NAME}.env"
cat > "$ENV_FILE" <<EOF
CONTEXT_PLATFORM_ROOT=${PROJECT_ROOT}
CONTEXT_PLATFORM_PORT=${PORT}
EOF
chmod 0640 "$ENV_FILE"

cp "$(dirname "$0")/context-platform.service" /etc/systemd/system/context-platform@.service

systemctl daemon-reload
systemctl enable --now "context-platform@${NAME}.service"

echo "Installed and started context-platform@${NAME}.service"
echo "Logs: journalctl -u context-platform@${NAME}.service -f"
