#!/bin/bash
set -euo pipefail

if [[ $(uname -s) != Darwin ]]; then
  echo "Signing requires macOS with Xcode command-line tools." >&2
  exit 1
fi
if [[ $# != 1 || ! -d $1 ]]; then
  echo "Usage: $0 <publish-directory>" >&2
  exit 1
fi
: "${MACOS_SIGNING_IDENTITY:?Set MACOS_SIGNING_IDENTITY to a Developer ID Application identity.}"
: "${MACOS_KEYCHAIN_PATH:?Set MACOS_KEYCHAIN_PATH to the signing keychain.}"
: "${MACOS_NOTARY_PROFILE:?Set MACOS_NOTARY_PROFILE to its notarytool credential profile.}"

script_dir=$(cd "$(dirname "$0")" && pwd -P)
publish_dir=$(cd "$1" && pwd -P)
rid=$(basename "$publish_dir")
case "$rid" in
  osx-x64) expected_arch=x86_64 ;;
  osx-arm64) expected_arch=arm64 ;;
  *) echo "Publish directory must be named osx-x64 or osx-arm64." >&2; exit 1 ;;
esac
host_arch=$(uname -m)
if [[ $host_arch != "$expected_arch" ]]; then
  echo "Native verification of $rid requires $expected_arch; this runner is $host_arch." >&2
  exit 1
fi
archive="$publish_dir.zip"
helper="$publish_dir/RiMCP.McpProxy"
if [[ -e $archive ]]; then
  echo "Archive already exists: $archive" >&2
  exit 1
fi
for name in RiMCP.McpProxy LICENSE.TXT THIRD-PARTY-NOTICES.TXT; do
  if [[ ! -f $publish_dir/$name || -L $publish_dir/$name ]]; then
    echo "Missing regular release file: $publish_dir/$name" >&2
    exit 1
  fi
done
if [[ ! -f $MACOS_KEYCHAIN_PATH ]]; then
  echo "Signing keychain does not exist: $MACOS_KEYCHAIN_PATH" >&2
  exit 1
fi
command -v python3 >/dev/null
xcrun --find notarytool >/dev/null
plutil -lint "$script_dir/macos-entitlements.plist"

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/rimcp-sign.XXXXXX")
trap 'rm -rf -- "$work_dir"' EXIT
chmod 755 "$helper"
codesign --force --sign "$MACOS_SIGNING_IDENTITY" \
  --keychain "$MACOS_KEYCHAIN_PATH" --timestamp --options runtime \
  --entitlements "$script_dir/macos-entitlements.plist" "$helper"
codesign --verify --strict --verbose=2 "$helper"
signature=$(codesign --display --verbose=4 "$helper" 2>&1)
if ! printf '%s\n' "$signature" | grep -q '^Authority=Developer ID Application:'; then
  echo "The helper must be signed with a Developer ID Application certificate." >&2
  exit 1
fi

python3 - "$helper" <<'PY'
import json
import subprocess
import sys

requests = [
    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-11-25", "capabilities": {},
        "clientInfo": {"name": "release-smoke", "version": "1"}}},
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
]
try:
    result = subprocess.run(
        [sys.argv[1]], input="".join(json.dumps(r) + "\n" for r in requests),
        capture_output=True, text=True, timeout=30,
    )
except subprocess.TimeoutExpired as error:
    if error.stderr:
        stderr = error.stderr.decode(errors="replace") if isinstance(error.stderr, bytes) else error.stderr
        print(stderr, file=sys.stderr)
    raise SystemExit("Signed helper did not finish within 30 seconds.")
if result.stderr:
    print(result.stderr, file=sys.stderr, end="")
if result.returncode:
    raise SystemExit(f"Signed helper exited with status {result.returncode}.")
responses = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
if len(responses) != 2 or any(r.get("jsonrpc") != "2.0" or "error" in r for r in responses):
    raise SystemExit("Helper returned unexpected JSON-RPC responses.")
by_id = {r.get("id"): r.get("result", {}) for r in responses}
if by_id.get(1, {}).get("serverInfo", {}).get("name") != "rimcp":
    raise SystemExit("Helper initialize response has the wrong server name.")
tools = by_id.get(2, {}).get("tools")
if not isinstance(tools, list) or not tools:
    raise SystemExit("Helper tools/list response is empty or invalid.")
print(f"Signed helper started successfully and returned {len(tools)} tools.")
PY

python3 - "$publish_dir" "$work_dir/submission.zip" <<'PY'
from pathlib import Path
import shutil
import stat
import sys
import zipfile

directory = Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], "x", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for name in ("RiMCP.McpProxy", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        entry = zipfile.ZipInfo.from_file(directory / name, f"{directory.name}/{name}")
        mode = 0o755 if name == "RiMCP.McpProxy" else 0o644
        entry.create_system = 3
        entry.external_attr = (stat.S_IFREG | mode) << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        with (directory / name).open("rb") as source, archive.open(entry, "w") as target:
            shutil.copyfileobj(source, target)
PY

submit_exit=0
xcrun notarytool submit "$work_dir/submission.zip" \
  --keychain-profile "$MACOS_NOTARY_PROFILE" --keychain "$MACOS_KEYCHAIN_PATH" \
  --wait --output-format json > "$work_dir/result.json" || submit_exit=$?
cat "$work_dir/result.json"
python3 - "$work_dir/result.json" "$work_dir" <<'PY'
import json
from pathlib import Path
import sys

try:
    result = json.loads(Path(sys.argv[1]).read_text())
except (ValueError, OSError):
    raise SystemExit("Could not read Apple's notarization response.")
for key in ("id", "status"):
    Path(sys.argv[2], key).write_text(str(result.get(key, "")))
PY
submission_id=$(cat "$work_dir/id")
status=$(cat "$work_dir/status")
if [[ $submit_exit != 0 || $status != Accepted ]]; then
  if [[ -n $submission_id ]]; then
    xcrun notarytool log "$submission_id" \
      --keychain-profile "$MACOS_NOTARY_PROFILE" --keychain "$MACOS_KEYCHAIN_PATH" \
      "$work_dir/notary-log.json" && cat "$work_dir/notary-log.json" || true
  fi
  echo "Notarization did not succeed (status: $status, exit: $submit_exit)." >&2
  exit 1
fi

# Standalone executables cannot have notarization tickets stapled to them.
cp "$work_dir/submission.zip" "$archive"
printf '\nNotarized archive: %s\nSubmission: %s\n' "$archive" "$submission_id"
