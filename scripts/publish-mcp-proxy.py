#!/usr/bin/env python3
"""Build and stage the native MCP helper for the current platform."""

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import urllib.request


TARGETS = {
    "win-x64": "x86_64-pc-windows-msvc",
    "linux-x64": "x86_64-unknown-linux-musl",
    "osx-x64": "x86_64-apple-darwin",
    "osx-arm64": "aarch64-apple-darwin",
}


def native_rid():
    system = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}.get(platform.system())
    architecture = {"AMD64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine())
    rid = f"{system}-{architecture}"
    if rid not in TARGETS:
        raise ValueError(f"Unsupported build platform: {platform.system()} {platform.machine()}")
    return rid


def dependency_notices(metadata, rust_version):
    parts = [f"Rust {rust_version} standard library (MIT OR Apache-2.0)"]
    for name in ("LICENSE-MIT", "LICENSE-APACHE"):
        url = f"https://raw.githubusercontent.com/rust-lang/rust/{rust_version}/{name}"
        with urllib.request.urlopen(url, timeout=30) as response:
            parts.append(response.read().decode("utf-8"))
    resolved = {node["id"] for node in metadata["resolve"]["nodes"]}
    for package in sorted(metadata["packages"], key=lambda item: (item["name"], item["version"])):
        if not package["source"] or package["id"] not in resolved:
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
    return "\n\n".join(parts) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=TARGETS, help="defaults to the current platform")
    parser.add_argument("--output-root", type=Path, help="defaults to RiMCP/Tools/McpProxy")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    rid = args.rid or native_rid()
    if rid != native_rid():
        raise ValueError(f"Build {rid} on a matching native host.")
    if rid == "linux-x64" and shutil.which("musl-gcc") is None:
        raise ValueError("Install musl-tools (musl-gcc) to build the standalone Linux helper.")

    target = TARGETS[rid]
    environment = dict(os.environ)
    if rid.startswith("osx-"):
        environment["MACOSX_DEPLOYMENT_TARGET"] = "10.15" if rid == "osx-x64" else "11.0"
    command_options = {"cwd": root, "env": environment, "check": True}
    subprocess.run(["rustup", "target", "add", target], **command_options)
    cargo = ["cargo", "build", "--locked", "--release", "--manifest-path", "McpProxy/Cargo.toml", "--target", target]
    subprocess.run(cargo, **command_options)
    metadata = json.loads(subprocess.run(
        ["cargo", "metadata", "--locked", "--manifest-path", "McpProxy/Cargo.toml", "--format-version", "1", "--filter-platform", target],
        capture_output=True, text=True, encoding="utf-8", **command_options,
    ).stdout)
    rust_version = subprocess.run(["rustc", "--version"], capture_output=True, text=True, **command_options).stdout.split()[1]
    notices = dependency_notices(metadata, rust_version)
    output = (args.output_root or root / "RiMCP/Tools/McpProxy").resolve() / rid
    output.mkdir(parents=True, exist_ok=True)
    suffix = ".exe" if rid == "win-x64" else ""
    binary = Path(metadata["target_directory"]) / target / "release" / f"rimcp-proxy{suffix}"
    helper = output / f"RiMCP.McpProxy{suffix}"
    shutil.copyfile(binary, helper)
    helper.chmod(0o755)
    shutil.copyfile(root / "LICENSE", output / "LICENSE.TXT")
    (output / "THIRD-PARTY-NOTICES.TXT").write_text(notices, encoding="utf-8")
    print(f"Published {rid}: {helper} ({helper.stat().st_size:,} bytes)")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Publishing failed: {error}")
