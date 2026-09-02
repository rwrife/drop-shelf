#!/usr/bin/env python3
"""Write a deterministic SHA-256 checksum list for explicit artifacts."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("artifacts", nargs="+", type=Path)
    args = parser.parse_args()
    lines = []
    for artifact in sorted((path.resolve() for path in args.artifacts), key=lambda path: path.name):
        if not artifact.is_file():
            parser.error(f"artifact is not a file: {artifact}")
        digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
        lines.append(f"{digest}  {artifact.name}")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"wrote {args.output.name} with {len(lines)} checksums")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
