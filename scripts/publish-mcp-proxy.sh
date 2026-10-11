#!/bin/sh
set -eu

script_dir=$(cd "$(dirname "$0")" && pwd)
exec python3 "$script_dir/publish-mcp-proxy.py" "$@"
