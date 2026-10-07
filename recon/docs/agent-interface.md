# Driving recon from a program

Everything a script or an agent needs is on stdout, in JSON, described by a schema the tool ships.
This page is the contract; [`docs/cli.md`](cli.md) is the command reference and the per-command
status documents are the evidence.

## The one rule

**A `--json` run prints exactly one JSON document on stdout.**

That holds when the run fails too. A command that has something to report prints its own document —
`verify` prints its report even when a hash does not match — and a command that has nothing to
report because it never got started prints an [error document](#when-a-run-fails) instead. Two
documents on stdout is not a contract, so it never happens.

| stream | what goes there |
| --- | --- |
| stdout | the one JSON document (with `--json`), or human-readable text without it |
| stderr | warnings and errors, always, in prose — for the human watching the same run. `--verbose` adds `debug:` lines there too: phase timings and route decisions (which inventory a listing read, what each stage of a comparison cost). stdout stays exactly one document either way, so a run can be verbose and machine-readable at once. |
| exit code | `0` ok, `1` a check failed, `2` bad arguments, `3` bad configuration, `4` a bug |

Human-readable lines are suppressed by `--json` and warnings are too, so stdout parses. Error text
is never suppressed, because a person may be watching a machine's run.

## The loop

The order a reconstruction actually goes in, and the command for each step:

```
recon init <dir> --binary <file> --json     # make the project; the document names every file
recon inventory --json                      # what is in the original: functions, sizes, producers
recon build --json                          # compile the units with the toolchain the binary names
recon diff --json                           # score the rebuild against the original, function by function
recon permute --function NAME --json        # when a function is close but not exact: which source
                                            #   written a different way is the one the bytes came from
recon report --json                         # how far along it is, and what is left
```

Each step reads what the last one wrote, so an agent can stop after any of them and resume later:
`project.toml` and `local.toml` are the state, and the JSON documents are the answers.

## Which commands emit JSON

| command | document | schema |
| --- | --- | --- |
| `init` | the project it made: every file written, the binary's hash | `init` |
| `verify` | every input's hash, the debug info, the units' sources and toolchains | `verify` |
| `validate` | an inventory checked against the oracles | `inventory` |
| `doctor` | this machine: what is installed and what is missing | `doctor` |
| `inventory` | functions, sections, imports, symbols, producers | `inventory` |
| `inspect sections` / `imports` / `exports` / `relocs` / `data` / `functions` / `xrefs` / `producers` / `debug` / `tls` / `stats` | one part of an inventory | `inspect-sections` … `inspect-stats`, one per subcommand |
| `sigs build` | how many patterns, from how many functions | `sigs-build` |
| `sigs apply` | which functions a pattern named | `sigs-apply` |
| `toolchain list` | every profile this build knows | `toolchain-list` |
| `toolchain show <id>` | one profile | `toolchain-profile` |
| `toolchain detect` | what the binary says built it | `inspect-producers` |
| `toolchain check <id>` | one toolchain, resolved on this machine | `toolchain-check` |
| `build` | what was compiled, with which flags, and the linked image | `build` |
| `permute` | the baseline, every variant with its score, and the winner | `permute` |
| `diff` | per-function scores and classified instruction differences | `comparison` |
| `report` | progress shares and history | `progress` |
| `delink` | the pieces of the original | `delink` |
| `link` | the relinked image and how much of it is rebuilt | `link` |
| `strings` | the runs of printable characters, with the offset, the address and the section of each | `strings` |
| `migrate` | every configuration file and the schema version it carries | `migrate` |
| any command that fails without a document of its own | why it failed | `error` |

Every command that emits JSON now has the schema that describes it, so `--check-schema` is honoured
everywhere it is accepted. The `inspect` schemas reuse the inventory's own definitions, which is how
they stay in step with the objects they describe.

## Reading a document

Two things are true of every document the tool writes:

* **`schema_version` is `0.1`** and properties are `snake_case`.
* **`--check-schema` validates what it just printed**, against the schema the tool ships, and exits
  non-zero if it does not match. An agent that wants to be sure can pass it every time; the cost is
  one validation.

The schemas are embedded in the binary and can be read back:

```
recon schema list              # build comparison delink error init inventory link migrate ...
recon schema show permute      # the document, as JSON Schema
```

## When a run fails

A failure with no document of its own produces this, on stdout, with the exit code the process
exits with:

```json
{
  "schema_version": "0.1",
  "command": "recon permute --project /work/sample --function nosuch --json",
  "tool_version": "1.0.0",
  "error": {
    "kind": "configuration",
    "message": "/work/sample/project.toml error: permute.unit: no unit's [[covers]] names \"nosuch\"; say which one with --unit",
    "errors": ["... every line the run wrote to stderr, in order ..."],
    "diagnostics": [
      {
        "file": "/work/sample/project.toml",
        "line": 0,
        "key_path": "permute.unit",
        "severity": "error",
        "message": "no unit's [[covers]] names \"nosuch\"; say which one with --unit"
      }
    ]
  },
  "exit_code": 3
}
```

`error.kind` is the exit code in words, and `diagnostics[]` is the part to act on: for a
configuration failure it names the file, the line and the key, so the fix does not require parsing
the sentence in `message`.

| `kind` | exit | what it means | what to do |
| --- | --- | --- | --- |
| `usage` | 2 | the arguments were wrong | fix the command; `message` says what was missing |
| `configuration` | 3 | a config file was wrong | fix the file in `diagnostics[0].file` |
| `check_failed` | 1 | a check ran and did not pass | read the command's own document, not this one |
| `internal` | 4 | a bug | `message` and `--verbose` for the stack |

## Example: adopting a permuter result

`recon permute --json` answers "which source written a different way produced these bytes". The
field to branch on is **`best_beats_baseline`**, not `best.score`: the best variant is reported even
when it only *ties* the source as it stands, and a variant that scores 1.0 against a baseline of 1.0
has found nothing.

```json
{
  "baseline_score": 0.5,
  "baseline_exact": false,
  "tried": 1,
  "candidate_count": 2,
  "stopped_because": "a variant reproduced the function exactly",
  "best": { "variant_id": "swap-ops-11-7", "kind": "operand-swap", "score": 1, "exact": true },
  "best_source": "/work/sample/build/permute/arith.c",
  "best_beats_baseline": true
}
```

```python
run = json.loads(check_output(["recon", "permute", "--function", name, "--json"]))
if run["best_beats_baseline"]:
    adopt(run["best_source"])          # the winning source, written beside the unit's
    # the unit's own source is untouched: adopting it is the caller's decision
else:
    keep(run["baseline_score"])        # the source as it stands is the best known answer
```

`stopped_because` says why the search ended — the budget, an exact match, the candidates running
out, an exact baseline, or a project that does not build — which is how a caller tells "nothing
better exists" from "did not look hard enough".

## Stability

Documents are contracts, not dumps: adding a field is a version change, and `--check-schema` is how
the tool checks its own. The human-readable text is *not* a contract — tests pin some of it, but an
agent should read the JSON.
