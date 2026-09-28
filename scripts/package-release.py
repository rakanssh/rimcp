#!/usr/bin/env python3
"""Stage a clean Workshop folder and ZIP from existing release builds."""

import argparse
from datetime import datetime
import hashlib
from pathlib import Path
import re
import shutil
import stat
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, help="new directory for release files")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version = ET.parse(root / "RiMCP/About/About.xml").findtext("modVersion", "").strip()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?", version):
        raise ValueError("About.xml must contain a valid modVersion")

    files = {
        "RiMCP/About/About.xml": "About/About.xml",
        "RiMCP/About/Preview.png": "About/Preview.png",
        "RiMCP/Assemblies/RiMCP.dll": "Assemblies/RiMCP.dll",
        "RiMCP/Tools/McpProxy/rimcp-proxy": "Tools/McpProxy/rimcp-proxy",
        "RiMCP/Tools/McpProxy/rimcp-proxy.cmd": "Tools/McpProxy/rimcp-proxy.cmd",
        "LICENSE": "LICENSE",
        "README.md": "README.md",
        "CHANGELOG.md": "CHANGELOG.md",
    }
    for rid in ("win-x64", "linux-x64", "osx-x64", "osx-arm64"):
        executable = "RiMCP.McpProxy.exe" if rid == "win-x64" else "RiMCP.McpProxy"
        for name in (executable, "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
            relative = f"Tools/McpProxy/{rid}/{name}"
            files[f"RiMCP/{relative}"] = relative
    if (root / "RiMCP/About/PublishedFileId.txt").exists():
        files["RiMCP/About/PublishedFileId.txt"] = "About/PublishedFileId.txt"

    for source in files:
        if not (root / source).is_file():
            raise FileNotFoundError(f"Missing release file: {source}. Build the mod and publish all proxies first.")

    output = args.output_dir or root / ".dotnet-home/releases" / f"v{version}"
    output = output.resolve()
    if output.exists():
        raise FileExistsError(f"Output already exists: {output}. Use --output-dir with a new directory.")
    output.mkdir(parents=True)
    staging = output / "RiMCP"
    archive = output / f"Rim-MCP-v{version}.zip"
    with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as package:
        for source, relative in files.items():
            destination = staging / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(root / source, destination)
            mode = 0o755 if destination.name in ("rimcp-proxy", "RiMCP.McpProxy") else 0o644
            destination.chmod(mode)
            modified = datetime.fromtimestamp((root / source).stat().st_mtime).timetuple()[:6]
            entry = zipfile.ZipInfo(f"RiMCP/{relative}", modified)
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | mode) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            with destination.open("rb") as data, package.open(entry, "w") as target:
                shutil.copyfileobj(data, target)

    digest = hashlib.sha256()
    with archive.open("rb") as data:
        for chunk in iter(lambda: data.read(1024 * 1024), b""):
            digest.update(chunk)
    archive.with_suffix(".zip.sha256").write_text(
        f"{digest.hexdigest()}  {archive.name}\n", encoding="ascii"
    )
    print(f"Workshop folder: {staging}")
    print(f"Release archive: {archive}")
    print(f"SHA256: {digest.hexdigest()}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, ET.ParseError) as error:
        raise SystemExit(f"Packaging failed: {error}")
