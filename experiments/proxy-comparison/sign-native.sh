#!/usr/bin/env bash
set -euo pipefail
directory=$1
helper="$directory/RiMCP.McpProxy"
chmod 755 "$helper"
codesign --force --sign "$MACOS_SIGNING_IDENTITY" \
  --keychain "$MACOS_KEYCHAIN_PATH" --timestamp --options runtime "$helper"
codesign --verify --strict --verbose=2 "$helper"
codesign --display --verbose=4 "$helper" 2>&1 | tee "$directory/signature.txt"
grep -q '^Authority=Developer ID Application:' "$directory/signature.txt"
python - "$directory" <<'PY'
from pathlib import Path
import sys
import zipfile
directory = Path(sys.argv[1])
with zipfile.ZipFile(str(directory) + ".zip", "w", zipfile.ZIP_DEFLATED) as archive:
    for name in ("RiMCP.McpProxy", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        archive.write(directory / name, directory.name + "/" + name)
PY
xcrun notarytool submit "$directory.zip" --keychain-profile "$MACOS_NOTARY_PROFILE" \
  --keychain "$MACOS_KEYCHAIN_PATH" --wait --output-format json > "$directory/notarization.json"
cat "$directory/notarization.json"
python - "$directory/notarization.json" <<'PY'
import json
from pathlib import Path
import sys
assert json.loads(Path(sys.argv[1]).read_text())["status"] == "Accepted", "Notarization failed"
PY
