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
package_version=$(dotnet msbuild "$repository/src/Aiursoft.DMS.Agent/Aiursoft.DMS.Agent.csproj" -getProperty:Version)
[[ "$package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
dotnet pack "$repository/src/Aiursoft.DMS.Agent/Aiursoft.DMS.Agent.csproj" --configuration Release \
    -p:GeneratePackageOnBuild=false --output "$temporary/packages" "${source_args[@]}"
cp "$temporary/packages/Aiursoft.DMS.Agent.$package_version.nupkg" "$output/agent.nupkg"
package_hash=$(sha256sum "$output/agent.nupkg" | cut -d ' ' -f 1)
tool_assembly_hash=$(sha256sum "$repository/src/Aiursoft.DMS.Agent/bin/Release/net10.0/Aiursoft.DMS.Agent.dll" | cut -d ' ' -f 1)
printf '{"PackageVersion":"%s","PackageSha256":"%s","AssemblySha256":"%s"}\n' \
    "$package_version" "$package_hash" "$tool_assembly_hash" > "$temporary/tool.json"
metadata=()
for runtime in linux-x64 linux-arm64; do
    # The independent native helper can migrate an existing service and survives stopping its Agent.
    dotnet publish "$repository/src/Aiursoft.DMS.Agent/Aiursoft.DMS.Agent.csproj" \
        --configuration Release --runtime "$runtime" --self-contained true \
        -p:UseAppHost=true -p:RuntimeFrameworkVersion=10.0.15 \
        -p:PackAsTool=false -p:GeneratePackageOnBuild=false \
        --output "$temporary/$runtime" "${source_args[@]}"
    cp "$output/agent.nupkg" "$temporary/$runtime/agent.nupkg"
    cp "$temporary/tool.json" "$temporary/$runtime/tool.json"
    tar -czf "$output/$runtime.tar.gz" -C "$temporary/$runtime" .
    archive_hash=$(sha256sum "$output/$runtime.tar.gz" | cut -d ' ' -f 1)
    assembly_hash=$(sha256sum "$temporary/$runtime/Aiursoft.DMS.Agent.dll" | cut -d ' ' -f 1)
    metadata+=("$(printf '{\"Architecture\":\"%s\",\"Version\":\"%s\",\"ArchiveSha256\":\"%s\",\"AssemblySha256\":\"%s\"}' "$runtime" "$version" "$archive_hash" "$assembly_hash")")
done
printf '[%s,%s]\n' "${metadata[0]}" "${metadata[1]}" > "$output/releases.json"
sed -e "s/__DMS_TOOL_VERSION__/$package_version/g" -e "s/__DMS_TOOL_SHA256__/$package_hash/g" \
    "$repository/scripts/install-agent.sh" > "$output/install.sh"
