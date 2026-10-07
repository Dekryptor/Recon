# recon

A reversing and reconstruction tool for compiled binaries: it builds a trustworthy inventory of what
is inside one, and it compares a rebuild against the original instruction by instruction, normalizing
away the addresses a rebuild is free to change. PE32 was the first format and is the most complete;
ELF32, ELF64 and Mach-O load, are inventoried, are attributed to the toolchain that built them and can
be compared — and a Linux project can be rebuilt with the compiler on `PATH`, with no install directory
configured anywhere.

The goal is the workflow in the plan this repository implements: take an original binary, reconstruct
its source until the rebuild matches, and have a tool that says — honestly and precisely — how far
along that is.

```
$ recon diff build/out.exe original.exe --summary
diff left 41b9c2eff66b vs right 84668014ea9b
  functions    142 left, 142 right, 139 matched (56 exact, 83 changed), 0 folded, 3 only left, 3 only right
  instructions 8124 left, 8076 right, 6802 equal
  score        0.8373
  matched by   name 138, similarity 1
  data         1 differing symbol(s): 146 identical, 0 changed, 1 only left, 0 only right
  references   1035 named, 2325 unnamed
  model        threshold 0.8, ignore padding true, profile gcc-13-mingw
```

## Status

| Milestone | State | Evidence |
| --- | --- | --- |
| **M1** — config, PE32 loader, PDB/map/DWARF/COFF debug info, x86 disassembly, function inventory, xrefs, producer detection, toolchain profiles | done | [`docs/m1-status.md`](docs/m1-status.md): every PDB function at the right address, size and name (7/7 C, 23/23 C++), DWARF and COFF oracles green on nine corpus binaries |
| **M2** — compare engine: relocation normalization, per-function scoring, JSON output, CLI `diff` | done | [`docs/m2-status.md`](docs/m2-status.md): a binary against itself scores 1.0 with 23/23 functions exact; two optimization levels of one program score 0.8373 with every unpaired function explained |
| **M3** — build orchestration: per-unit toolchain lookup, content-hash caching, Ninja generation, the `build` command | done | [`docs/m3-status.md`](docs/m3-status.md): the corpus's `sample.c` rebuilt with its own profile scores **1.0** against the original (142/142 functions exact, 8076/8076 instructions equal, 146 data symbols identical); a rebuild with one function added reports the 73 address shifts it caused |
| **M4** — diff viewer and progress reports | done | [`docs/m4-status.md`](docs/m4-status.md): a progress site that is one HTML file with no dependencies; per-unit shares over the MinGW corpus (84.22% of the original's instructions exact, 56/142 functions verified), and two defects it uncovered — a rebuild's own debug info was never read, which had been costing a project-based comparison half its score |
| **M5** — delink/relink | done | [`docs/m5-status.md`](docs/m5-status.md): all six MinGW corpus binaries relink **byte-identical** — every section at its address with its flags and bytes, header included — and `recon diff` then scores **1.0** (142/142 functions exact, 146/146 data symbols identical); an MSVC-ABI binary relinks identically too, with a GNU ld driver |
| **M7** — the permuter and the agent interface | in progress: the permuter is done, `--flags` finds the build's optimization level, the p-code opcode table is measured off the runtime, big binaries are fast | [`docs/m7-status.md`](docs/m7-status.md): `recon permute` rebuilds each provably-equivalent variant of a unit's source with the project's own toolchain and scores it with the compare engine, so from a source whose `add` is written `b + a` it finds the `a + b` the bytes came from — exact, and written beside the unit's source, never over it. The run is a JSON document (`permute.schema.json`) an agent can branch on: `best_beats_baseline`, `best_source`, `stopped_because`. Checked by 35 tests (19 on the generator's soundness, 9 running the loop over the ELF corpus with a real compiler, 7 on the agent contract). A Visual Basic 6 program is now told apart from a native one by `ProjectInfo.aNativeCode`, the field that is zero when the program is p-code: `binary.vb6` publishes the header, `binary.isa` becomes `vb6-pcode`, the decoder refuses it with the reason in `problems[]` instead of reading interpreter tokens as x86, and it is no longer attributed to the `vb6-native` profile, which describes a compilation that did not happen. And a p-code program's own method tables now reach the inventory: one function per procedure, its extent taken from the descriptor the compiler wrote rather than estimated, its instruction count measured against the runtime that interprets it — **680 functions and 55,135 instructions over the 42-program corpus, no problems**, where the same programs used to inventory as one function named `entry`. Those same programs are now attributed to a toolchain of their own, `vb6-pcode`: they used to be called `msvc-6`, which was true of the linker stamp and wrong about everything else, and what tells them from a native VB6 build is the one field only the program's own header states — `aNativeCode`, zero for p-code. A profile that names a kind of Visual Basic program is suggested for that kind only and the other one is withdrawn from the evidence it matched, so `vb6-native` no longer appears at all for a program with no native code in it. Without a runtime the procedures are still published with their extents and the missing count is stated instead of guessed. Checked by 2 tests over a corpus program, holding the inventory and `recon pcode` to the same procedure list. Each opcode row also says **what the bytes after it are**, read off the handler's own instructions rather than taken from a table of names — of the 1,163 implemented rows, 485 have no operand, 330 read one as a value (an index, a length, a pool reference, an immediate), 303 name a frame slot the handler sign-extends and addresses the frame by, and 45 are a branch the handler adds to the instruction stream's base and goes to — with how many operand bytes there are, where in them the word the kind is about sits (a `Next` takes its counter's frame slot *and* the distance it branches by, so the two are fields of their own), and an `operand_basis` sentence naming the instruction the reading came from. That makes `recon pcode` render a selected procedure the way `disasm` renders machine code — `0x32FA  66 e4 fe 98 01  0x66  5  frame[-0x11C] then +0x198 -> 0x3288`, the operand as it means rather than as it lies — with `target_is_an_instruction_start` saying whether what a branch resolves to is an instruction of the procedure. The kinds are held to the corpus rather than asserted: of the **1,878 branch operands** of the 42 programs, over 680 procedure listings, **1,877 resolve to an instruction start inside their own procedure** — and the one that does not is a `Resume` (`FD 0C`) whose word is `0xFFFF`, which its handler compares against −2 and −1 and, for those, follows to a different stream instead of adding it, so the listing prints `(not an instruction start)` rather than an address — and all **25,812 frame slots** named lie inside the frame the procedure's own descriptor declares. The independent reconstruction's `operand_format` column names a frame slot for 292 of the 303 slot rows and a label or a two-byte distance for all 45 branch rows; the 45 rows it calls a slot or a label while this reader reads data are named in the status document by family, and the 5 of those that are branches in the reference — `OnErrorGoto`, `BranchFVar` — are branches whose handler jumps away with an error code and lets the interpreter's own code do the branching, so there is no shape in the handler to measure. Checked by 5 tests over a synthetic image in both forms, with a real VB6 binary as the reference. An 11.2 MB Visual Basic 6 game client — 4.4 million instructions — used to be OOM-killed after nine seconds; it now inventories in 36 s at a peak of 163 MB, because the analysis decodes a window of a section at a time and keeps the aggregates instead of the instructions — and 36 s became **18 s** once the analysis could say where its time went: `--verbose` prints each phase's wall time (scan 4.7 s, jump tables 4.9 s, prologues 1.4 s, functions 1.6 s, conventions 4.8 s, xrefs 0.4 s), which showed that the seed pass was making two avoidable passes over every instruction and that the size estimator was decoding whole function bodies with register and memory tracking to read one boolean per instruction. The same report said no to a plausible optimization: running the two whole-image phases at once took the analysis to 41.4 s on this two-core machine, so that was reverted with the number recorded. The report then said something about the code rather than the timing: `scan`, `jump_tables` and `prologues` were three walks over the same instructions decoding the same bytes, 7.7 s of a 12.9 s analysis, and they are **one walk now** — the tables are reported as a share of it (`scan/jump_tables`) rather than beside it, the prologue search went from 871 ms to 3 ms, and the analysis is **10.4 s** where it was 12.9 s, with the inventory compared against the pre-change document field by field and **zero paths different**. `recon disasm` was the other half: a listing built the whole inventory to print four instructions (18 s), and now reads the three things it needs — function ranges, import names, data names — from the inventory on disk when it is this build's reading of this binary (0.6 s), with both routes held to identical output by a test and by three listings of the real client compared byte for byte.

The comparison had the same disease and it was still terminal: `recon diff` on that 11.8 MB client was **killed by the kernel with nothing printed** (exit 137), because it normalized every function of both sides and kept all of them — 4.4 million instructions each. It keeps a summary per function now and makes each body when it is asked for, and the same command answers: **304 of 304 functions exact, 4,443,084 of 4,443,084 instructions equal, in 37.6 s at a peak of 684 MB**, with `diff --verbose` reporting the stages. Two scoring bugs came out of the same work, both of them reporting a moved address as a difference: a function's address held in an immediate, and a branch target inside the function being compared. With them fixed, the corpus's debug-versus-release comparison — the same source at two optimization levels — went from **56 exact functions and a score of 0.8373 to 127 exact and 0.9705**, with exactly the same 139 pairings. A side whose inventory `recon inventory` has already written is **read back instead of analysed again** when the document is this build's reading of those bytes: on the client, a pair with neither document on disk compared in **49.2 s** and the same pair with both in **24.4 s**, each side's `analysis_ms` down from 12.6/13.3 s to 0.6/0.4 s and the reports equal field by field apart from `analysis_ms` and `inventory_source` — the field that says which happened, because a report that did not say would be hiding the cost rather than saving it. Reading is guarded the way a listing is guarded and for the same reason (written by `recon`, at this version, about these bytes, not older than them, and actually an inventory and not the comparison document that lives in the same directory), and a document read for a comparison is **not the whole document**: an inventory of this client is 71.7 MB and 49.9 MB of it is the cross-reference list a comparison never reads, so the cache drops it as it reads — before that, reading both sides was killed by the kernel where building them finished. The comparison also stopped decoding a body it does not have to: a pair whose two keys agree — a key being a hash over exactly the normalized instructions the differ compares — is decided from the summaries and counted (`bodies_proven_identical`), the aligned listing of a function asked for by name being the one case that still needs the bodies; what the pairing's similarity stage does decode the comparing stage now keeps, through the same budget that bounds it; and the summary pass accumulates the key, the count and the histograms in one walk over the stream instead of materializing the body, which is what put the peak where it was — inside a pass, on the largest function, not in the documents. On this client that took the compare stage from **27–29 s to 4 ms** and a self-comparison from **100 s at 1.0 GB to 37.6 s at 684 MB**, with 301 of the 304 pairs proven identical without a decode and the corpus's debug-versus-release comparison unmoved at 127 exact of 139 matched, 0.9705. The same work settled how many version strings one tool has: there were three (the entry assembly's informational version, `0.1.0` in two library defaults, and `Assembly.GetName().Version`'s `1.0.0.0` in the comparison), and the five golden files carried two of them about one tool. `src/Recon.Core/ToolVersion.cs` is the one answer now, asked by everything that writes or checks a version, and the goldens were regenerated deliberately. Three measured costs came out of it: a lookup that walked every function for every instruction, a `--check-schema` that built an object per JSON value and was killed by the very documents it exists to check, and operands decoded for passes that never read them. Checked by 2 tests over a code section larger than one window — one of which found a real bug, a five-byte `call` cut by a window boundary and mis-decoded as two bytes, so that the rest of the section was read from inside it. It also searches the build: `recon permute --flags` tries the optimization levels the unit's profile declares (`-O0`…`-O3`, `-Os`, `-Og` for GCC and Clang, `/Od`…`/Ox` for MSVC — data in the profile, not a rule in the code) and scores each, so from a project built at `-O0` against an `-O2` original it names `-O2`, at 0.905 against a 0.182 baseline. What wins is a flag and not an edit, so `best_source` stays empty and the run says so instead of writing the source out unchanged. Three bugs found by using the tool were fixed on the way: `recon init --project` wrote the project into the current directory instead of the one it was given, `recon toolchain show` printed a profile's own file rather than what it resolves to (so it said `mangling: none` about a compiler that mangles everything), and the example project's copies of the shipped profiles had gone stale — missing two profiles and carrying seven out-of-date ones, which was also holding a golden file at an answer M6 had already fixed. `recon doctor` now warns when a project's copies fall behind. A library beside the binary is read too: `recon lib` lists a `.LIB`'s members and reads each object's `@comp.id` — the `(product id << 16) | build` stamp every Microsoft object carries — so `VBAEXE6.LIB` names **masm_6.13 build 7299**, the same tool its own banner does, and a real import library's 21,667 members classify into counts that add up exactly, its import symbols checked against the archive's own symbol index. Checked by 15 tests over three archives from three producers. And the p-code instruction set is now **measured rather than cited**: `src/Recon.Core/Vb6/PcodeRuntime.cs` finds the interpreter's dispatch in `MSVBVM60.DLL` — twelve bytes, `xor eax,eax` / `mov al,[esi]` / `inc esi` / `jmp dword ptr [eax*4+table]` — and **294 sites naming 6 tables**. 289 of them share one, because every handler dispatches the next opcode when it finishes; that table's 256 entries hold **252 distinct handlers**, so an opcode and a handler are the same thing there. The other five hold 165/188/204/168/**71** and the four unguarded ones each send a block of slots to a handler all four agree on — the one for a case the interpreter does not handle. The sixth is guarded, and the guard is the interpreter's own statement of the table's length: `cmp eax, 46h` before the jump, so 71 entries rather than 256 and the bytes after it are not opcodes at all. So VB6 p-code is **256 opcodes plus the sub-opcodes of the five lead bytes**, each an address in the runtime's own `ENGINE` section, read out of the shipped file — not a table reconstructed from a blog. Checked by 5 tests against the real runtime, with an ordinary DLL as the control. A VB6 program is read as the structure it is: `recon vb6` walks entry point → VB header → project data → object table and reports the objects the project is made of — **73 for ElementEvil.exe (39 forms, 9 classes, 25 modules)**, 36 for Microsoft's own VISDATA sample — every name and kind read from files VB6 built, with the build path still in them in UTF-16: VISDATA says `d:\sources\vb98\vbsamp\samples\visdata\visdata.vbp`. Checked by 64 tests in all, counted class by class — the runtime's tables, the p-code programs, the VB6 structures and the compiler inputs (OpcodesTests 21, Vb6ProgramTests 15, PcodeProgramTests 11, RealVb6Tests 7, PcodeRuntimeTests 5, Vb6PcodeTests 5): `recon opcodes` reads each of the runtime's six tables and reports what each handler calls and how long the instruction is, naming the 159 rows whose evidence is exactly one runtime function (`vbaStrCat`, `vbaLenBstr`, `vbaFileOpen`, `vbaVarLike`, `vbaNextEachVar` — the function's own name, undecorated) and leaving the rest unnamed with the reason written out, because a name taken from the wrong call is the plausible reconstruction this reader exists to replace. A call through a register the handler loaded (`mov ebx, imm` … `call ebx`) is resolved, a handler that calls only a helper is named for what the helper reaches and the report marks that evidence second-hand (`via`), and the 373 slots the runtime does not implement — 185 empty, 188 sent to the one handler that raises — are reported as that instead of being named after the error path, which is what an earlier revision did to all 188 of them. Every row of the primary table has one measured length, and where the independent reconstruction of this same runtime states one too, 1,327 of the 1,340 comparable rows agree (99.0%) — the 13 that differ are named in the status document rather than smoothed over, and `tools/compare-opcodes.py` reproduces the comparison. Each row also states **what the opcode does to the stack and whether it can raise**: the effect is read out of the handler's own code in dwords of the machine stack (which in this interpreter *is* the operand stack), a call removing what the callee's own `ret N` pops — 806 of the 1,163 implemented rows have a single effect, 2 have two, and the other 355 say in words why there is none. `raises` is a fact about reachability of one function, the runtime's raiser at **0x6603852C** — found from the shape of the code that jumps to it with an error code loaded, `mov eax,9C68h` / `jmp` / `push eax` / `call`, not from a name or a count of how many handlers jump where — and 330 rows can raise, 52 of them implemented ones with no stack effect at all because an instruction that raises never reads the next opcode. The effects are cross-checked against the same independent table's `pops`/`pushes` columns: 532 of the 777 comparable rows state the same net slots (68%), and the differences are counted in different units rather than read differently — a variant is four slots there and one stack dword here. Two bugs came out of this: the decoder never filled in `RetPopBytes`, so every call read as removing nothing and the inventory's `ret N` evidence had never been printed, and a routine's `ret` has to be read up to the first one (the runtime is a run of thunks each ending in its own `ret N`) with a jump past that bound resolved as a tail call, which is what took the measured rows from 555 to 806. The procedure table and instruction stream are read too: 42 p-code programs — the DeForm6 corpus, built by the VB6 IDE on a Windows XP host, with their sources — give **680 procedures, every one of which decodes to its own descriptor** (222 exactly, 458 with one to four bytes of alignment), none admitting two readings and none unreadable — four wrong length measurements had left 100 of them without a reading at all, and each is now a test. `recon pcode` names the runtime it measured the lengths from and prints that file's own version, because a listing decoded against another build's interpreter would be a plausible listing of the wrong thing. Still open: the reading that needs the programs rather than the runtime — checking the decoded streams of the corpus against the sources that produced them — the mnemonics of the 1,377 unnamed rows worked through one at a time, 32-bit ARM, and the Borland/Delphi/Watcom profiles and WASM plugin API, which are deferred rather than missing |
| **Patterns** — library signature database (plan §3.1) | done | [`docs/signatures-status.md`](docs/signatures-status.md): `recon sigs build` over the MinGW corpus produces **135 patterns** from 141 named functions (65 when restricted to the runtime's own units); applied to a stripped binary with no symbols, no debug info and no map, those 65 name **24 of its 107 functions** — `mainCRTStartup`, `atexit`, `__mingw_vfprintf` and the rest — all attributed to `mingw-w64-crt`, while the program's own `fib`, `loop_sum` and `dispatch` stay unnamed because that is the work left to do. Checked by 24 tests |
| **M6** — portability: ELF first, then Mach-O | done, except 32-bit ARM | [`docs/m6-status.md`](docs/m6-status.md): `src/Recon.Core/Elf/` reads ELF32/ELF64 in either byte order — sections, segments, both symbol tables with versioning (`printf@GLIBC_2.2.5`), relocations with the address each one points at, the dynamic table, notes and the build id; `src/Recon.Core/Macho/` reads thin and fat Mach-O in either byte order, including the `.dSYM` bundle a Mach-O build keeps its DWARF in and the indirect symbol table that names its stubs. On top of an `IBinaryImage` abstraction the inventory, decoder, xrefs (including jump tables in both position-independent idioms, and in `__text` inside the function that reads them), producer detection, compare engine and reports take either input; four profiles (`gcc-14-elf64`, `gcc-14-elf32`, `clang-19-elf64`, `clang-19-macho64`) attribute every corpus binary to the compiler that built it, and `examples/elf-project` rebuilds two units of it and scores **0.8143**. An ARM/ARM64 image is loaded and inventoried — its relocation types named against LLVM's own assembler, its functions named from its map and its bundle — and AArch64 is **decoded** (`docs/m7-status.md`): every instruction is four bytes, so a walk can tell where the next one starts without knowing what this one does, which is what licenses a partial decoder. On the corpus' arm64 binary it reads **206 of 206** instructions of `__text`, and all 14 calls land on addresses the linker's own map names. 32-bit ARM is still refused with the reason in `problems[]`. Sixteen profile files ship in all: the fourteen `recon toolchain list` shows (the four above, the MinGW and MSVC sets, and the two Visual Basic 6 ones) plus the two bases they extend; the last to be added is `vb6-pcode`, the profile for a Visual Basic 6 program compiled to p-code, told from the native one by the program's own header — Visual C++ 5.x, the toolset of Visual Studio 97, measured on VBA6.DLL, which carries both a 5.12 linker stamp and product id 0x0013 at build 8078, and which names the toolset behind the three Visual Basic 6 files that stamp 5.2 — see [`docs/toolchains.md`](docs/toolchains.md). Checked by 88 tests and by the oracles over the corpus (`validate-corpus.sh`: **ok, 24 binaries**; `relink-corpus.sh`: **6 identical**) |
The exception directory is read now, which is the compiler's own table of function extents. Every
64-bit PE carries one in `.pdata` — a begin and an end per function, plus where the unwinder's data for
it lives — and it is the only thing in a binary that says where a function *ends*, so a stripped image
that has it does not have to guess every size: the same source compiled `-s` against itself compiled
`-g`, with DWARF as the oracle, went from **96 functions with every size estimated to 126 of DWARF's
128**, all 96 extents used as sizes, no start invented, and 30 unknown sizes left where there had been
96. Making that work turned up a bug the corpus could not have caught, because **every binary in it is
32-bit**: the loader read `NumberOfRvaAndSizes` at the 32-bit offset on every image, and in a PE32+
header that offset holds the high half of `SizeOfHeapReserve` — zero — so **no data directory of any
64-bit image had ever been read**: no imports, no exports, no exception directory, no debug directory,
on every 64-bit image the tool has ever loaded. Both are read now, both have tests, and the 11.8 MB VB6
client still inventories to the same 304 functions, 4,417,764 instructions and 414,282 xrefs it did
before the round.


**Licence: not decided yet.** The plan's §11 asks for one to be chosen early; there is no `LICENSE`
file, so the code is "all rights reserved" by default. Until a licence is added, treat it as something
to read rather than to redistribute.

Windows-first and 32-bit x86 PE32 first, per the plan. Four toolchains are covered today: MinGW GCC
(DWARF + COFF + map files), the MSVC ABI (PDB + CodeView, produced by clang/lld-link in the test
corpus), GCC/Clang on ELF (DWARF + the symbol table + map files) and clang on Mach-O (DWARF in a
`.dSYM` bundle + the symbol table + ld64 map files). Written in C#, published as a single NativeAOT
binary — no runtime needed on the target machine.

## Quick start

```bash
. tools/env.sh                    # .NET SDK location for this checkout
dotnet build src/Recon.Cli/Recon.Cli.csproj
dotnet test tests/Recon.Tests/Recon.Tests.csproj

# build the test corpora (both are generated, never committed)
bash tools/build-corpus.sh        # MinGW PE32 corpus
bash tools/build-msvc-corpus.sh   # MSVC-ABI corpus: clang + lld-link, real PDB
bash tools/build-elf-corpus.sh    # ELF corpus: gcc, g++, clang, -m32, shared objects, stripped
bash tools/build-macho-corpus.sh  # Mach-O corpus: clang for an Apple target, linked by ld64.lld
                                  # (needs clang-19 and lld-19; skipped when they are missing)

# run the acceptance harness: inventory every corpus binary and check it against an oracle
bash tools/validate-corpus.sh --verbose

# native binary (~6 MB, no runtime dependency)
dotnet publish src/Recon.Cli/Recon.Cli.csproj -c Release -r linux-x64
out/publish/Recon.Cli/release-linux-x64/recon --help
```

A first project, from a binary and nothing else:

```bash
recon init --name sample --input sample.exe     # writes project.toml with the input hash
recon verify                                    # locks and checks the inputs
recon inventory -o build/inventory.json --check-schema
recon inspect functions
recon inspect xrefs --min-confidence high
recon inspect data --all            # data symbols; --all adds the section fallback ranges
recon disasm 0x1010 --count 20

# naming library code from patterns, for a binary that has no symbols at all
recon sigs build --library mingw-w64-crt --unit crt,mingw -o signatures.json
recon sigs apply --signatures signatures.json     # what it names in this binary
recon inventory --signatures signatures.json -o build/inventory.json

# reconstructing: name the units and the toolchain, build, then measure
recon build --dry-run               # what would run, and with which flags
recon build                         # compile each unit, link, cache the result
recon diff build/sample.exe inputs/sample.exe --summary --min-score 1.0

# how far along is it, and where
recon report                        # progress.json, the site, and a history entry
recon report --aligned              # the site carries instruction-level detail too
recon serve --port 8080             # the same viewer, live, while you work

# delink and relink: the original, cut into pieces a linker puts back at the same addresses
recon delink --check-schema         # build/delink/*.s, link.ld, delink.json
recon link                          # assemble and link them, then compare with the original
                                    # a unit with provider = "rebuilt" is filled from its own object

# the search: the same program written another way, and which one the bytes came from
recon permute --function add        # score the source as it stands, then every variant of it
recon permute --function add --json --check-schema   # the same run, for a script or an agent

# a library beside the binary: the objects a link can pull in, and what compiled them
recon lib inputs/VBAEXE6.LIB       # members, and each object's @comp.id

# the text in a binary: where it lies, so the message leads to the code that prints it
recon strings inputs/sample.exe --filter "%s" --json --check-schema
recon strings inputs/sample.exe --encoding utf16 --section .rdata --unique

# a Visual Basic 6 program: its objects, their kinds, and the path it was built in
recon vb6 inputs/VISDATA.EXE
recon opcodes inputs/msvbvm60.dll # the runtime's 256 p-code opcodes, from their handlers
```

`recon init` writes a project; `local.toml` (next to it, not committed) can redirect where the inputs
actually live, so `project.toml` keeps only relative paths and hashes.

`examples/progress-demo/` is a project that measures progress: three units declaring which functions
each is responsible for, over the MinGW corpus's release build as the original and its debug build as
the rebuild. `recon report --project examples/progress-demo --aligned` writes the report and the site;
`recon serve --project examples/progress-demo` opens the same viewer live.

`examples/elf-project/` is the same workflow on Linux, and is what "no configuration" looks like: it
names no toolchain at all, so `recon build` uses the profile the binary's own evidence points at and
runs the `gcc` on `PATH` — `recon doctor` says which profile it chose and why.

## What it produces

* **`build/inventory.json`** — the contract later milestones build on (schema 0.1): sections, imports,
  exports, relocations linked to the instructions that use them, functions with per-function
  confidence and calling convention, data, jump tables, xrefs, producers, TLS, and `problems[]` for
  everything that could not be decided. Addresses are RVAs.
* **`build/comparison.json`** — the compare engine's output (schema 0.1): the two sides, the model
  (relocation classes, reference counts, how functions were paired, the similarity threshold), a
  summary with the score, and per function the status, the score, and the instruction-level
  differences classified as `opcode`, `register`, `immediate`, `reference`, `addressing`, `operand`,
  `added` or `removed`.
* **`build/build.json`** — what `recon build` did (schema 0.1): every unit with its toolchain, flags,
  cache key and cache reason, the compiler's own dependency list, and the linked image. It is read
  back on the next run, so it is both the report and the cache.
* **`build/build.ninja`** — the same build as a Ninja file, for anyone who would rather drive it with
  ninja.
* **`build/report/progress.json`** — how far the reconstruction is (schema 0.1): the three shares
  that must not be confused — `instruction_exact` (the original reproduced instruction for
  instruction), `verified` (original functions matching exactly) and `similarity` (the compare
  engine's score) — then one entry per `[[unit]]` with the functions its covers claim, its share,
  its missing functions, and the functions no unit claims at all. `history[]` and `previous` carry
  the snapshots `[report] history = true` appends to `build/report/history.jsonl`.
* **`build/report/index.html`** — the progress site: the whole comparison and progress report
  embedded in one file, no framework, no build step, no network. Function-by-function aligned
  listings, a per-unit table, and a treemap of the image coloured by status.
* **`build/delink/`** — the original taken apart: one `.s` file per section (bytes as bytes, every
  absolute address written as a reference to the symbol it points at), `link.ld` putting every section
  back at the address it came from, and `delink.json` (schema 0.1) naming every piece, every fixup and
  the unit responsible for each. `recon link` turns it back into an image and prints the verdict — and a
  piece a unit claims with `provider = "rebuilt"` is filled from **that unit's compiled object**, with
  the compiler's addresses written out as references to the image's own names, so reconstruct a unit's
  source and the relinked image carries its bytes (the corpus's own source as one unit: 13 of 13 pieces,
  859 bytes, 30 references, `relinked identical`):
  `relinked identical: 16 section(s), 192851 byte(s), header included`.
* **`build/permute/`** — the search's answer: the source of the best variant, written beside the
  unit's (never over it) when it beats the source as it stands. The run itself is a JSON document
  (`permute.schema.json`): the baseline, every variant with its score, `best_beats_baseline`, where
  the winning source was written, and why the run stopped.
* **Human-readable `inspect …`** for every part of the inventory, and `diff`'s summary tables.

Everything above is also a JSON document on stdout with `--json`, described by a schema the tool
ships and checked by `--check-schema`: [`docs/agent-interface.md`](docs/agent-interface.md) is the
contract for driving it from a program, including what a *failed* run prints.

Exit codes are stable across commands: `0` ok, `1` a check failed (hash mismatch, schema violation,
`--min-score`), `2` usage, `3` configuration, `4` internal error.

## How correctness is established

Nothing here is "probably right": every claim in the status documents was produced by running the
tool against sources the tool does not own.

| Source | What it proves |
| --- | --- |
| PDB read by `llvm-pdbutil` (`tests/tools/validate_pdb.py`) | every `S_GPROC32` appears with its address, size and name; public symbols agree on function vs data |
| DWARF DIEs, COFF symbol table, GNU ld map (`tests/tools/validate_inventory.py`) | function boundaries, sizes and the function/data split for the MinGW corpus |
| Golden files (`tests/Recon.Tests/golden/*.json`) | inventories and comparisons are byte-stable across changes; a diff is either intended or a bug |
| `dotnet test` (602 tests) | the engines' behaviour on synthetic fixtures and on all four corpora |
| `bash tools/relink-corpus.sh` | every corpus binary delinked and relinked: 6 identical, 0 differing |
| NativeAOT vs framework build | the published binary produces the same JSON as the test build |

Run `bash tools/validate-corpus.sh --verbose` to see the per-binary table.

## Repository layout

```
src/Recon.Core/          the library: everything except argument handling
  Pe/                    PE32 loader, Rich header, MSVC signature demangler
  Elf/                   M6: ELF32/ELF64 loader: sections, segments, symbols, relocations, dynamic
  Macho/                 M6: Mach-O loader: thin and fat, segments, nlist, the indirect symbol table
  Images/                the image abstraction (`IBinaryImage`) both formats satisfy, and the loader
                         that picks one by sniffing the file
  DebugInfo/             PDB, DWARF, COFF symbols, ELF and Mach-O symbols, map files (GNU ld, MSVC
                         link and ld64); merged into one symbol set
  Analysis/              iced-x86 decoding, function boundaries, jump tables, xrefs, producers
  Inventory/             the inventory model + source-generated JSON
  Signatures/            patterns that name library code when no symbol does (recon sigs)
  Compare/               comparison sides, address identity, normalization, diffing, scoring
  Build/                 M3: tool resolution, the plan, the cache, depfiles, the runner, ninja
  Reporting/             M4: the progress model, the site, and the local server
  Delink/                M5: the plan, the assembly, the linker script, the header, the verdict
  Config/  Toml/  Project/  Schema/  Schemas/  Toolchains/  Verify/  Reporting/
src/Recon.Cli/           the front end: commands, help, generated docs
tests/Recon.Tests/       xUnit tests, fixtures, golden files
tests/corpus/            corpus sources (generated binaries are ignored)
tests/tools/             the two Python oracles
tools/                   env.sh, corpus builders, the acceptance harness
docs/                    cli.md (generated), m1-status.md … m6-status.md, signatures-status.md,
                         toolchains.md
```

## Design decisions worth knowing

* **Addresses are identities, not numbers.** The compare engine turns every address-bearing operand
  into what it points at (`Dll!Import`, `symbol+4`, `jt#3@owner`, or `.text+0x1A2` when nothing names
  it). A generated name such as `sub_1010` is never treated as a symbol: it is derived from the
  address a rebuild may change.
* **Unknowns are recorded, never guessed.** `problems[]`, per-function `confidence`, `unknowns[]` and
  the comparison's `only_left`/`only_right` exist so that a reader can see what the tool could not
  decide. A comparison that quietly dropped functions would look better than it is.
* **Toolchain behavior lives in profiles.** `src/Recon.Core/Toolchains/profiles/*.toml` carry
  detection evidence, calling conventions, compare settings and — since M3 — how to run the real
  compiler and linker, with inheritance; the code asks the profile instead of hard-coding a
  toolchain's habits. Where the tools are on *this* machine is `local.toml`'s job, never the
  profile's.
* **JSON via source-generated contexts**, so the NativeAOT publish keeps working: adding a document
  type means adding it to a `JsonSerializerContext` and to `src/Recon.Core/Schemas/*.schema.json`.

## Documentation

* [`docs/m1-status.md`](docs/m1-status.md) — what M1 implements, how to re-check it, and the measured
  evidence per corpus binary.
* [`docs/m2-status.md`](docs/m2-status.md) — the compare engine, its model, and its evidence.
* [`docs/m3-status.md`](docs/m3-status.md) — build orchestration: what a unit is, what invalidates a
  cached object, and the measured end-to-end result.
* [`docs/m4-status.md`](docs/m4-status.md) — the diff viewer and the progress report: the three shares
  that must not be confused, and how a unit earns its share.
* [`docs/m5-status.md`](docs/m5-status.md) — delink and relink: the pieces, the plan, the verdict,
  and the six corpus binaries that come back identical.
* [`docs/m6-status.md`](docs/m6-status.md) — the ELF and Mach-O loaders, the analysis over them, and
  the profiles that name who built one.
* [`docs/cli.md`](docs/cli.md) — generated command reference (`recon gen-docs`).
* [`docs/agent-interface.md`](docs/agent-interface.md) — the JSON contract for driving it from a
  program: one document per run, the exit codes, the error document, and the loop to follow.
* [`TODO.md`](TODO.md) — what is left, in order.
* [`AGENTS.md`](AGENTS.md) — how to work in this repository (build, test, conventions).
