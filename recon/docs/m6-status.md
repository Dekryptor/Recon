# M6 — portability: ELF, and the analysis over it

**State: in progress.** The plan's M6 is *ELF, then Mach-O; x64 and ARM; GCC on ELF, Clang and VB6
native-code profiles; library signature database*. It is being taken in that order, one slice at a
time, and this document records the four slices that are finished — the ELF loader, the analysis over
it, the toolchain profiles that name who built one, and Mach-O — plus the ARM work that went in front
of Mach-O. The rest of the milestone is tracked in `TODO.md`.

| Slice | What it is | Where |
| --- | --- | --- |
| 1. The ELF loader | ELF32 and ELF64, either byte order, executables / shared objects / relocatable objects: sections, segments, both symbol tables with symbol versioning, relocations with the address each one points at, the dynamic table, notes, build id, `.comment`, `.gnu_debuglink` | `src/Recon.Core/Elf/` |
| 2. The analysis over ELF | An image abstraction (`IBinaryImage`) that both formats satisfy, so the inventory, the decoder, xrefs, producer detection, the compare engine and the reports take an ELF input the way they take a PE one | `src/Recon.Core/Images/` |
| 3. The ELF toolchain profiles | `gcc-14-elf64`, `gcc-14-elf32` and `clang-19-elf64`: what a Linux binary's own evidence is matched against, and what `recon build` runs on this machine — including when the compiler is on `PATH` and has no install directory | `src/Recon.Core/Toolchains/profiles/` |
| 4. ARM at the loader level | An ARM or ARM64 image loads, is inventoried and is reported as such — and is then *not* decoded, because the decoder speaks x86 and reading ARM bytes as x86 would invent a program. Decoding stays with M7 | `src/Recon.Core/Analysis/CodeDecoder.cs` |
| 5. Mach-O | The third format: a loader and one adapter, over an image that keeps its DWARF in a bundle beside itself, its switch tables inside the functions that read them, and its imports behind a stub each | `src/Recon.Core/Macho/` |

The rule the second slice established: **everything downstream of the loader speaks
`IBinaryImage`, and nothing format-shaped.** `PeImage` and `ElfImage` keep everything that belongs
to their own format, and a command that genuinely needs PE — `delink`, `relink` — asks
`image.Pe` and refuses with a reason when it is not there, instead of failing later.

## The ELF loader

Both classes (32 and 64 bit) and both byte orders parse, because the byte order is a field in the
file and not a property of the machine reading it. Relocation *types* are named per machine —
`R_X86_64_PC32` and `R_386_PC32` are both type 2, so the name has to come from the machine, not
from the number.

| What the loader reads | Why it matters |
| --- | --- |
| Sections and segments | What is loaded, with which permissions, and where its bytes are in the file |
| `.symtab` and `.dynsym` | The program's own functions, and what it imports and exports |
| `.gnu.version`, `.gnu.version_d`, `.gnu.version_r` | An import is `printf@GLIBC_2.2.5`, not just `printf` |
| `.rel` / `.rela` | Each entry's type, the symbol it names, and the address it points at |
| `.dynamic` | `DT_NEEDED` libraries, `DT_SONAME`, `DT_RUNPATH` |
| Notes | The build id (`NT_GNU_BUILD_ID`) and the ABI tag |
| `.comment` | The compiler that produced the file |
| `.gnu_debuglink` | The separate debug file, which is what a stripped ELF points at |

An entry that cannot be read becomes a line in `Problems[]`; nothing throws.

## The analysis over ELF

`recon inventory`, `inspect`, `disasm`, `diff`, `report`, `doctor` and `verify` all work on an ELF
input, and the compare engine decodes 64-bit code (`Iced` in 64-bit mode) and normalizes it the
same way it normalizes 32-bit code.

| Binary | Functions | Instructions | Xrefs | Data ranges | Jump tables |
| --- | --- | --- | --- | --- | --- |
| `sample-elf64-release` (gcc -O2 -g) | 21 | 249 | 75 | 48 | 1 |
| `sample-elf64-debug` (gcc -O0 -g) | 22 | 322 | 88 | 47 | 1 |
| `sample-elf64-stripped` (gcc -O2 -s) | 20 | 249 | 75 | 35 | 1 |
| `sample-elf64-clang` (clang -O2 -g) | 21 | 274 | 78 | 46 | 1 |
| `shapes-elf64.so` (g++ -O2 -g, shared) | 31 | 352 | 115 | 48 | 0 |
| `sample-elf32-release` (gcc -m32 -O2 -g) | 27 | 403 | 60 | 46 | 1 |
| `shapes-elf32.so` (g++ -m32 -O2 -g) | 35 | 517 | 95 | 48 | 0 |

Four things worth calling out:

* **A stripped ELF still gets an inventory.** It has no `.symtab`, so nothing names its code; the
  linker map beside it does, and the tool reads it — a rule that had to be added, because a
  stripped ELF's dynamic symbol table names the one object it exports and no function at all, and a
  source that names no function must not be preferred over one that names every one.
* **An ELF export is not always code.** `.dynsym` exports data objects as well as functions, and
  seeding a function from `stderr` invented a 16-byte "function" in `.bss`. Exports whose address is
  not in a code section are no longer seeds.
* **The debug source is not one thing.** `debug.sources` lists every source that named a symbol, and
  a Linux binary usually has two: `["dwarf", "elf"]`. DWARF describes what the compiler emitted and
  the symbol table describes what it did not — the runtime's stubs, for instance. Saying only
  `dwarf` credited DWARF with names it never had.
* **A stripped binary with nothing beside it is still readable.** `tests/corpus/elf/blind/sample` is
  the stripped binary with its map taken away: 19 of its 21 functions are found, from call targets,
  the entry point, the PLT thunks and the four places the C runtime is handed a code address (see
  below).

### Jump tables, in both position-independent idioms

A 32-bit absolute binary indexes the table in the branch itself (`jmp dword [0xa058 + eax*4]`), so
the table's address is a constant in the instruction. A position-independent binary cannot do that:

```
# x86-64, gcc -O2 -fpie: the table's address is loaded, then the entry is an offset from it
lea 0xdb4(%rip),%rdx          # rdx = the table
movslq (%rdx,%rdi,4),%rax     # rax = int32 at table + index*4
add %rdx,%rax                 # rax = table + offset
jmp *%rax

# 32-bit, gcc -O2 -fpie: the return address becomes the base, and a delta turns it into the table
call __x86.get_pc_thunk.ax    # eax = the address after the call
add $0x2d1f,%eax              # eax = the table's base
mov -0x1fec(%eax,%edx,4),%ecx # read from the table below it
add %eax,%ecx
jmp *%ecx
```

Both are found, because the analysis follows which register holds which address: `lea`, and the
`get_pc_thunk` stub recognised by its bytes (`8B /r 24 C3`) plus the `add $delta` that follows it.
Every encoding the file might have used is tried — the entry as a full address, as an offset from
the base register, as an offset from the table — and the one that accounts for most of the table is
believed; which one it was is recorded as `kind` (`absolute` or `relative`). All four corpus
binaries that contain a `switch` report an 11-entry `relative` table owned by `dispatch`; the four
built from the source with no `switch` report none. `dispatch` also carries the `has_jump_table`
flag, which it did not before, because the flag used to ask only whether a branch named a table.

### Data references that are really data

`[esi+4]` is not `[4]`. The decoder used to resolve any displacement that fell inside the image as
an RVA, which invented data xrefs into the header — 15 of them in the MinGW corpus binary, at RVAs
`0x6`, `0x14` and `0x18`. Now only an operand the file itself addresses is resolved: a displacement
with no base register (the 32-bit default), one relative to the instruction pointer (the 64-bit
default), or one that lands inside the image proper. That last case is what keeps the real
references: MSVC writes an array access as `add 0x403004(%edi),%ecx`, and the displacement there
*is* `_g_table`'s address, so those xrefs survive.

### What the runtime calls, and nothing else does

Four places hand a code address to the C runtime rather than calling it:

* `DT_INIT` and `DT_FINI`: the initialisation and finalisation functions the dynamic loader calls,
  which nothing in the file refers to and a stripped file does not name either;
* `.init_array` and `.fini_array`: every entry is a pointer to code the loader runs before and after
  `main`, and the file says so in a relocation;
* the arguments of `__libc_start_main`: the entry point of a Linux binary does not call `main`, it
  loads `main`'s address — and the addresses of the runtime's own initialisers — into registers and
  hands them over.

All four are seeded (`found_by: runtime_callback`, medium confidence). On the blind corpus binary
that is `_init`, `main`, `frame_dummy`, `__do_global_dtors_aux` and `_fini`: five functions that no
call target, no symbol and no map would have named. The two the loader calls bracket the program —
nothing is below `_init` and nothing above `_fini` — which is what the test pins, since there is no
name to assert on.

### The oracle agrees

`tools/validate-corpus.sh` sweeps the ELF corpus with the linker map as the oracle — the same GNU ld
map format the MinGW corpus uses — and now also runs `inventory --check-schema`, so a value the tool
emits but the published schema does not know about fails the sweep:

```
corpus validation: ok (24 binaries, PDB oracle on)
```

For `sample-elf64-release` the DWARF subprograms and the map's symbols are matched with exact
addresses and exact sizes:

```
DWARF subprograms with a body: 10
present in the inventory:      10

confidence   matched  addr ok  size exact  size close  size off
high              10       10          10           0         0

map symbols inside the image:  26
  matched by name:             12
```

Two sections the oracle reads are named per format, and it had to learn both: an ELF code section is
flagged `exec` where a PE one is flagged `code`, and the relocation kinds are `R_X86_64_*`,
`R_386_*`, `R_AARCH64_*` and `R_ARM_*` rather than PE's type names.

### A comparison over ELF

```
$ recon diff tests/corpus/elf/sample-elf64-release tests/corpus/elf/sample-elf64-release --summary
  functions    21 left, 21 right, 21 matched (21 exact, 0 changed), 0 folded, 0 only left, 0 only right
  instructions 231 left, 231 right, 231 equal
  score        1

$ recon diff tests/corpus/elf/sample-elf64-debug tests/corpus/elf/sample-elf64-release --summary
  functions    22 left, 21 right, 20 matched (4 exact, 16 changed), 0 folded, 2 only left, 1 only right
  instructions 320 left, 231 right, 111 equal
  score        0.3469
```

The first is what an ELF binary compared with itself has to be; the second is the same program at
`-O0` and `-O2`, which is a real difference and is reported as one.

## The ELF toolchain profiles

Three profiles, all extending the GCC base (clang emits the same ABI on Linux — Itanium mangling,
DWARF, the GNU linker, glibc — so it inherits the same codegen and compare settings):

| Profile | Target | What names it |
| --- | --- | --- |
| `gcc-14-elf64` | elf64/x64 | 8-byte pointers, `sysv` calling convention, 64-bit prologue hints, `.comment` saying `GCC: (` plus a `GNU C… 14` DWARF producer and a `libc.so` import |
| `gcc-14-elf32` | elf32/x86 | the same compiler with `-m32` in its default flags: 4-byte pointers, `cdecl`, the 32-bit prologues |
| `clang-19-elf64` | elf64/x64 | `clang version 19` in `.comment` and in the DWARF producer |

Detection over the corpus, with nothing configured in `project.toml`:

| Binary | Attributed to | Confidence | Evidence |
| --- | --- | --- | --- |
| `sample-elf64-release` | `gcc-14-elf64` | high | `import_dll`, `comment_section`, `pdb_compiland`, `dwarf_producer`, `section` |
| `sample-elf64-debug` | `gcc-14-elf64` | high | the same |
| `sample-elf64-stripped` | `gcc-14-elf64` | medium | `import_dll`, `comment_section`, `section` — the DWARF is what stripping took |
| `sample-elf64-clang` | `clang-19-elf64` | high | the same, matched on clang's own producer string |
| `sample-elf32-release` | `gcc-14-elf32` | high | the same, and no `elf64` profile is offered |
| `shapes-elf64.so`, `shapes-elf64-release`, `shapes-elf64-debug` | `gcc-14-elf64` | high | the same |
| `shapes-elf32.so` | `gcc-14-elf32` | high | the same |

Three things the slice had to fix on the way:

* **A clang binary on a GNU system carries a GCC string too**, because it links GCC's startup files:
  its `.comment` says both `GCC: (Debian 14.2.0-19)` and `Debian clang version 19.1.7`. The clang
  profile matches on the clang string, and the GCC profile on the GCC one; the tie between them is
  broken by how many *kinds* of evidence each matched, so a stripped binary — where only the GCC
  comment survives — is not attributed to clang because `clang-19-elf64` sorts first.
* **A Unix toolchain has no install directory.** `local.toml [toolchain.<id>] root` used to be
  required, and a bare `exe = "gcc"` in a profile resolved to a path inside the project instead of
  being looked up on `PATH`. Both are fixed: `root` is optional, and `recon toolchain check` reports
  what it will actually run —

  ```
  root:  (none — the profile's tools are looked up on PATH)
  cc:    /usr/bin/gcc
  link:  /usr/bin/gcc
  toolchain gcc-14-elf64: ok
  ```
* **x86-64 needed its own calling-convention name.** The vocabulary was `cdecl`, `stdcall`,
  `fastcall`, `thiscall`, `vectorcall`; none of them is true of the System V convention, so `sysv`
  was added rather than calling it `cdecl`.

`recon build` uses the profile the binary names when the project names none, which is what makes a
new project work the moment it is pointed at a file. `examples/elf-project` is exactly that: two
units of C, a `local.toml` with an inputs directory and an empty `[toolchain.gcc-14-elf64]`, and

```
$ recon build --project examples/elf-project
  toolchain gcc-14-elf64     cc /usr/bin/gcc (ok, no install root in local.toml)
  CC     src/entry.c (gcc-14-elf64 -> build/obj/entry.o)
  CC     src/arith.c (gcc-14-elf64 -> build/obj/arith.o)
2 unit(s): 2 compiled, 0 cached in 443 ms; link linked; build/sample-elf (17664 bytes, sha256 ad9f31d43b48)

$ recon report --project examples/elf-project
report sample-elf: score 0.8143, 193/231 instructions exact (83.55%), 10/21 functions verified
  unit arith                100%     8/8 instructions, 0 function(s) missing
  unit entry                100%     36/36 instructions, 0 function(s) missing
```

`recon doctor` says which profile it would use and why (`unit "arith" names no toolchain: the build
will use "gcc-14-elf64", which this binary's evidence points at`), so the guess is visible and
`unit.toolchain` overrides it.

## ARM: loaded and described (AArch64 is now decoded — see `m7-status.md`)

The plan puts *x64 and ARM* in this milestone, and `Iced` — the decoder everything else is built on —
is x86 only. Handed an AArch64 image it does not fail; it reads ARM bytes as x86-64 and produces
instructions that were never there, which is the one failure this tool must not have, because the
result looks like an answer. So the milestone splits:

* **What is done** (this slice): an ARM or ARM64 image is loaded and described. `ElfImage.Architecture`
  and `IBinaryImage.Isa` already name `arm` (EM_ARM, 40) and `arm64` (EM_AARCH64, 183) and the
  configuration accepts both, so the machine is reported rather than assumed. Its relocation types are
  named too: the loader carries tables for EM_AARCH64 (16 types) and EM_ARM (5), and every name in
  them was read out of an object the test assembles with `llvm-mc-19` and prints with
  `llvm-readelf-19 -r`, so the tool's table and LLVM's cannot disagree silently. A type neither table
  has keeps the right namespace (`R_AARCH64_1027`) instead of being given someone else's name, and a
  machine with no table at all says whose number it is (`type 257 of machine AArch64`).
  Building that found a real defect, now fixed: `r_info` was split by machine rather than by word
  size, so every ELF64 file that was not x86-64 had its relocation type truncated to a byte —
  AArch64's 275 (`R_AARCH64_ADR_PREL_PG_HI21`) was read as 19, which is 275 & 0xFF.

  The decoder says what it speaks (`CodeDecoder.CanDecode`, `DecodeProblem`) and returns nothing for
  anything else, so an inventory of a machine it does not have — 32-bit ARM today — is its sections,
  symbols, relocations, imports and data, with `instructions: 0` and a line in `problems[]` saying
  which instruction set went unread, instead of a fabricated program. `recon disasm` refuses with the
  same reason instead of printing a listing of instructions that are not there.
* **AArch64 is decoded now.** It was the other half of this item and it is written up in
  [`docs/m7-status.md`](m7-status.md) ("Reading ARM"), because that is where the plan puts the
  non-x86 instruction sets. The short of it: every AArch64 instruction is four bytes, so where one
  starts is arithmetic, and a partial decoder is safe in a way it would never be on x86. What is still
  not decoded is 32-bit ARM (A32/T32), which is variable-length enough in Thumb that the same
  argument does not hold, and which waits for a binary to check it against. There is none on this
  machine and none in the
  corpus, and an untested decoder is worse than an honest refusal. It belongs to M7, which the plan
  already gives the non-x86 instruction sets, and the `isa` axis it needs is in place.

The split is testable without an ARM toolchain, because the fixture that covers the byte order no
desktop has can carry a machine type no desktop has either: `SyntheticElf` takes a `Machine`, and two
tests (`An_image_of_another_machine_is_loaded_but_not_decoded`,
`The_cli_says_so_too_when_it_cannot_read_the_instruction_set`) pin both halves — the file is still
inventoried, and nothing is decoded. The relocation names do not get that luxury: they are numbers
from a specification, so `An_aarch64_object_names_its_relocations` assembles one with `llvm-mc-19`
and makes the loader's names match what `llvm-readelf-19 -r` prints, and skips where no assembler is
installed.

## Mach-O

The plan's order is *ELF, then Mach-O*, and Mach-O is the harder of the two to take on trust: it is
the format in which a compiled C program puts its jump tables somewhere neither of the others would,
keeps its DWARF somewhere else entirely, and reaches an import through two indirections instead of
one. That makes the slice a test of whether the second slice really did move the format out of the
analysis. It had: a loader and one adapter (`src/Recon.Core/Images/MachoBinaryImage.cs`) were all
that had to be written, and the inventory, the decoder, the xrefs, the producer detection and the
compare engine took a Mach-O input unchanged.

* **The file.** Thin (`0xFEEDFACE`, `0xFEEDFACF`) and fat (`0xBEBAFECA`, `0xBFBAFECA`) magics in
  either byte order; segments and the sections inside them from `LC_SEGMENT`/`LC_SEGMENT_64`; the
  entry point from `LC_MAIN`'s `entryoff`, which is an offset into a segment rather than an address;
  symbols from `LC_SYMTAB`'s `nlist`, whose `n_desc` carries the library ordinal of an import; and
  the indirect symbol table from `LC_DYSYMTAB`, which is what names a stub.
* **`__PAGEZERO` is not part of the image.** It is a four-gigabyte hole with no protection, kept so a
  null pointer faults. Counting it as a segment puts the image base at 0 and every address in the
  file off by the hole. Only segments that are mapped, or that occupy file space, are counted.
* **A symbol's size is not in the file.** `nlist` has no size field, so `MachoSymbols` leaves every
  size unknown rather than inferring one from the next symbol's address.
* **The DWARF is in a `.dSYM` bundle.** clang leaves a debug map in the executable and `dsymutil`
  collects the real thing into `<binary>.dSYM/Contents/Resources/DWARF/<name>` — another Mach-O file,
  with nothing but debug sections in it. `ProjectContext` looks there when the image itself has no
  DWARF, which is what turns twelve named functions into twelve functions with a unit and a line each.
* **An import is reached through a stub, and the stub is reached through a pointer.** There is no
  import table: a call lands on a six-byte stub in `__stubs`, which jumps through a pointer in
  `__la_symbol_ptr` or `__got`, and the indirect symbol table is what says which pointer belongs to
  which import. Naming that pointer as the import's slot is what lets the existing thunk detection
  call the stub `thunk__printf`; reading the stubs out of the same table is what names them at all in
  an arm64 image, where nothing is decoded.
* **A switch's table sits in `__text`, inside the function that reads it** — clang's choice on x86-64
  Mach-O, where a PE or an ELF build would keep it in a read-only section beside the code. The
  analysis finds it where it is. The comparison skips a known table's bytes when it reads a body, so
  that a switch does not differ from itself because its table was decoded as instructions.
* **A map file beside the binary is read, and never displaces debug information.** ld64's map is a
  format of its own (`0xADDR\t0xSIZE\t[ file] name`) and is the only thing that names the functions
  of a stripped image. But a map lists names and addresses where DWARF also carries the translation
  unit, the line and the size, so a map competes with a symbol table and not with debug info — even
  when it names more entries, which for this corpus it does, because it lists the stubs.
* **arm64 is loaded, named and decoded.** Its functions come from the map and the bundle, and its
  code is read as ARM: 206 instructions of `__text` in the corpus binary, every call landing on an
  address the linker's own map names as a function.

`clang-19-macho64` is the profile. Its evidence is the shape of the sections (`__stubs`,
`__la_symbol_ptr`), because a Mach-O image has no `.comment` to read a compiler out of and the
producer string exists only inside the bundle. Building with it needs headers the host does not have,
which is why the corpus is compiled against `tests/corpus/macho-stubs/` — the handful of libc
declarations the corpus source uses, and nothing more.

The corpus is five images (`tools/build-macho-corpus.sh`): x86-64 at `-O2` and at `-O0`, a dylib,
arm64, and a stripped copy — each with its ld64 map beside it, and four with a `.dSYM` bundle. The
oracle for a Mach-O image is that map file and not `objdump`, which cannot read the format at all;
saying so matters, because an oracle that silently checks nothing looks exactly like one that passes.

What the corpus cannot be is the 32-bit image, the byte-reversed one, or the fat file in front of
either: one toolchain, two architectures, and no PowerPC Mac. Those three are what
`tests/Recon.Tests/Fixtures/SyntheticMacho.cs` is for — a file built in memory in whichever word size
and byte order, optionally wrapped in a fat container with a byte-reversed PowerPC slice ahead of it,
so that the loader is tested against the format rather than against one linker's output.

## The Visual Basic 6 profile

The plan's §3.1 asks for two more profiles after the ELF ones: *Clang and VB6 native-code profiles*.
The Clang one (`clang-19-msvc`) landed with the MSVC corpus; VB6 is the last item of M6, and it is a
profile with an unusual shape: it is identified by what the program is linked against, not by what
compiled it.

`src/Recon.Core/Toolchains/profiles/vb6-native.toml` extends `msvc-base` — VB6 compiles with the
Visual C++ back end, so the ABI, the padding, the SEH model and the folding rules are Microsoft's —
and adds the runtime import:

```toml
[[detect.import_dll]]
contains = "msvbvm60"
```

MSVBVM60.DLL is the Visual Basic 6.0 virtual machine, and nothing but a Visual Basic program imports
it. For most of M6 that import was the entire profile: `AGENTS.md` says no number goes into a profile
unless a command printed it, no VB6 binary existed on this machine to print one from, and a number
invented here would become a number the tool quotes. Then a set of real 1998-2004 binaries arrived,
and the rest of the profile was measured instead of guessed — linker version 6.0, and the two Rich
product ids a Visual Basic program carries. The numbers and the file they came from are in
[`toolchains.md`](toolchains.md#visual-basic-6); what measuring them turned up beyond the profile is
in [What real Visual Basic binaries exposed](#what-real-visual-basic-binaries-exposed) below.

Two things follow, and both are stated in the profile and the doc rather than hidden:

* The attribution is `medium` confidence, from three kinds of evidence measured on a real VB6
  program: the runtime it imports, the 6.0 linker stamp, and two Rich product ids. The tool says
  "Visual Basic 6" and shows all three.
* A p-code VB6 program imports the same DLL but carries bytecode instead of x86. Telling the two
  apart is the plan's `isa` axis, which is M7; until then the profile describes the native case.

`tests/Recon.Tests/Vb6ToolchainTests.cs` (6) pins it with the synthetic PE: a file importing
MSVBVM60.DLL with the Visual Studio 98 linker's 6.0 stamp and no Microsoft producer strings is
attributed to VB6 and analysed with the profile; the same file importing KERNEL32 is not; and a file
that carries *both* the VB6 import and a Visual C++ 2008 linker stamp is attributed to the stronger
evidence, with Visual Basic still reported as the weaker possibility.

`tests/Recon.Tests/RealVb6Tests.cs` (7) does the same against the real 1998-2004 binaries, when a copy
is on the machine: VISDATA.EXE attributes to `vb6-native` on all three kinds of evidence, its Rich
header decodes to 1 assembler object, 36 Basic objects and one `vb60` record, and LINK.EXE, C2.EXE
and CVPACK.EXE attribute to `msvc-6` with no GCC profile mentioned at all. They need an input
directory that cannot be committed, so they skip rather than fail — `RECON_VB6_INPUTS` points at it —
and among them is the one test that keeps the inventory linear: the largest of those binaries has to
finish in seconds, not minutes.

Working on it exposed a detection defect that had been hidden by a stale directory: the example
project carried a six-profile copy of the shipped set, written before the ELF and Mach-O profiles
existed, so no test had ever run detection over a MinGW binary with both `gcc-13-mingw` and
`gcc-14-mingw` available. With both, the corpus binary was attributed to **GCC 13** — the release that
built the MinGW runtime objects it links — because the two matched the same kinds of evidence and the
tie fell to the alphabet. `ProducerDetector` now breaks that tie on the compiler release the evidence
names, so the binary is attributed to GCC 14 — the release that compiled the program, and the one a
rebuild would have to reproduce — with GCC 13 still reported for the runtime's units. The per-unit
view was always right; it was the binary-level answer that was wrong. Pinned by
`InventoryTests.The_binary_is_attributed_to_the_compiler_that_built_the_program`, and the golden
inventories were re-recorded against the full shipped set, which is what they should have pinned all
along.

## What is deliberately not done yet

* **`delink` and `relink` stay PE-only.** They are about PE sections, PE relocations and a linker
  script; on an ELF input `recon delink` says so and exits non-zero rather than writing half a plan.
* **A shared object has no entry point**, so `entry_rva` is 0 for one, which the oracle accepts as
  "this file has none" rather than as a broken header.
* **One function a blinded binary still misses.** A cold block such as `dispatch.cold` is reached by
  a conditional jump and by nothing else, and jump targets are not seeds, because most of them are
  labels inside a function rather than boundaries. `recon doctor` says this on a 64-bit image
  instead of hiding it.
* **Decoding ARM.** The ARM work above went in front of Mach-O deliberately: the most common Mach-O
  today is arm64, so a Mach-O loader that could not describe one would be a loader for yesterday's
  binaries. Decoding it is M7, where the plan puts the other instruction sets, and it waits for a
  target that needs it — an untested decoder is worse than the honest refusal above. Everything else
  the plan puts in M6 is done: the ELF loader, the analysis over it, the ELF profiles, Mach-O, the
  Visual Basic profile above, and the pattern database in
  [`signatures-status.md`](signatures-status.md).

## Tests

* `ElfLoaderTests` (20): the synthetic file — which is what covers the byte order and word size no
  desktop machine has — and the corpus: versioned imports, a soname, PIE relocations, a stripped
  binary, clang, ELF32 against ELF64.
* `ElfInventoryTests` (17): the loader's choice of format, an ELF inventory (functions of the
  source, `isa` `x64`, PLT thunks named after their import), the symbol table as debug information,
  a shared object as a library, a stripped ELF inventing nothing, ELF32, a jump table in each
  position-independent idiom and none where there is no `switch`, data xrefs that stay inside the
  image, the runtime's callbacks — the arrays, the loader's two and `main` — in a binary with no
  symbols at all, an ARM64 image being inventoried but not decoded (and `recon disasm` refusing it) —
  now a 32-bit ARM image, the instruction set the decoder genuinely does not speak —
  an AArch64 object's relocation types agreeing with LLVM's own assembler and reader, and end to end
  through the CLI
  — `recon inventory` on an ELF project, `recon diff` of an ELF with itself scoring 1, and
  `recon delink` refusing an ELF with a reason.
* `MachoLoaderTests` (12): a file built in memory — which is what covers the 32-bit image, the
  byte-reversed one and the fat container, none of which any corpus binary here is — the sections and
  the entry point, the symbols and the import's slot, a stub named from the indirect symbol table, a
  32-bit image inventoried end to end, a file that is not a Mach-O one, and a header whose load
  commands the file does not hold.
* `MachoInventoryTests` (17): the image and its base (with `__PAGEZERO` kept out of it), the stub
  section saying what it holds, an import's slot being the pointer its stub jumps through, a stub
  named after its import, a switch table inside the function that reads it and recorded as data, a
  stripped image named by the ld64 map beside it, that map's own format, the symbol table claiming no
  size for anything, an arm64 image loaded, named and decoded, a dylib's exports, the DWARF in
  the `.dSYM` bundle, a map file not displacing debug information, a stub named from the indirect
  symbol table with nothing decoded, the `clang-19-macho64` profile being detected, and two builds
  compared by name.
* `ElfToolchainTests` (16): the three profiles and their targets, 64-bit prologues that are not
  32-bit ones, no ELF profile matching on a MinGW import, the prologue vocabulary in both widths,
  detection over the corpus (GCC, clang, `-m32`, and the stripped case), the rule that the project's
  choice wins over detection and the format's default otherwise, `local.toml` without a root, a
  compiler found on `PATH`, an install root that still wins over `PATH`, and `recon toolchain check`
  on a root-less toolchain.

## Gates

* `dotnet test tests/Recon.Tests/Recon.Tests.csproj` — 404 passed, 0 failed (297 before M6). The 24
  added since the Mach-O slice are the pattern feature (`recon sigs`), described in
  [`signatures-status.md`](signatures-status.md); the 7 after those are the Visual Basic profile and
  the detection tie-break it uncovered (below); and the last 7 are `RealVb6Tests`, which run over real
  1998-2004 Visual Basic 6 and Visual Studio 98 binaries when a copy is on the machine
  (`RECON_VB6_INPUTS`) and skip when it is not.
* `bash tools/validate-corpus.sh` — ok (24 binaries: 9 PE with the PDB oracle, 10 ELF with the map
  oracle, 5 Mach-O with the ld64 map oracle), every inventory checked against the published schema,
  including the relink sweep (6 identical, 0 differing).
* `dotnet publish src/Recon.Cli/Recon.Cli.csproj -c Release -r linux-x64` — no warnings.

## Files

| Path | What it is |
| --- | --- |
| `src/Recon.Core/Elf/ElfModel.cs`, `ElfLoader.cs` | the ELF file: sections, segments, symbols, relocations, dynamic, notes |
| `src/Recon.Core/Images/BinaryImage.cs` | `IBinaryImage` and the common section/symbol/relocation/import/export records |
| `src/Recon.Core/Images/PeBinaryImage.cs`, `ElfBinaryImage.cs` | each format translated into that common shape |
| `src/Recon.Core/Images/ImageLoader.cs` | loads a file by sniffing it, rather than by trusting `target.format` |
| `src/Recon.Core/DebugInfo/ElfSymbols.cs` | the symbol table as debug information |
| `src/Recon.Core/Analysis/ProloguePattern.cs` | the prologue vocabulary a profile may name, in both widths |
| `src/Recon.Core/Toolchains/ToolchainSelector.cs` | which profile one image is analysed and built with |
| `src/Recon.Core/Toolchains/profiles/gcc-14-elf64.toml`, `gcc-14-elf32.toml`, `clang-19-elf64.toml` | the ELF profiles |
| `src/Recon.Core/Analysis/CodeDecoder.cs` | where an image's instruction set is checked against what the decoder speaks |
| `src/Recon.Core/ByteReader.cs` | bounds-checked reads in either byte order (moved out of `Pe/` — both loaders use it) |
| `tools/build-elf-corpus.sh` | the ELF corpus: gcc, g++, clang, `-m32`, shared objects, a stripped copy, and a copy of that with no sidecar at all |
| `tests/Recon.Tests/Fixtures/SyntheticElf.cs` | an ELF built in memory, in either class and byte order |
| `src/Recon.Core/Macho/MachoModel.cs`, `MachoLoader.cs` | the Mach-O file: thin and fat, either byte order; segments, sections, `nlist`, the indirect symbol table, `LC_MAIN` |
| `src/Recon.Core/Images/MachoBinaryImage.cs` | Mach-O translated into `IBinaryImage` |
| `src/Recon.Core/DebugInfo/MachoSymbols.cs` | the symbol table, the stubs it does not name, and the indirect table that does |
| `src/Recon.Core/Toolchains/profiles/clang-19-macho64.toml` | the Mach-O profile |
| `tools/build-macho-corpus.sh` | the Mach-O corpus: clang for an Apple target, linked by ld64.lld, with ld64 maps and `dsymutil` bundles |
| `tests/corpus/macho-stubs/` | the libc declarations the corpus source is compiled against, since there is no SDK here |
| `tests/Recon.Tests/Fixtures/SyntheticMacho.cs` | a Mach-O built in memory: 32-bit, byte-reversed, and wrapped in a fat file |
| `src/Recon.Core/Macho/MachoLoader.cs` | also where a header whose load commands the file does not hold is reported rather than read as an empty image |
| `tests/tools/image_format.py` | prints a binary's format and architecture, for scripts that write a project file |
| `examples/elf-project/` | a reconstruction project over an x86-64 ELF: no toolchain configured anywhere, built with the compiler on `PATH` |

## What real Visual Basic binaries exposed

The VB6 profile was written with one rule and a note saying why: nothing stronger could be written
down without a VB6 binary to read it from. A set of real 1998-2004 inputs then turned up
(VISDATA.EXE, the Visual Studio 98 tools LINK.EXE, C2.EXE and CVPACK.EXE, and the runtime DLLs beside
them), and measuring them found four defects that the synthetic fixtures could not, because a fixture
can only assert what its author already believes.

**The Rich header was decoded wrongly.** The parser XORed the `DanS` marker and its padding and read
the records as plain text. They are encrypted too, with the same key, so every real file reported
nonsense: VISDATA.EXE's three records came out as product ids `0x8919`, `0x891e` and `0x891a` with
builds in the tens of thousands, and no count smaller than a billion. Decrypted properly, the same
bytes say `masm_6.13` build 7299 ×1, `vb6_basic` build 8041 ×36 and `vb60` build 8167 ×1 — a Visual
Basic program's bill of materials. Nothing in the test suite caught it, because the only Rich headers
in it are written by a fixture that made the same mistake.

**"The last record is the linker" is not true.** In VISDATA.EXE the last record is the Basic compiler
and there is no linker record at all; in LINK.EXE, C2.EXE and CVPACK.EXE it is `cvtres_5.0`, with the
linker (`0x0004`) somewhere in the middle. The linker is now identified by product id
(`RichHeader.LinkerProdIds`), the last record is reported as `(last entry)` on its own, and a header
with no linker record reports a null `LinkerEntry`. Several product-id names were wrong as well —
`0x0004` is the Visual Studio 98 linker, not the CRT DLL, and `0x0013` is the 5.12 linker, not the
static CRT — so the table now cites the `comp_id.txt` of the public `richprint` project for every id
and names nothing outside it.

**Inventorying a real binary was quadratic.** The prologue scan asked, for every byte of every code
section, whether any known function start covered it; the thunk scan asked, for every instruction,
whether any other instruction called it; and each function's instructions were found by filtering all
of them. On 1998-sized files (700 KB) that was seconds, so it never showed; on a 1.9 MB binary it was
over ten minutes, `inspect sections` included, because every `inspect` subcommand builds a full
inventory. The three scans are now a binary search over the sorted function starts, a `HashSet` of
call targets built in one pass, and a window into instructions sorted once. Same answers — all 404
tests pass unchanged — and the same 1.9 MB binary inventories in five seconds.

**MSVCRT.dll is not evidence.** All three Visual Studio 98 tools import it, and `gcc-base` counted
that as a rule, so LINK.EXE was reported as `gcc-13-mingw`. Every 32-bit Windows binary imports
MSVCRT.dll; the GCC profiles now name MinGW's own runtime instead (`libgcc*`, `libstdc++*`,
`libwinpthread*`), and those files are attributed to `msvc-6`, the profile their linker stamps and
product ids actually describe.

The runtime DLLs in the same set were left open here, and M7 closed them: MSVBVM60.DLL, VBA6.DLL,
VB6.EXE and VB6IDE.DLL stamp Visual C++ 5.x linkers and are now attributed to `msvc-5`. The reason
this was open is worth keeping, because it was closed by measuring rather than by deciding: the claim
was that none of them carries a Rich header, and VBA6.DLL does — four records, one of which is
product id 0x0013 at build 8078, the Visual Studio 97 linker, beside a 5.12 stamp in the optional
header. See [`docs/m7-status.md`](m7-status.md). MSO97RT.DLL is still reported as unknown, and
should be: it stamps 3.10, older than the 5.x line and older than any product id measured here.
