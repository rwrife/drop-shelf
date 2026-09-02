#!/usr/bin/env python3
"""Create a sorted ZIP with fixed timestamps and preserved executable bits."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import stat
import zipfile

FIXED_TIMESTAMP = (2000, 1, 1, 0, 0, 0)


def archive_name(path: Path, source: Path, prefix: str) -> str:
    relative = path.relative_to(source).as_posix()
    return "/".join(part for part in (prefix.strip("/"), relative) if part)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--prefix", default="")
    args = parser.parse_args()

    source = args.source.resolve()
    if not source.is_dir():
        parser.error(f"source is not a directory: {source}")

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    files = sorted(path for path in source.rglob("*") if path.is_file())
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in files:
            name = archive_name(path, source, args.prefix)
            info = zipfile.ZipInfo(name, FIXED_TIMESTAMP)
            info.create_system = 3
            mode = 0o755 if os.access(path, os.X_OK) else 0o644
            info.external_attr = (stat.S_IFREG | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with path.open("rb") as stream:
                archive.writestr(info, stream.read(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    print(f"created {output.name} with {len(files)} files")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
