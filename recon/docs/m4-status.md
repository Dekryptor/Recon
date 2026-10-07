# M4 — diff viewer and progress reports

**State: done.** Two commands, one artefact, and a schema:

* `recon report` — measures how far the reconstruction is, per unit, and writes a progress site:
  one HTML file that carries its own data, so it can be opened from a disk, mailed, or checked in.
* `recon serve` — serves the same viewer locally, computing instruction alignment one function at a
  time, so you can rebuild in one window and reload the page in the other.
* `progress.schema.json` (schema 0.1) — the report's contract, like the inventory and comparison
  schemas before it. `recon report --check-schema` validates its own output, and `recon schema list`
  now names `progress` alongside the others.

```
$ recon report --project . --check-schema
report sample: score 0.8373, 6802/8076 instructions exact (84.22%), 56/142 functions verified
  unit arith                33.33%   3/9 instructions, 0 function(s) missing
  unit control              11.54%   6/52 instructions, 0 function(s) missing
  unit startup              98.33%   59/60 instructions, 0 function(s) missing
warning: 136 of 142 original function(s) are not covered by any unit's covers
  wrote build/report/progress.json
  wrote build/report/index.html and history.jsonl
progress report matches schema 0.1
```

That is the MinGW corpus — `sample-release.exe` as the original, `sample-debug.exe` standing in as
the rebuild — with three `[[unit]]` entries declaring covers. The numbers say what the plan's
"honest metrics" note asks for: what is proven, what is derived, and what cannot be measured.

## Three numbers, never one

A reconstruction has more than one way of being "80% done", and mixing them is how a number starts
lying. The report keeps them apart, and says which is which:

| Field | Meaning | How it is computed |
| --- | --- | --- |
| `instruction_exact` | how much of the original is reproduced, instruction for instruction | `summary.instructions_equal / Σ instructions.left` |
| `verified` | how many original functions match exactly | `summary.exact / count(functions with a left side)` |
| `similarity` | the compare engine's own score | `summary.score` (equal instructions over the larger side) |

Every one of them is measured **against the original**, never against the rebuild: a rebuild that
invented a thousand extra functions would not move the first two by a single point, and a rebuild
that dropped half of them would. In the run above, `similarity` is 0.8373 (equal over the larger
side) while `instruction_exact` is 0.8422 (equal over the original's 8076 instructions) — close
here, because the two builds are the same size, and far apart whenever one side is not.

## Per-unit progress, and what "no covers" means

A unit earns its share through its `[[unit.covers]]`: each cover resolves against the *original's*
functions — by symbol, then by the demangled spelling of that symbol, then (only when unambiguous)
by substring, and a `rva` + `size` cover by overlap. A unit's progress is its equal instructions
over its covered instructions.

Two cases that a single number would have to lie about:

* **A unit that covers nothing measurable reports `null`, not `0`.** "No covers" and "covered
  nothing yet" are different facts, and a dashboard that prints `0%` for both makes a
  not-yet-written unit look like a failed one. The report says so in `notes[]` and the CLI prints a
  dash.
* **Functions no unit claims are counted separately**, in `uncovered`, and the report warns about
  them. A project where nobody wrote covers must not look like a project that is 0% done; it looks
  like a project that has 136 of 142 functions unclaimed, which is the truth and is actionable.

Covers that resolve to nothing are recorded too (`unresolved: true` plus a note naming the symbol),
because a stale cover is a bug in `project.toml` and the only way to notice is to be told.

## History: the number that moves

With `[report] history = true`, every run appends one line to `build/report/history.jsonl` — one
JSON snapshot per line, not a pretty-printed document, because a file that is appended to cannot be
indented. `progress.json` then carries `previous` (the run before this one) and `history` (all of
them), so the site can show movement instead of a snapshot:

```
$ cat build/report/history.jsonl | head -1
{"at":"2026-10-04T22:31:29.2924496+00:00","score":0.8373,"instructions_equal":6802,"instructions_original":8076,"functions_exact":56,"functions_original":142,"units":[{"name":"arith","progress":0.3333333333333333}, …]}
```

A line that cannot be parsed is skipped, not fatal: history is a record, not an input, and losing
one entry must not lose the run.

## The site

`build/report/index.html` is one file: the comparison and the progress report are inside it as JSON,
there is no framework, no build step, no network request, and nothing else to copy. Two tabs:

* **Functions** — every function pair, filtered by name and by status, sorted by address, by
  differences or by size. Selecting one shows the aligned body: two columns, `equal` / `changed` /
  `added` / `removed` rows with both addresses and both texts. The list is virtualized, because a
  real binary has thousands of functions and a browser should not be asked to hold them all as DOM
  nodes.
* **Progress** — one row per unit with its share, its instruction counts and its missing functions;
  a treemap of the image where each rectangle is a function sized by its instruction count and
  coloured by status (grey is a function no unit covers, which is the picture that tells you where
  to declare covers next); and the history table.

`recon report --aligned` puts the aligned rows for up to 400 non-exact functions into the file, so a
static report opens with instruction-level detail and no server behind it. Without it — or when the
report is built from `--comparison` and the two binaries are not at hand — the page says so and
falls back to the recorded differences: an empty listing would look like a bug.

`recon serve` is the same page against a running tool. It answers four paths — the page itself,
`api/comparison`, `api/progress`, and `api/aligned?id=…`, which computes the alignment for one
function on demand — and nothing else. Rebuild, reload, and the numbers are the new ones.

```
$ recon serve --port 8080
viewer for sample: 145 function(s), score 0.8373
serving the diff viewer on http://localhost:8080/ (Ctrl-C to stop)
```

## One deliberate deviation from the plan

Module 9 asks for "plain HTML and TypeScript or Web Components (no UI framework)". The constraint
that survives to the artefact is the one in parentheses, and it is honoured: no framework, no
bundler, no `node_modules`, no transpiled output checked in beside the source. TypeScript was
dropped rather than added, because a TS source file means a build step between the repository and
something a user can open, and the point of `index.html` is that it *is* the deliverable — one
file that works when double-clicked, in six months, on a machine with nothing installed. The
viewer is therefore one HTML file with inline CSS and inline JavaScript, and the only thing it
requests from the network is nothing at all. If it ever needs the type safety, the place to get it
is the JSON documents it consumes, which are already schema-pinned.

## Two defects found while building this

Both were found by asking the report questions the earlier milestones never asked, and both changed
results that had been reported as correct:

1. **`func_a` was classified as a placeholder.** A name like `sub_401000` is made up from an
   address, so two builds never agree on it and it cannot be used to pair functions. The test
   `sub_401000` was `prefix + all-hex-digits` — which is also true of `func_a`, `data_be` and any
   other real symbol whose name ends in hex characters. Those functions were therefore paired by
   body and by similarity only, and a rebuild with a genuinely different body was reported as a
   missing function instead of a changed one. The suffix now has to be at least four hex digits,
   which is what an address is.
2. **The rebuild was analysed without its own debug info.** Debug information was loaded through the
   project, and a project has one `debug` input — the original's. The reference side was therefore
   built from code alone: its functions had no names unless they were exports or imports, so they
   could not be paired by name even when the rebuild carried full symbols. Debug info is now read
   per binary: a rebuild is described by its own COFF symbols, DWARF or the PDB beside it, while the
   project's configured `debug` and `map` inputs are still used only for the original.

   The size of the defect, measured by putting the old behaviour back for one run: comparing the
   MinGW corpus's `sample-release.exe` (original) with `sample-debug.exe` (reference) *through a
   project* scored **0.4739** — 142 functions against 108, 67 matched, 74 reported missing on the
   left and 41 on the right, and all 146 data symbols "differing" because the rebuild's symbol
   table was never read. With each binary described by its own debug info, the same two files score
   **0.8373**: 142 against 142, 139 matched (138 of them by name), 3 and 3 unpaired, 146 data
   symbols identical — which is what comparing the same two binaries without a project has always
   reported. The project path had been losing half the comparison, and nothing in the existing
   tests compared the two paths against each other.

## Tests

`dotnet test` — **261 tests**, of which 17 are M4's: 13 in `ProgressTests` and 4 in `CliTests`.

`ProgressTests` covers the three totals and what each is measured against; a rebuild that adds
functions not being credited for them; a unit scored on the functions its covers claim, including a
cover that resolves through the demangled spelling and one that resolves by range; a unit that
covers nothing reporting `null` rather than zero; uncovered functions being counted separately;
history appending one snapshot per run and surviving a damaged line; the report matching
`progress.schema.json` at version 0.1; the static site being one self-contained file that carries
both documents and fetches nothing; the server page pointing at the API instead; alignment for one
function computed on demand; and the server answering all four paths (`/`,
`api/comparison`, `api/progress`, `api/aligned?id=…`, plus 404s) with alignment computed lazily.

`CliTests` drives the same through the entry point: the report writing the document, the site and a
history entry, a second run producing a second entry and a `previous`; `--aligned` putting rows into
the file; a report built from `--comparison` saying that alignment is unavailable instead of
silently skipping it; and a report over two loose binaries with no project at all, which measures
the whole image and keeps no history.

The test fixture gained `SyntheticPeOptions.MutateFuncA` — one opcode changed, same length — because
until it existed there was no synthetic pair that was present on both sides *and* different, which
is the case the viewer exists for.

## Gates

* `dotnet test tests/Recon.Tests/Recon.Tests.csproj` — 261 passed, 0 failed.
* `bash tools/validate-corpus.sh` — ok (9 binaries, PDB oracle on).
* `dotnet publish … -r linux-x64` (NativeAOT) — no trimming or AOT warnings, and the published
  binary's `progress.json` is identical to the framework build's apart from `generated_at` and
  `analysis_ms`; the sites differ only in those same timings.
* `recon report --check-schema` — 0 violations against `progress.schema.json`.

## Files

| Path | What it is |
| --- | --- |
| `src/Recon.Core/Reporting/Progress.cs` | the progress model, the measurement, and the history file |
| `src/Recon.Core/Reporting/ReportSite.cs` | the viewer template as an embedded resource, and the two ways to boot it |
| `src/Recon.Core/Reporting/viewer.html` | the viewer: no framework, no build step, no network |
| `src/Recon.Core/Reporting/ViewerServer.cs` | the local server: four paths, alignment on demand |
| `src/Recon.Core/Schemas/progress.schema.json` | schema 0.1 |
| `src/Recon.Core/Compare/ComparisonBuilder.cs` | `AlignedFor`: one function's alignment, for the server |
| `src/Recon.Cli/Commands.cs` | `recon report`, `recon serve` |
