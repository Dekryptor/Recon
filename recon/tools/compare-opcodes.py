#!/usr/bin/env python3
"""Compare the measured opcode lengths against another reconstruction, row by row.

    tools/compare-opcodes.py --ours <opcodes.json> --theirs <opcodes.csv>

`--ours` is `recon opcodes <msvbvm60.dll> --json`, which states a length for each row of each of the
six tables. `--theirs` is a CSV with `table,opcode,size` columns — the shape the Rust `visualbasic`
crate ships in `data/opcodes.csv`, where table 0 is the primary table and 1..5 are the tables the lead
bytes 0xFB..0xFF select.

It also compares *stack effects*, against the `pops`/`pushes` columns of the same CSV. Those are the
operand-stack slots an opcode takes and leaves, so their difference is the effect this tool measures
from the handler's machine code, where a positive number is slots added. The comparison is expected to
disagree where the two are counting different things: the crate counts a variant as four slots, and a
handler that writes one through the stack moves the pointer by sixteen bytes but pushes nothing, so
rows like `LitVarI2` (0/4 there, plus one here) are a difference in units and not in reading. Where the
two agree is where they are counting the same thing — a call that pops its own arguments — and that is
the number to look at.

It also compares *names*, which is a weaker comparison on purpose: this tool names a row after the
runtime function its handler calls, and the crate names it after the operation as the author read it.
The two traditions put different words on the same function, so the comparison strips the conventional
prefixes (`vba`, `rtc`, `Fn`, `C`) and compares what is left, then falls back to a distinctive word in
common. Rows where neither matches are printed to be read, because a disagreement two ways is worth an
eye.

It also compares *operands*, which is the weakest of the four comparisons and is printed as a list to
read rather than as an agreement to quote. This tool states what the bytes after an opcode are as a
kind — none, data, a frame slot, a target — read from the handler's own instructions, and says where
in the operand the word it is about sits; the crate states an `operand_format` such as `%a %2` or
`%a %s %l`. The two are not the same kind of statement: `%a` is a frame slot on both sides and those
rows are the comparison that means something, while the crate's `%2` on a `Next` is the distance the
instruction branches by (this reader calls it a target, and the corpus is what says so: every one of
them lands on an instruction start). The rows where this reader says `data` and the format names a
slot or a label are printed, because those are the places this reading is *less* than the other one,
and a reader of the documentation should be able to see how many there are and what they are.

The point of this tool is that the agreement it prints is a number in the documentation, and a number
in the documentation should have a command behind it. Run it, get the same counts, and disagree with
the prose if the counts move.

Exit status is 0 when the only length differences are ones this tool can name as a shape (a row either
side measures in two readings, or a row one side does not state a length for), and 1 otherwise, so it
can be used as a check rather than only as a report.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import sys

# The crate's table numbering: 0 is the primary table, 1..5 are the lead bytes.
LEAD_OF = {0: None, 1: 0xFB, 2: 0xFC, 3: 0xFD, 4: 0xFE, 5: 0xFF}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ours", required=True, help="recon opcodes --json output")
    parser.add_argument("--theirs", required=True, help="a CSV with table,opcode,size columns")
    args = parser.parse_args()

    indexed: dict[tuple[int | None, int], dict] = {
        (row["lead"], row["opcode"]): row for row in json.load(open(args.ours))["opcodes"]
    }
    ours: dict[tuple[int | None, int], tuple[int, ...]] = {
        key: tuple(sorted(set(row["sizes"]))) for key, row in indexed.items() if row["sizes"]
    }

    theirs: dict[tuple[int | None, int], tuple[int, str]] = {}
    with open(args.theirs, newline="", encoding="utf-8") as handle:
        rows = [line for line in handle if not line.startswith("#")]
    for row in csv.DictReader(rows):
        table = int(row["table"])
        theirs[(LEAD_OF[table], int(row["opcode"].strip(), 16))] = (
            int(row["size"]), row.get("mnemonic", "").strip())

    stated = {key: value for key, value in theirs.items() if value[0] >= 1}
    def order(key: tuple[int | None, int]) -> tuple[int, int]:
        # The primary table (no lead byte) first, then the lead bytes in order.
        return (-1 if key[0] is None else key[0], key[1])

    both = sorted(set(stated) & set(ours), key=order)
    agree = [key for key in both if ours[key] == (stated[key][0],)]
    differ = [key for key in both if ours[key] != (stated[key][0],)]

    def label(key: tuple[int | None, int]) -> str:
        lead = "primary" if key[0] is None else f"0x{key[0]:02X}"
        return f"{lead} 0x{key[1]:02X}"

    print(f"rows: ours {len(ours)} measured, theirs {len(stated)} stated, {len(both)} comparable")
    print(f"agree {len(agree)} ({100 * len(agree) / len(both):.1f}%), differ {len(differ)}")
    for key in differ:
        print(f"  {label(key):<14} ours {ours[key]} theirs {stated[key][0]} ({stated[key][1]})")

    ours_only = sorted(set(ours) - set(stated), key=order)
    theirs_only = sorted(set(stated) - set(ours), key=order)
    print(f"measured here and not stated there: {len(ours_only)}")
    print(f"stated there and not measured here: {len(theirs_only)}")

    # Stack: the crate's pops and pushes against the effect measured from the handler's own code. A
    # missing `pops` or `pushes` of -1 means "depends on the operand", so those rows are not comparable.
    effect = {key: row["stack_effect"] for key, row in indexed.items()}
    with open(args.theirs, newline="", encoding="utf-8") as handle:
        stack_rows = [line for line in handle if not line.startswith("#")]
    comparable = 0
    stack_agree = []
    stack_differ = []
    for row in csv.DictReader(stack_rows):
        key = (LEAD_OF[int(row["table"])], int(row["opcode"].strip(), 16))
        try:
            pops, pushes = int(row["pops"]), int(row["pushes"])
        except (KeyError, ValueError):
            continue
        if pops < 0 or pushes < 0 or effect.get(key) is None:
            continue
        comparable += 1
        net = pushes - pops
        (stack_agree if net == effect[key] else stack_differ).append((key, net, effect[key], pops, pushes))

    print()
    if comparable:
        print(f"stack: {comparable} rows where both sides state an effect")
        print(f"  the same net slots: {len(stack_agree)} ({100 * len(stack_agree) / comparable:.0f}%)")
        print(f"  different:          {len(stack_differ)}")
        print("  (a difference in units shows up here as a variant counted as four slots over there,")
        print("   and as the bytes the handler moved the stack pointer by over here)")
        for key, net, ours_effect, pops, pushes in stack_differ[:12]:
            print(f"    {label(key):<14} pops {pops} pushes {pushes} = {net:3d} there, {ours_effect:3d} here")
        if len(stack_differ) > 12:
            print(f"    … {len(stack_differ) - 12} more")

    # Names: the same rows, matched by what is left after the two naming conventions are taken off.
    names_ours = {key: row["name"] for key, row in indexed.items() if row["name"]}
    shared = sorted(set(names_ours) & set(stated), key=order)
    same, word, none = [], [], []
    for key in shared:
        theirs_name = stated[key][1]
        if not theirs_name:
            continue
        ours_core, theirs_core = _core(names_ours[key]), _core(theirs_name)
        if ours_core and theirs_core and (
            ours_core == theirs_core or ours_core in theirs_core or theirs_core in ours_core
        ):
            same.append(key)
        elif _words(names_ours[key]) & _words(theirs_name):
            word.append(key)
        else:
            none.append((key, names_ours[key], theirs_name))

    print()
    print(f"names: {len(shared)} rows named on both sides")
    if shared:
        print(f"  the same function after the prefixes come off: {len(same)} ({100 * len(same) / len(shared):.0f}%)")
        print(f"  a distinctive word in common:                  {len(word)} ({100 * len(word) / len(shared):.0f}%)")
        print(f"  no word in common:                             {len(none)}")
        for key, ours_name, theirs_name in none:
            print(f"    {label(key):<14} ours {ours_name:<24} theirs {theirs_name}")

    # Operands: what this reader measured against the crate's `operand_format`. Printed, not scored.
    formats = {}
    with open(args.theirs, newline="", encoding="utf-8") as handle:
        format_rows = [line for line in handle if not line.startswith("#")]
    for row in csv.DictReader(format_rows):
        formats[(LEAD_OF[int(row["table"])], int(row["opcode"].strip(), 16))] = row.get("operand_format", "").strip()

    handled = {key: row for key, row in indexed.items() if not row["unhandled"]}
    slot_named = [key for key in handled if "%a" in formats.get(key, "") and handled[key]["operand"] == "slot"]
    slot_elsewhere = [key for key in handled if "%a" not in formats.get(key, "")
                      and handled[key]["operand"] == "slot"]
    branch_named = [key for key in handled if handled[key]["operand"] == "target"
                    and ("%l" in formats.get(key, "") or "%2" in formats.get(key, ""))]
    branch_other = [key for key in handled if handled[key]["operand"] == "target"
                    and not ("%l" in formats.get(key, "") or "%2" in formats.get(key, ""))]
    data_has_slot = [key for key in handled if handled[key]["operand"] in ("data", "none")
                     and "%a" in formats.get(key, "")]
    data_has_label = [key for key in handled if handled[key]["operand"] in ("data", "none")
                      and "%l" in formats.get(key, "") and "%a" not in formats.get(key, "")]
    two_words = [key for key in handled if handled[key]["operand_slot_at"] >= 0
                 and handled[key]["operand"] == "target"]

    print()
    print(f"operands: {len(handled)} rows this reader states a kind for")
    print(f"  a frame slot on both sides:                    {len(slot_named)}")
    print(f"  a slot here, and the format column names none: {len(slot_elsewhere)}")
    print(f"  a branch here, and the format names %l or %2:  {len(branch_named)}")
    print(f"  a branch here, and the format names neither:   {len(branch_other)}")
    print(f"  two words on both sides (slot and a branch):   {len(two_words)}")
    print(f"  data here, and the format names a slot (%a):   {len(data_has_slot)}")
    print(f"  data here, and the format names a label (%l):  {len(data_has_label)}")
    for key in sorted(data_has_slot, key=order):
        print(f"    slot missed   {label(key):<14} format {formats.get(key, ''):<12} {stated.get(key, (0, ''))[1]}")
    for key in sorted(data_has_label, key=order):
        print(f"    branch missed {label(key):<14} format {formats.get(key, ''):<12} {stated.get(key, (0, ''))[1]}")

    # A difference is expected only where a row is measured two ways here, or where a whole table is
    # outside the bound the interpreter guards it at.
    unexpected = [key for key in differ if len(ours[key]) == 1]
    if unexpected:
        print(f"differences to read by hand: {len(unexpected)}")
        print("  each is a length where both sides state a single number and they differ: the handler")
        print("  has to be read to say which is right. docs/m7-status.md records the ones this project")
        print("  has read, and the bytes that settle each.")
        return 1

    return 0


# The conventions differ, so a name is compared for what is left after the prefix each tradition adds.
_PREFIX = re.compile(r"^(vba|rtc|rt|fn|f|c)", re.IGNORECASE)


def _core(name: str) -> str:
    return re.sub(r"[^a-z0-9]", "", _PREFIX.sub("", name.strip("_").split(" (")[0]).lower())


def _words(name: str) -> set[str]:
    stripped = _PREFIX.sub("", name.strip("_").split(" (")[0])
    return {word.lower() for word in re.findall(r"[A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z0-9]+", stripped) if len(word) >= 3}


if __name__ == "__main__":
    sys.exit(main())
