#!/usr/bin/env python3
"""Prints the format and architecture of a binary, so a script can write a project.toml that
describes the file it was given instead of assuming one.

    python3 tests/tools/image_format.py build/sample.exe     ->  pe32 x86
    python3 tests/tools/image_format.py build/sample-macho64 ->  macho64 x64

The two values are the ones project.toml accepts for [target] format and arch. Only the header is
read: this is the same decision ImageLoader makes, kept here so a shell script does not have to
grow a parser of its own.
"""
import struct
import sys


def describe(path: str) -> tuple[str, str]:
    with open(path, "rb") as handle:
        head = handle.read(64)

    if head[:4] == b"\x7fELF":
        width = "elf64" if head[4] == 2 else "elf32"
        machine = int.from_bytes(head[18:20], "little")
        return width, {3: "x86", 62: "x64", 40: "arm", 183: "arm64"}.get(machine, "x86")

    # Mach-O: 0xFEEDFACF (64-bit) or 0xFEEDFACE (32-bit), either byte order, or the fat container
    # that holds one of each. The CPU type carries the word size in bit 24 rather than in its own
    # field, and the cputype alone says which machine.
    magic = int.from_bytes(head[:4], "little")
    if magic in (0xFEEDFACF, 0xFEEDFACE, 0xCFFAEDFE, 0xCEFAEDFE, 0xBEBAFECA, 0xBFBAFECA):
        if magic in (0xBEBAFECA, 0xBFBAFECA):
            # A fat file: the first slice's own header is where the answer is.
            count = int.from_bytes(head[4:8], "big")
            wide = magic == 0xBFBAFECA
            entry = head[8 : 8 + (32 if wide else 20)]
            offset = int.from_bytes(entry[8:16] if wide else entry[8:12], "big")
            with open(path, "rb") as handle:
                handle.seek(offset)
                head = handle.read(64)
            magic = int.from_bytes(head[:4], "little")

        width = "macho64" if magic in (0xFEEDFACF, 0xCFFAEDFE) else "macho32"
        swapped = magic in (0xCFFAEDFE, 0xCEFAEDFE)
        order = "big" if swapped else "little"
        cpu = int.from_bytes(head[4:8], order)
        return width, {7: "x86", 0x01000007: "x64", 12: "arm", 0x0100000C: "arm64"}.get(cpu, "x86")

    if head[:2] == b"MZ":
        offset = int.from_bytes(head[0x3C:0x40], "little")
        with open(path, "rb") as handle:
            handle.seek(offset)
            if handle.read(4) != b"PE\0\0":
                return "pe32", "x86"
            coff = handle.read(20)
            magic = handle.read(2)

        machine = int.from_bytes(coff[0:2], "little")
        plus = magic == b"\x0b\x02"
        arch = {0x014C: "x86", 0x8664: "x64"}.get(machine, "x86")
        return ("pe64" if plus else "pe32"), arch

    raise SystemExit(f"image_format: {path} is neither ELF, PE nor Mach-O")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: image_format.py <binary>")
    print(" ".join(describe(sys.argv[1])))
