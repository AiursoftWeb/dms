#!/bin/bash
set -euo pipefail
output=$(realpath -m "${1:?Output directory is required}")
repository=$(cd "$(dirname "$0")/.." && pwd)
source_args=()
if [[ -n "${2:-}" ]]; then source_args=(--source "$2"); fi
mkdir -p "$output"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
for runtime in linux-x64 linux-arm64; do
    # Ship the security-serviced runtime even when the build SDK bundles an older patch.
    dotnet publish "$repository/src/Aiursoft.DMS.Agent/Aiursoft.DMS.Agent.csproj" \
        --configuration Release --runtime "$runtime" --self-contained true \
        -p:UseAppHost=true -p:RuntimeFrameworkVersion=10.0.15 \
        --output "$temporary/$runtime" "${source_args[@]}"
    tar -czf "$output/$runtime.tar.gz" -C "$temporary/$runtime" .
done
install -m 644 "$repository/scripts/install-agent.sh" "$output/install.sh"
