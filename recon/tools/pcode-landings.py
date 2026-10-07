#!/usr/bin/env python3
"""Check every branch operand and every frame slot of a corpus of VB6 p-code programs.

    tools/pcode-landings.py <corpus-dir> --runtime <msvbvm60.dll> [--binary <recon>] [--limit N]

Two claims the p-code reader makes are measured here, because both are what make an operand's
*meaning* evidence rather than a guess:

  * **a branch operand is a distance in the instruction stream.** For every row whose operand the
    opcode reader calls a `target`, the word the row says is the operand is resolved against the
    procedure's own stream base, and the address it names has to be the start of an instruction of
    the *same* procedure, inside its code. A 16-bit word landing on an instruction boundary is not
    a coincidence: this is the check that refused the first classifier, which had 184 rows it called
    targets.
  * **a frame slot is inside the frame.** Every `frame_slot` a listing names has to lie inside
    `[-(frame_size + 132), argument_size + 4]`, where `frame_size` and `argument_size` are the
    procedure's own descriptor's numbers. The ±132 window is the runtime's convention, not a rule
    from a specification: the deepest slot in the corpus sits exactly 132 bytes below `frame_size`.

Over the 42-program corpus this printed **1,877 of 1,878 branch operands landing** and **25,812 of
25,812 frame slots in the window**. The one operand that does not land is named there and in
`docs/m7-status.md`: it is `FD 0C` (`Resume`) with the word `0xFFFF`, a sentinel whose handler
compares the word against -2 and -1 and follows those to a different stream rather than adding
them, so the word is not an offset at all. A nonzero exit from this script means a claim failed,
and the failing operand, procedure and program are printed as they are found.

The corpus is not part of the repository (it is third-party software; `NOTICES` in the input
directory records where each piece came from) and neither is the runtime. Point this at a directory
of `.exe` files and at a real `msvbvm60.dll`.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import subprocess
import sys

# Directories that hold the corpus's own sources rather than built programs.
SKIP_DIRS = {"source", ".git", "__pycache__"}


def exes(root: str) -> list[str]:
    found = []
    for directory, children, files in os.walk(root):
        children[:] = [c for c in children if c not in SKIP_DIRS]
        for name in sorted(files):
            if name.lower().endswith(".exe"):
                found.append(os.path.join(directory, name))
    return sorted(found)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("corpus", help="directory to walk for .exe files")
    parser.add_argument("--runtime", required=True, help="the VB6 runtime the programs run on (msvbvm60.dll)")
    parser.add_argument("--binary", default="recon", help="the recon binary to run (default: recon on PATH)")
    parser.add_argument("--limit", type=int, default=0, help="stop after this many programs (default: all)")
    parser.add_argument("--quiet", action="store_true", help="print only the summary and the failures")
    args = parser.parse_args()

    def run(argv: list[str]) -> dict:
        result = subprocess.run(argv, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"{' '.join(argv[1:3])}: exit {result.returncode}: {result.stderr.strip()[:200]}")
        return json.loads(result.stdout)

    def pcode(path: str, *extra: str) -> dict:
        return run([args.binary, "pcode", path, "--runtime", args.runtime, "--json", *extra])

    programs = exes(args.corpus)
    if args.limit:
        programs = programs[:args.limit]
    if not programs:
        # A walk that found nothing has proved nothing, and printing a clean summary for it is how a
        # measurement becomes a skip that looks like a pass: this is an error, not a result.
        print(f"no .exe files under {args.corpus}: there is nothing here to measure", file=sys.stderr)
        return 1

    per_opcode = collections.Counter()
    landed = collections.Counter()
    nonzero_landed = collections.Counter()
    zero_word = collections.Counter()
    rows = words = landed_total = procedures = with_body = slots = 0
    failures: list[str] = []
    no_listing: list[str] = []

    for number, program in enumerate(programs, 1):
        try:
            summary = pcode(program)
        except RuntimeError as failure:
            no_listing.append(f"{program}: {failure}")
            continue
        listed = False
        for procedure in summary["procedures"]:
            selected = pcode(program, "--object", procedure["object"],
                             "--procedure", str(procedure["method"]))
            instructions = selected.get("instructions") or []
            if not instructions:
                no_listing.append(f"{program} {procedure['object']}[{procedure['method']}]: no instructions")
                continue
            listed = True
            procedures += 1
            start = procedure["code_rva"]
            end = start + procedure["code_size"]
            starts = {instruction["rva"] for instruction in instructions}
            for instruction in instructions:
                rows += 1
                if instruction["frame_slot"] is not None:
                    slots += 1
                    low = -(procedure["frame_size"] + 132)
                    high = procedure["argument_size"] + 4
                    if not low <= instruction["frame_slot"] <= high:
                        failures.append(
                            f"slot out of frame: {program} {procedure['object']}[{procedure['method']}] "
                            f"{hex(instruction['rva'])} slot {instruction['frame_slot']} frame "
                            f"{procedure['frame_size']} argument {procedure['argument_size']}")
                if instruction["operand"] != "target":
                    continue
                raw = bytes.fromhex(instruction["bytes"].replace(" ", ""))
                at = len(raw) - instruction["operand_bytes"] + instruction["operand_at"]
                word = int.from_bytes(raw[at:at + 2], "little")
                key = (instruction["opcode"], instruction["size"])
                per_opcode[key] += 1
                words += 1
                if word == 0:
                    zero_word[key] += 1
                if instruction["target_is_an_instruction_start"] and instruction["target"] in starts:
                    landed_total += 1
                    landed[key] += 1
                    if word:
                        nonzero_landed[key] += 1
                else:
                    where = "an instruction start" if instruction["target"] in starts \
                        else "the middle of an instruction" if start <= instruction["target"] < end \
                        else "outside the procedure"
                    failures.append(
                        f"does not land: {program} {procedure['object']}[{procedure['method']}] "
                        f"at {hex(instruction['rva'])} opcode {instruction['opcode']} word {word} -> "
                        f"{hex(instruction['target'])} (start {hex(start)}, end {hex(end)}: {where})")
        if listed:
            with_body += 1
        if not args.quiet:
            print(f"[{number}/{len(programs)}] {with_body} programs, {procedures} procedures, {rows} rows, "
                  f"{words} branch operands, {landed_total} land, {slots} slots", file=sys.stderr, flush=True)

    print(f"{len(programs)} programs ({with_body} with a body), {procedures} procedure listings")
    print(f"{rows} instruction rows")
    print(f"{words} branch operands, {landed_total} land on an instruction start")
    nonzero = words - sum(zero_word.values())
    print(f"{nonzero} of them have a non-zero word, and {sum(nonzero_landed.values())} of those land")
    print(f"{slots} frame slots named, checked against [-(frame_size+132), argument_size+4]")
    print()
    print(f"{'opcode':10}{'size':>5}{'words':>7}{'land':>6}{'word=0':>8}{'nz':>5}{'nz land':>9}")
    for key in sorted(per_opcode, key=lambda k: (-per_opcode[k], k)):
        opcode, size = key
        count = per_opcode[key]
        print(f"{opcode:10}{size:>5}{count:>7}{landed[key]:>6}{zero_word[key]:>8}"
              f"{count - zero_word[key]:>5}{nonzero_landed[key]:>9}")
    if no_listing:
        print(f"\n{len(no_listing)} program(s) or procedure(s) with no listing:")
        for item in no_listing:
            print(f"   {item}")
    if failures:
        print(f"\n{len(failures)} claim(s) that failed:")
        for item in failures[:60]:
            print(f"   {item}")
    else:
        print("\nevery branch operand landed and every slot was in frame")
    return 1 if (failures or no_listing) else 0


if __name__ == "__main__":
    sys.exit(main())
