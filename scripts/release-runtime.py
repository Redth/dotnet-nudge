#!/usr/bin/env python3
"""Package and validate the six self-contained engine release assets."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "plugins/dotnet-nudge/runtime-manifest.json"
VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$")


def digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def package(publish_dir: Path, output: Path, version: str, rid: str) -> None:
    name = "dotnet-nudge.exe" if rid.startswith("win-") else "dotnet-nudge"
    executable = publish_dir / name
    if not executable.is_file() or executable.is_symlink():
        raise ValueError(f"Missing single-file engine: {executable}")
    unexpected = [p.name for p in publish_dir.iterdir() if p.name != name and p.suffix != ".pdb"]
    if unexpected:
        raise ValueError(f"Publish must bundle all dependencies, including native libraries: {unexpected}")
    output.mkdir(parents=True, exist_ok=True)
    asset = output / f"dotnet-nudge-{version}-{rid}.gz"
    with executable.open("rb") as source, asset.open("xb") as destination:
        with gzip.GzipFile(filename="", mode="wb", fileobj=destination, mtime=0) as compressed:
            shutil.copyfileobj(source, compressed)
    artifact = {
        "rid": rid,
        "format": "gzip",
        "url": f"https://github.com/Redth/dotnet-nudge/releases/download/v{version}/{asset.name}",
        "sha256": digest(asset),
        "executableSha256": digest(executable),
    }
    (output / f"{rid}.json").write_text(json.dumps(artifact, indent=2) + "\n", encoding="utf-8")


def manifest(artifacts_dir: Path, output: Path, version: str) -> None:
    result = json.loads(TEMPLATE.read_text(encoding="utf-8"))
    artifacts = []
    for rid in result["supportedRids"]:
        artifact = json.loads((artifacts_dir / f"{rid}.json").read_text(encoding="utf-8"))
        asset = artifacts_dir / f"dotnet-nudge-{version}-{rid}.gz"
        expected_url = f"https://github.com/Redth/dotnet-nudge/releases/download/v{version}/{asset.name}"
        if artifact["rid"] != rid or artifact["format"] != "gzip" or artifact["url"] != expected_url:
            raise ValueError(f"Invalid release artifact identity for {rid}")
        if digest(asset) != artifact["sha256"]:
            raise ValueError(f"Archive digest mismatch for {rid}")
        with gzip.open(asset, "rb") as executable:
            if hashlib.file_digest(executable, "sha256").hexdigest() != artifact["executableSha256"]:
                raise ValueError(f"Executable digest mismatch for {rid}")
        artifacts.append(artifact)
    result.update(status="published", version=version, artifacts=artifacts)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    pack = commands.add_parser("package")
    pack.add_argument("--publish-dir", required=True, type=Path)
    pack.add_argument("--rid", required=True)
    collect = commands.add_parser("manifest")
    collect.add_argument("--artifacts-dir", required=True, type=Path)
    for command in (pack, collect):
        command.add_argument("--version", required=True)
        command.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not VERSION.fullmatch(args.version):
        parser.error("Version must be an exact release version.")
    rids = json.loads(TEMPLATE.read_text(encoding="utf-8"))["supportedRids"]
    if args.command == "package":
        if args.rid not in rids:
            parser.error("Unsupported RID.")
        package(args.publish_dir, args.output, args.version, args.rid)
    else:
        manifest(args.artifacts_dir, args.output, args.version)


if __name__ == "__main__":
    main()
