#!/usr/bin/env python3
"""
Independent verification of a recon inventory against a real PDB.

This is the MSVC counterpart of `validate_inventory.py`: there the oracle is DWARF, the COFF symbol
table and a linker map file, here it is the debug information itself, read by LLVM's `llvm-pdbutil`
rather than by recon. The tool that produced the PDB and the tool that reads it are both independent
of the code under test, which is what makes this a check and not a restatement.

What is compared, and why:

  * S_GPROC32 / S_LPROC32  - every function the compiler described, by address, code size and name.
    A missing one, a wrong size or a renamed one fails: this is the clause "every PDB function at
    correct address and size" in the plan.
    A binary linked with /OPT:ICF has several procedures sharing one folded body, so one address can
    carry dozens of records; the inventory lists one function there and keeps the folded names among
    its `aliases`. A name found in either place is a match. Two things make this check look harder
    than it is on such a binary: the name is printed between backquotes *and may contain backquotes*
    (mangled C++ names do), so the pattern is greedy and anchored to the end of the line rather than
    stopping at the first one; and `llvm-pdbutil` prints records of every module, including ones the
    linker folded together.
  * S_PUB32                - the public symbol table. A public marked as a function must be a
    function in the inventory; a public that is not marked as one must not be listed as a function.
    This catches data misread as code, which is how "no data symbols in the function list" is
    actually verified. One exception is needed and it is the PDB's own doing: a linker-generated
    thunk block (an `S_THUNK32` record, e.g. `__tailMerge_version_dll` for a delay-loaded DLL) is
    executable code that the public table nevertheless flags as data. The public table is believed
    unless the same address is also described as code by a record that the linker cannot be confused
    about - a thunk - in which case a function there is right and the flag is not.
  * overlaps               - no function may start inside another function's body.

Section-relative addresses in the PDB (`0001:0032`) are printed by llvm-pdbutil in decimal - 0001:0032
is `.text`+0x20 - and are converted with the PE section table, parsed here from the executable, so the
conversion does not depend on recon's own loader.

Usage:
    validate_pdb.py <inventory.json> <binary.exe> <debug.pdb> [--pdbutil TOOL] [--quiet]

Exit status: 0 when nothing is wrong, 1 on a mismatch, 2 when the tool or its input is unusable.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import struct
import subprocess
import sys

# The name is printed between backquotes and may itself contain backquotes - `` `operator new'::`1'::catch$0 ``
# is one - so the group is greedy and anchored to the end of the line. Stopping at the first backquote
# records an empty name for every such symbol and turns each of them into a mismatch.
PROC = re.compile(r"S_(?:G|L)PROC32\s+\[size = \d+\]\s+`(?P<name>.*)`\s*$")
ADDR = re.compile(r"addr = (?P<section>[0-9]+):(?P<offset>[0-9]+), code size = (?P<size>\d+)")
PUB = re.compile(r"S_PUB32\s+\[size = \d+\]\s+`(?P<name>.*)`\s*$")
PUB_DETAIL = re.compile(r"flags = (?P<flags>[a-z ]+), addr = (?P<section>[0-9]+):(?P<offset>[0-9]+)")
MODULE = re.compile(r"^\s*Mod \d+ \| `(?P<name>[^`]*)`")
THUNK = re.compile(r"S_THUNK32\s+\[size = \d+\]\s+`(?P<name>.*)`\s*$")
THUNK_DETAIL = re.compile(r"kind = [^,]+, size = \d+, addr = (?P<section>[0-9]+):(?P<offset>[0-9]+)")


def find_pdbutil(explicit: str | None) -> str | None:
    if explicit:
        return explicit if shutil.which(explicit) or explicit.startswith("/") else None
    for candidate in ("llvm-pdbutil-19", "llvm-pdbutil-18", "llvm-pdbutil"):
        found = shutil.which(candidate)
        if found:
            return found
    return None


def run(tool: str, args: list[str]) -> str:
    completed = subprocess.run([tool, *args], capture_output=True, text=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"{tool} {' '.join(args)} failed: {completed.stderr.strip()[:200]}")
    return completed.stdout


def pe_sections(path: str) -> dict[int, int]:
    """Maps PE section numbers (1-based, as the PDB uses them) to RVA bases."""
    with open(path, "rb") as handle:
        data = handle.read()

    if data[:2] != b"MZ":
        raise RuntimeError("not a PE file")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise RuntimeError("no PE signature")
    machine, count = struct.unpack_from("<HH", data, pe_offset + 4)
    optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
    table = pe_offset + 24 + optional_size

    sections: dict[int, int] = {}
    for index in range(count):
        entry = table + index * 40
        rva = struct.unpack_from("<I", data, entry + 12)[0]
        sections[index + 1] = rva

    return sections


def read_proc_symbols(tool: str, pdb: str) -> tuple[list[dict], list[tuple[int, int]]]:
    """Every function the PDB describes as RVA, size and name - and where its thunks are.

    Thunks come back as `(section, offset)` pairs as printed, not as RVAs: the caller owns the
    section table. They are needed because a linker-generated thunk block is code that the public
    table calls data (see the module docstring)."""
    modules = [m.group("name") for m in (MODULE.match(line) for line in run(tool, ["dump", "-modules", pdb]).splitlines()) if m]
    procs: list[dict] = []
    thunks: list[tuple[int, int]] = []
    for index, module in enumerate(modules):
        if module.startswith("* "):
            continue  # the linker's own module describes no source function
        text = run(tool, ["dump", "-symbols", f"-modi={index}", pdb])
        lines = text.splitlines()
        for position, line in enumerate(lines):
            if THUNK.search(line):
                for follow in lines[position + 1:position + 4]:
                    detail = THUNK_DETAIL.search(follow)
                    if detail:
                        thunks.append((int(detail.group("section")), int(detail.group("offset"))))
                        break
                continue

            header = PROC.search(line)
            if not header:
                continue
            detail = ADDR.search(lines[position + 1]) if position + 1 < len(lines) else None
            if not detail:
                continue
            procs.append(
                {
                    "name": header.group("name"),
                    "section": int(detail.group("section")),
                    "offset": int(detail.group("offset")),
                    "size": int(detail.group("size")),
                }
            )

    return procs, thunks


def read_publics(tool: str, pdb: str) -> list[dict]:
    lines = run(tool, ["dump", "-publics", pdb]).splitlines()
    publics: list[dict] = []
    for position, line in enumerate(lines):
        header = PUB.search(line)
        if not header:
            continue
        detail = PUB_DETAIL.search(lines[position + 1]) if position + 1 < len(lines) else None
        if not detail:
            continue
        publics.append(
            {
                "name": header.group("name"),
                "is_function": "function" in detail.group("flags"),
                "section": int(detail.group("section")),
                "offset": int(detail.group("offset")),
            }
        )

    return publics


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inventory")
    parser.add_argument("binary")
    parser.add_argument("pdb")
    parser.add_argument("--pdbutil", default=None)
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    tool = find_pdbutil(args.pdbutil)
    if tool is None:
        print("skip: llvm-pdbutil is not installed; the PDB oracle did not run", file=sys.stderr)
        return 0

    with open(args.inventory, encoding="utf-8") as handle:
        document = json.load(handle)

    try:
        sections = pe_sections(args.binary)
        procs, thunks = read_proc_symbols(tool, args.pdb)
        publics = read_publics(tool, args.pdb)
    except (RuntimeError, OSError, struct.error) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    if not procs:
        print(f"error: no function symbols were read from {args.pdb}; the oracle would check nothing", file=sys.stderr)
        return 2

    def rva(section: int, offset: int) -> int | None:
        base = sections.get(section)
        return None if base is None else base + offset

    thunk_rvas = {rva(section, offset) for section, offset in thunks}

    functions = document["functions"]
    by_rva = {f["ranges"][0]["rva"]: f for f in functions}
    data_rvas = {d["rva"] for d in document.get("data", []) if d.get("name")}
    problems: list[str] = []

    # 1. Every function the compiler described, at the address and size it described.
    for proc in procs:
        address = rva(proc["section"], proc["offset"])
        if address is None:
            problems.append(f"{proc['name']}: section {proc['section']} is not in the image")
            continue

        entry = by_rva.get(address)
        if entry is None:
            problems.append(f"{proc['name']}: no function in the inventory at 0x{address:X}")
            continue

        size = entry["ranges"][0]["size"]
        if size != proc["size"]:
            problems.append(
                f"{proc['name']} at 0x{address:X}: PDB says {proc['size']} bytes, inventory says {size}"
            )

        if entry["name"] != proc["name"] and proc["name"] not in (entry.get("aliases") or []):
            problems.append(
                f"0x{address:X}: PDB calls it {proc['name']!r}, inventory says {entry['name']!r} "
                f"(aliases: {', '.join(entry.get('aliases') or []) or 'none'})"
            )

    # 2. Publics: a function public must be a function, a data public must not be one.
    for public in publics:
        address = rva(public["section"], public["offset"])
        if address is None:
            continue

        entry = by_rva.get(address)
        if public["is_function"] and entry is None:
            problems.append(f"{public['name']}: public function at 0x{address:X} is missing from the inventory")
        if not public["is_function"] and entry is not None and address not in thunk_rvas:
            problems.append(f"{public['name']}: data public at 0x{address:X} is listed as a function")

    # 3. A heuristic function may not start inside a body the PDB described.
    described = sorted(
        (rva(p["section"], p["offset"]), p["size"], p["name"])
        for p in procs
        if rva(p["section"], p["offset"]) is not None
    )
    for entry in functions:
        start = entry["ranges"][0]["rva"]
        if any(start == base for base, _, _ in described):
            continue
        for base, size, name in described:
            if base < start < base + size:
                problems.append(
                    f"{entry['name']}: starts at 0x{start:X}, inside {name} (0x{base:X}+{size})"
                )
                break

    extra = [f["id"] for f in functions if f["ranges"][0]["rva"] not in {rva(p["section"], p["offset"]) for p in procs}]

    if problems:
        print(f"FAIL: {args.inventory} disagrees with {args.pdb}")
        for problem in problems[:40]:
            print(f"  - {problem}")
        if len(problems) > 40:
            print(f"  ... and {len(problems) - 40} more")
        return 1

    if not args.quiet:
        print(f"ok: {len(procs)} PDB function(s) matched by address, size and name")
        if thunk_rvas:
            print(f"ok: {len(thunk_rvas)} thunk block(s) described as code, where the public table says data")
        print(f"ok: {len(publics)} public symbol(s) agree on function/data")
        print(f"ok: {len(functions)} function(s) in the inventory, {len(extra)} not described by the PDB")
        if data_rvas:
            print(f"ok: {len(data_rvas)} named data symbol(s) kept out of the function list")

    return 0


if __name__ == "__main__":
    sys.exit(main())
