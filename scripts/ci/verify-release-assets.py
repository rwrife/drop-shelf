#!/usr/bin/env python3
"""Verify release asset presence, SPDX shape, and every published checksum."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re

CHECKSUM_LINE = re.compile(r"^(?P<digest>[0-9a-f]{64})  (?P<name>[^/\\]+)$")
EXPECTED_FILES = {
    "DropShelf-win-x64.zip",
    "DropShelf-win-x64.msix",
    "DropShelf-windows-SHA256SUMS.txt",
    "DropShelf-windows-third-party-licenses.spdx.json",
    "DropShelf-macos-x64.app.zip",
    "DropShelf-macos-x64.dmg",
    "DropShelf-macos-arm64.app.zip",
    "DropShelf-macos-arm64.dmg",
    "DropShelf-macos-SHA256SUMS.txt",
    "DropShelf-macos-third-party-licenses.spdx.json",
    "verification-windows.txt",
    "verification-macos.txt",
    "verification-windows.log",
    "verification-macos.log",
    "DropShelf-windows.trx",
    "DropShelf-macos.trx",
}
CHECKSUM_FILES = {"DropShelf-windows-SHA256SUMS.txt", "DropShelf-macos-SHA256SUMS.txt"}


def digest(path: Path) -> str:
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    if not root.is_dir():
        parser.error(f"release root is not a directory: {root}")

    files = {path.name: path for path in root.iterdir() if path.is_file()}
    missing = sorted(EXPECTED_FILES - files.keys())
    if missing:
        parser.error(f"missing release assets: {', '.join(missing)}")
    unexpected = sorted(files.keys() - EXPECTED_FILES - CHECKSUM_FILES)
    if unexpected:
        parser.error(f"unexpected release assets: {', '.join(unexpected)}")

    verified: set[str] = set()
    for checksum_file in sorted(root.glob("DropShelf-*-SHA256SUMS.txt")):
        for line_number, line in enumerate(checksum_file.read_text(encoding="utf-8").splitlines(), 1):
            match = CHECKSUM_LINE.fullmatch(line)
            if match is None:
                parser.error(f"invalid checksum line {checksum_file.name}:{line_number}")
            name = match.group("name")
            if name in verified:
                parser.error(f"asset occurs in more than one checksum file: {name}")
            target = files.get(name)
            if target is None:
                parser.error(f"checksummed asset is absent: {name}")
            if digest(target) != match.group("digest"):
                parser.error(f"checksum mismatch: {name}")
            verified.add(name)

    required_checksummed = {name for name in EXPECTED_FILES if name.endswith((".zip", ".msix", ".dmg", ".spdx.json"))}
    unchecked = sorted(required_checksummed - verified)
    if unchecked:
        parser.error(f"release assets missing checksums: {', '.join(unchecked)}")

    for inventory in sorted(root.glob("*.spdx.json")):
        document = json.loads(inventory.read_text(encoding="utf-8"))
        if document.get("spdxVersion") != "SPDX-2.3" or not document.get("packages"):
            parser.error(f"invalid or empty SPDX inventory: {inventory.name}")

    print(f"verified {len(files)} release files and {len(verified)} checksums")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
