# Milestone 2 — status (compare engine)

Scope: the plan's roadmap line for M2 — *"Compare engine: relocation normalization, per-function
scoring, JSON output, CLI diff"* — and the relocation model of §3.1, which asks for one internal
form for the address-bearing parts of an instruction so that a rebuild's moved addresses do not read
as differences.

Everything below was produced by running the tool; the commands are given so the claims can be
re-checked.

## What is implemented

| Piece | Where | Behaviour |
| --- | --- | --- |
| Comparison document (JSON, schema 0.1) | `src/Recon.Core/Compare/CompareModel.cs` | sides, model, summary, per-function results with per-instruction differences, data comparison, problems |
| Address identity | `Compare/SideIndex.cs` | an RVA is reduced to what it points at: `Dll!Import`, a real symbol name, `name+delta`, `jump_table[3] of owner`, or `.text+0x1A2` when nothing names it |
| Relocation model | `Compare/RelocationModel.cs` (`ReferenceClass`) | one table maps PE, ELF and Mach-O relocation kinds onto `Absolute`, `Relative`, `Import` and `JumpTable`; the per-kind counts and their meaning appear in the document's `model` block (`relocation_classes`, `relocation_model`) |
| Normalization | `Compare/FunctionNormalizer.cs` | decoded body with every reference replaced by its identity, trailing `int3`/`nop` padding trimmed, undecodable bytes counted |
| Symbolic formatting | `Compare/SideIndex.cs` (`SideSymbolResolver`) | the formatter gets the identities through `ISymbolResolver`; see *what counts as a reference* below for which operands are treated as addresses |
| Instruction-level diff | `Compare/FunctionDiffer.cs` | longest-common-subsequence alignment, then removed/added runs paired positionally; each difference is classified `opcode`, `register`, `immediate`, `reference`, `addressing`, `operand`, `added`, `removed` |
| Pairing | `Compare/ComparisonBuilder.cs` | name → alias → identical normalized body → similarity above the threshold, best pair first; stubs (import thunks) never pair with a real body of the same name; whatever is left is reported as `only_left`/`only_right` |
| Scoring | `Compare/ComparisonBuilder.cs` | per function: equal instructions over the longer body; per comparison: equal instructions over the larger side |
| Data comparison | `Compare/ComparisonBuilder.cs` | named data symbols, with relocated words reduced to the symbol they point at, so a vtable of moved function pointers still compares equal |
| CLI | `recon diff`, `docs/cli.md` | text summary, `--function NAME` instruction listing, `-o` JSON, `--threshold`, `--limit`, `--min-score` (exit 1 below), `--check-schema`, `--json` |
| Toolchain settings (§3.1) | `Toolchains/ToolchainProfile.cs` (`[compare]`), `Compare/ComparisonBuilder.cs` (`FromProfile`) | `similarity_threshold`, `ignore_padding`, `difference_limit` come from the toolchain profile of the left side; the command line overrides them, and the document records what was used (`model.profile`, `model.similarity_threshold`, `model.difference_limit`) |

The CLI takes its sides as project directories or as binaries; with fewer than two arguments the
current project supplies what is missing, so the everyday case is:

```bash
recon diff rebuilt.exe                 # the project's original against a fresh build
recon diff old.exe new.exe --summary   # two files
recon diff                             # the project's original against its "reference" input
```

## What counts as a reference

Every operand the formatter asks about is judged by one rule, and the judgement is what keeps the
comparison honest — a reference that is invented shows up as a difference that is not there, and a
reference that is missed shows up as one that is:

* a **branch target** is an address, always: it is where the code goes, and a rebuild moves it;
* a **memory operand** is an address when its target does not depend on a register. `[0x40A044]` is
  an address; `[eax+4]` and `[ebp-8]` are not, and are left as they are so a stack slot is never
  reported as a moved symbol;
* an **immediate** is an address only when the container relocated it *and* it holds the address of
  something this side can name (`mov [esp],offset _matherr`). A constant that happens to fall inside
  the image stays a number.

The identity is then, in order: an import (`msvcrt.dll!fprintf`), the name of the function or data
symbol the address is exactly, the jump-table slot, the containing data symbol plus the offset
(`g_table+8`, or `.data+64` for the section pseudo-entry), and finally the section plus the offset
(`.text+0x68C`). Classifying a *thunk* address as `Import` rather than `Absolute` is what makes the
counts in `model.reference_classes` say how much of a binary is import traffic.

## Evidence

### A binary compared with itself is a perfect match

```
$ recon diff tests/corpus/msvc/sample-cxx.exe tests/corpus/msvc/sample-cxx.exe --summary
diff left 5a3371bf2ee2 vs right 5a3371bf2ee2
  functions    23 left, 23 right, 23 matched (23 exact, 0 changed), 0 only left, 0 only right
  instructions 213 left, 213 right, 213 equal
  score        1
  matched by   name 23
  data         0 differing symbol(s): 27 identical, 0 changed, 0 only left, 0 only right
  references   22 named, 26 unnamed
  model        threshold 0.8, ignore padding true, profile clang-19-msvc
```

Note *what* is being normalized: the same bytes are compared, but every direct call, every absolute
`.rdata` operand and every jump-table load is rendered as the symbol it points at, so the match is
earned rather than trivially true. The same check runs as a test and as a golden file.

### Two optimization levels of one program

`sample-debug.exe` and `sample-release.exe` are the same sources built `-O0` and `-O2` by the same
MinGW toolchain, so this is the honest case: much matches, much does not.

```
$ recon diff tests/corpus/mingw/sample-debug.exe tests/corpus/mingw/sample-release.exe --summary
diff left 41b9c2eff66b vs right 84668014ea9b
  functions    142 left, 142 right, 139 matched (56 exact, 83 changed), 3 only left, 3 only right
  instructions 8124 left, 8076 right, 6802 equal
  score        0.8373
  matched by   name 138, similarity 1
  data         1 differing symbol(s): 146 identical, 0 changed, 1 only left, 0 only right
  references   1035 named, 2325 unnamed
  model        threshold 0.8, ignore padding true, profile gcc-13-mingw
```

The `model` line is new: `recon diff --summary` now says which toolchain's settings the score was
computed under, so a number can never be read without knowing the noise model behind it. Those
settings are the profile's, not the engine's — `[compare]` in `msvc-base.toml`/`gcc-base.toml`:

```toml
[compare]
similarity_threshold = 0.80   # a pair below this is not paired by similarity
ignore_padding = true         # the padding bytes of this toolchain are not code
difference_limit = 24         # differences listed per function
```

`ComparisonOptions.FromProfile` reads that section and maps the profile's `codegen.padding_bytes`
onto mnemonics (0x90 → `nop`, 0xCC → `int3`), so "what this toolchain calls padding" is stated once,
in the toolchain's file. A binary diffed as a plain file has no profile and says so
(`profile null` in the document, "built-in defaults" in the summary).

The three functions that matched nothing are exactly the ones a reader would expect from that pair
of builds, and each is reported rather than hidden:

| side | function | why |
| --- | --- | --- |
| left | `fprintf` 0x14F0, `printf` 0x151D | the debug build has the real bodies |
| right | `fprintf.constprop.0` 0x1540, `printf.constprop.0` 0x1510 | the release build has inlined clones under new names, and only IAT thunks for the originals |
| left | `twin_b` 0x1770 | identical-body folding (`-fipa-icf`) gave the release build one body with two names |
| right | `dispatch.cold` 0x8308 | the release build split the cold path into `.text.unlikely` |

A real function is never paired with an import thunk of the same name (that pairing was produced by
an earlier revision and is now refused by the `IsStub` rule; a test pins it).

The instruction-level view is available per function:

```
$ recon diff tests/corpus/mingw/sample-debug.exe tests/corpus/mingw/sample-release.exe --function=add
changed  name   add   0x00001559  0x00001570  0.1429  6  opcode,removed

  opcode     00001559  push ebp              | 00001570  mov eax,[esp+8]
  opcode     0000155a  mov ebp,esp           | 00001574  add eax,[esp+4]
  removed    0000155c  mov edx,[ebp+8]       |
  removed    0000155f  mov eax,[ebp+0Ch]     |
  removed    00001562  add eax,edx           |
  removed    00001564  pop ebp               |
```

### Across the ABI border

Comparing the MSVC C corpus against its C++ sibling (different sources, same toolchain) pairs the
functions they share by name and leaves the C++-only ones unpaired — the engine does not need
matching builds to say something useful:

```
$ recon diff tests/corpus/msvc/sample.exe tests/corpus/msvc/sample-cxx.exe --summary
  functions    7 left, 23 right, 7 matched (4 exact, 3 changed), 0 only left, 16 only right
  instructions 163 left, 213 right, 105 equal
  score        0.493
```

### Checks that run on every change

* `dotnet test tests/Recon.Tests/Recon.Tests.csproj` — **261 tests**, of which 43 are the compare
  engine's: difference classification, alignment around inserted/removed instructions, symbol
  normalization (`_mul_std@8`, `@sub_fast@8`, `__imp__printf`), the whole engine on both corpora,
  accounting invariants (every function of either side is `exact`, `changed`, `folded`, `only_left`
  or `only_right`), threshold behaviour, the profile's `[compare]` section and
  `ComparisonOptions.FromProfile`, schema validation and the CLI's exit codes.
* Three new golden files: `comparison.sample-debug-vs-release.json` (the full comparison, pinned so
  a change in matching, normalization or scoring shows up), `comparison.sample-cxx-self.json` and
  `inventory.sample-msvc-c.json`. Paths and analysis times are scrubbed; hashes and scores are not.
* `bash tools/validate-corpus.sh` still passes on all nine corpus binaries (the compare engine does
  not change the inventory path, but the gate is what keeps that true).
* The NativeAOT publish (`-c Release -r linux-x64`, 6 167 248 bytes) produces the same comparison
  JSON as the framework build — field by field on the MSVC C-vs-C++ pair, with only the analysis
  timings differing — so the source-generated JSON context covers the comparison document, and a
  `--check-schema` run of the published binary prints `comparison matches schema 0.1`.

## Known gaps and honest unknowns

* **A profile carries three of the settings §3.1 lists, not all of them.** Threshold, padding and
  the difference limit are read from `[compare]`; "folded functions" and "section ordering" are
  still behaviour of the engine and the inventory rather than knobs. The schema (`toolchain.schema.json`)
  rejects anything else in the section, so adding a knob means adding it to the profile schema first.
* **Only PE's relocation kinds arrive from a loader.** The table understands PE, ELF and Mach-O
  kinds (14 of them are tested), and it is what the resolver consults for an operand the container
  relocated, but this environment only ever produces PE: the ELF and Mach-O halves of the table are
  exercised by unit tests and by a fixture relocation, not by a real ELF or Mach-O object. That is
  the plan's M6 portability axis, and it was always the loader that is missing, not the model.
* **Folding is reported, not resolved.** A body that survives under another name is `folded` and
  scores 1.0 with a note saying which function it is identical to; the case the evidence cannot
  decide is a body that is *gone* from one side, which stays `only_left`. Identical bodies also still
  defeat name pairing in one direction: the build that kept two names pairs one of them normally and
  the other one folds.
* **Similarity pairing is bounded, not free.** Every candidate pair is first reduced to a mnemonic
  histogram bound (`SharedMnemonics`), which no alignment can beat, so a pair that cannot reach the
  threshold is never aligned; alignments above 400 000 cells are truncated too. What remains is a
  comparison of histograms per pair, which is cheap but still O(n·m): thousands of unmatched
  functions on both sides would be felt. Measured: 142 functions in ~0.3 s, 23 in ~0.05 s.
* **Data comparison covers named symbols only.** Anonymous data ranges and the section
  pseudo-entries are not compared; the words shown are capped at 16 per symbol so the document stays
  readable.
* **Per-instruction results are a difference list, not a full side-by-side listing.** Every
  difference is reported with both addresses and both texts, and `--function` prints them, but the
  complete aligned listing belongs to the M4 viewer, where it can be scrolled.
* `.pdata`/SEH entries are still parsed but not used to seed boundaries (carried over from M1), and
  `data[]` section pseudo-entries are still emitted next to the symbols in them; the compare engine
  filters them out of its data comparison rather than fixing the inventory.
