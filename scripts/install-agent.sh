#!/bin/bash
set -euo pipefail

if [[ $(id -u) != 0 ]]; then
    echo 'Run this installer as root.' >&2
    exit 1
fi
controller=''
args=("$@")
while (($#)); do
    case "$1" in
        --controller-url) controller="${2:?Controller URL is required}"; shift 2 ;;
        --api-key|--cluster-name|--name) [[ $# -ge 2 ]] || { echo 'An option value is missing.' >&2; exit 1; }; shift 2 ;;
        *) echo 'Supported installer options: --controller-url, --api-key, --cluster-name, --name.' >&2; exit 1 ;;
    esac
done
if [[ ! "$controller" =~ ^https?://[^/?\#[:space:]]+/?$ ]] || [[ "$controller" == *@* ]]; then
    echo 'Supply --controller-url with the Controller HTTP(S) origin.' >&2
    exit 1
fi
controller="${controller%/}"
for command in curl tar systemctl; do command -v "$command" >/dev/null || { echo "$command is required." >&2; exit 1; }; done
state=/var/lib/aiursoft-dms-agent
destination=/opt/aiursoft-dms-agent
if systemctl is-active --quiet dms-agent.service && [[ -f "$state/joined" ]]; then
    exec "$destination/Aiursoft.DMS.Agent" join "${args[@]}"
fi
# A pending Agent has no executable machine credential yet. Preserve all enrollment/journal state.
if systemctl is-active --quiet dms-agent.service; then systemctl stop dms-agent.service; fi
case "$(uname -m)" in
    x86_64) arch=linux-x64 ;;
    aarch64) arch=linux-arm64 ;;
    *) echo 'The Agent installer supports Linux x64 and arm64.' >&2; exit 1 ;;
esac
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
curl --fail --silent --show-error "$controller/agent/download/$arch" -o "$temporary/agent.tar.gz"
mkdir "$temporary/extracted"
tar -xzf "$temporary/agent.tar.gz" -C "$temporary/extracted"
install -d -m 755 "$destination"
cp -a "$temporary/extracted/." "$destination/"
chmod 755 "$destination/Aiursoft.DMS.Agent"
ln -sfn "$destination/Aiursoft.DMS.Agent" /usr/local/bin/dms-agent
"$destination/Aiursoft.DMS.Agent" join "${args[@]}"
cat > /etc/systemd/system/dms-agent.service <<'UNIT'
[Unit]
Description=Aiursoft DMS Agent
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=root
ExecStart=/opt/aiursoft-dms-agent/Aiursoft.DMS.Agent run
Restart=on-failure
RestartSec=15
UMask=0077

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now dms-agent.service
echo 'Agent installed. Pending enrollment resumes automatically; an administrator can inspect and approve its Lockbox through DMS.'
