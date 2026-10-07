# Working in this repository

Notes for anyone — human or agent — making changes here. The short version: build, test and run the
corpus harness on every change; never invent evidence; keep the published binary warning-free.

## Commands

```bash
. tools/env.sh                 # exports DOTNET_ROOT and NUGET_PACKAGES; source it before any dotnet call
                               # (a build without it still succeeds: Directory.Build.props redirects the
                               #  package cache to $(DOTNET_ROOT)/packages when the repo is under $HOME
                               #  and DOTNET_ROOT is outside it, which keeps the workspace snapshot small)
dotnet build src/Recon.Cli/Recon.Cli.csproj -v q --nologo
dotnet test  tests/Recon.Tests/Recon.Tests.csproj --nologo -v q

bash tools/perf-probe.sh          # the large-input probe: one 11.8 MB program, phases, listings, comparison
python3 tools/big-diff-pair.py <binary>   # the pair instrument: two projects over one program, both routes of the comparison, per-stage cost and per-child peak RSS
bash tools/build-corpus.sh        # MinGW corpus   -> tests/corpus/mingw   (reproducible)
bash tools/build-msvc-corpus.sh   # MSVC-ABI corpus -> tests/corpus/msvc   (clang + lld-link)
bash tools/build-elf-corpus.sh    # ELF corpus     -> tests/corpus/elf    (gcc/g++/clang of the host)
bash tools/build-macho-corpus.sh  # Mach-O corpus  -> tests/corpus/macho  (clang + ld64.lld; skips without them)
bash tests/corpus/pdata-seh/build.sh <dir>  # a 64-bit MinGW pair, the exception-directory (`.pdata`) measurement
bash tools/validate-corpus.sh --verbose   # the acceptance harness: every PE, ELF and Mach-O binary, every oracle
bash tools/relink-corpus.sh               # delink + relink every corpus binary, and the verdict

dotnet publish src/Recon.Cli/Recon.Cli.csproj -c Release -r linux-x64   # NativeAOT, must stay warning-free

# The same gates run on a machine that is not this one: .github/workflows/ci.yml, at the *repository*
# root, because that is where the only directory GitHub reads workflows from is. The project is in
# `recon/`, so every step sets that as its working directory — a copy of that file under `recon/` is
# invisible and runs nothing. Its Windows job runs `--filter "requires!=elf-corpus"`: the ELF corpus
# cannot be built on Windows, and the classes that need it say so with a trait instead of failing.
dotnet out/bin/Recon.Cli/debug/recon.dll gen-docs                       # regenerate docs/cli.md
RECON_UPDATE_GOLDEN=1 dotnet test --filter GoldenTests                  # rewrite the golden files (deliberately)
```

Fresh sandbox: `sudo apt-get install -y -q --no-install-recommends llvm-19 lld-19` gives
`clang-19`, `lld-link-19`, `llvm-pdbutil-19`, `llvm-nm-19`, `llvm-readobj-19`, `llvm-undname-19`.
The MinGW corpus needs `i686-w64-mingw32-gcc`, the 64-bit tests need `x86_64-w64-mingw32-gcc` (both come
with `mingw-w64`), and the Mach-O corpus needs `clang-19` and `ld64.lld-19`
(both builders skip instead of failing when their tools are missing, and the tests skip with them).
The SDK is not part of the checkout either:
`curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.401 --install-dir
<dir>` puts it wherever you like, and `tools/env.sh` finds it under `/opt/dotnet`,
`~/.dotnet`, `~/.cache/dotnet` or `/tmp/dotnet` — pick a directory with room for it, since a
partially extracted SDK looks installed until `dotnet --version` prints nothing.

## Ground rules

1. **Run the gates before claiming anything.** `dotnet test` and `bash tools/validate-corpus.sh` are
   the evidence, and since M5 so is `bash tools/relink-corpus.sh` (six binaries relink byte-identical;
   a change that moves one byte of a relinked image is a bug). A change that makes the corpus harness
   fail is not done, and a claim without a run behind it does not belong in a status document.
2. **Do not fabricate evidence.** No address, size, count or version may be written into a test, a
   golden file or a document unless a tool produced it. When a fact comes from an oracle, say which
   oracle and show the command. The same rule applies to the harness's own summary: an oracle that
   did not run is reported as skipped, never as passed, so a machine missing `llvm-pdbutil` cannot
   look like one where the PDB oracle agreed.
3. **Report unknowns instead of guessing.** If the tool cannot decide something, it goes into
   `problems[]`, a per-function `confidence`, an `unknowns[]` entry or an `only_left`/`only_right`
   result. Silent degradation is the one bug that would make the whole tool worthless.
4. **Ask the binary, not the documentation.** iced-x86's API and PDB record layouts both bit us by
   being different from their documentation (there is no `SymbolResolver` base class; CodeView record
   lengths differ between `link.exe` and `lld`). Probe first, then write the code, then pin what you
   learned in a test or a comment.
5. **Never commit binaries.** The test corpora are generated and git-ignored; `tests/tools/msvc-corpus.sha256`
   is a drift record for the corpus, not a lock file.
6. **The analysis speaks `IBinaryImage`, never `PeImage`.** Everything downstream of the loader —
   inventory, decoding, xrefs, producer detection, compare, reports — takes the abstraction in
   `src/Recon.Core/Images/`. A format's own record (`PeImage`, `ElfImage`) is reached through
   `image.Pe` / `image.Elf`, and only by code that genuinely cannot work any other way (`delink`
   and `relink` are PE-only and say so when they are handed something else). A new format is a new
   loader plus one adapter, not a rewrite.

## Code conventions

* C# with nullable enabled, file-scoped namespaces, `sealed` classes, collection expressions (`[]`,
  `[..]`), primary constructors where they fit.
* XML doc comments on public types and on any method whose *why* is not obvious; comments explain
  intent or a trap that was actually hit, never restate the code.
* No `TODO` left in source: if something is not done, it belongs in `TODO.md`, and the doc is
  updated in the same change.
* Everything the tool prints is either stable text (tests pin it) or JSON (schema-pinned). No
  timestamps in JSON except behind `--timestamp`.
* `Program.cs` holds argument handling and exit codes; `Commands.cs` holds one method per command;
  `CommandSpecs.cs` is the single source for help text and `docs/cli.md`.

## Adding to the codebase

**A new command**: add a `CommandSpec` (`src/Recon.Cli/CommandSpecs.cs`), a switch arm in
`Program.cs`, a method in `Commands.cs`, a case in the help test, and run `recon gen-docs`. If the
command emits JSON, add the DTOs, a `[JsonSerializable]` line in `ReportsJsonContext`, a schema in
`src/Recon.Core/Schemas/`, and a test that validates the output against it.

**Emitting JSON**: a `--json` run prints exactly one document on stdout —
[`docs/agent-interface.md`](docs/agent-interface.md) is the contract. Use `EmitWithSchemaCheck`, not
`EmitJson`, so `--check-schema` is honoured; the entry point then prints an `error` document only
for runs that failed *without* printing one of their own. Never print a second document: two on
stdout is not a contract.

**A new toolchain profile**: copy the closest file in `src/Recon.Core/Toolchains/profiles/`, keep the
detection evidence in the order the plan specifies, and update the profile-id list that
`ConfigTests` checks. A profile that does not inherit from `*-base` needs a reason in the file. A
profile that should be able to *build* needs `[compile]` (`exe`, `default_flags`, `include_flag`,
`define_flag`, `output_flag`, and `depfile_flag` when the compiler can write one) and `[link]`
(`exe`, `default_flags`, `output_flag`); where those tools live is `local.toml`'s business, never the
profile's.

**A change to the build**: `Build/BuildPlanner.cs` decides, `Build/BuildRunner.cs` runs,
`Build/NinjaWriter.cs` exports — keep the three in agreement, because a test expands each generated
ninja edge back into the command the runner would have executed. Anything that can change a
compilation belongs in the cache key (`BuildPlanner.CacheKey`) *and* in `units[].inputs` in the
manifest, so a rebuild can say which input moved.

**A change to delink or relink**: the pieces must tile each section exactly — no overlap, no gap,
to the section's *virtual* size, because the padding a file rounds up with is not part of the image —
and `recon link` must keep printing a verdict it measured. The traps are all in the assembler and the
linker, and every one of them was found by a byte that differed: GNU as gives a `.debug_*` section a
one-byte alignment and every other section four (so renaming a debug section silently adds up to three
bytes to it); GNU as writes a `.debug_line` of its own for the file it assembles, and ld drops an
input section it cannot place into the output section of *the same name*, which is why ours are
prefixed and the script discards the rest; `.section .text, "xr"` makes gas warn and then ignore the
attributes it had already assumed; `--dynamicbase` makes ld append a relocation table of its own to
the one the plan wrote, so the flag is copied into the header instead; and ld has no way to be told
where an import address table is, which is why the header is finished afterwards. `lld-link` and
`link.exe` cannot place a section at an address at all, so a profile whose linker writes `/OUT:` is
refused rather than attempted.

**A new viewer or report**: the page is one file with the documents inside it
(`Reporting/viewer.html`, embedded as a resource), booted either from `window.__RECON__` (static) or
`window.__RECON_API__` (served). Nothing in it may come from the network, and JSON that is appended
to line by line — `history.jsonl` — must be written with `Reports.SerializeLine`, because a
pretty-printed snapshot spans twenty lines and none of them parse.

**A new document**: give every member a `[JsonPropertyName]` in snake_case, register the type in a
source-generated context, add the schema, validate it in a test (`JsonSchemaValidator` covers
`type`, `required`, `additionalProperties`, `enum`, `pattern`, `minimum`/`maximum`, `allOf/anyOf/oneOf`,
`items` and `$ref`).

**A new oracle or corpus check**: put it in `tests/tools/` as a Python script with the same protocol
as the others — `<inventory.json> <binary> [extra]`, `--quiet`, exit `0` ok / `1` mismatch / `2`
unusable input, and a failure it can be wrong about only if the tool is right.

## Tests

* Tests that need a corpus *skip* when it is missing (`if (!MsvcCorpus.Available("sample")) return;`)
  — the corpora are generated, so a fresh checkout has none.
* Anything that captures `Console` goes in the `cli` xUnit collection; the CLI runs in-process and two
  collections capturing stdout at once capture each other. `CliRun.Run` also locks around the
  capture as a backstop — a class that forgets the attribute used to make the suite fail two random
  tests per run, so the attribute is the rule and the lock is the safety net, not the other way round.
* Real binaries that cannot be checked in are read from a directory named by an environment variable,
  and the tests that need them *skip* when it is unset — `RECON_VB6_INPUTS` for the Visual Basic 6
  runtime files, `RECON_VB6_WILD` for the real VB6 programs in `/home/user/vb6-wild`,
  `RECON_LIB_INPUTS` for the `.lib`/`.a` archives, and `RECON_PCODE_INPUTS` for the 42 p-code VB6
  programs a `recon pcode` decode is measured over. Each has a `README.md` beside it in `/home/user/`
  saying where the files came from. A test that asserts over a real file is a test that
  cannot be run by everyone, so it is a test that must say why it skipped.
* Golden files are compared after `GoldenTests.Scrub`, which replaces machine-specific paths; hashes,
  versions and scores stay in, because those are what the golden is for.
* A new behaviour needs a test that fails without it, and a change to a golden file must be explained
  in the commit/notes — goldens are regenerated deliberately, never to make a run green.
* **A skip is a test that is not running, and a passing suite says nothing about it.** Four tests here
  return early when mingw is absent, and it was absent for the whole life of this container, so they had
  been green without ever executing. The one that closes the reconstruction loop — build the source,
  compare against the original, every function — was also wrong twice over, and only showed it once it
  ran: it asked for an input file its own project does not declare, and it compared the image *after*
  the test had deliberately changed the source against the original. So:
  when a tool a guard checks gets installed (mingw, clang, llvm, a corpus built by a script), **re-run
  the gate and count what ran, not just what passed**; and when a guard goes from skipping to running,
  expect failures, because a test nobody could run is a test nobody could correct.

## Workspace hygiene

The workspace is snapshotted and has a budget (128 MB, 10,000 files), and going past it silently drops
files. It went past it once, so the rules are worth writing down rather than rediscovering:

* **Nothing heavy is written under `/home/user` that can be regenerated.** In this environment — a
  checkout *inside* the workspace with the SDK outside it — `tools/env.sh` moves the build output to
  **`$RECON_ARTIFACTS`** (default `/tmp/recon-out`), because a full build is over a thousand files and
  a few hundred megabytes: inside the workspace they are excluded from the snapshot but still *counted
  against its budget*, and going over the budget drops files, which is what the second over-budget
  failure was. `. tools/env.sh` before any `dotnet` command; `bootstrap.sh` sources it and builds into
  the same place. `Directory.Build.props` is where the redirect is (`ArtifactsPath`), and it still
  defaults to `out/` for a checkout anywhere else, so nothing about a normal clone changes.
  `RECON_REPO_ROOT` exists for the same shape: a test run whose binaries were placed outside the
  checkout cannot walk up to the repository from them, so it is told where the repository is. Scratch —
  inventories, candidate binaries, corpora built to compare against — goes to `/tmp`. One inventory of
  the 11.8 MB VB6 client is 71 MB, more than half the budget on its own.
* **What must stay** is the source, the docs, the tools and the real inputs the tests read
  (`vb6-inputs/`, `vb6-wild/`, `pcode-inputs/`, `lib-inputs/`). These look like the biggest thing in
  the workspace and they are the *reason* it can be checked at all: deleting one turns a test that
  measures something into a test that returns early and reports success.
* **What may be deleted at any time** — everything below is regenerated by a command, and deleting it
  does not lose a measurement: `out/` and `$RECON_ARTIFACTS` (bootstrap.sh rebuilds them),
  `tests/corpus/` (`tools/build-corpus.sh` and its `*-elf`/`*-msvc`/`*-macho` siblings), `/tmp/**`, and
  the `.dotnet`/`.nuget` caches beside the SDK (bootstrap.sh reinstalls the SDK).
* `/tmp` and `/opt` do not survive between turns, and a **workspace over its budget resets the
  sandbox**: `/tmp`, `/opt` and every build output go, while the repository source and the real inputs
  stay. `bootstrap.sh` rebuilds the rest in about eighty seconds, and a test run right after a restore
  is a test run against a rebuild: check that the tools it guards on are actually installed before
  trusting the count. Scratch scripts and measurement logs do not survive either, so a number that
  matters has to be written down (in `docs/`, or re-measurable by a tool kept in `tools/`).

## The shape of a pass over a large binary

Three things in this codebase have been killed, or nearly killed, by the kernel on the same 11.8 MB
program, and they were all the same mistake: the inventory, the listing path, and the comparison each
built a complete representation of every instruction and kept it. What works is a rule, not a trick:

* **A pass that reads every instruction must not keep every instruction.** Decode a body (or a window),
  take the summary it exists to produce — a bit, a count, a hash, a histogram — and let the body go.
  `InventoryAnalyzer` decodes windows; `ComparisonBuilder.SummarizeAll` keeps a `BodySummary` and drops
  the body; `SideBodies` makes a body again when a stage asks, and only keeps the ones that fit in a
  budget that is deliberately small.
* **A number that is not measured is a guess, and the guess is usually wrong.** Every phase report in
  this project (`inventory --verbose`, `diff --verbose`) exists because the obvious culprit was not the
  cost: the seed pass was the slow part of the analysis, not the function builder; the alignment was not
  the slow part of the comparison, the normalizing was. Add the phase report before optimizing a pass,
  not after.
* **An address is not a number.** A comparison between two builds is only meaningful for what the
  linker may move, so every address-shaped operand has to be resolved to an identity: a symbol, an
  import, a jump-table slot, a branch target *relative to the function that contains it*, an immediate
  that lands on something the symbol table names. When one of those is compared as a number, a rebuild
  that moved a function scores like a rebuild that rewrote it — 0.86 for two builds of identical source,
  which is exactly what this cost before it was fixed.

## Layout

See `README.md`. The rule of thumb: if it can be tested without a command line, it belongs in
`src/Recon.Core/`; `src/Recon.Cli/` only parses arguments, prints and returns exit codes.
