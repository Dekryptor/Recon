# Milestone 1 — status

Scope: config, PE32 loader, debug-info readers, x86 disassembly, function inventory, xrefs and
producer detection, with the CLI surface from §7.3 of the plan.

Everything below was produced by running the tool, not by inspection; the commands are given so the
claims can be re-checked.

## What is implemented

| Plan item (§7.2) | Where | State |
| --- | --- | --- |
| 1. `project.toml` loader with hash-locked inputs | `src/Recon.Core/Config/*`, `src/Recon.Core/Toml/*` | done, strict parser (17 tests) |
| 2. PE32 loader: sections, imports, exports, relocations, entry | `src/Recon.Core/Pe/PeLoader.cs`, `PeImage.cs` | done (18 tests) |
| 3. Debug info: PDB first, map file second | `DebugInfo/{PdbReader,DwarfReader,CoffSymbols,MapFileReader}.cs` | done: 11 tests on synthetic MSF files plus the MSVC corpus, where every function in a real PDB is found at the right address and size |
| 4. x86 disassembly of code regions | `Analysis/CodeDecoder.cs` (Iced) | done |
| 5. Function boundaries with per-function confidence | `Analysis/InventoryAnalyzer.cs` | done, `high/medium/low` per function |
| 6. Xref table | `Analysis/InventoryAnalyzer.cs` + `xrefs[]` | done, 2208 xrefs on the sample, every in-code relocation has one |
| 7. JSON inventory + `inspect` | `Inventory/*`, `recon inspect …` | done, schema 0.1 clean |
| 8. Producer detection (Rich header, PDB compilands, DWARF, comments, imports, sections) | `Analysis/ProducerDetector.cs` | done, plus per-compilation-unit attribution; no unmatched producer strings left in the corpus |
| 9. `ToolchainProfile` loader, MSVC first | `Toolchains/*` | done: 8 profiles, inheritance resolved, MSVC 2008/2010 plus `clang-19-msvc` for the MSVC-ABI corpus |

CLI: `init`, `verify`, `validate`, `doctor`, `inspect <…>`, `inventory`, `disasm`, `toolchain <…>`,
`hash`, `migrate`, `schema <…>`, `gen-docs`, and since milestone 2 `diff` (compare engine; see
`docs/m2-status.md`). Exit codes are stable: `0` ok, `1` check failed, `2` usage, `3`
configuration, `4` internal.

## How to run and re-check

```bash
. tools/env.sh                                   # .NET SDK location
dotnet build src/Recon.Cli/Recon.Cli.csproj      # -> out/bin/Recon.Cli/debug/recon.dll
dotnet test  tests/Recon.Tests/Recon.Tests.csproj

bash tools/build-corpus.sh                       # MinGW PE32 corpus (reproducible)
bash tools/build-msvc-corpus.sh                  # MSVC-ABI corpus: clang + lld-link, real PDB
bash tools/validate-corpus.sh --verbose          # inventory + independent oracle, per binary

dotnet publish src/Recon.Cli/Recon.Cli.csproj -c Release -r linux-x64   # NativeAOT, ~5.6 MB
```

The corpus is built reproducibly (`SOURCE_DATE_EPOCH`, `--no-insert-timestamp`,
`-frandom-seed`), so its SHA-256 values, `project.toml` locks and the golden inventory stay valid
between builds.

## Verification evidence

Two things are run on every change: `dotnet test tests/Recon.Tests/Recon.Tests.csproj` (261 tests,
including the synthetic PDB fixtures, both corpora, the four golden files — two inventories and two
comparisons — and the compare engine)
and
`bash tools/validate-corpus.sh` (below). Both are green as of this writing.

`tools/validate-corpus.sh` builds an inventory for every corpus binary and checks it against
sources the tool does not own: DWARF DIEs (`objdump --dwarf=info`), the COFF symbol table
(`objdump -t`), the GNU ld map file, and the structural invariants of the JSON itself.
`bash tools/validate-corpus.sh --verbose` prints this table; all eight binaries end in `RESULT: ok`:

| binary | DWARF bodies found | address ok | size exact | size close | COFF function symbols at a start | map symbols without a function |
| --- | --- | --- | --- | --- | --- | --- |
| `sample-debug.exe` | 106/106 | 106 | 106 | 0 | 143/143 | 0 of 209 |
| `sample-release.exe` | 104/104 | 104 | 104 | 0 | 145/145 | 0 of 209 |
| `sample-stripped.exe` (map only) | – | – | – | – | – | 0 of 209 |
| `blind/sample.exe` (nothing at all) | – | – | – | – | – | – |
| `shapes-debug.exe` | 172/172 | 172 | 172 | 0 | 231/231 | 0 of 243 |
| `shapes-release.exe` | 103/103 | 103 | 102 | 1 | 152/152 | 0 of 224 |
| `shapes-dll.dll` | 99/99 | 99 | 98 | 1 | 137/137 | 0 of 196 |
| `msvc/sample.exe` (C) | – (PDB) | 7/7 | 7/7 | 0 | – | – |
| `msvc/sample-cxx.exe` (C++) | – (PDB) | 23/23 | 23/23 | 0 | – | – |

The two `msvc/` rows are the MSVC-ABI corpus: clang 19 with `--target=i686-pc-windows-msvc`, linked
by lld-link 19 with `/debug`, so the debug information is a PDB written by a Microsoft-format
toolchain. `sample.exe` is C; `sample-cxx.exe` is C++ with virtual functions, vtables, RTTI records,
static and const members and member functions decorated `__thiscall`, plus a small stub object for
the three symbols the Microsoft runtime normally supplies (`operator delete`, `type_info`'s vtable,
`_purecall`) so the corpus links without a CRT. Neither binary has DWARF or a COFF symbol table, so
the oracle for them is `llvm-pdbutil` (`tests/tools/validate_pdb.py`), running on every corpus pass:

```
sample.exe:      ok: 7 PDB function(s) matched by address, size and name
                 ok: 10 public symbol(s) agree on function/data
                 ok: 7 function(s) in the inventory, 0 not described by the PDB
sample-cxx.exe:  ok: 23 PDB function(s) matched by address, size and name
                 ok: 51 public symbol(s) agree on function/data
                 ok: 23 function(s) in the inventory, 0 not described by the PDB
```

That is the plan's "every PDB function at correct address and size" clause checked against an
independent reader of the same file: addresses, sizes **and names** must agree with the
`S_GPROC32` records, every public marked as a function must be a function, and no public that is
data may be listed as one. `recon` also has to survive the two length conventions CodeView records
use — link.exe writes the padded size, lld writes it minus two, which is what an `S_END` (four bytes
declaring two) exposes; the reader accepts both and the synthetic fixtures cover each.

The two `size close` cases are the same function in the C++ binaries: the inventory size is eleven
bytes longer than `DW_AT_high_pc`, which is a cold-path tail rather than a mis-detected boundary.
Everything else matches exactly.

`blind/sample.exe` is the stripped binary copied without its sidecar files: no DWARF, no COFF
symbols, no map. The tool still produces a structural-clean inventory (107 functions, 33 high —
entry point, TLS callbacks and import thunks — and 74 medium from call targets, plus both jump
tables and all 2208 xrefs), with no symbol source to lean on.

Specific milestone-1 acceptance criteria:

* **Every function the debug info knows is present, at the right address and size.** The oracle
  matches the inventory against the DWARF DIEs name by name; 104/104 with exact sizes. The function
  count is corroborated by a second, independent source: all 145 COFF function symbols have a
  function starting at exactly their address, and 141 of the 142 inventory functions are COFF-backed
  (the extra one carries DWARF/COFF evidence as an alias).
* **Relocations are linked to the using instructions.** 504 relocations, all `HIGHLOW`; every
  relocation inside code appears as an `xref` with `via_reloc: true`, `in_function` set and
  `in_data` set for the ones in data (the invariant is enforced by the oracle on every run).
* **Jump tables are not misread as code.** Both tables (`0xA058` with 11 entries, `0xA354` with 91)
  live in `.rdata`, are covered by `data[]`, are owned by exactly one function each, and their
  targets are inside that owner.
* **MSVC first.** Both corpus binaries are MSVC-ABI and MSVC-format: every function at the address,
  size and name the PDB gives (7/7 and 23/23), the `.data` publics kept out of the function list,
  the vtables and RTTI records of the C++ binary recorded as data, `stdcall`/`fastcall`/`thiscall`
  read from the decorated public names, and every function attributed to the `clang-19-msvc` profile
  with no `toolchain_unknown` left.
* **Same commands on Windows and Linux.** The tool is a single NativeAOT binary with no
  platform-specific code paths; the corpus and the oracle run from the same script on both, and the
  AOT binary produces a byte-identical inventory to the framework build.

NativeAOT specifics: JSON goes through source-generated contexts (`InventoryJsonContext`,
`ReportsJsonContext`), so the release publish has no `IL2026`/`IL3050` warnings left and the
published binary is 5.6 MB stripped.

## Known gaps and honest unknowns

* `DW_AT_ranges` subprograms (functions GCC splits into hot/cold fragments, e.g. `dispatch`) are not
  compared by the oracle: it reads `low_pc`/`high_pc` only. Their size in the inventory therefore
  comes from terminator analysis and is reported as `size_to_last_terminator`.
* The PDB path is proven against a real PDB, but not against one written by Visual Studio itself:
  clang and lld-link write the same format and the same MSVC ABI, and its records are read with
  llvm-pdbutil, yet a `link.exe`-produced PDB from a Windows box remains the last thing to check.
  The difference that bit once already is the record-length convention, and both are covered.
* The MSVC signature decoder (`Pe/Demangler.cs`) handles free functions, member functions, the
  common argument types, constructors, destructors, vtables and RTTI labels. Function pointers and
  numbered back-references are marked `partial` instead of being guessed: the latter prints
  `<type N>` so a reader can see why the signature stops.
* Producer strings from the GCC 13 runtime objects (`GNU C17 13-win32`) now match a `gcc-13-mingw`
  profile, and the C++ variants (`GNU C++17 14-win32`) match `gcc-14-mingw`; the corpus runs
  warning-free on producers. An unknown producer would still stay unknown rather than be guessed.
* 130 DWARF subprograms are declarations without a body (`verify` and `doctor` report them); they
  are correctly excluded from the function inventory.
* `data[]` lists the read-only and data sections themselves next to the symbols in them (an entry
  named `.rdata` with kind `read_only`, one named `.data`), so one address can appear twice: once as
  a section region and once as a symbol. The checks are unaffected; it is noise for a reader.
* `.pdata` (MSVC exception tables) is parsed as a section and its entries are exposed, but it does
  not yet seed function boundaries; that arrives with the MSVC work.
