#!/usr/bin/env python3
"""Build the synthetic PE32 images the analysis is measured with.

These exist because the questions they answer only appear on a program too big to hold: a
Visual Basic 6 client is 4.4 million instructions, and the analysis decodes it a window at a
time rather than keeping it. Nothing about that is visible on a fixture that fits in one
window, so these images are deliberately larger than one.

    straddle <out.exe>   one five-byte call cut in half by a window boundary
    nops <out.exe> [n]   a code section of one-byte nops, three windows long
    manyfn <out.exe> [n] n call targets, i.e. n functions to find

The images are written as PE32 with a single .text section and nothing else: no imports, no
exports, no symbols. The inventory reports on the code it can read.
"""

import struct
import sys

HEADERS_SIZE = 0x400
SECTION_RVA = 0x1000
ENTRY_RVA = 0x1000
WINDOW_SIZE = 0x40000


def image(code: bytes) -> bytes:
    """A one-section PE32 whose whole code section is the given bytes."""
    out = bytearray(HEADERS_SIZE + len(code))

    out[0:2] = b"MZ"
    struct.pack_into("<I", out, 0x3C, 0x40)

    pe = 0x40
    out[pe:pe + 4] = b"PE\0\0"
    struct.pack_into("<H", out, pe + 4, 0x014C)      # i386
    struct.pack_into("<H", out, pe + 6, 1)           # one section
    struct.pack_into("<H", out, pe + 20, 0xE0)       # size of the optional header
    struct.pack_into("<H", out, pe + 22, 0x0102)     # executable, 32-bit

    opt = pe + 24
    struct.pack_into("<H", out, opt, 0x10B)          # PE32
    struct.pack_into("<I", out, opt + 16, ENTRY_RVA)
    struct.pack_into("<I", out, opt + 28, 0x400000)  # image base
    struct.pack_into("<I", out, opt + 32, 0x1000)    # section alignment
    struct.pack_into("<I", out, opt + 36, 0x200)     # file alignment
    struct.pack_into("<H", out, opt + 68, 2)         # GUI subsystem
    struct.pack_into("<I", out, opt + 56, 0x2000 + len(code))
    struct.pack_into("<I", out, opt + 60, HEADERS_SIZE)
    struct.pack_into("<I", out, opt + 92, 16)        # data directories

    sec = opt + 0xE0
    out[sec:sec + 8] = b".text\0\0"
    struct.pack_into("<I", out, sec + 8, len(code))          # virtual size
    struct.pack_into("<I", out, sec + 12, SECTION_RVA)       # virtual address
    struct.pack_into("<I", out, sec + 16, len(code))         # raw size
    struct.pack_into("<I", out, sec + 20, HEADERS_SIZE)      # raw pointer
    struct.pack_into("<I", out, sec + 36, 0x60000020)        # code, execute, read

    out[HEADERS_SIZE:] = code
    return bytes(out)


def call_to(code: bytearray, at: int, target_rva: int) -> None:
    """Write a five-byte call rel32 at `at`. rel32 is relative to the end of the instruction."""
    next_rva = SECTION_RVA + at + 5
    code[at] = 0xE8
    struct.pack_into("<i", code, at + 1, target_rva - next_rva)


def straddle() -> bytes:
    """A section a few bytes longer than one window, of nops, with a call across the boundary.

    The call starts two bytes before the window ends, so the walk has to notice that the
    instruction it was given is only part of one: decode it again from its own start, count it
    once, and keep going. Reads as `callAt + 1 + (size - callAt - 5)` instructions.
    """
    size = WINDOW_SIZE + 8
    call_at = WINDOW_SIZE - 2
    code = bytearray(b"\x90" * size)
    call_to(code, call_at, ENTRY_RVA)   # one target, so the program has one function, not thousands
    return image(bytes(code))


def nops(count: int) -> bytes:
    """Three windows' worth of nop. One byte, one instruction, so the count is arithmetic."""
    return image(b"\x90" * ((WINDOW_SIZE * 2) + 0x1234 if count is None else count))


def manyfn(count: int) -> bytes:
    """`count` call targets, each a one-instruction function: a program with that many functions."""
    slot = 16                      # a ret and fifteen nops, so each target is its own function
    calls = bytearray(5 * count)
    bodies = bytearray()
    for i in range(count):
        target = SECTION_RVA + (count * 5) + (i * slot)
        call_to(calls, i * 5, target)
        bodies += b"\xC3" + b"\x90" * (slot - 1)
    return image(bytes(calls) + bytes(bodies))


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip())
        return 2
    what, out = argv[1], argv[2] if len(argv) > 2 else f"/tmp/{argv[1]}.exe"
    if what == "straddle":
        data = straddle()
    elif what == "nops":
        data = nops(int(argv[3]) if len(argv) > 3 else None)
    elif what == "manyfn":
        data = manyfn(int(argv[3]) if len(argv) > 3 else 52429)
    else:
        print(f"unknown image: {what}")
        return 2
    with open(out, "wb") as handle:
        handle.write(data)
    print(f"{out}: {len(data):,} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
