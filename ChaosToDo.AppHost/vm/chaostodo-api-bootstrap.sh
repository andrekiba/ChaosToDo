#!/bin/bash
# ChaosToDo API bootstrap for the VM scale set instances.
# Idempotent: cloud-init runs it on first boot (VMSS custom data) and the Aspire
# deploy step re-runs it through Run Command to install the latest package.
# Placeholder tokens (double curly braces) are rendered by vm-api.bicep for custom data
# and by VmApiPublisher for Run Command, from the same deployment values.
set -euo pipefail

STORAGE_ACCOUNT='{{STORAGE_ACCOUNT}}'
PACKAGE_CONTAINER='{{PACKAGE_CONTAINER}}'
CLIENT_ID='{{CLIENT_ID}}'
APP_ROOT=/opt/chaostodo
SERVICE=chaostodo-api

if ! id chaostodo >/dev/null 2>&1; then
  useradd --system --home-dir "$APP_ROOT" --shell /usr/sbin/nologin chaostodo
fi
mkdir -p "$APP_ROOT/releases" /etc/chaostodo

if ! ldconfig -p | grep -q libicuuc; then
  for attempt in 1 2 3 4 5; do
    if apt-get -o DPkg::Lock::Timeout=180 update -q &&
      DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=180 install -y -q libicu74; then
      break
    fi
    sleep 15
  done
fi

cat > /etc/chaostodo/api.env <<'ENV'
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:8080
AZURE_CLIENT_ID={{CLIENT_ID}}
AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential
HealthChecks__ExposeEndpoints=true
Demo__ServedByHeader=true
ConnectionStrings__database="{{SQL_CONNECTION}}"
ConnectionStrings__keyvault={{KEYVAULT_URI}}
DOTNET_PRINT_TELEMETRY_MESSAGE=false
ENV
chmod 0644 /etc/chaostodo/api.env

cat > /etc/systemd/system/$SERVICE.service <<'UNIT'
[Unit]
Description=ChaosToDo API
Wants=network-online.target
After=network-online.target
ConditionPathExists=/opt/chaostodo/current/ChaosToDo.Api

[Service]
User=chaostodo
WorkingDirectory=/opt/chaostodo/current
EnvironmentFile=/etc/chaostodo/api.env
ExecStart=/opt/chaostodo/current/ChaosToDo.Api
Restart=always
RestartSec=2
TimeoutStopSec=15

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

token=$(curl -sf --retry 10 --retry-connrefused --retry-delay 3 -H Metadata:true \
  "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fstorage.azure.com%2F&client_id=${CLIENT_ID}" |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

status=$(curl -s --retry 3 -o "$tmp/api.tar.gz" -w '%{http_code}' \
  -H "Authorization: Bearer $token" -H 'x-ms-version: 2023-11-03' \
  "https://${STORAGE_ACCOUNT}.blob.core.windows.net/${PACKAGE_CONTAINER}/latest.tar.gz")
if [ "$status" = "404" ]; then
  echo "CHAOSTODO_PACKAGE_MISSING $(hostname)"
  exit 0
fi
if [ "$status" != "200" ]; then
  echo "CHAOSTODO_DOWNLOAD_FAILED $(hostname) status=$status" >&2
  exit 1
fi

release="$APP_ROOT/releases/$(date -u +%Y%m%d%H%M%S%N)"
mkdir -p "$release"
tar -xzf "$tmp/api.tar.gz" -C "$release"
chmod +x "$release/ChaosToDo.Api"
chown -R chaostodo:chaostodo "$release"
ln -sfn "$release" "$APP_ROOT/current.tmp"
mv -Tf "$APP_ROOT/current.tmp" "$APP_ROOT/current"
systemctl restart "$SERVICE"
ls -1dt "$APP_ROOT"/releases/* | tail -n +3 | xargs -r rm -rf

for attempt in $(seq 1 60); do
  if curl -sf http://127.0.0.1:8080/alive >/dev/null; then
    echo "CHAOSTODO_DEPLOY_OK $(hostname)"
    exit 0
  fi
  sleep 2
done

echo "CHAOSTODO_DEPLOY_UNHEALTHY $(hostname)" >&2
journalctl -u "$SERVICE" -n 40 --no-pager >&2 || true
exit 1
