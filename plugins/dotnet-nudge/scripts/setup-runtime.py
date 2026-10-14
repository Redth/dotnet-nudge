#!/usr/bin/env python3
"""Install a locally supplied, SHA-256-pinned dotnet-nudge single-file archive."""

from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import os
import platform
import re
import shutil
import stat
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path
from typing import BinaryIO

SUPPORTED_RIDS = {
    "linux-x64",
    "linux-arm64",
    "osx-x64",
    "osx-arm64",
    "win-x64",
    "win-arm64",
}
MAX_FILE_BYTES = 1024 * 1024 * 1024
MAX_PACKAGE_BYTES = 2 * 1024 * 1024 * 1024
VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$")
RID_PATTERN = re.compile(r"^[a-z0-9]+-[a-z0-9]+$")


class SetupError(Exception):
    pass


def default_data_dir() -> Path:
    if os.environ.get("NUDGE_STATE_DIR"):
        return Path(os.environ["NUDGE_STATE_DIR"]).expanduser()
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA")
        if not base:
            raise SetupError("LOCALAPPDATA is not set; provide --state-dir or NUDGE_STATE_DIR.")
        return Path(base) / "dotnet-nudge"
    if os.environ.get("XDG_DATA_HOME"):
        return Path(os.environ["XDG_DATA_HOME"]).expanduser() / "dotnet-nudge"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "dotnet-nudge"
    return Path.home() / ".local" / "share" / "dotnet-nudge"


def current_rid() -> str:
    system = {
        "Darwin": "osx",
        "Linux": "linux",
        "Windows": "win",
    }.get(platform.system())
    machine = {
        "x86_64": "x64",
        "AMD64": "x64",
        "aarch64": "arm64",
        "arm64": "arm64",
    }.get(platform.machine())
    if not system or not machine:
        raise SetupError(f"Unsupported platform: {platform.system()} {platform.machine()}.")
    return f"{system}-{machine}"


def archive_member_parts(name: str, is_directory: bool) -> tuple[str, ...]:
    if name in (".", "./") and is_directory:
        return ()
    if not name or name.startswith("/") or "\\" in name or "\0" in name:
        raise SetupError("Archive contains an absolute or invalid path.")
    normalized = name[2:] if name.startswith("./") else name
    if is_directory and normalized.endswith("/"):
        normalized = normalized[:-1]
    parts = normalized.split("/")
    if (
        not normalized
        or any(part in ("", ".", "..") for part in parts)
        or ":" in parts[0]
        or any(part.endswith((".", " ")) for part in parts)
        or any(
            re.fullmatch(r"(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part, re.IGNORECASE)
            for part in parts
        )
    ):
        raise SetupError("Archive contains an unsafe path.")
    return tuple(parts)


def check_contained_path(root: Path, path: Path) -> None:
    if not path.resolve().is_relative_to(root.resolve()):
        raise SetupError("Archive path resolves outside the staging directory.")


def copy_limited(source: BinaryIO, destination: Path, size: int, mode: int) -> None:
    if size <= 0 or size > MAX_FILE_BYTES:
        raise SetupError("Archive file has an invalid or excessive size.")
    remaining = size
    with destination.open("xb") as output:
        while remaining:
            chunk = source.read(min(1024 * 1024, remaining))
            if not chunk:
                raise SetupError("Archive file ended before its declared size.")
            output.write(chunk)
            remaining -= len(chunk)
        if source.read(1):
            raise SetupError("Archive file exceeds its declared size.")
    destination.chmod(mode & 0o777 or 0o644)


def extract_payload(archive: Path, staging: Path, executable_name: str) -> list[str]:
    staged_files: list[str] = []
    seen: set[str] = set()
    total_size = 0

    def write_member(name: str, is_directory: bool, size: int, mode: int, source: BinaryIO | None) -> None:
        nonlocal total_size
        parts = archive_member_parts(name, is_directory)
        if not parts:
            return
        relative = "/".join(parts)
        if relative == "runtime.json":
            raise SetupError("Archive may not supply the installer-owned runtime.json file.")
        if relative in seen:
            raise SetupError("Archive contains duplicate paths.")
        seen.add(relative)

        target = staging.joinpath(*parts)
        if is_directory:
            target.mkdir(parents=True, exist_ok=True)
            check_contained_path(staging, target)
            return
        if source is None:
            raise SetupError("Archive regular file could not be read.")
        total_size += size
        if total_size > MAX_PACKAGE_BYTES:
            raise SetupError("Archive payload exceeds the supported total size.")
        target.parent.mkdir(parents=True, exist_ok=True)
        check_contained_path(staging, target.parent)
        copy_limited(source, target, size, mode)
        staged_files.append(relative)

    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as package:
            for entry in package.infolist():
                mode = (entry.external_attr >> 16) & 0xFFFF
                file_type = stat.S_IFMT(mode)
                is_directory = entry.is_dir() or file_type == stat.S_IFDIR
                if file_type not in (0, stat.S_IFDIR if is_directory else stat.S_IFREG):
                    raise SetupError("Archive may contain only regular files and directories.")
                if entry.flag_bits & 0x1:
                    raise SetupError("Encrypted archive entries are not supported.")
                if is_directory:
                    write_member(entry.filename, True, 0, mode, None)
                else:
                    with package.open(entry) as source:
                        write_member(
                            entry.filename,
                            False,
                            entry.file_size,
                            mode or 0o644,
                            source,
                        )
    elif tarfile.is_tarfile(archive):
        with tarfile.open(archive, mode="r:*") as package:
            for entry in package.getmembers():
                if entry.isdir():
                    write_member(entry.name, True, 0, entry.mode, None)
                elif entry.isfile():
                    source = package.extractfile(entry)
                    if source is None:
                        raise SetupError("Archive regular file could not be read.")
                    with source:
                        write_member(entry.name, False, entry.size, entry.mode, source)
                else:
                    raise SetupError("Archive may contain only regular files and directories.")
    else:
        raise SetupError("Archive must be a ZIP or tar archive.")

    if executable_name not in staged_files:
        raise SetupError(f"Archive does not contain the expected root executable {executable_name}.")
    if executable_name not in seen:
        raise SetupError("Archive executable identity could not be verified.")
    executable_path = staging / executable_name
    if not executable_path.is_file() or executable_path.is_symlink():
        raise SetupError("Expected engine executable is not a regular file.")
    if os.name != "nt":
        executable_path.chmod(executable_path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return sorted(staged_files)


def install(args: argparse.Namespace) -> Path:
    archive = Path(args.archive).expanduser()
    if not archive.is_file():
        raise SetupError(f"Archive does not exist: {archive}")
    if not VERSION_PATTERN.fullmatch(args.version):
        raise SetupError("Version must be an exact semantic version.")
    if not RID_PATTERN.fullmatch(args.rid) or args.rid not in SUPPORTED_RIDS:
        raise SetupError("RID is not in the supported runtime identifier list.")
    if args.rid != current_rid():
        raise SetupError(f"Archive RID {args.rid} does not match this machine ({current_rid()}).")
    if not re.fullmatch(r"[0-9a-fA-F]{64}", args.sha256):
        raise SetupError("--sha256 must be exactly 64 hexadecimal characters.")

    digest = hashlib.sha256()
    with archive.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    actual_digest = digest.hexdigest()
    if not hmac.compare_digest(actual_digest.lower(), args.sha256.lower()):
        raise SetupError("Archive SHA-256 does not match the supplied digest.")

    executable_name = "dotnet-nudge.exe" if args.rid.startswith("win-") else "dotnet-nudge"
    data_root = Path(args.state_dir).expanduser() if args.state_dir else default_data_dir()
    data_root.mkdir(parents=True, exist_ok=True)
    data_root = data_root.resolve(strict=True)
    engines_dir = data_root / "engine"
    engines_dir.mkdir(exist_ok=True)
    engines_dir = engines_dir.resolve(strict=True)
    if not engines_dir.is_relative_to(data_root):
        raise SetupError("Engine install directory resolves outside the selected data directory.")

    version_dir = engines_dir / args.version
    version_dir.mkdir(exist_ok=True)
    version_dir = version_dir.resolve(strict=True)
    if not version_dir.is_relative_to(engines_dir):
        raise SetupError("Version install directory resolves outside the engine directory.")
    install_dir = version_dir / args.rid
    current_path = engines_dir / "current"
    if current_path.is_symlink():
        raise SetupError("Refusing to replace an engine selection symlink.")
    if current_path.exists() and not current_path.is_file():
        raise SetupError("Engine selection path must be a regular file.")
    if install_dir.is_symlink():
        raise SetupError("Refusing to replace an engine install symlink.")
    if not args.replace and install_dir.exists():
        raise SetupError(f"An engine is already installed at {install_dir}; pass --replace to replace it.")

    with tempfile.TemporaryDirectory(prefix=".dotnet-nudge-setup-", dir=version_dir) as staging_name:
        staging = Path(staging_name).resolve(strict=True)
        if not staging.is_relative_to(version_dir):
            raise SetupError("Staging directory resolves outside the engine install directory.")
        staged_files = extract_payload(archive, staging, executable_name)
        staged_metadata = staging / "runtime.json"
        staged_metadata.write_text(
            json.dumps(
                {
                    "engine": "dotnet-nudge",
                    "version": args.version,
                    "rid": args.rid,
                    "sha256": actual_digest,
                    "executable": executable_name,
                    "files": staged_files,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        if install_dir.exists():
            backup_dir = version_dir / f".{args.rid}-previous"
            if backup_dir.exists():
                raise SetupError(f"Refusing to overwrite an existing setup backup: {backup_dir}")
            os.replace(install_dir, backup_dir)
            try:
                os.replace(staging, install_dir)
            except OSError:
                os.replace(backup_dir, install_dir)
                raise
            shutil.rmtree(backup_dir)
        else:
            os.replace(staging, install_dir)

    with tempfile.NamedTemporaryFile(
        mode="w",
        encoding="utf-8",
        prefix=".current-",
        dir=engines_dir,
        delete=False,
    ) as pointer:
        pointer.write(f"{args.version}/{args.rid}\n")
        pointer_path = Path(pointer.name)
    os.replace(pointer_path, current_path)
    install_path = install_dir / executable_name
    return install_path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, help="Path to a locally supplied ZIP or tar archive.")
    parser.add_argument("--version", required=True, help="Exact version published with this archive.")
    parser.add_argument("--rid", required=True, help="Exact runtime identifier published with this archive.")
    parser.add_argument("--sha256", required=True, help="Exact trusted 64-character archive SHA-256 digest.")
    parser.add_argument("--state-dir", help="Writable data directory; defaults to the platform user-data directory.")
    parser.add_argument("--replace", action="store_true", help="Explicitly replace an installed engine.")
    args = parser.parse_args()
    try:
        install_path = install(args)
    except (OSError, tarfile.TarError, zipfile.BadZipFile, SetupError) as error:
        print(f"dotnet-nudge setup failed: {error}", file=sys.stderr)
        return 1
    print(f"Installed dotnet-nudge {args.version} ({args.rid}) at {install_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
