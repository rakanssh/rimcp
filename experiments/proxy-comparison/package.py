import argparse
import json
from pathlib import Path
import stat
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--artifacts", type=Path, required=True)
parser.add_argument("--released", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
rids = ("win-x64", "linux-x64", "osx-x64", "osx-arm64")
results = {}
for rid in rids:
    matches = list(args.artifacts.rglob("results-" + rid + ".json"))
    assert len(matches) == 1, (rid, matches)
    results[rid] = json.loads(matches[0].read_text())

summary = {"platforms": results, "packages": {}}
with zipfile.ZipFile(args.released) as original:
    original_files = [entry for entry in original.infolist() if not entry.is_dir()]
    summary["packages"]["baseline"] = {"bytes": sum(entry.file_size for entry in original_files),
                                         "zip_bytes": args.released.stat().st_size}
    for variant in ("aot", "go"):
        output = args.output / ("RimMCP-experimental-" + variant + ".zip")
        total = 0
        replaced = set()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for entry in original_files:
                data = original.read(entry)
                for rid in rids:
                    if "/Tools/McpProxy/" + rid + "/" in entry.filename:
                        matches = list(args.artifacts.rglob(variant + "-" + rid + ".zip"))
                        assert len(matches) == 1, matches
                        with zipfile.ZipFile(matches[0]) as helper:
                            data = helper.read(rid + "/" + Path(entry.filename).name)
                        replaced.add(rid)
                        break
                info = zipfile.ZipInfo(entry.filename, entry.date_time)
                info.create_system = 3
                executable = Path(entry.filename).name == "RiMCP.McpProxy" or Path(entry.filename).name == "rimcp-proxy"
                info.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, data)
                total += len(data)
        assert replaced == set(rids)
        with zipfile.ZipFile(output) as check:
            assert check.testzip() is None and len(check.namelist()) == len(original_files)
        summary["packages"][variant] = {"bytes": total, "zip_bytes": output.stat().st_size}
args.output.joinpath("comparison.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
print(json.dumps(summary["packages"], indent=2))
