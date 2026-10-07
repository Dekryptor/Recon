#!/usr/bin/env python3
"""Read a corpus of VB6 p-code programs with `recon pcode`, and report what came out.

    tools/pcode-corpus.py <corpus-dir> --runtime <msvbvm60.dll> [--binary <recon>]
                                             [--opcodes <opcodes.json>] [--rows <rows.json>]

This is the instrument the p-code reader was developed against. Every number the milestone
documents — how many procedures decode exactly, how many end in an exit, how many bytes are
alignment, which opcodes appear and how often — comes from running this over a corpus of real
p-code builds, so it is kept in the repository rather than in a scratch directory: a result that
cannot be re-measured is not evidence.

The corpus is not part of the repository (it is third-party software, and `NOTICES` in the input
directory records where each piece came from). Point this at a directory of `.exe` files; each is
read with `--json` and only the JSON is used, so the numbers here are the tool's own report.

`--opcodes <runtime json>` additionally reports the opcode histogram across every procedure, and
`--rows <sizes.json>` compares the measured length of every opcode row against another source given
as a JSON mapping, which is how the tables were cross-checked against an independent reconstruction.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import subprocess
import sys

# Names of the directories that hold the corpus's own sources rather than built programs.
SKIP_DIRS = {"source", ".git", "__pycache__"}


def exes(root: str) -> list[str]:
    found = []
    for directory, children, files in os.walk(root):
        children[:] = [c for c in children if c not in SKIP_DIRS]
        for name in sorted(files):
            if name.lower().endswith(".exe"):
                found.append(os.path.join(directory, name))
    return found


def run(binary: str, args: list[str]) -> dict:
    result = subprocess.run([binary, *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"{' '.join(args[:2])}: exit {result.returncode}: {result.stderr.strip()[:200]}")
    return json.loads(result.stdout)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("corpus", help="directory to walk for .exe files")
    parser.add_argument("--runtime", required=True, help="the VB6 runtime the programs run on (msvbvm60.dll)")
    parser.add_argument("--binary", default="recon", help="the recon binary to run (default: recon on PATH)")
    parser.add_argument("--opcodes", action="store_true", help="also report the opcode histogram of every instruction")
    parser.add_argument("--problems", type=int, default=10, help="how many distinct problems to list (default 10)")
    args = parser.parse_args()

    programs = exes(args.corpus)
    if not programs:
        print(f"no .exe files under {args.corpus}", file=sys.stderr)
        return 2

    status = collections.Counter()
    problems = collections.Counter()
    histogram = collections.Counter()
    totals = collections.Counter()
    procedures_with_problems = 0

    for path in programs:
        document = run(args.binary, ["pcode", path, "--runtime", args.runtime, "--json"])
        summary = document["summary"]
        for key in ("objects", "procedures", "exact", "padded", "ended_with_exit", "instructions",
                    "code_bytes", "empty_slots", "entries_not_procedures"):
            totals[key] += summary.get(key, 0)
        totals["unmeasured"] += sum(summary.get("undecodable", {}).values())

        for procedure in document["procedures"]:
            status[procedure["status"]] += 1
            if procedure["problems"]:
                procedures_with_problems += 1
            for problem in procedure["problems"]:
                problems[problem.rsplit(" at ", 1)[0][:70]] += 1

            if args.opcodes:
                instructions = run(args.binary, [
                    "pcode", path, "--runtime", args.runtime,
                    "--object", procedure["object"], "--procedure", str(procedure["method"]), "--json",
                ]).get("instructions", [])
                for instruction in instructions:
                    histogram[instruction["opcode"]] += 1
                    totals["opcode_instructions"] += 1

    print(f"programs: {len(programs)}")
    print(f"procedures: {totals['procedures']}  objects: {totals['objects']}  "
          f"empty slots: {totals['empty_slots']}  entries that are not procedures: {totals['entries_not_procedures']}")
    print(f"per procedure: " + "  ".join(f"{name} {count}" for name, count in sorted(status.items())))
    print(f"ended with an exit: {totals['ended_with_exit']}")
    print(f"instructions: {totals['instructions']} in {totals['code_bytes']} bytes of code")

    if args.opcodes:
        print(f"opcodes seen: {len(histogram)} distinct in {totals['opcode_instructions']} instructions")
        for opcode, count in sorted(histogram.items(), key=lambda kv: -kv[1]):
            print(f"  {opcode:<22} {count}")

    print(f"procedures with a problem: {procedures_with_problems} ({len(problems)} distinct)" if problems
          else "procedures with a problem: none")
    for problem, count in problems.most_common(args.problems):
        print(f"  {count:>5}  {problem}")

    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
