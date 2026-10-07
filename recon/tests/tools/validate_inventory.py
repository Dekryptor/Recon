#!/usr/bin/env python3
"""
Independent verification of a recon inventory.

This is a test-harness tool, not part of `recon`. It exists so that the claims the tool makes
about a binary can be measured against sources recon does not own:

  * DWARF DIEs, read with `objdump --dwarf=info` (subprogram names, addresses, sizes),
  * the COFF symbol table, read with `objdump -t`,
  * a GNU ld map file, when one is available.

On top of the cross-checks it verifies the structural invariants of the inventory itself
(ranges inside sections, no overlaps, relocation/xref consistency, jump tables covered by
data ranges, RVAs only, lower-case wire strings).

Usage:
    validate_inventory.py <inventory.json> <binary> [--map FILE] [--objdump TOOL] [--quiet]

Exit status is 0 when nothing structural is wrong, 1 when a high-confidence function is
missing or misplaced, or when an invariant is violated, and 2 on usage errors.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import defaultdict

# --------------------------------------------------------------------------------------- DWARF

DIE_HEADER = re.compile(r"^ <(?P<depth>\d+)><(?P<offset>[0-9a-f]+)>:\s*Abbrev Number:\s*(?P<abbrev>\d+)(?:\s*\((?P<tag>[A-Za-z_0-9]+)\))?")
ATTRIBUTE = re.compile(r"^ +<(?P<offset>[0-9a-f]+)>\s+(?P<name>DW_AT_[A-Za-z_0-9]+)\s*:\s*(?P<value>.*)$")
INDIRECT_STRING = re.compile(r"\(indirect string, offset: 0x[0-9a-f]+\):\s*(?P<text>.*)$")


def parse_dwarf(binary: str, objdump: str) -> list[dict]:
    """Returns one record per DW_TAG_subprogram that has code: name, rva, size."""
    try:
        completed = subprocess.run(
            [objdump, "--dwarf=info", binary], capture_output=True, text=True, check=False
        )
    except FileNotFoundError:
        print(f"error: {objdump} not found", file=sys.stderr)
        return []
    if completed.returncode != 0:
        print(f"error: {objdump} failed: {completed.stderr.strip()[:200]}", file=sys.stderr)
        return []

    # A DIE owns every attribute line that follows it until the next DIE header of the same or a
    # lower depth. Tracking depth is what keeps nested (inlined) subprograms from being counted as
    # top-level functions, and what stops an attribute of a sibling DIE being attributed wrongly.
    die = None
    stack: list[tuple[int, dict]] = []
    found: list[dict] = []

    def close(record: dict) -> None:
        if record is None:
            return
        if record["tag"] != "DW_TAG_subprogram":
            return
        if record["declaration"] or record["low_pc"] is None:
            return
        if record["high_pc"] is None:
            return
        found.append(record)

    for line in completed.stdout.splitlines():
        header = DIE_HEADER.match(line)
        if header:
            depth = int(header.group("depth"))
            while stack and stack[-1][0] >= depth:
                close(stack.pop()[1])
            die = {
                "tag": header.group("tag") or "",
                "depth": depth,
                "name": None,
                "low_pc": None,
                "high_pc": None,
                "declaration": False,
                "inlined": False,
            }
            stack.append((depth, die))
            continue

        if die is None:
            continue

        attribute = ATTRIBUTE.match(line)
        if not attribute:
            continue

        name, value = attribute.group("name"), attribute.group("value").strip()
        indirect = INDIRECT_STRING.match(value)
        if indirect:
            value = indirect.group("text").strip()

        if name == "DW_AT_name":
            die["name"] = value
        elif name == "DW_AT_low_pc":
            match = re.match(r"0x[0-9a-fA-F]+", value)
            if match:
                die["low_pc"] = int(match.group(0), 16)
        elif name == "DW_AT_high_pc":
            match = re.match(r"0x[0-9a-fA-F]+", value)
            if match:
                die["high_pc"] = int(match.group(0), 16)
        elif name == "DW_AT_declaration":
            die["declaration"] = True
        elif name in ("DW_AT_abstract_origin", "DW_AT_specification"):
            # A concrete instance of an abstract function: keep it, the origin supplies the name.
            pass

    while stack:
        close(stack.pop()[1])

    records = []
    for record in found:
        low = record["low_pc"]
        high = record["high_pc"]
        # DWARF 4 and later write high_pc as an offset from low_pc; older versions as an address.
        size = high - low if high >= low else high
        if size <= 0:
            continue
        records.append({"name": record["name"], "low": low, "size": size})
    return records


# ---------------------------------------------------------------------------------------- COFF


def parse_coff(binary: str, objdump: str) -> dict[str, tuple[int, int, bool]]:
    """Returns {name: (section, value, is_function)} for symbols in the COFF symbol table.

    objdump prints the COFF type in hex (0x20 is a function) and the value relative to the section,
    so the caller has to add the section's RVA.
    """
    try:
        completed = subprocess.run(
            [objdump, "-t", binary], capture_output=True, text=True, check=False
        )
    except FileNotFoundError:
        return {}

    symbols = {}
    for line in completed.stdout.splitlines():
        match = re.search(
            r"\(sec\s+(-?\d+)\)\s*\(fl\s+0x[0-9a-f]+\)\s*\(ty\s+([0-9a-f]+)\)\s*"
            r"\(scl\s+([0-9a-f]+)\)\s*\(nx\s+\d+\)\s+0x([0-9a-f]+)\s+(\S+)",
            line,
        )
        if not match:
            continue
        section = int(match.group(1))
        if section <= 0:  # undefined, absolute or debug
            continue
        symbol_type = int(match.group(2), 16)
        name = match.group(5)
        symbols[name] = (section, int(match.group(4), 16), symbol_type == 0x20)
    return symbols


# ----------------------------------------------------------------------------------------- MAP

MAP_LINE = re.compile(r"^\s+0x(?P<addr>[0-9a-f]{8,16})\s+(?P<name>[A-Za-z_?@.$][^\s=]*)\s*$")

# ld64 (and ld64.lld) writes a different shape entirely: address, size, the object file's index and
# then the name, separated by tabs.
LD64_MAP_LINE = re.compile(
    r"^0x(?P<addr>[0-9a-fA-F]{6,16})\t0x[0-9a-fA-F]+\t\[\s*\d+\]\s+(?P<name>\S+)\s*$"
)


def parse_map(path: str) -> dict[str, int]:
    """Returns {name: address} from the symbol dump of a linker's map file: GNU ld's, or ld64's."""
    symbols: dict[str, int] = {}
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = LD64_MAP_LINE.match(line) or MAP_LINE.match(line)
            if match:
                symbols.setdefault(match.group("name"), int(match.group("addr"), 16))
    return symbols


# --------------------------------------------------------------------------- inventory checks


def _starts(functions: list[dict]) -> set[int]:
    return {function["ranges"][0]["rva"] for function in functions}


def check_structure(inventory: dict) -> list[str]:
    """Invariants the inventory must satisfy whatever the binary is."""
    problems: list[str] = []

    image_base = inventory["binary"]["image_base"]
    size_of_image = inventory["binary"]["size_of_image"]
    macho = inventory["binary"]["format"].startswith("macho")
    sections = [(s["name"], s["rva"], s["virtual_size"], s["flags"]) for s in inventory["sections"]]

    def section_of(rva: int) -> str | None:
        for name, start, size, _flags in sections:
            if start <= rva < start + max(size, 1):
                return name
        return None

    # A section's flags are named per format: PE says "code" (IMAGE_SCN_CNT_CODE), ELF says "exec"
    # (SHF_EXECINSTR). Both mean the same thing, and an oracle that only knew one of them would call
    # every address in an ELF binary "not code".
    def is_code(rva: int) -> bool:
        for _name, start, size, flags in sections:
            if start <= rva < start + max(size, 1):
                return "code" in flags or "exec" in flags
        return False

    if not inventory["functions"]:
        problems.append("functions[] is empty")

    seen_starts: dict[int, str] = {}
    for function in inventory["functions"]:
        name = function.get("name") or function["id"]
        ranges = function["ranges"]
        if not ranges:
            problems.append(f"{name}: no ranges")
            continue

        for index, entry in enumerate(ranges):
            start, size = entry["rva"], entry["size"]
            if start >= size_of_image and start != 0:
                problems.append(f"{name}: rva 0x{start:X} is outside the image")
            if size <= 0:
                problems.append(f"{name}: range {index} has size {size}")
            if section_of(start) is None:
                problems.append(f"{name}: rva 0x{start:X} is in no section")

        ordered = sorted(ranges, key=lambda r: r["rva"])
        if [r["rva"] for r in ordered] != [r["rva"] for r in ranges]:
            problems.append(f"{name}: ranges are not sorted by rva")
        for first, second in zip(ordered, ordered[1:]):
            if first["rva"] + first["size"] > second["rva"]:
                problems.append(f"{name}: ranges overlap")
        if ranges[0]["rva"] in seen_starts:
            problems.append(
                f"{name}: shares its start address with {seen_starts[ranges[0]['rva']]} "
                "(an alias should be listed as one function)"
            )
        seen_starts[ranges[0]["rva"]] = name

        if function["confidence"] not in ("low", "medium", "high"):
            problems.append(f"{name}: confidence {function['confidence']!r} is not a wire value")

    relocation_sites = {relocation["rva"] for relocation in inventory["relocations"]}
    xref_pairs = {(xref["from_rva"], xref["to_rva"]) for xref in inventory["xrefs"]}
    # PE names its relocation types (HIGHLOW, DIR64); ELF spells out the machine and the
    # calculation (R_X86_64_RELATIVE, R_386_PC32). Both are accepted, and anything else is not.
    pe_kinds = ("ABSOLUTE", "HIGHLOW", "DIR64", "HIGH", "LOW", "HIGHADJ")
    elf_prefixes = ("R_X86_64_", "R_386_", "R_AARCH64_", "R_ARM_")
    for relocation in inventory["relocations"]:
        kind = relocation["kind"]
        if kind not in pe_kinds and not kind.startswith(elf_prefixes):
            problems.append(f"relocation at 0x{relocation['rva']:X}: unexpected kind {kind!r}")
        if relocation["rva"] >= size_of_image:
            problems.append(f"relocation at 0x{relocation['rva']:X} is outside the image")

    # Every relocation inside code has to show up as an xref, which is what "relocations are
    # linked to the instructions that use them" (milestone 1) means in practice.
    for site in (site for site in relocation_sites if is_code(site)):
        if not any(from_rva == site for from_rva, _to in xref_pairs):
            problems.append(f"relocation at code address 0x{site:X} has no xref")

    function_ranges = [
        (r["rva"], r["rva"] + r["size"], f.get("name") or f["id"])
        for f in inventory["functions"]
        for r in f["ranges"]
    ]

    def owning_function(rva: int) -> str | None:
        for start, end, name in function_ranges:
            if start <= rva < end:
                return name
        return None

    data_ranges = [(d["rva"], d["rva"] + d["size"]) for d in inventory["data"] if d["size"] > 0]

    def covered_by_data(start: int, end: int) -> bool:
        return any(data_start <= start and end <= data_end for data_start, data_end in data_ranges)

    for table in inventory["jump_tables"]:
        span_end = table["rva"] + 4 * table["entries"]
        if not covered_by_data(table["rva"], span_end):
            problems.append(
                f"jump table at 0x{table['rva']:X} ({table['entries']} entries) is not covered by data[]"
            )
        owner = owning_function(table["rva"])
        if owner is not None and not (macho and all(owning_function(target) == owner for target in table["targets"])):
            # clang on x86-64 Mach-O puts a switch's table inside the function that reads it, with
            # the arms around it. That is not code misread as a table — which is what this rule is
            # for — when every target is another arm of the same function.
            problems.append(f"jump table at 0x{table['rva']:X} overlaps function {owner}")
        for target in table["targets"]:
            if not is_code(target):
                problems.append(f"jump table at 0x{table['rva']:X}: target 0x{target:X} is not code")

    for entry in inventory["data"]:
        if entry["size"] == 0:
            continue
        if owning_function(entry["rva"]) is not None and entry["kind"] != "jump_table":
            problems.append(f"data at 0x{entry['rva']:X} overlaps a function")

    # Lower-case wire strings: the JSON contract is written in lower case throughout.
    lowering_checks = [
        ("binary.format", inventory["binary"]["format"]),
        ("binary.arch", inventory["binary"]["arch"]),
        ("binary.isa", inventory["binary"]["isa"]),
    ]
    for where, value in lowering_checks:
        if value != value.lower():
            problems.append(f"{where} must be a lower-case wire string")

    for function in inventory["functions"]:
        for key in ("found_by", "flags"):
            for value in function[key]:
                if value != value.lower():
                    problems.append(f"{function.get('name')}: {key} value {value!r} must be lower case")
        for source in function.get("sizes", {}).get("sources", []):
            if source != source.lower():
                problems.append(f"{function.get('name')}: size source {source!r} must be lower case")

    if image_base % 0x1000 != 0:
        problems.append("image_base is not page aligned")
    # An entry point of 0 means "this file has none", which is the normal state for a shared
    # object: it is loaded by something else and never entered. Any other value has to point
    # inside the image.
    if inventory["binary"]["entry_rva"] and not 0 < inventory["binary"]["entry_rva"] < size_of_image:
        problems.append("entry_rva is not inside the image")

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inventory")
    parser.add_argument("binary")
    parser.add_argument("--map", dest="map_path")
    parser.add_argument("--objdump", default="objdump")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    with open(args.inventory, encoding="utf-8") as handle:
        inventory = json.load(handle)

    image_base = inventory["binary"]["image_base"]
    functions = inventory["functions"]

    by_name: dict[str, dict] = {}
    by_start: dict[int, dict] = {}
    for function in functions:
        for key in filter(None, (function.get("name"), function.get("demangled"))):
            by_name.setdefault(key, function)
        by_start.setdefault(function["ranges"][0]["rva"], function)
        for alias in function.get("aliases", []):
            by_name.setdefault(alias, function)

    stats: dict[str, dict[str, int]] = defaultdict(
        lambda: {"total": 0, "found": 0, "addr_ok": 0, "size_exact": 0, "size_close": 0, "size_bad": 0}
    )
    missing: list[str] = []
    both_wrong: list[tuple[str, int, int, int, int]] = []
    size_outliers: list[tuple[str, int, int]] = []
    size_near: list[tuple[str, int, int]] = []

    # GNU objdump reads ELF and PE; a Mach-O file is not one of those, and asking it produces
    # nothing but a complaint. The Mach-O oracle is the linker's map file.
    dwarf = [] if inventory["binary"]["format"].startswith("macho") else parse_dwarf(args.binary, args.objdump)
    for record in dwarf:
        name, low, size = record["name"], record["low"] - image_base, record["size"]
        function = by_name.get(name) if name else None
        if function is None and name is None:
            function = by_start.get(low)
        if function is None:
            missing.append(name or f"<unnamed at 0x{low:X}>")
            continue

        confidence = function["confidence"]
        bucket = stats[confidence]
        bucket["total"] += 1
        bucket["found"] += 1

        rva = function["ranges"][0]["rva"]
        reported = function["ranges"][0]["size"]
        if rva == low:
            bucket["addr_ok"] += 1
        else:
            both_wrong.append((name or function["id"], low, rva, size, reported))
        if size == 0:
            bucket["size_exact"] += 1
        elif reported == size:
            bucket["size_exact"] += 1
        elif abs(reported - size) <= max(4, size // 8):
            bucket["size_close"] += 1
            size_near.append((name or function["id"], size, reported))
        else:
            bucket["size_bad"] += 1
            size_outliers.append((name or function["id"], size, reported))

    if not args.quiet:
        print(f"DWARF subprograms with a body: {len(dwarf)}")
        print(f"present in the inventory:      {sum(b['found'] for b in stats.values())}")
        if stats:
            print()
            print(f"{'confidence':<12}{'matched':>8}{'addr ok':>9}{'size exact':>12}{'size close':>12}{'size off':>10}")
            for confidence, bucket in sorted(stats.items()):
                print(
                    f"{confidence:<12}{bucket['found']:>8}{bucket['addr_ok']:>9}"
                    f"{bucket['size_exact']:>12}{bucket['size_close']:>12}{bucket['size_bad']:>10}"
                )

        if missing:
            print()
            print(f"not in the inventory ({len(missing)}): {', '.join(sorted(missing)[:12])}"
                  + (" ..." if len(missing) > 12 else ""))
        if size_near:
            print()
            print("sizes within the tolerance but not exact (dwarf, inventory):")
            for name, expected, reported in size_near[:10]:
                print(f"  {name:<30} {expected:>7} {reported:>7}")
        if size_outliers:
            print()
            print("size disagreements (dwarf, inventory):")
            for name, expected, reported in sorted(size_outliers, key=lambda e: -abs(e[1] - e[2]))[:10]:
                print(f"  {name:<30} {expected:>7} {reported:>7}")

    failures = 0

    if both_wrong:
        print()
        print(f"ERROR: {len(both_wrong)} DWARF function(s) at the wrong address: {both_wrong[:5]}")
        failures += 1

    hard_missing = [name for name in missing if not name.startswith("<unnamed")]
    if hard_missing:
        print()
        print(f"ERROR: {len(hard_missing)} DWARF function(s) missing from the inventory: "
              f"{hard_missing[:8]}")
        failures += 1

    coverage = sum(b["found"] for b in stats.values()) / len(dwarf) if dwarf else 1.0
    if dwarf and coverage < 0.95:
        print()
        print(f"ERROR: DWARF coverage is {coverage:.0%}, below the 95% floor")
        failures += 1

    if args.map_path:
        map_symbols = parse_map(args.map_path)
        if map_symbols:
            known = set(by_name)
            in_image = {
                name: address - image_base
                for name, address in map_symbols.items()
                if image_base <= address < image_base + inventory["binary"]["size_of_image"]
            }
            found = [name for name in in_image if name in known]
            # The map also lists section symbols, labels and linker-generated names, so a symbol
            # missing here is only interesting when its address has no function at all.
            data_starts = {d["rva"] for d in inventory["data"]}
            unexplained = [
                name for name in in_image
                if name not in known and in_image[name] not in by_start and in_image[name] not in data_starts
            ]
            if not args.quiet:
                print()
                print(f"map symbols inside the image:  {len(in_image)}")
                print(f"  matched by name:             {len(found)}")
                print(f"  at an address with no function: {len(unexplained)}")
                if unexplained:
                    print(f"    e.g. {', '.join(sorted(unexplained)[:8])}")
            if len(in_image) and len(unexplained) > 0.1 * len(in_image):
                print("ERROR: more than 10% of map symbols have neither a function nor data at their address")
                failures += 1

    # The COFF symbol table is the second independent list of functions: every external or static
    # function the linker recorded. Unlike DWARF it covers functions with no debug info, so it is
    # what catches a *missing* function rather than a wrong one.
    coff = {} if inventory["binary"]["format"].startswith("macho") else parse_coff(args.binary, args.objdump)
    if coff:
        starts = _starts(functions)
        bases = [section["rva"] for section in inventory["sections"]]

        def coff_rva(section: int, value: int) -> int | None:
            # COFF section numbers are 1-based and follow the section table order.
            return bases[section - 1] + value if 1 <= section <= len(bases) else None

        rvas = {name: coff_rva(section, value) for name, (section, value, _f) in coff.items()}
        function_symbols = {name for name, (_s, _v, is_function) in coff.items() if is_function}
        misplaced = [
            (name, rvas[name]) for name in function_symbols
            if rvas[name] is not None and rvas[name] not in starts
        ]
        if not args.quiet:
            print()
            print(f"COFF function symbols:         {len(function_symbols)}")
            print(f"  at a function start:         {len(function_symbols) - len(misplaced)}")
        if misplaced:
            print()
            print(f"ERROR: {len(misplaced)} COFF function symbol(s) have no function at their address: "
                  f"{[(n, hex(a)) for n, a in misplaced[:8]]}")
            failures += 1

    structural = check_structure(inventory)
    if structural:
        print()
        print(f"ERROR: {len(structural)} structural problem(s):")
        for problem in structural[:20]:
            print(f"  {problem}")
        if len(structural) > 20:
            print(f"  ... and {len(structural) - 20} more")
        failures += 1
    elif not args.quiet:
        print()
        print("structural invariants: ok")

    print()
    print("RESULT:", "FAIL" if failures else "ok")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
