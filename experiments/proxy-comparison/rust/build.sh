#!/usr/bin/env bash
set -euo pipefail
rid=$1
root=$2
mkdir -p "$root/baseline/$rid" "$root/rust/$rid"
curl --fail --location --silent --show-error \
  https://github.com/rakanssh/rimcp/releases/download/v0.5.4/RimMCP-v0.5.4.zip \
  --output "$root/released.zip"
python - "$root" "$rid" <<'PY'
from pathlib import Path
import sys
import zipfile
root, rid = Path(sys.argv[1]), sys.argv[2]
with zipfile.ZipFile(root / "released.zip") as archive:
    for name in archive.namelist():
        if f"/Tools/McpProxy/{rid}/" in name and not name.endswith("/"):
            path = root / "baseline" / rid / Path(name).name
            path.write_bytes(archive.read(name))
            if path.name == "RiMCP.McpProxy":
                path.chmod(0o755)
PY
dotnet run --project experiments/proxy-comparison/exporter/Export.csproj \
  -p:NuGetAudit=false -- RiMCP/About/About.xml experiments/proxy-comparison/go/manifest.json
target=''
suffix=''
case "$rid" in
  win-x64) target=x86_64-pc-windows-msvc; suffix=.exe ;;
  linux-x64) target=x86_64-unknown-linux-musl ;;
  osx-x64) target=x86_64-apple-darwin; export MACOSX_DEPLOYMENT_TARGET=10.15 ;;
  osx-arm64) target=aarch64-apple-darwin; export MACOSX_DEPLOYMENT_TARGET=11.0 ;;
  *) echo "Unknown platform: $rid" >&2; exit 1 ;;
esac
rustup target add --toolchain 1.89.0 "$target"
export CARGO_TARGET_DIR="$root/rust-target"
manifest=experiments/proxy-comparison/rust/Cargo.toml
cargo +1.89.0 fmt --manifest-path "$manifest" --check
cargo +1.89.0 clippy --locked --manifest-path "$manifest" --target "$target" -- -D warnings
cargo +1.89.0 build --locked --release --manifest-path "$manifest" --target "$target"
cp "$CARGO_TARGET_DIR/$target/release/rimcp-proxy$suffix" "$root/rust/$rid/RiMCP.McpProxy$suffix"
cp LICENSE "$root/rust/$rid/LICENSE.TXT"
cargo +1.89.0 metadata --locked --manifest-path "$manifest" --format-version 1 > "$root/cargo-metadata.json"
python - "$root" "$rid" <<'PY'
from pathlib import Path
import json
import sys
import urllib.request
root, rid = Path(sys.argv[1]), sys.argv[2]
parts = ["Rust 1.89.0 standard library (MIT OR Apache-2.0)"]
for name in ("LICENSE-MIT", "LICENSE-APACHE"):
    with urllib.request.urlopen(f"https://raw.githubusercontent.com/rust-lang/rust/1.89.0/{name}") as response:
        parts.append(response.read().decode())
for package in sorted(json.loads((root / "cargo-metadata.json").read_text())["packages"], key=lambda item: item["name"]):
    if not package["source"]:
        continue
    directory = Path(package["manifest_path"]).parent
    parts.append(f'{package["name"]} {package["version"]}\n{package["license"] or ""}\n{package["repository"] or ""}')
    files = set()
    for child in directory.iterdir():
        if child.name.lower().startswith(("license", "copying", "notice")):
            if child.is_file():
                files.add(child)
            elif child.is_dir():
                files.update(path for path in child.rglob("*") if path.is_file())
    if package.get("license_file"):
        files.add(directory / package["license_file"])
    for path in sorted(files):
        parts.append(f'{path.relative_to(directory)}\n{path.read_text(encoding="utf-8")}')
(root / "rust" / rid / "THIRD-PARTY-NOTICES.TXT").write_text("\n\n".join(parts), encoding="utf-8")
PY
if [[ $rid == osx-* ]]; then
  otool -l "$root/rust/$rid/RiMCP.McpProxy" > "$root/rust-macos-load-commands.txt"
elif [[ $rid == linux-* ]]; then
  readelf -d -V "$root/rust/$rid/RiMCP.McpProxy" > "$root/rust-linux-libraries.txt"
  file "$root/rust/$rid/RiMCP.McpProxy" >> "$root/rust-linux-libraries.txt"
fi
