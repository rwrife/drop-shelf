#!/usr/bin/env python3
"""Generate deterministic PNG shelf icons without third-party dependencies."""

from __future__ import annotations

import argparse
import binascii
from pathlib import Path
import struct
import zlib


def chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", binascii.crc32(kind + data) & 0xFFFFFFFF)


def create_icon(path: Path, width: int, height: int) -> None:
    rows = bytearray()
    border = max(1, min(width, height) // 16)
    shelf = max(1, min(width, height) // 10)
    for y in range(height):
        rows.append(0)
        for x in range(width):
            frame = x < border or x >= width - border or y < border or y >= height - border
            divider = abs(y - height // 3) < shelf // 2 or abs(y - (height * 2) // 3) < shelf // 2
            if frame or divider:
                rows.extend((246, 248, 255, 255))
            else:
                rows.extend((45, 80, 103, 255))
    payload = b"\x89PNG\r\n\x1a\n"
    payload += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    payload += chunk(b"IDAT", zlib.compress(bytes(rows), level=9))
    payload += chunk(b"IEND", b"")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--width", required=True, type=int)
    parser.add_argument("--height", type=int)
    args = parser.parse_args()
    height = args.height or args.width
    if args.width < 1 or height < 1 or args.width > 1024 or height > 1024:
        parser.error("dimensions must be between 1 and 1024 pixels")
    create_icon(args.output, args.width, height)
    print(f"created {args.output.name} ({args.width}x{height})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
