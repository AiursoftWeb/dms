#!/bin/bash
set -euo pipefail
umask 077

if [[ $(id -u) != 0 ]]; then
    echo 'Run this installer as root.' >&2
    exit 1
fi
controller=''
package_version='__DMS_TOOL_VERSION__'
package_sha256='__DMS_TOOL_SHA256__'
args=()
while (($#)); do
    case "$1" in
        --controller-url) controller="${2:?Controller URL is required}"; shift 2 ;;
        --api-key|--name) [[ $# -ge 2 ]] || { echo 'An option value is missing.' >&2; exit 1; }; args+=("$1" "$2"); shift 2 ;;
        *) echo 'Supported installer options: --controller-url, --api-key, --name.' >&2; exit 1 ;;
    esac
done
if [[ ! "$controller" =~ ^https?://[^/?\#[:space:]]+/?$ ]] || [[ "$controller" == *@* ]]; then
    echo 'Supply --controller-url with the Controller HTTP(S) origin.' >&2
    exit 1
fi
controller="${controller%/}"
# The Controller supplies its origin unless explicitly overridden by the operator.
args+=(--controller-url "$controller")
. /etc/os-release
if [[ "$ID" != ubuntu && "$ID" != anduinos ]]; then
    echo 'The Agent installer supports Ubuntu and AnduinOS.' >&2
    exit 1
fi
case "$(uname -m)" in
    x86_64|aarch64) ;;
    *) echo 'The Agent installer supports Linux x64 and arm64.' >&2; exit 1 ;;
esac
for command in curl systemctl sha256sum; do command -v "$command" >/dev/null || { echo "$command is required." >&2; exit 1; }; done
state=/var/lib/aiursoft-dms-agent
destination=/opt/aiursoft-dms-agent
# Re-running bootstrap must never perform an unapproved upgrade or restart of an enrolled Agent.
if [[ -f "$state/connection.json" ]]; then
    installed="$destination/dms-agent"
    [[ -x "$installed" ]] || installed="$destination/Aiursoft.DMS.Agent"
    [[ -x "$installed" ]] || { echo 'Existing Agent identity found without its installation. Restore the installation before retrying.' >&2; exit 1; }
    "$installed" join "${args[@]}"
    systemctl enable --now dms-agent.service
    echo 'Existing Agent installation retained. Use the approved Agent upgrade workflow to update it.'
    exit 0
fi
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.'; then
    apt-get update
    apt-get install -y dotnet10
fi
dotnet --list-sdks | grep -q '^10\.' || { echo '.NET 10 SDK is required.' >&2; exit 1; }
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
curl --fail --silent --show-error "$controller/agent/package" -o "$temporary/Aiursoft.DMS.Agent.$package_version.nupkg"
printf '%s  %s\n' "$package_sha256" "$temporary/Aiursoft.DMS.Agent.$package_version.nupkg" | sha256sum --check --status || {
    echo 'Agent package changed or was corrupted. Download the installer again.' >&2
    exit 1
}
# Restrict restore to this Controller package. No public NuGet fallback or additional dependencies.
cat > "$temporary/nuget.config" <<'CONFIG'
<configuration><packageSources><clear/><add key="controller" value="."/></packageSources></configuration>
CONFIG
tool_directory="$destination/tools/$package_version-${package_sha256:0:12}"
install -d -m 755 "$destination" "$destination/tools"
NUGET_PACKAGES="$temporary/packages" dotnet tool update Aiursoft.DMS.Agent --version "$package_version" --tool-path "$tool_directory" --configfile "$temporary/nuget.config"
chmod 755 "$tool_directory"
"$tool_directory/dms-agent" --version
ln -sfn "$tool_directory/dms-agent" "$destination/dms-agent"
ln -sfn "$destination/dms-agent" /usr/local/bin/dms-agent
"$destination/dms-agent" join "${args[@]}"
cat > /etc/systemd/system/dms-agent.service <<'UNIT'
[Unit]
Description=Aiursoft DMS Agent
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=root
ExecStart=/opt/aiursoft-dms-agent/dms-agent run
Restart=on-failure
RestartSec=15
UMask=0077

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now dms-agent.service
echo 'Agent installed. Pending enrollment resumes automatically; an administrator can inspect and approve its Lockbox through DMS.'
