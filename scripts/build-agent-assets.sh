#!/bin/bash
set -euo pipefail
output=$(realpath -m "${1:?Output directory is required}")
repository=$(cd "$(dirname "$0")/.." && pwd)
source_args=()
if [[ -n "${2:-}" ]]; then source_args=(--source "$2"); fi
mkdir -p "$output"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
version=$(dotnet msbuild "$repository/src/Aiursoft.DMS.Agent/Aiursoft.DMS.Agent.csproj" -target:GetAssemblyVersion -getProperty:AssemblyVersion)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
metadata=()
for runtime in linux-x64 linux-arm64; do
    # Native server bundles include the serviced runtime and are separate from NuGet tool packaging.
    dotnet publish "$repository/src/Aiursoft.DMS.Agent/Aiursoft.DMS.Agent.csproj" \
        --configuration Release --runtime "$runtime" --self-contained true \
        -p:UseAppHost=true -p:RuntimeFrameworkVersion=10.0.15 \
        -p:PackAsTool=false -p:GeneratePackageOnBuild=false \
        --output "$temporary/$runtime" "${source_args[@]}"
    tar -czf "$output/$runtime.tar.gz" -C "$temporary/$runtime" .
    archive_hash=$(sha256sum "$output/$runtime.tar.gz" | cut -d ' ' -f 1)
    assembly_hash=$(sha256sum "$temporary/$runtime/Aiursoft.DMS.Agent.dll" | cut -d ' ' -f 1)
    metadata+=("$(printf '{\"Architecture\":\"%s\",\"Version\":\"%s\",\"ArchiveSha256\":\"%s\",\"AssemblySha256\":\"%s\"}' "$runtime" "$version" "$archive_hash" "$assembly_hash")")
done
printf '[%s,%s]\n' "${metadata[0]}" "${metadata[1]}" > "$output/releases.json"
install -m 644 "$repository/scripts/install-agent.sh" "$output/install.sh"
