#!/bin/sh
set -eu

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/.." && pwd)
project="$repo_root/McpProxy/RiMCP.McpProxy.csproj"
output_root="$repo_root/RiMCP/Tools/McpProxy"

for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
  publish_dir="$output_root/$rid"
  mkdir -p "$publish_dir"
  dotnet publish "$project" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishDir="$publish_dir/"
done
