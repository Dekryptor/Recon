# TODO

What is left, in the order it should be done. Everything closed below is closed *with evidence* in `docs/m1-status.md` … `docs/m6-status.md`;
nothing is carried as a "known gap" unless it cannot be tested in this environment, and those say so
explicitly.

## Now: M6 — portability

The plan's M6 is *ELF, then Mach-O; x64 and ARM; GCC on ELF, Clang and VB6 native-code profiles;
library signature database* (plan §6). It is being taken in that order, one slice at a time, and
every slice lands on its own with tests and a corpus rather than at the end of the milestone.

- [x] **The ELF loader** (`src/Recon.Core/Elf/`): ELF32 and ELF64, either byte order, executables,
      shared objects and relocatable objects — sections and segments, both symbol tables with symbol
      versioning (`printf@GLIBC_2.2.5`), relocations with their type names and the address they point
      at, the dynamic table, notes, the build id, `.comment`, `.gnu_debuglink`.
      Corpus: `tools/build-elf-corpus.sh` builds ten binaries from the same sources as the PE corpus
      (gcc, g++, clang, `-m32` where the multilib runtimes are installed, plus a stripped copy with
      no sidecar at all). Tests: `ElfLoaderTests` (20), over a synthetic file built in memory — which
      is what covers the byte order and word size no desktop machine has — and over that corpus.
      Evidence: `docs/m6-status.md`.
- [x] **The analysis over ELF** (`src/Recon.Core/Images/`): an `IBinaryImage` abstraction both
      `PeImage` and `ElfImage` satisfy, so the inventory, the decoder (x86-64 through Iced), xrefs,
      producer detection, the compare engine, `report`, `doctor` and `verify` take an ELF input the
      way they take a PE one. `delink` and `relink` stay PE-only and say why when they are handed
      something else. `tools/validate-corpus.sh` sweeps the ELF corpus too, with the linker map as
      the oracle: **ok, 24 binaries**; an ELF compared with itself scores 1, and `-O0` against `-O2`
      of the same program scores 0.3469, which is the honest number. Tests: `ElfInventoryTests` (14).
      Evidence: `docs/m6-status.md`. What it found and fixed on the way: an ELF export is not always
      code (a `stderr` export was inventing a function in `.bss`); a stripped ELF's dynamic symbol
      table, which names no function, must not be preferred over the map file beside it; `debug.sources`
      is a list, because DWARF describes what the compiler emitted and the symbol table what it did
      not; a jump table in a position-independent binary has no constant address, so it is found by
      following which register holds which address and by trying every encoding (`absolute` or
      `relative`); and a displacement is only a data reference when the file addresses it that way
      (`[esi+4]` is not `[4]`, which was inventing xrefs into the header).
- [x] **DWARF from the ELF file's own sections.** The reader reads DWARF by section name and is
      handed the ELF file's sections, so a stripped binary's DWARF is nothing at all and an
      unstripped one's is complete — the oracle matches 10/10 subprograms of
      `sample-elf64-release` at the right address and size.
- [x] **The ELF toolchain profiles** (`gcc-14-elf64`, `gcc-14-elf32`, `clang-19-elf64`): a producer
      is *named* for an ELF binary, from `.comment`, the DWARF producer, the imports and the
      compiler's own section ids, and `recon build` runs that compiler here. Two things the slice
      had to fix: clang on a GNU system carries a GCC string too (it links GCC's startup files), so
      the tie is broken by how many kinds of evidence each profile matched rather than by the order
      they are declared; and a Unix toolchain has no install directory, so `local.toml`'s
      `toolchain.<id>.root` is optional and a bare `exe` in a profile is looked up on `PATH` — which
      is what made `recon toolchain check` report every Linux profile as failed. Tests:
      `ElfToolchainTests` (16). Evidence: `docs/m6-status.md`, `examples/elf-project` (built with the
      `gcc` on `PATH` and scored **0.8143** against the original).
- [x] **`DT_INIT` and `DT_FINI`.** `ElfModel` now carries the two addresses and the seeds them, so a
      blinded binary finds 19 of its 21 functions instead of 17 — `_init` and `_fini` are named by
      nothing else in the file, not even by the map. Evidence: `docs/m6-status.md` ("What the runtime
      calls"), test `A_binary_with_no_symbols_at_all_finds_the_functions_the_runtime_calls`, which
      pins them as the functions below the entry point and above the last call target, since there is
      no name to assert on.
- [x] **Mach-O.** `src/Recon.Core/Macho/` reads thin and fat images in either byte order — segments,
      sections, `nlist`, the indirect symbol table, `LC_MAIN` — and `MachoBinaryImage` adapts it, so
      the inventory, the decoder, the xrefs, the producer detection and the compare engine take a
      Mach-O input unchanged. Four things in the format are not like the other two and are each
      handled on their own terms: `__PAGEZERO` is left out of the image base; an import is reached
      through a stub that jumps through a pointer, and both are named from the indirect symbol table;
      a switch's table sits in `__text` inside the function that reads it, and the comparison skips
      it; and the DWARF lives in a `.dSYM` bundle beside the image, which `ProjectContext` reads when
      the image itself has none. `clang-19-macho64` is the profile; the corpus *can* be built here
      (clang compiles for an Apple target and ld64.lld links it), so it is: five images with ld64 maps
      and dSYM bundles, checked by `MachoInventoryTests` (17) and by the map oracle in
      `tools/validate-corpus.sh` (`objdump` cannot read Mach-O, so the map is the oracle). The 32-bit,
      byte-reversed and fat cases, which no corpus binary here is, are covered by
      `tests/Recon.Tests/Fixtures/SyntheticMacho.cs` and `MachoLoaderTests` (12) — including a 32-bit
      image inventoried end to end. Evidence: `docs/m6-status.md` ("Mach-O").
- [x] **ARM at the loader level** (the decision: describe ARM now, decode it in M7). `Iced` is x86
      only, and handed an AArch64 image it does not fail — it reads ARM bytes as x86-64 and invents a
      program, which is worse than refusing. So an ARM/ARM64 image now loads, is inventoried
      (sections, symbols, relocations, imports, data) and reports `instructions: 0` with
      `the decoder speaks x86 and x86-64, not arm64` in `problems[]`; `recon disasm` refuses with
      that reason. Evidence: `docs/m6-status.md` ("ARM: loaded and described, not decoded"), tests
      `An_image_of_another_machine_is_loaded_but_not_decoded` and
      `The_cli_says_so_too_when_it_cannot_read_the_instruction_set` — both over
      `SyntheticElfOptions.Machine = 183`, which is how it is tested with no ARM toolchain present.
- [x] **Decoding ARM** (M7, where the plan puts the non-x86 instruction sets). `src/Recon.Core/
      Analysis/Arm64Decoder.cs` decodes AArch64. What made it worth doing is a property of the
      instruction set rather than of the code: **every AArch64 instruction is four bytes**, so where
      one starts is arithmetic, not decoding. A walk cannot lose its place, a window boundary cannot
      cut an instruction in half (the bug `StreamingAnalysisTests` caught on x86 cannot happen here),
      and an unrecognised word costs a mnemonic rather than the alignment of everything after it.
      That is what licenses a partial decoder: it decodes every form of branch — `b`, `bl`, `b.cond`,
      `cbz`/`cbnz`, `tbz`/`tbnz`, `br`, `blr`, `ret` — the `adr`/`adrp` pair a position-independent
      binary uses for every global, the `stp`/`ldp` pairs that open and close a frame, and the
      immediate and register arithmetic that builds a constant; a word it does not decode is
      reported as `.inst 0x…`, counted and sized, never as `(bad)`, because calling it invalid would
      be a claim about the program rather than about the decoder. On the corpus' arm64 binary:
      **206 of 206 instructions in `__text` decoded**, none unknown. The check that matters is not
      the count but the graph: **all 14 calls reach an address the linker's own `.map` names as a
      function**, and the addresses the decoder computes for globals are the ones the relocation
      records give (`adr x22` → `0x100008010` = `_g_table`; `adrp`/`str` → `0x100008040` =
      `_g_counter`). Two bugs found by writing it, neither of which the corpus can catch because
      both sit in cases the corpus does not contain: a **sign extension written in unsigned
      arithmetic**, which put every backward branch four gigabytes above its target (all 14 corpus
      calls are forward), and a **bitmask immediate rotated the wrong way** — ROR, not ROL — for
      every immediate in the corpus sits at `immr == 0`, where a rotation and its mirror agree.
      Not done: the sign-extending loads, the SIMD/FP groups and the 32-bit ARM (A32/T32) instruction
      set, the last of which is still refused with the reason in `problems[]`. Evidence:
      `tests/Recon.Tests/Arm64DecoderTests.cs` (14).
- [x] **A VB6 native-code profile** (plan §3.1). Ordinary x86 PE32 from the Visual C++ back end, so
      `vb6-native` inherits `msvc-base` and names the Visual Basic 6 virtual machine
      (`MSVBVM60.DLL`), which nothing but a Visual Basic program imports. Real 1998-2004 binaries
      then arrived and the rest of the evidence was measured from them rather than invented:
      linker version 6.0, and the two Rich product ids a VB6 program carries (36 Basic objects and
      one `vb60` link record, read off VISDATA.EXE). The attribution went from `low` on one kind of
      evidence to `medium` on three. P-code stays in M7, which is what the plan's `isa` axis exists
      for. Evidence: `docs/toolchains.md` ("Visual Basic 6"), `docs/m6-status.md` ("What real Visual
      Basic binaries exposed"), tests `tests/Recon.Tests/Vb6ToolchainTests.cs` (6) over the
      synthetic PE and `tests/Recon.Tests/RealVb6Tests.cs` (7) over the real ones.
- [x] **A Visual C++ 6.0 profile** (`msvc-6`). The toolset behind Visual Basic 6, and the one that
      built LINK.EXE, C2.EXE and CVPACK.EXE: linker version 6.0 and Rich product id `0x0004` at
      builds 8447/8047/8168, all three measured. It exists because those files were being attributed
      to MinGW — `gcc-base` counted an `MSVCRT.dll` import as evidence, and every 32-bit Windows
      binary has one. The GCC profiles now name MinGW's own runtime instead.
- [x] **A Visual C++ 5.x profile.** `src/Recon.Core/Toolchains/profiles/msvc-5.toml`, the toolset of
      Visual Studio 97. The premise this item was written under was wrong, and finding that out is
      what closed it: it says every one of these files "carries no Rich header", and VBA6.DLL does —
      it has four records, one of which is **product id 0x0013 at build 8078**, the Visual Studio 97
      linker, next to a **5.12** stamp in the optional header. Two independent measurements of the
      same tool, which is exactly the evidence the item was waiting for. The profile therefore has
      two rules that fire separately — linker 5.0–5.99, and Rich product ids 0x0002/0x0010/0x0013 in
      the linker role — because the other three files (VB6.EXE, VB6IDE.DLL, MSVBVM60.DLL) stamp 5.2
      and carry no Rich header at all; a rule demanding both would have looked at three real
      binaries and reported nothing. The two MSVC generations do not overlap (5.x against 6.x,
      0x0013 against 0x0004), and every first-place attribution on the existing inputs is unchanged:
      LINK.EXE, C2.EXE and CVPACK.EXE still lead with `msvc-6`, and the two VB6 programs with
      `vb6-native`. MSO97RT.DLL still reports no toolchain, which is right: it stamps 3.10, older
      than the 5.x line and older than any product id measured here. Evidence:
      `tests/Recon.Tests/Msvc5ToolchainTests.cs` (13: 9 synthetic, 4 over the real 1998 files).
- [x] **COFF archives (`.LIB`).** `src/Recon.Core/Archive/CoffArchive.cs` reads the `!<arch>` member
      format, and `recon lib <file.lib>` lists what is inside. An archive is not a program, so it is
      deliberately *not* another `IBinaryImage`: it has no entry point, no sections to decode and
      nothing to put in an inventory, and forcing it into that shape would have meant widening the
      inventory schema's `format` enum with something that is not a program. It is read on its own,
      by path, because a library sits beside the binary under reconstruction rather than being it —
      and the loader says so when handed one (`it is a COFF archive, not a program: recon lib <path>
      lists its members`) instead of refusing with a bare magic number.
      What it reports: each member's name (resolved through the long-name table), kind, size and
      offset; for an object, its machine, sections, symbols and — the reason to read one at all — its
      `@comp.id`, the `(product id << 16) | build` stamp every Microsoft object carries, named from
      the same product-id table the PE Rich header uses; and for a short import member, the DLL and
      the symbol. Measured on three real archives from three producers, in `RECON_LIB_INPUTS`:
      VBAEXE6.LIB (Microsoft, 1998), whose one object reads `@comp.id` 0x000E1C83 = **masm_6.13
      build 7299**, which is the same tool the object's own banner names — two records of one fact,
      written by the same tool, read by two different paths; `windows.0.52.0.lib` (Microsoft, from
      the `windows_x86_64_msvc` crate), **21,667 members whose counts add up exactly** — 2 symbol
      indexes, 1 long-name table, 1,113 import-descriptor objects and 20,551 short import records —
      with the import symbols checked against the archive's *own* big-endian symbol index, a
      separate list written by the librarian; and `libkernel32.a` (GNU ar, mingw-w64), 1,716 COFF
      objects whose names come through the long-name table as `/N`.
      Reading a Unix `.a` was the bug worth finding: `!<arch>` is one container with two contents,
      and reading a COFF header out of an ELF object gives a machine called **0x457F** — the first
      two bytes of `\x7FELF` little endian — and a section count from the ELF class and endianness
      fields. That is a confident answer about a format this reader does not have, which is the one
      thing it must not give, so ELF members are now named as ELF and left alone. Evidence:
      `tests/Recon.Tests/CoffArchiveTests.cs` (10) and `CliTests` (5).
- [x] **Memory held by decoding.** Closed, and measured on the binary that was failing because of
      it: a Visual Basic 6 game client with an 11.2 MB `.text` (`ElementEvil.exe`, 4,417,764
      instructions) used to be **OOM-killed** — RSS reached 1.06 GB in nine seconds, exit 137, no
      output — and now inventories in **36 s at a peak of 163 MB**, with `--check-schema` on, in
      39 s. The analysis no longer holds the instructions: `InventoryAnalyzer` decodes a window at a
      time (256 KB) and keeps what it learned instead of what it read — a bit per instruction start
      (1.4 MB for 2.5 M of them), the set of branch targets, the xrefs, the jump tables. What each
      window hands back is decoded again by the next pass that needs it, which is the trade: seconds
      of decoding instead of gigabytes of retention. Three things fell out of measuring rather than
      guessing:
      - `BuildFunctionLookup` walked the whole function list for every instruction that asked which
        function it belonged to — 304 functions x 4.4 M instructions. It is now a forward sweep,
        which took the xref pass from 27.5 s to 7.9 s on that binary.
      - `--check-schema` parsed the document into a `JsonNode` graph, an object per value: it was
        **killed** by the 51 MB inventory of a 52,000-function program that takes 3 s to build
        without it. The validator now walks a `JsonDocument` instead — read-only, no graph — and
        checks that document in 5.4 s.
      - Every instruction paid for operands no pass read: a string per register, a list per
        instruction with a memory operand. Registers and memory operands are now per-pass options
        (`CodeDecoder.TrackRegisters`, `TrackMemory`, `RegisterMnemonics`), register names come from
        one table built once, and a pass that asks only where branches go does not ask for anything
        else. Together with the two fixes above: 114 s to 36 s.
      Supporting pieces: `DecodedInsn` keeps its booleans in one flags word and allocates its three
      lists on first touch, and `MemoryRef` is a struct rather than a class.
      Evidence: `tests/Recon.Tests/StreamingAnalysisTests.cs` — a code section two windows long, and
      a five-byte `call` placed so a window boundary cuts it in half. The second one found a real
      bug: the decoder reports a length for an instruction the buffer ends inside, so the walk
      resumed mid-instruction and decoded the rest of the section from the wrong address — a
      plausible-looking program out of another program's misaligned bytes. Windows now leave a
      fifteen-byte margin, the width of the longest x86 instruction, and decode what is left again.
      The three small VB6 binaries report **identical** instruction, function, xref and jump-table
      counts before and after the rewrite.
- [x] **Library signature database** (plan §3.1: "so effort isn't wasted reconstructing a standard
      CRT"). FLIRT-style patterns over the corpus the tool already builds, attributed per function as
      `found_by: signature`, with the pattern file in `src/Recon.Core/Signatures/`, its own schema, and
      `recon sigs build|apply` plus `inventory --signatures` (or `[analysis] signatures` in
      `project.toml`) to use them — [`docs/signatures-status.md`](docs/signatures-status.md): 65
      patterns taken from the runtime's own units name **24 of the 107 functions** of a stripped MinGW
      binary that no symbol, no debug info and no map names anything in. A pattern yields to every
      symbol source, never invents a function, and is dropped when two functions share it; 23 tests,
      including one that names a CRT function in that binary.

## Now: M7 — the permuter, and what comes after it

The plan's M7 is *Permuter, plugin API (WASM), agent-friendly interface; non-x86 instruction sets
(VB6 p-code); Borland, Delphi and Watcom profiles* (§6), and it is being taken in that order one
slice at a time. The permuter is first because the other four are either blocked on binaries this
machine does not have — no Delphi, Watcom, Borland or p-code VB6 file, and `AGENTS.md` says no number
enters a profile unless a command printed it — or, for the plugin ABI, explicitly deferred by §3.1
("don't invent a plugin ABI yet"). Design and status: [`docs/m7-status.md`](docs/m7-status.md).

- [x] **The variant generator** (`src/Recon.Core/Permute/VariantGenerator.cs`). The same program
      written a different way: swap two statements that cannot observe each other, swap the operands
      of a commutative operator, rewrite an increment two ways, swap two uninitialised locals, invert
      an `if`/`else`. A variant is only emitted when its equivalence follows from the shape of the
      code, because one that changes what the program means may match the bytes and still be the
      wrong source. Deterministic: the same source gives the same variants in the same order. Tests
      `tests/Recon.Tests/PermuteTests.cs` (19), most of them about when a variant is *refused*.
- [x] **The permutation loop** (`recon permute`, `src/Recon.Core/Permute/Permuter.cs`). Build and
      relink each variant with the unit's own toolchain — every unit, not the one being permuted,
      because a permutation changes the linked image and a filtered build would compare against a
      stale binary — score the covered functions with the compare engine, rank them, and write the
      winning source *beside* the unit's, never over it. The baseline is the score of the source as
      it stands, and when it is already exact the run stops there: nothing can beat 1.0. The unit's
      source is restored in a `finally`, so a failed build or a budget stop cannot leave a
      reconstruction permuted. Tests `tests/Recon.Tests/PermuteRunnerTests.cs` (9) run it for real
      over the ELF corpus with this machine's gcc; one of them starts from `b + a` and ends with the
      search finding `a + b`, exact, written and adopted nowhere without being asked.
- [x] **A schema and `--json` for the run**, so an agent can drive it
      (`src/Recon.Core/Schemas/permute.schema.json`): what was tried, what each scored, why the run
      stopped, and `best_beats_baseline` to branch on — `best` is the best variant even when it only
      ties the source, and reading `best.score == 1` as "found it" would be wrong half the time.
      `--check-schema` validates it, as it does for every other document.
- [x] **The agent interface** ([`docs/agent-interface.md`](docs/agent-interface.md)). One rule:
      a `--json` run prints exactly one document on stdout, and that holds when it fails — a command
      with something to report prints its own, and one that never got started prints an `error`
      document carrying the exit code, `error.kind`, every stderr line, and for a configuration
      failure the file, line and key to fix (`error.schema.json`). `init` and `migrate` gained
      `--json`, so a program can set a project up and read back every path; `verify` and `link`
      gained published schemas, and `verify` now agrees with `build` — a unit that names no
      toolchain was being reported as broken although the build asks the binary who made it and
      succeeds, so `ok: false` was sending anyone reading it after nothing. Tests
      `tests/Recon.Tests/AgentInterfaceTests.cs` (7).
- [x] **Schemas for every command that emits JSON.** `doctor`, `sigs build`/`sigs apply`, the four
      `toolchain` subcommands, `disasm` and all eleven `inspect` subcommands now publish theirs (17
      new schemas), and every one goes out through `EmitWithSchemaCheck`, so `--check-schema` is
      honoured by all of them rather than a select few. The `inspect` schemas are generated from the
      inventory schema's own `$defs`, so they cannot drift from the objects they describe. Writing
      them found the last document still printing PascalCase while its schema says snake_case —
      `sigs build` and `sigs apply`, whose DTOs had no `[JsonPropertyName]` at all.
- [x] **Optimization-level variants** (`--flags`): which `-O`/`/O` setting reproduces the bytes is
      the other half of "which build made this", and it is not a question about the source. The
      levels are **profile data**, not a list in the code that guesses which compiler it is looking
      at: `[compile.optimization] levels = [...]` in `gcc-base` (`-O0` … `-O3`, `-Os`, `-Og`, which
      clang inherits by extending it) and in `msvc-6`, `msvc-2008` and `clang-19-msvc`
      (`/Od`, `/O1`, `/O2`, `/Ox`) — three files rather than `msvc-base`, because `vb6-native`
      extends that one and has no compiler, and giving it an empty `[compile]` table would replace
      "this profile has no [compile] section" with a worse message. `-Ofast` is deliberately not
      offered: it may change floating-point results, so a rebuild at `-Ofast` would not prove the
      same program. `recon permute --flags` adds one candidate per level, tried **before** the source
      edits, because the level changes every function in the image: while it is wrong, no edit to one
      function can be exact. Measured on `fib` from the ELF corpus with the machine's own gcc, from a
      project whose `[defaults]` say `-O0`: the baseline scores 0.182, `-O2` scores **0.905**,
      `-O1` 0.381, `-Os` 0.429, `-Og` 0.333 — the level the original was built at is the winner. (The
      fixture's `add` cannot be used for this: it is one instruction at every level from `-O1` up,
      and the only difference is padding, which the compare engine ignores.) Two details that are
      easy to get wrong and were: the level a flag list asks for is the **last** one, because that is
      the one a compiler acts on — reporting the first describes a build that is not happening and
      then changes a flag that `[defaults]` had already overridden; and a profile that declares no
      levels is left alone rather than having a flag appended to it. What won is published as
      `best_optimization`, with `best_source` left null and `best_write_error` saying why: a flag is
      not a source edit, and writing the file out unchanged would look like a result. Evidence:
      `tests/Recon.Tests/PermuteTests.cs` (3: inheritance, a profile with no levels, the last-level
      rule) and `PermuteRunnerTests.cs` (2: the search finding `-O2`, and the source going back).
- [x] **VB6 p-code: recognised, and not decoded as x86.** A Visual Basic 6 program is either native
      x86 or p-code that MSVBVM60 interprets, and one field says which: `ProjectInfo.aNativeCode`,
      zero when there is no native code. `src/Recon.Core/Pe/Vb6Header.cs` finds the VB5! header the
      entry stub pushes, reads the project structure behind it, and publishes the facts as
      `binary.vb6` in the inventory (`signature`, `header_rva`, `language_dll`, `template_version`,
      `code_start_rva`/`code_end_rva`, `exception_handler_rva`, `native_code_rva`, `is_pcode`). A
      p-code program then reports `binary.isa = "vb6-pcode"` — which is what makes the decoder
      refuse it (`the decoder speaks x86 and x86-64, not vb6-pcode`) instead of reading interpreter
      tokens as instructions — and it is no longer attributed to the `vb6-native` profile, which
      describes a compilation that did not happen. Evidence: `tests/Recon.Tests/Vb6PcodeTests.cs`
      (5) — a synthetic image built from the published layout, in p-code and native forms, plus a
      control showing the native form *does* still get `vb6-native`; and a real VB6 binary measured
      by the same reader (VB5!, VB6DE.DLL, template 0x1F4, native code at rva 0x5000).
- [x] **The p-code opcode table, measured off the runtime.** The blocker used to be here: a
      disassembler for p-code needs the interpreter's opcode table, the format was never published,
      every table in circulation is somebody's reconstruction of it, and no binary on this machine is
      p-code to check one against. The last part was true and beside the point: the table is not in a
      p-code *program*, it is in the *runtime*, and `MSVBVM60.DLL` is on this machine.
      `src/Recon.Core/Vb6/PcodeRuntime.cs` reads it there. The interpreter dispatches with twelve
      bytes, and the same twelve bytes everywhere in it: `33 c0` `xor eax,eax`, `8a 06` `mov al,[esi]`
      (the opcode), `46` `inc esi`, then `ff 24 85 &lt;imm32&gt;` `jmp dword ptr [eax*4+table]`.
      Searching the file for the *jump itself* (`ff 24 85` and the table address after it, with a
      `mov al,[esi]` `inc esi` in the sixteen bytes before it) finds **294 sites naming 6 tables** —
      and the imbalance is the finding: **289 sites share one table** (every handler ends by
      dispatching the next opcode, which is what a threaded interpreter looks like from outside) and
      the other five are reached once each, from five consecutive sites at the interpreter's entry.
      Matching the whole eleven-byte sequence instead misses the sixth: 0xFF's dispatcher is guarded
      (`cmp eax, 46h` before the jump), and the guard is the interpreter's own statement that the
      table holds **71** entries — everything above is rejected as a case, and the 185 slots after it
      in the file are bytes that point into `.data`, not handlers.
      **The main table is the opcode table, and counting is what says so.** Its 256 entries hold
      **252 distinct handlers**: an opcode and a handler are the same thing there, which is what an
      instruction set looks like from the inside. The five others hold 165, 188, 204, 168 and 71, and
      the four unguarded ones each send a large block of their slots to a shared handler — one that
      all four agree on, so it is where a case the interpreter does not handle goes. So: **the VB6
      p-code instruction set is 256 opcodes plus the sub-opcodes of the five lead bytes, and each
      opcode's handler is an address in the runtime's own `ENGINE` section**, read out of the shipped
      file rather than taken on anybody's word. Checked by 5 tests against the real `MSVBVM60.DLL`
      (`RECON_VB6_INPUTS`), with an ordinary DLL as the control that finds nothing.
      What is still not done, and is now a smaller job than it was: reading a p-code *program* — the
      header, the object and procedure tables, and the instruction stream — which wants a real p-code
      binary in the same way any decoder wants a specimen. Deciding what each opcode *means* is a
      separate step, and the handler addresses are now the way in: a handler that calls `__vbaVarAdd`
      is the opcode that adds.
- [x] **A p-code program decoded: every procedure, and the instruction stream the interpreter runs.**
      `recon pcode <program.exe>` reads a Visual Basic 6 program compiled to p-code — the specimen this
      work had been waiting for, now 42 of them, published as the DeForm6 corpus and built by the
      Visual Basic 6 IDE on a Windows XP host, with their sources beside them. The method table is the
      structure a native build leaves empty, and in a p-code build it is filled in: each object's
      `ObjectInfo` gives a method count and a dispatch table, and each entry of that table is a
      `ProcDscInfo` whose back-offset says how many bytes the procedure's stream holds.
      **What is decoded.** Every procedure, instruction by instruction, with the lengths measured out
      of the runtime's own handlers — all six tables of them, the primary one and one per lead byte
      0xFB–0xFF, which is what a stream needs when it contains a lead instruction. The tables are the
      measurement of `recon opcodes`, so the runtime is named in the report and its version printed
      from its own version resource (6.00.9848), because a listing decoded against another build's
      interpreter would be a plausible listing of the wrong thing.
      **What it measures.** Of the corpus's **680 procedures, 222 end exactly on their descriptor and
      458 have one to four bytes of alignment before it — all 680 in all — and none admits two
      readings**. Getting there took four measurement fixes, each of which had made a length plausible
      and wrong: the sixth table was missing from the scan, so every `ff` instruction was unmeasured;
      a lead byte's length was taken from its own row rather than from what its sub-handler skips; a
      pointer restored from a register (`sub esi, ecx`) invalidated every fetch measured after it; and
      a stream that ends in alignment (`13 ff ff ff`) was decoded as code rather than as an exit and
      its fill. Before them, 100 of the 680 had no reading at all and 7 were read two ways. The counts
      are re-measurable — `tools/pcode-corpus.py` walks a corpus and prints them — and the row-by-row
      check against the independent table is 1,327 of 1,340 comparable rows agreeing (99.0%), reproducible
      with `tools/compare-opcodes.py`. The 13 that differ are not carried as open: ten were read out of
      the runtime's own code, which says this reader is right and the table understates them (the
      currency and date literals read ten operand bytes — `mov ebx,6`/`mov ebx,7` then a shared
      routine that does `add esi,10h` — and `NextEachVar` reads four, in two words, in the two
      routines it is made of), two have no single length at all because they skip a table sized in the
      stream (`OnGosub`/`OnGoto`, where the report gives the fixed part), and one is a row this reader
      states both readings for where the table states one.
      **What it refuses.** A slot with no address is an event a project declares and never writes
      (89 of them); an entry whose descriptor does not name its own object is not a procedure of that
      object (102), which is how a method table longer than the object's code is settled without
      guessing. A native program is refused with the rva of its machine code, and a program with no
      runtime beside it says which option names one.
      Checked by 9 tests (`tests/Recon.Tests/PcodeProgramTests.cs`) over the corpus, plus the source
      that says how many procedures the program should have: Mandelbrot's form declares seven Subs and
      one Function, and the program holds eight procedures, the fractal's being 338 instructions.
- [x] **A VB6 program read as the structure it is.** `recon vb6 <file>` reads the chain a VB program
      is: the entry point pushes the address of the VB header, the header names the project data, the
      project data names the object table, and the object table lists one descriptor per form, class
      and standard module. `src/Recon.Core/Vb6/Vb6Program.cs` walks it, following a pointer only after
      checking it lands inside the image — which matters here more than anywhere else in this tool,
      because a VB program's most interesting structure is reached *entirely* by pointers, and the
      ones the linker never filled in are not zero, they are whatever was in its buffer.
      Checked against **five real programs built by VB6**, not against a fixture: ElementEvil.exe
      reports **73 objects — 39 forms, 9 class modules, 25 standard modules**; VISDATA.EXE (Microsoft's
      own sample) **36 — 34/1/1**; XiasporaServer.exe 10; Basic Server.exe 2. Every one of the 121
      objects across them has a name that reads as the identifier a programmer wrote and a kind that
      is one of the words the compiler emits, which a wrong stride or offset could not produce. The
      build path is still in there too, in UTF-16: VISDATA says it was built from
      `d:\sources\vb98\vbsamp\samples\visdata\visdata.vbp` — Microsoft's own build directory, and
      a check on that field's offset and encoding in one.
      Two numbers that looked like an invariant and are not: the object table's *compiled* count is
      larger than the number of objects it declares in ElementEvil.exe (76 against 73), so it is
      reported as the file's own number rather than checked; and the object count itself is
      authoritative — a file that holds fewer descriptors than it declares gets the number it asked
      for, read out of whatever follows, because a reader cannot tell a three-character object name
      from the start of the DOS header. What *is* checked is the compile-state word, 0x000A in all
      five programs, which says whether the pointer landed on an object table at all.
      Checked by 17 tests: the five real programs, the p-code/native split, and the ways the table can
      disagree with the file.
- [x] **A VB6 procedure table and the p-code instruction stream.** Done, and this entry stayed open
      after it was: the work below says the blocker was that no p-code program existed on this machine,
      and 42 of them arrived with the DeForm6 corpus and are decoded by `recon pcode` (see the entry
      above). What is left of it is not reading procedures — it is the inventory carrying them, which is
      its own item.
- [x] **Naming opcodes from their handlers, and measuring their lengths.** `recon opcodes` reads every
      table of the runtime and reports, for each row, what its handler does — the runtime functions it
      calls (resolved against the export table, through `jmp` stubs, through import thunks, and through
      a register the handler loaded with an address: `mov ebx, 66111940h` … `call ebx` is a call to
      66111940h), what it *reaches* through a helper it calls, and the length of the instruction it
      reads.
      **Names.** A name is derived only when the handler's call is unambiguous and not shared with the
      rest of the runtime: **159 of 1,536 rows are named**, 122 of them differently. The name is the
      runtime function's undecorated name — `__vbaStrCat` is reported as `vbaStrCat` — so what comes
      out is a readable piece of the interpreter: `vbaInStr`, `vbaUbound`, `vbaFileOpen`, `vbaVarLike`,
      `vbaObjIs`, `vbaNextEachVar`. Where the handler calls no export but calls a helper that is a
      preface to one, the name comes from the helper's reach and the report marks it `via` — one level
      only, straight-line and through unconditional jumps, **never through a conditional one**: a
      helper that tests something has an error path, and following it named 188 opcodes `RtlUnwind`.
      **Unimplemented slots are not named at all.** 373 rows are slots the runtime does not implement —
      185 with no address in them, 188 sent to the one handler that raises (`0x1105C2`, `cmp eax,46h; ja`
      rejects them) — and they report that fact rather than a name derived from the error path. The
      independent reconstruction marks 187 rows `InvalidExcode`; they are the same rows. An import the
      file names only by number is reported as the number it is (`OLEAUT32.dll!#150`) and no name is
      invented from it.
      **Lengths.** The interpreter reads the next opcode from `[esi+K]` and moves `esi` past the operand
      and that byte, so an instruction is what the path consumed before that fetch, plus the fetch's
      displacement, plus the opcode byte; a handler that reads its operand *into* `esi` is measured by
      the operand's width; one that restores `esi` from a register by what it read; one that hands off
      to shared code by what it read before the hand-off, and a path whose only act is the hand-off is
      a body two opcodes share, measured in the code it lands in. The running total travels with the
      path, because two paths through one handler can consume different amounts.
      **Every row of the primary table gets one length**; three rows of the lead tables get two and
      report both (`0xFB 0x87`, `0xFB 0x88`, `0xFC 0x85`). The independent reconstruction states a
      length for 1,340 rows; **1,327 of those agree (99.0%)**, and the 13 that differ were each read
      out of the handler — `tools/compare-opcodes.py` reproduces the comparison. Checked by 20 tests
      (`tests/Recon.Tests/OpcodesTests.cs`), most against the real `MSVBVM60.DLL`, whose version the
      report prints out of the file's own version resource.
- [x] **What each opcode does to the stack, and which of them can raise.** Each row states a
      `stack_effect` in dwords of the machine stack — which in this interpreter *is* the operand stack —
      a `stack_effects` list where the paths disagree, a `raises` flag, and a `stack_basis` sentence
      saying what the number is made of. `push`/`pushfd` add one, `pop`/`popfd` take one, `add esp,imm`
      and `sub esp,imm` move what they state, and a call removes what the callee's own `ret N` pops —
      read once per callee and remembered. **Of the 1,163 implemented rows, 806 have a single effect,
      2 have two and 355 have none**, each of those with a reason instead of a blank: 167 leave the
      interpreter on every path, 96 call through a pointer, 66 call a routine whose own returns
      disagree, 26 write the stack pointer by something the walk cannot follow.
      **Raising.** A handler can raise when the runtime's raiser is reachable from it. The raiser is one
      function, **0x6603852C** in 6.00.9848, found by the shape of the code that jumps to it with an
      error code loaded (`mov eax, 9C68h` / `jmp 66108D50h` / `push eax` / `call 66385 2Ch`) rather
      than by a name or a popularity count: **330 rows can raise**, 142 of them implemented, 52 with no
      stack effect at all — an instruction that raises never reads the next opcode, so what it leaves
      on the stack is a stack nobody reads. The two rejected rules are recorded in `docs/m7-status.md`
      with what they cost (43 false rows, then 652).
      **Two bugs found by this work.** The decoder never filled in `DecodedInsn.RetPopBytes`, so every
      call was read as removing nothing (`ret 8` → `ret 0`) and the inventory's `ret N` evidence was
      never printed; populating it changed 282 rows and took 0x2A to −1, the number the independent
      table states. And a routine's `ret` has to be read up to the **first** `ret` — the runtime is a
      run of small thunks each ending in its own `ret N` — with a jump past that bound resolved as a
      tail call; without the bound 592 handler paths reported that a routine's returns disagreed with
      itself, which is what kept most rows unmeasured. Cross-checked against the independent table's
      `pops`/`pushes`: **532 of 777 comparable rows state the same net slots (68%)**, with the
      differences read and named (`LitVarI2` counts a variant as four slots there, one stack dword
      here) — `tools/compare-opcodes.py` prints it.
- [x] **The 5-or-6 tables question, answered from the opcodes themselves.** The Rust `visualbasic`
      crate models **six** dispatch tables ("0=primary, 1-5=lead0-lead4") where this tool measures
      **five** in the runtime, so one of the two is wrong about the file. The decoded table says which.
      Opcodes **0xFB, 0xFC, 0xFD and 0xFE** are one byte each and their handlers sit **twelve bytes
      apart** (`0x10A914`, `0x10A920`, `0x10A92C`, `0x10A938`) — four lead bytes, four tables reached
      once each from the interpreter's entry, which is exactly the four secondary tables measured here
      (`0x10AE24`, `0x10B224`, `0x10B624`, `0x10BA24`). Four leads plus the one every handler
      dispatches through is **five**; a six-table model has one lead byte more than the file has. The
      crate's own columns say the same thing from the other side: its `t1` names `EqvUI1`, `OrI2`,
      `AndUI1` and `LeI4` for byte numbers where `t0` names `ImpAdCallHresult`, `LitStr`, `ImpAdLdPr`
      and `Ary1StStrCopy` — a second byte after a lead, not another reading of the same array. What
      the comparison settled, and why it was worth doing, is the cross-check: the crate's `size`
      column agrees with the lengths measured off the handlers in **251 of the 252 opcodes where it
      states one**.
- [x] **The comparison, on a binary big enough to matter.** Opened by fixing the end-to-end build test
      (below), and finished: `recon diff` on the 11.8 MB Visual Basic 6 client was **killed by the kernel
      with nothing printed** (exit 137) because the comparison normalized every function of both sides
      and kept all of them — 4.4 million instructions per side, twice over. It now keeps a summary per
      function and makes each body when it is asked for, which is what the analysis itself already did;
      the same command gives **304 of 304 functions exact and 4,443,084 of 4,443,084 instructions equal
      in 100 s at a peak of 1.0 GB**, with `diff --verbose` reporting the stages (summarize 17 s + 17 s,
      pair 4 ms, compare 34 s). Two things came out of the same measurement: a comparison pass was being
      paid twice (bodies are kept while the total is under 100,000 instructions, streamed above it — the
      first budget tried, 400,000, turned a run that finished into one that was killed), and every
      instruction allocated two empty `List<>` instances for references it did not have (822,000 of
      4,443,084 carry one; the lists are lazy now).
- [x] **A function that moved is not a function that changed.** The other scoring bug the build test
      uncovered, measured with the same source built twice with one flag added — `-falign-functions=32`,
      which changes no statement: **78 of 142 functions "changed", score 0.8638** (a pre-fix gcc figure —
      re-measured later at 0.9998, see `docs/m7-status.md`), every difference either
      `mov dword ptr [esp],4019B0h` (an immediate holding the address of a function the symbol table
      names) or `jmp .text+0x5D7` against `jmp .text+0x657` (a branch target inside the function being
      compared). Both are identities now — a named immediate is an address whether or not the linker
      relocated it, and a branch inside the function is an offset from that function's entry — and the
      same comparison is **140 of 142 exact, 0.9998**, the two that remain being real (an extra `jmp` the
      alignment produced, and a branch into `dispatch` at a different offset). The corpus agrees at
      scale: debug against release went from **56 exact / 0.8373** to **127 exact / 0.9705** with exactly
      the same 139 pairings. Both golden comparison documents were regenerated for it, explained in
      `docs/m7-status.md`: the C++ self comparison differs by exactly two counters, both the named
      immediate becoming a reference.
      Guarded by `tests/Recon.Tests/MovedCodeTests.cs`, which builds one source at two alignments, asserts
      the two functions above are exact and that no difference anywhere in the document is two
      section-relative names against each other — and fails on the un-fixed code with
      `Expected: "exact" / Actual: "changed"`.
- [x] **The comparison builds an inventory a side may already have on disk.** Done: a side whose
      inventory `recon inventory` has written is read back instead of analysed again, when the document
      is this build's reading of the bytes being compared — written by `recon`, at this build's version,
      declaring the inventory contract and carrying a `functions` array, about the file's own hash, and
      not older than it. Evidence: on the 11.8 MB client, a pair with neither document compared in
      **108.8 s** and the same pair with both in **72.8 s** (`analysis_ms` 17,615/18,674 → 725/488),
      with the reports equal field by field apart from `analysis_ms` and `inventory_source`, both routes
      scoring 0.9996 over 304 functions. Two findings came out of the measurement: reading *both*
      documents with their cross-references (49.9 MB of the 71.7 MB document, which a comparison never
      reads) was killed by the kernel until the cache dropped that property at the token level; and the
      first guard compared against the hash the project *records*, so a comparison against a rebuilt
      binary refused its own document — what has to match is the bytes in hand. Reading a document is
      guarded by a version, which is how the tool was found to have three of them: the library defaults
      said `0.1.0`, the comparison read `Assembly.GetName().Version` (`1.0.0.0`) and the CLI asked the
      entry assembly, so the five goldens carried two answers about one tool. `src/Recon.Core/ToolVersion.cs`
      is the one identity now, and everything that writes or checks a version asks it. Checked by 4 new
      tests (`CompareCliTests` 9 in all, and one version test in `CliTests`); the suite stood at **587 tests, 0
      warnings** then; written into `docs/m7-status.md` ("The inventory a side already had") and `README.md`.
- [x] **The comparison still decodes each side's bodies twice.** Closed, and the fix turned out not to be
      the one the item predicted: streams would have removed the storage, but the comparing stage was not
      storing anything — it was *decoding a body it had already decoded*, to answer a question the pairing
      had already answered. `BodyKey` is a hash over exactly the normalized instructions the differ
      compares, which is why the pairing trusts it, so **a pair whose two keys agree is decided from the
      summaries with no decode at all**, counted in `model.bodies_proven_identical` (301 of the client's
      303 matched functions). The aligned listing is the one case that still needs the bodies, and a pair
      asked for by name is decoded as before. What the pairing's similarity stage does decode is now kept
      by the same budget-enforcing `Keep`, so one decode serves both moments, and the summary pass itself
      stopped materializing a body: `FunctionNormalizer.Summarize` walks the stream once and accumulates
      the count, the incremental key and the histograms. Two smaller leaks went with it — the symbol
      resolver kept every reference it had ever answered (822,000 entries, ~118 MB; `TakeReferences`
      forgets as it hands over) and the *largest function's body* was what set the peak, not the
      documents, which is why collecting between the passes moved nothing and the answer was to stop
      building bodies at all. Evidence on the 11.8 MB client: the read route **57.4 s / 837 MB → 29.4 s →
      24.4 s / 590 MB**, the built route **83.6 s → 55.7 s → 49.2 s / 612 MB**, the compare stage
      **27–29 s → 4 ms**, and the self-comparison **100 s / 1.0 GB → 39.8 s → 37.6 s / 684 MB** (score 1,
      4,443,084 of 4,443,084 instructions equal). Both routes still score 0.9996 on the edited pair with
      the two reports equal field by field, and the corpus pair has not moved: 142 of 142 functions, 139
      matched (127 exact, 12 changed), 0.9705. Checked by 4 tests (`CompareCliTests` 11, `CompareTests`
      40) — one per claim: the key shortcut, the named-function listing that must still decode, and the
      two that hold the streaming summary to the materialized body. The goldens gained only the new field
      (127 and 23 pairs proven identical); the suite stood at **591 tests, 0 failures** then. Written into
      `docs/m7-status.md` ("The bodies a comparison decodes twice"), where the instrument
      (`tools/big-diff-pair.py`, in the repository) also corrects a number this list had been carrying:
      the 977 MB attributed to both routes was a *cumulative* reading — the largest child so far, i.e. the
      inventory — not the comparison's peak, which was 1.02–1.08 GB built and 837 MB read.
- [x] **Carry p-code into the inventory.** Done, and it is the item that made the p-code work visible to
      the rest of the tool. A p-code program used to inventory as **one function named `entry` covering the
      whole section, zero instructions, and a problem saying the decoder does not speak its instruction
      set** — honest, and invisible to `inspect`, `report`, a comparison and every question an agent asks
      that is not `recon pcode`. The instruction set is now an axis: the x86 analysis is not run at all for
      a p-code program, and its own method tables are read instead. Over the 42 corpus programs:
      **680 functions (one per procedure), 55,135 instructions, none with an unknown size, zero problems** —
      the same numbers `recon pcode` measures, checked procedure by procedure against that command. Each
      function's extent is `high` confidence because the compiler *wrote it down* in the procedure's
      descriptor rather than it being inferred; its `found_by` is a new evidence kind `pcode_procedure`,
      added to the schema's enum rather than smuggled in; and its name is the program's own identity
      (`FrmHex[3]`, object and method index) with `name_is_object_and_method_index` in `unknowns`, because
      VB6 does not store procedure names in the binary and a report must not look like it found some.
      The runtime is looked for (named, beside the program, or in the project's inputs) and **its absence is
      a state, not a failure**: without it every procedure is still published with the extent its descriptor
      states, the instruction counts are zero, and one problem line says what to pass. Nothing is claimed
      about a stream that was not read — the first version flagged all 680 procedures as having no exit
      instruction without having decoded anything. `recon disasm` on a p-code program still refuses, and now
      names the commands that do read it. Checked by 2 tests over the corpus (`PcodeInventoryTests`) plus the
      synthetic-image test's third case, and all 42 programs were run end to end (0 problems).
- [x] **A `vb6-pcode` toolchain profile.** Done, and it is the first profile whose decisive evidence is
      a **program's own header** rather than something a compiler left in the image. What it fixes is a
      wrong answer: handed one of the 42 p-code programs of the corpus, `recon inspect producers` said
      `msvc-6: medium via linker_version` — true of the linker stamp, wrong about everything else, and
      the program was not compiled to machine code at all. The new profile extends `vb6-native` (same
      IDE, same Visual Studio 98 linker, same runtime, other compilation mode) and adds one rule,
      `[[detect.vb6_header]] kind = "pcode"`, which is a **gate**: a profile that names a kind of Visual
      Basic program is suggested for that kind only, and the profile naming the other kind is withdrawn
      from every piece of evidence it matched. The withdrawal used to be a hard-coded profile id in the
      detector; it is now the rule in the file, and it works both ways round. The evidence, measured
      over all 42: one Rich record each (`0x000D`/`vb60`, count 1 — a native program carries three),
      linker version 6.0, MSVBVM60.DLL the only import, `template 500`, `aNativeCode` 0, and the header's
      `wRuntimeBuild` 9782. A draft of the profile comment said that 9782 *is* the Rich record's build
      and that the two are one number; the native control disproved it — VISDATA.EXE carries a `vb60`
      record at 8167 and a header saying 8169 — so the rule matches the product id and the builds are
      recorded as measurements. Two numbers became readable rather than implied: `recon pcode` now
      prints the runtime build the program was built against beside the one the runtime file reports
      (`6.00.9848` here, so the two differ, which is ordinary for a service-packed DLL), and
      `recon toolchain show` prints `code_kind` (`pcode`/`native`, null elsewhere). `[compile]` is
      deliberately absent: nothing here can rebuild a p-code program, and a profile with no optimization
      levels is what `recon permute --flags` reports when it has no flags to try. Checked by 6 new tests
      in `Vb6ToolchainTests` (the class is 12 now) plus the VISDATA attribution test, and the whole suite
      is **579 tests, all green**; evidence in `docs/m7-status.md`, `docs/toolchains.md`.
- [x] **The analysis makes three passes over every instruction where one would do.** Closed for the two
      that were whole-image passes, and measured rather than argued for the third. The scan, the
      jump-table search and the prologue search each decoded every instruction of every code section —
      three walks, 8.2 s of a 13.6 s analysis on the 11.8 MB client — and they are **one walk now**,
      because a pass over every instruction is where an analysis of a large program spends its time.
      The merge is exact by construction: the decoder is asked for registers only on the three mnemonics
      that compute an address (`lea`, the `call`/`add` pair of the position-independent idiom), which is
      what the table finder needs and neither of the others wanted to pay for, and what an instruction
      *is* does not depend on what was recorded about it, so the three walks were always the same chain.
      A prologue is only a *candidate* in the scan — an instruction start that begins with a prologue
      pattern — and which candidates are function entries is decided later, where it always was, when the
      seeds exist. Two things the merge found: the table **reading** has to wait for the walk to finish,
      because an entry counts only when it lands on a decoded instruction and the scan is still marking
      instruction starts as it goes (measured mid-walk: 95,063 candidates, zero tables); and the phase
      report had to stop calling `jump_tables` a phase of its own — it is reported underneath as the
      share of the scan it is (`scan/jump_tables`), with a test that fails if a top-level `jump_tables`
      line ever comes back. Evidence, all on the same input in the same sitting: the three passes
      **7.7 s → 5.8 s**, the analysis **12.9 s → 10.4 s** (`scan 3374–3509 + jump_tables 3311–3389 +
      prologues 871–1001` against `scan 5581–5841, of which 624–677 ms tables, and prologues 3 ms`), and
      **the inventory byte-for-byte identical** — 25 jump tables, 304 functions, 414,282 xrefs,
      4,417,764 instructions, compared field by field against the pre-change document with zero
      differences. The third pass is per function rather than whole-image, and folding *it* in was
      measured too: the size walk asks only for where blocks end (1.25 s), and with registers and memory
      turned on it costs 3.4 s, so a merged walk would replace 1.25 + 3.1 s with at most 3.4 s — under a
      second, for re-deriving the convention facts over a range the function's own body does not cover.
      Recorded rather than done. Checked by the existing corpus tests (jump tables in PE, ELF and Mach-O
      binaries, prologue seeds) and by `StreamingAnalysisTests`, which now pins the report's shape; the
      suite is **591 tests, 0 failures**. Written into `docs/m7-status.md` ("One pass over every
      instruction, where there were three").
- [x] **Where the analysis spends its time, and what the interactive path costs.** The analysis reports
      the wall time of each of its phases and `recon inventory --verbose` prints them, which is the
      feature that made the rest of this item possible rather than guesswork. On the 11.8 MB VB6 client:
      **scan 4.7 s, jump_tables 4.9 s, prologues 1.4 s, functions 1.6 s, conventions 4.8 s, xrefs
      0.4 s**, 17.9 s in all — down from **36.5 s**, with the inventory compared against the pre-change
      one and identical apart from its timestamps. Three things did it, each a rule rather than a trick:
      the seed pass was making two avoidable passes over every instruction (finding `jmp [IAT]` thunks,
      and collecting call targets the scan already had) and went **6.2 s → 21 ms**; `functions` decoded
      every body with registers and memory operands tracked to read one boolean per instruction and went
      **5.4 s → 1.6 s**; and the conventions pass streams the instructions it reads instead of
      materialising them (**5.0 s → 4.5 s**). A fourth change was tried and reverted with its number:
      running the two whole-image phases at once, and splitting the conventions loop across cores, took
      the analysis to **41.4 s** — the container has two cores and decoding is the whole cost, so the
      parallelism bought nothing and the contention cost a great deal. `recon disasm` was the other half
      of this item: a listing built the entire inventory to print four instructions (**18 s**), and now
      reads the three things it needs from the inventory on disk when it is this build's reading of this
      binary, in **0.6 s**. Only that exact file is trusted — right tool version, right sha256, not older
      than the binary — and both routes are held to the same output by a test and by three listings of the
      real client compared byte for byte. Checked by 2 tests (569 in all).

## Later, when a target needs it

**Not being taken now, at the user's request.** The **Borland, Delphi and Watcom profiles** and the
**WASM plugin API** are unscheduled rather than missing — the agent interface and p-code came first.
Neither says anything about the state of what exists, and the plan itself says not to invent a plugin
ABI yet (§3.1).

- [ ] **Relinking without GNU ld.** `recon link` places sections with a linker script, and
      `lld-link`/`link.exe` have no equivalent — `/section:` sets attributes, not addresses — so an
      image whose only installed linker is one of those can be delinked but not relinked. Both are
      enough to delink with; closing this needs either a placement mechanism in those linkers or a
      separate layout step. Until then `recon link` refuses with that reason instead of failing
      later. Proven not to block anything today: the relink is mechanical, so a GNU ld driver relinks
      an MSVC-ABI binary identically (`docs/m5-status.md`, "The relink is mechanical").
- [ ] A PDB written by Visual Studio's own `link.exe`. Everything the current corpus proves comes
      from a Microsoft-format PDB written by lld-link; the two length conventions CodeView records
      use are both covered by fixtures, but a real Windows-produced PDB is still worth one run.
- [x] **Render p-code as an instruction listing.** Done. It was the last step between "this program has
      23 procedures" and "this procedure reads a file name, opens it and loops over its records", and it
      took two readings that the rows did not have yet: **what the bytes after an opcode are**, and
      **where a branch goes**. Both are read off the handler's own instructions, never off a table of
      names — four kinds: `none` (485 of the 1,163 implemented rows), `data` (330), `slot` (303 — the
      word is sign-extended and added to the frame base, in three shapes: a memory form `[ebp+r]`/
      `[r+ebp]`, a computed `add r,ebp`, and the same written with the registers the other way round),
      and `target` (45 — `movzx esi,word[esi]` + `add esi,[ebp-58h]`, `mov esi,[ebp-58h]` + `add
      esi,eax`, and `add eax,[ebp-58h]` + `xchg esi,eax`, which is every branch in the instruction
      set). The word's offset is the pointer's *advance*, not the displacement in the instruction, which
      is the bug the first version shipped: every `For Each`/`Next` branch was being resolved on the
      counter's frame slot. A `Next` names two words — its counter's slot and the distance it branches
      by — so `operand_slot_at` is a field beside `operand_at`, and the listing prints
      `frame[-0x11C] then +0x198 -> 0x3288`. Every row's `operand_basis` names the instruction the
      reading came from, `data` rows included, which they did not until this pass. `recon pcode
      <program> --object <name> --procedure <n>` prints `rva bytes opcode size operand name` with the
      same facts in `--json` (`operand`, `operand_bytes`, `operand_at`, `operand_slot_at`, `target`,
      `target_is_an_instruction_start`, `frame_slot`, `frame_slot_at`), and both schemas require them.
      Held to the corpus rather than asserted: **every branch operand of the 42 programs lands on an
      instruction start inside its own procedure**, and **all 25,812 frame slots named lie inside the
      frame the procedure's own descriptor declares** (`[-frame_size-132, argument_size+4]`, a bound the
      corpus hits on both sides). Cross-checked against the independent reconstruction's
      `operand_format`: 292 of the 303 slot rows are `%a` there, and it names a label or a two-byte
      distance for all 45 branch rows; the 45 rows it calls a slot or a label while this reader reads
      `data` (the `Redim`/`Late`/`WMem`/`ExitProcCb` families, and `OnErrorGoto`/`BranchFVar`, which
      jump away with an error code and let the interpreter branch) are named in `docs/m7-status.md`
      one family at a time rather than averaged away. `tools/compare-opcodes.py` prints the comparison
      beside the length, stack and name ones. Checked by 3 tests (the kinds and their arithmetic over
      all 1,163 handled rows, two rendered listings against rows disassembled by hand, and a fifth of
      the corpus held to the landing rule); suite **582 tests, 0 warnings** then.

## Closed
- [x] **`.pdata`/SEH entries seeding function boundaries on binaries that have them.** The loader reads the
      table — 8 bytes per entry on PE32, 12 on PE32+, both widths real and both read — and keeps it
      faithful (begin, end, the 64-bit form's unwind address); `BinaryFunctionRange` carries the ranges
      across the format boundary; the analysis seeds a function at every entry that begins in a code
      section, takes the extent as its size, and records `pdata` in `found_by`. It refuses three things on
      purpose: an entry outside code (a table is a statement about functions), a zero end ("no extent
      stated", not "ends where the next one begins"), and membership in the sources that outrank a pattern
      (the table names nothing, so a signature may still name what it sized). Measured on a 64-bit MinGW
      build against DWARF as the oracle: the same source with `-s` went from **96 functions, every size
      estimated** to **126 of DWARF's 128** with all **96 extents as sizes**, 30 unknown sizes left
      (from 96), and **no start invented**; the build with DWARF is unchanged at 128 functions, 127 named.
      **The item's premise was wrong and the round found the bug behind it:** it was filed as unprovable
      here because "no binary in this environment has a `.pdata` section" — true of the 32-bit corpus
      (x86 MinGW builds DWARF-based SEH), false of a 64-bit one — and while making it work, the loader was
      found to read `NumberOfRvaAndSizes` at the 32-bit offset for *every* image, which in a PE32+ header
      is the high half of `SizeOfHeapReserve` (zero), so **no data directory of any 64-bit image had ever
      been read**: no imports, no exports, no exception directory, no debug directory. All of that is read
      now, and a test counts a real 64-bit image's imports, iat and basereloc directory entries.
      Checked by 4 new tests in `PdataTests`; the suite is **602 tests, 0 failed**; the corpus re-validates
      (24 binaries, PDB oracle on) and the six MinGW binaries relink byte-identical; the 11.8 MB VB6 client
      still inventories to 304 functions / 4,417,764 instructions / 414,282 xrefs. Written into
      `docs/m7-status.md` ("`.pdata`: the compiler's own table of function extents"), with the recipe in
      `tests/corpus/pdata-seh/`; `README.md` updated.

- [x] **Pair unnamed data addresses.** Same source, same flags, one literal added: the comparison scored
      **0.9934** with 15 functions "changed", every difference a pair of `.rdata` addresses that both
      carried a relocation and neither had a name. An address with no name is now compared by what is at
      it — `"literal"` for printable bytes, `[w0 w1 w2]` of words otherwise, each word itself a name, a
      nested identity (one level, code sections never entered), `->`, or a bare number — over a window of
      exactly the bytes the instruction reads (register-indexed memory gets 12, because the instruction
      addresses the table, not the word; a fixed window was a measurement bug of its own: 16 bytes paired
      31 of 52). The identity replaces the positional tail as well as the address, except `jump_table[…]`
      labels, which keep their slot; a name is never overwritten by an identity; an unnamed immediate is
      admitted only when an identity exists, and a branch target only when it names something or stays
      inside its function. Measured **0.9934 → 0.9999** (141 exact, 1 changed — `__pow5mult_D2A` reads a
      mantissa word that really does differ), `references 1158 named, 110 identified by content, 2372
      unnamed`. The count travels: `model.references_identified` in the model, the schema and the CLI
      summary. Checked by 3 new tests (`CompareTests` 42 in all, `MovedCodeTests` 2); the suite is
      **598 tests, 0 failed, 0 warnings**. Written into `docs/m7-status.md` ("An address that moved is not
      a difference, part two: what is at the address") and `README.md`.


- [x] **Rebuilding one piece and relinking it.** `recon link` now fills a piece claimed by a unit with
      `provider = "rebuilt"` from that unit's own compiled object: the function's bytes at the piece's
      address, with the compiler's addresses written out as references to the names the image defines
      (`.long <name>` for an address, `.long <name> - . - 4` for a call — the i386-PE addend rule,
      measured). The corpus's own source as one unit is **13 of 13 pieces, 859 bytes, 30 references, and
      the relinked image is the original, byte for byte**; one function with no references is 9 bytes and
      the same verdict. A rebuild that cannot be placed keeps the original's bytes and says why: a stale
      object (the build system's own cache key, not a second opinion), a function the object does not
      define, a rebuild longer than the piece, a name inside the rebuilt range, a reference that is not
      `dir32`/`rel32` or whose name the image does not have, and a reference the assembler resolved
      inside the object — a call between two functions of the same unit is four finished bytes with no
      relocation naming it, so the unit's layout has to agree with the image's, and the piece is refused
      when a field in it is a reference to a name that moved. `recon link --json` separates the claim
      (`rebuilt`) from the supply (`rebuilt_pieces`, with the unit, source, bytes, references and the
      original's bytes kept), and `problems` says why a claimed piece stayed original. Getting there
      fixed three bugs the one-function case could not reach: a cover resolved against a single function
      made every substring unambiguous (`symbol = "add"` claimed `__multadd_D2A`), a relocation's symbol
      index counts *records* while the reader collects symbols (`main`'s format string read as
      `.bss + 0xE7`), and a section-symbol reference spent its addend twice (`add + 0x80` for the object's
      `_add` at 0x80). Checked by 4 new tests in `DelinkTests`, 20 in all; the suite stood at **595 tests, 0
      failed, 0 warnings** then; written into `docs/m7-status.md` ("A unit's own build takes a piece's place")
      and `README.md`.
- [x] **M5 — delink/relink.** `recon delink` cuts the original into pieces a linker can put back at
      the same addresses — one assembly file per section, a linker script, and `delink.json`
      (schema 0.1) with every piece, every absolute address turned into a reference, and the unit
      responsible for each — and `recon link` assembles them with the toolchain the binary asks for,
      links them at the original's base and entry, finishes the header entries a linker cannot know
      (timestamp, the directories it did not build, section flags, the assembler's alignment padding),
      and then compares the result with the original and prints the verdict. All six MinGW corpus
      binaries come back **byte-identical** — every section at its address with its name, size, flags
      and bytes, header included — and `recon diff` then scores **1.0** (142/142 functions exact,
      8076/8076 instructions equal, 146/146 data symbols identical); the MSVC-ABI corpus's binaries
      relink identically with a MinGW GNU ld driver, which is what shows the relink is mechanical and
      not tied to the toolchain that built the image. Getting there fixed three differences that were
      not in the bytes: names with no size were never written (they are labels now), every symbol in
      the relinked image looked like data (COFF spells a typed symbol `.def`/`.endef`), and the
      assembler wrote a `.debug_line` of its own *in front of* ours (ours keep their names, which is
      also what gives them their alignment; the assembler's own are prefixed and discarded). Deliberately
      not reproduced, both outside the image: the COFF symbol table and the checksum that covers
      it. Evidence: `docs/m5-status.md`; tests `DelinkTests` (16: tiling with no gap and no overlap to
      the virtual size, the relocation table planned rather than left to the linker, labels for names
      with no size, one name at two addresses, section flags from the original's characteristics,
      debug sections keeping their names and the assembler's own being discarded, the plan against
      its schema, header entries copied only when the linker left them empty, the verdict on an
      unchanged and on a damaged image, and a corpus binary delinked and relinked with a real cross
      compiler). Gates: `bash tools/relink-corpus.sh` (6 identical, 0 differing) and
      `bash tools/validate-corpus.sh`, which now runs it.
- [x] **M4 — diff viewer and progress reports.** `recon report` measures progress and writes the
      progress site; `recon serve` serves the same viewer with alignment computed per function;
      `progress.schema.json` (0.1) is the contract and `--check-schema` validates the report against
      it. Three shares, reported separately and all measured against the original:
      `instruction_exact`, `verified` and `similarity`. A unit's share is the equal instructions
      over the instructions its `[[unit.covers]]` claim — `null`, never `0`, when it covers nothing
      measurable — and functions no unit claims are counted in `uncovered` so an undecorated
      project cannot be mistaken for one at zero. `build/report/index.html` is one file with both
      documents inside it (no framework, no build step, no network): virtualized function list,
      aligned two-column listings, per-unit table, a treemap of the image coloured by status, and
      the history table. `[report] history = true` appends one JSON line per run to
      `history.jsonl`; `previous` and `history[]` carry them into the document. Evidence:
      `docs/m4-status.md` — over the MinGW corpus (`sample-release.exe` as the original,
      `sample-debug.exe` as the rebuild) 84.22% of the original's 8076 instructions exact, 56/142
      functions verified, three units at 33.33% / 11.54% / 98.33%, and 136 unclaimed functions
      reported as such. Tests: `ProgressTests` (13: the three totals and what each is measured
      against, an added function not earning credit, covers resolved by symbol, by demangled
      spelling and by range, `null` rather than zero, uncovered counted separately, history
      appending and surviving a damaged line, the schema, the site being self-contained, alignment
      on demand, the server's four paths) and `CliTests` (4: the document, the site and two history
      entries; `--aligned`; `--comparison` saying alignment is unavailable; two loose binaries with
      no project).
- [x] **Per-binary debug information in a comparison.** A project has one `debug` input, the
      original's, and the reference side was analysed without any: its functions went unnamed
      unless they were exports or imports, so nothing paired by name even when the rebuild carried
      full symbols. Debug info is now read per binary — its own COFF symbols, DWARF, or the PDB
      beside it — while the configured `debug` and `map` inputs stay with the original. Measured by
      putting the old behaviour back for one run: the MinGW corpus compared through a project went
      from **0.4739** (142 against 108 functions, 67 matched, 146 data symbols "differing") to
      **0.8373** (142 against 142, 139 matched, 146 identical). Evidence: `docs/m4-status.md`
      ("Two defects found while building this"), `ProjectContext.LoadDebugInfo(image,
      includeConfiguredInputs)`, `ComparisonSide.BuildInventory`.
- [x] **`func_a` is a symbol, not a placeholder.** Names made up from an address (`sub_401000`)
      must not be used to pair functions, and the test for one was "prefix + all hex digits" — which
      `func_a`, `data_be` and every other real name ending in hex characters also satisfy, so those
      functions never paired by name. The suffix now has to be at least four hex digits. Evidence:
      `SideIndex.IsPlaceholder`, `docs/m4-status.md`.

- [x] **M3 — build orchestration.** `recon build` resolves each unit's toolchain (`unit.toolchain`,
      else `target.default_toolchain`), runs the profile's compiler and linker with flags merged in
      the documented order, caches by content hash (source, depfile-reported headers, flags, the
      toolchain's fingerprint and installed binaries, its environment), writes
      `build/build.json` (schema 0.1) and exports `build/build.ninja`. `recon doctor` reports whether
      the tools a unit needs are installed. Evidence: `docs/m3-status.md` — the corpus's `sample.c`
      rebuilt with `gcc-13-mingw` scores **1.0** against the original (142/142 functions exact,
      8076/8076 instructions equal, 146 data symbols identical); a rebuild with one function added
      recompiles it and the comparison then reports the 73 address shifts it caused; an MSVC-family
      profile builds with `.obj` and `/out:` through clang + lld-link; `recon verify` now checks
      that every unit's source and toolchain are usable, so an unbuildable project cannot verify
      clean. Tests: `BuildTests` (36: depfile parsing, tool resolution, flag order, planning
      problems, cache hit/miss per input, every ninja edge expanded back into a command line, the
      command end to end, `verify` over the units) and `ToolchainProfileTests` (the new profile keys;
      `[compile]` inherits key by key).

- [x] **Toolchain noise as a profile setting** (plan §3.1). `[compare]` in the profile schema with
      `similarity_threshold`, `ignore_padding`, `difference_limit`; both shipped base profiles state
      them; `ComparisonOptions.FromProfile` maps the profile's `codegen.padding_bytes` onto padding
      mnemonics; `recon diff` takes the model from the left side's resolved profile, the command line
      overrides it, and the document records what was used. Evidence: `docs/m2-status.md`
      ("Toolchain settings", "Two optimization levels of one program"), `[compare]` blocks in
      `src/Recon.Core/Toolchains/profiles/{msvc,gcc}-base.toml`, tests
      `Compare_settings_come_from_the_toolchain_profile`, `Without_a_profile_the_compare_defaults_apply_and_are_recorded_as_such`,
      `A_profile_can_carry_the_compare_settings_of_section_3_1`.
- [x] **Folded functions reported as such.** A body that survives under another name on the other
      side is `status = "folded"`, scores 1.0 and names the function it is identical to, instead of
      reading as a missing function; `summary.folded` counts them. Evidence: fixture option
      `SyntheticPeOptions.DuplicateFuncA`, test
      `A_body_that_survives_under_another_name_is_reported_as_folded`, `docs/m2-status.md`.
- [x] **Full aligned listing on demand.** `recon diff --function NAME` prints the whole aligned body
      — `equal`, `changed`, `added`, `removed` rows with both addresses and both texts — in the table
      and in `comparison.functions[].aligned`. Evidence: `docs/m2-status.md`, CLI test
      `The_diff_command_prints_instruction_level_detail_for_one_function`, and the `add` example in
      `docs/m2-status.md` with six rows.
- [x] **`data[]` section pseudo-entries.** `inspect data` (new) lists data symbols only and reports
      how many section fallback ranges exist; `--all` lists them, and `--json` keeps them in their own
      `section_ranges` array so the fallback stays visible instead of being silently dropped. The
      inventory JSON still carries them under `"source": "section"` — the compare engine's identity
      fallback (`.data+64`) is built on them. Evidence: `src/Recon.Core/Reporting/Reports.cs`
      (`DataReport`), `src/Recon.Cli/Commands.cs`, CLI test
      `Inspect_data_separates_symbols_from_section_fallback_ranges`.
- [x] **Relocation model for the other containers** (plan §3.1). One table classifies PE (`HIGHLOW`,
      `DIR64`, `HIGH`/`LOW`/`HIGHADJ`), ELF (`R_386_32`, `R_386_PC32`, `R_X86_64_PC32`, …) and Mach-O
      (`X86_64_RELOC_UNSIGNED`, `_BRANCH`, `_GOT`, …) relocation kinds into the four abstract
      classes, and the loader's own kinds reach it: `relocation_classes` in a comparison document maps
      the inventory's kinds onto the model. Evidence: `Compare/RelocationModel.cs`, tests *all three
      containers' kinds reduce to the abstract model* and *a comparison reports the relocation kinds
      of one side by their abstract class*, `docs/m2-status.md`.
- [x] **Bounded similarity search.** Before any alignment, the similarity stage bounds what a pair
      could possibly score by how many mnemonics they share — a bound no alignment can beat — and
      skips the pair when the bound is below the threshold. Alignments are additionally capped. The
      bound is checked against a real pair rather than asserted: the pair the MinGW corpus matches by
      similarity still matches *and* its bound is above what the alignment found. Evidence:
      `ComparisonBuilder.SharedMnemonics` / `MatchBySimilarity`, tests
      `The_similarity_prefilter_is_an_upper_bound_on_a_real_pair` and
      `Unrelated_bodies_are_below_the_similarity_bound`, `docs/m2-status.md` (timings).
