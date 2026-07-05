#!/usr/bin/env python3
"""Generates the solid-colour placeholder PNG icons for the .spk package.
Pure stdlib (zlib + struct) so it runs anywhere without Pillow."""
import sys
import zlib
import struct


def write_png(path: str, size: int, rgb=(11, 95, 255)) -> None:
    raw = bytearray()
    for _ in range(size):
        raw.append(0)  # filter byte: none
        for _ in range(size):
            raw += bytes(rgb)

    def chunk(typ: bytes, data: bytes) -> bytes:
        body = typ + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0)  # 8-bit truecolour
    idat = zlib.compress(bytes(raw), 9)
    with open(path, "wb") as f:
        f.write(sig + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b""))


if __name__ == "__main__":
    write_png(sys.argv[1], int(sys.argv[2]))
