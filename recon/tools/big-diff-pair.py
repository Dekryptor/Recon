#!/usr/bin/env python3
"""Measure `recon diff` on a large pair, and say what each stage cost.

    tools/big-diff-pair.py <binary> [--binary-recon PATH] [--work DIR] [--skip-built]

Two projects over one program: A is the program itself, B a copy with three two-byte edits inside
three of its functions — found from A's inventory, because a byte changed in the padding between
functions is a difference no comparison looks at, and a pair that scores 1 proves nothing about the
two reports being equal. The pair is then compared twice: with neither side's inventory on disk, so
both are analysed, and with both, so both are read back. The stage report from `diff --verbose` is
printed for each, with wall time and peak RSS.

This is the instrument behind the numbers in "The inventory a side already had" and the
body work in "The bodies a comparison decodes twice", `docs/m7-status.md`. It is a script rather than a shell one-liner because the
edit has to land inside a function and the comparison has to be shown to be the *same* comparison —
the two reports are compared field by field, with only the run-dependent fields zeroed.

An inventory of an 11.8 MB client is 71 MB, so everything this writes goes to /tmp (`--work` to move
it), and `/tmp` is throwaway by construction.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace

Completed = SimpleNamespace


def run(binary: str, args: list[str]) -> tuple[Completed, float, float]:
    """Run one command and report its own wall time and its own peak RSS.

    `resource.getrusage(RUSAGE_CHILDREN)` is *cumulative* — its `ru_maxrss` is the high-water mark of
    every child this process has waited for, so attributing it to "the last command" silently reports
    the biggest run so far (here: the inventory, which is bigger than the comparison). `os.wait4` is
    per child, which is the number this instrument exists to print.
    """
    with tempfile.TemporaryFile() as out, tempfile.TemporaryFile() as err:
        started = time.time()
        process = subprocess.Popen([binary, *args], cwd="/tmp", stdout=out, stderr=err)
        _, status, usage = os.wait4(process.pid, 0)
        elapsed = time.time() - started
        process.returncode = os.waitstatus_to_exitcode(status)
        out.seek(0)
        err.seek(0)
        return Completed(returncode=process.returncode, stdout=out.read().decode(), stderr=err.read().decode()), \
            elapsed, usage.ru_maxrss / 1024


def phases(stderr: str) -> list[str]:
    return [line.strip() for line in stderr.splitlines() if "debug:" in line]


def side_facts(path: str) -> tuple:
    document = json.loads(pathlib.Path(path).read_text())
    return (
        document["left"].get("inventory_source"),
        document["right"].get("inventory_source"),
        document["summary"]["score"],
        document["left"].get("analysis_ms"),
        document["right"].get("analysis_ms"),
    )


def without_run_fields(path: str) -> dict:
    document = json.loads(pathlib.Path(path).read_text())
    for side in ("left", "right"):
        document[side]["analysis_ms"] = 0
        document[side].pop("inventory_source", None)
    return document


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("binary", help="the large program to measure")
    parser.add_argument("--binary-recon", default="recon", help="the recon binary to run (default: recon on PATH)")
    parser.add_argument("--work", default="/tmp/bigpair", help="scratch directory (default /tmp/bigpair)")
    parser.add_argument("--skip-built", action="store_true",
                        help="skip the route with no documents on disk (the slow half)")
    args = parser.parse_args()

    recon = args.binary_recon
    source = pathlib.Path(args.binary).resolve()
    work = pathlib.Path(args.work)
    a, b = work / "A", work / "B"
    built_report, read_report = work / "built.json", work / "read.json"

    # A: the program. Its inventory is built to find where the edits can go.
    shutil.rmtree(work, ignore_errors=True)
    for project in (a, b):
        (project / "inputs").mkdir(parents=True)
        shutil.copy2(source, project / "inputs" / source.name)
        done = subprocess.run([recon, "init", str(project), "--binary", str(project / "inputs" / source.name),
                               "--force"], capture_output=True, text=True, cwd="/tmp")
        if done.returncode != 0:
            print("init failed:", done.stderr[:400])
            return 1

    done, seconds, _ = run(recon, ["inventory", "--project", str(a)])
    if done.returncode != 0:
        print("inventory failed:", done.stderr[:400])
        return 1

    inventory = json.loads((a / "build" / "inventory.json").read_text())
    target = b / "inputs" / source.name
    data = bytearray(target.read_bytes())
    pe = int.from_bytes(data[0x3C:0x40], "little")
    optional = int.from_bytes(data[pe + 20:pe + 22], "little")
    sections = int.from_bytes(data[pe + 6:pe + 8], "little")
    text = None
    for index in range(sections):
        entry = pe + 24 + optional + index * 40
        if bytes(data[entry:entry + 8]).rstrip(b"\0") == b".text":
            text = (int.from_bytes(data[entry + 12:entry + 16], "little"),
                    int.from_bytes(data[entry + 20:entry + 24], "little"))
    if text is None:
        print("no .text section in", source)
        return 1
    virtual, raw = text
    patched = []
    for function in inventory["functions"]:
        ranges = function.get("ranges") or []
        if not ranges or ranges[0]["size"] < 400:
            continue
        rva = ranges[0]["rva"] + 200
        data[raw + (rva - virtual)] ^= 0x5A
        data[raw + (rva - virtual) + 1] ^= 0x5A
        patched.append(hex(rva))
        if len(patched) == 3:
            break
    if not patched:
        print("no function large enough to edit")
        return 1
    target.write_bytes(bytes(data))
    print(f"{source.name}: {len(inventory['functions'])} functions, "
          f"three two-byte edits inside functions at {', '.join(patched)}")

    # B's inventory is A's, which describes the file before the edit: thrown away so the comparison
    # analyses B unless and until the second half of the run asks for it to be written.
    shutil.rmtree(b / "build", ignore_errors=True)

    def compare(label: str, output: pathlib.Path) -> tuple | None:
        # `--summary`, not `--json`: a `--json` run prints exactly one document and nothing else, which
        # is the agent contract, so the stage report has no way out of it. The document still lands in
        # `-o`, which is what this reads.
        done, seconds, rss = run(recon, ["diff", str(a), str(b), "--summary", "-o", str(output), "--verbose"])
        if done.returncode != 0:
            print(f"{label}: exit {done.returncode} after {seconds:.1f} s")
            print(done.stderr[-2000:])
            return None
        facts = side_facts(str(output))
        print(f"{label}: {seconds:.1f} s, peak RSS {rss:.0f} MB, score {facts[2]}, routes {facts[0]}/{facts[1]}, "
              f"analysis_ms {facts[3]}/{facts[4]}")
        for line in phases(done.stderr):
            print("   ", line)
        return facts

    if not args.skip_built:
        # Neither side has a document: both are analysed.
        shutil.rmtree(a / "build", ignore_errors=True)
        if compare("neither on disk", built_report) is None:
            return 1

    for project in (a, b):
        done, seconds, rss = run(recon, ["inventory", "--project", str(project)])
        document = project / "build/inventory.json"
        size = document.stat().st_size / (1024 * 1024) if document.exists() else -1
        print(f"inventory {project.name}: {seconds:.1f} s, peak RSS {rss:.0f} MB, document {size:.0f} MB")
        if done.returncode != 0:
            print(done.stderr[:400])
            return 1

    if compare("both on disk", read_report) is None:
        return 1

    if not args.skip_built and built_report.exists():
        same = without_run_fields(str(built_report)) == without_run_fields(str(read_report))
        print("the two reports are equal apart from timing and route:", same)
        if not same:
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
