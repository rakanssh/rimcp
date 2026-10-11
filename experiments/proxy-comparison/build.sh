#!/usr/bin/env bash
set -euo pipefail
rid=$1
root=$2
mkdir -p "$root/baseline/$rid" "$root/aot/$rid" "$root/go/$rid"
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
dotnet publish McpProxy/RiMCP.McpProxy.csproj \
  -c Release -r "$rid" --self-contained true \
  -p:RuntimeFrameworkVersion=8.0.31 -p:PublishAot=true \
  -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false \
  -p:OptimizationPreference=Size -p:StripSymbols=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -p:TreatWarningsAsErrors=true -p:NuGetAudit=false \
  -p:PublishDir="$root/aot/$rid/"
dotnet run --project experiments/proxy-comparison/exporter/Export.csproj \
  -p:NuGetAudit=false -- RiMCP/About/About.xml experiments/proxy-comparison/go/manifest.json
suffix=''
if [[ $rid == win-* ]]; then suffix=.exe; fi
(
  cd experiments/proxy-comparison/go
  gofmt -w main.go
  go vet ./...
  CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o "$root/go/$rid/RiMCP.McpProxy$suffix" .
)
cp "$(go env GOROOT)/LICENSE" "$root/go/$rid/LICENSE.TXT"
python - "$(go env GOROOT)" "$root/go/$rid/THIRD-PARTY-NOTICES.TXT" <<'PY'
from pathlib import Path
import sys
root = Path(sys.argv[1])
notices = [root / "PATENTS", *sorted((root / "src/vendor").rglob("LICENSE"))]
text = "\n\n".join(f"{path.relative_to(root)}\n{path.read_text()}" for path in notices if path.is_file())
Path(sys.argv[2]).write_text(text, encoding="utf-8")
PY
