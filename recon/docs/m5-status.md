# M5 — delink and relink

**State: done.** Two commands, one plan, and a verdict:

* `recon delink` — cuts the original into pieces a linker can put back at the same addresses: one
  assembly file per section, one linker script, and `delink.json` (schema 0.1) describing every piece,
  every absolute address that became a reference, and which unit is responsible for which piece.
* `recon link` — assembles the pieces with the toolchain the binary asks for, links them at the
  original's image base with the original's entry point, finishes the header entries a linker cannot
  know, and then **compares the result with the original, section by section and byte by byte**, and
  prints the verdict.

```
$ recon delink --project examples/progress-demo --check-schema
delink sample: 16 section(s), 296 piece(s), 195527 byte(s)
  pieces      142 function(s), 0 data, 2 jump table(s), 152 filler(s)
  providers   291 original, 5 rebuilt
  fixups      504 absolute address(es) emitted as references
  wrote build/delink/text.s (218752 byte(s))
  ...
  wrote build/delink/link.ld
  wrote build/delink/delink.json
delink plan matches schema 0.1

$ recon link --project examples/progress-demo
  toolchain   gcc-13-mingw (detected from the binary (high confidence: import_dll, pdb_compiland, dwarf_producer, section))
link sample: 296 piece(s) at 0x400000
  wrote build/relinked.exe
  header copied from the original: timestamp 0x00000000 (the linker stamped the link, not the build); iat directory -> 0xe118+0xdc; dll characteristics 0x0140
  relinked identical: 16 section(s), 192851 byte(s), header included
```

That last line is the claim of the milestone, and it is measured, not asserted: `RelinkVerifier`
reloads the relinked image and compares every section's name, address, size, flags and bytes, plus
the header entries the image is found by (base, entry, subsystem, size of image). It prints the
differences when there are any, in the same place.

## Why this is the milestone that matters

Everything before it measures a reconstruction; this is what makes a *partial* reconstruction
possible. If the original can be cut into pieces and put back at the same addresses, then a piece can
be replaced by one that was compiled from reconstructed source, and the rest of the image — the
compiler's runtime, the startup code, the data nobody has looked at yet — is still there, still at
its address, still correct. That is what a reconstruction of a real binary needs, and it is why the
verdict is printed rather than left to be assumed.

## What is implemented

| Part | Code | What it does |
| --- | --- | --- |
| The plan | `Delink/DelinkPlanner.cs` | Claims each section's bytes for functions, then named data, then jump tables; whatever is left is filler, because an image without its padding is not the same image |
| The pieces | `Delink/AsmWriter.cs` | One `.s` file per section: bytes as bytes, every absolute address as a `.long <symbol>`, each piece at its offset, each name typed with `.def`/`.endef` |
| The layout | `Delink/LinkerScriptWriter.cs` | `link.ld`: every section at the address it came from, plus `/DISCARD/` for the sections an assembler writes for itself |
| The header | `Delink/HeaderCompletion.cs` | Copies what a linker cannot know: the timestamp, data directories it did not build (IAT, TLS, debug), `DllCharacteristics`, section flags, and the rounding the assembler adds |
| The verdict | `Delink/RelinkVerifier.cs` | Compares the relinked image with the original and says so |
| The document | `Schemas/delink.schema.json` | Schema `0.1`, validated by `--check-schema` and by a test |
| The commands | `Commands.Delink()`, `Commands.Link()` | `recon delink [-o DIR] [--check-schema] [--json]`, `recon link [-o FILE] [--dry-run] [--json]` |

## Evidence

### Every binary in the corpus

```
$ bash tools/relink-corpus.sh
binary                     result     detail
-------------------------- ---------- ----------------------------------------
sample-debug.exe           identical  identical: 16 section(s), 191288 byte(s), header included
sample-release.exe         identical  identical: 16 section(s), 192851 byte(s), header included
sample-stripped.exe        identical  identical: 8 section(s), 40880 byte(s), header included
shapes-debug.exe           identical  identical: 16 section(s), 281195 byte(s), header included
shapes-release.exe         identical  identical: 16 section(s), 276939 byte(s), header included
shapes-dll.dll             identical  identical: 17 section(s), 264389 byte(s), header included
-------------------------- ---------- ----------------------------------------
6 binary(ies)              summary    6 identical, 0 differing, 0 skipped
```

A debug build (DWARF in nine sections), a release build, a stripped build (no symbol table at all:
eight sections, forty kilobytes, still byte-identical), and a DLL (with its export table and
`-shared`).

### The tool's own comparison agrees

The relinked image is not only byte-identical; it is identical *as an image*, which is what
`recon diff` measures — functions, data symbols, references:

```
$ recon diff tests/corpus/mingw/sample-release.exe examples/progress-demo/build/relinked.exe --summary
  functions    142 left, 142 right, 142 matched (142 exact, 0 changed), 0 folded, 0 only left, 0 only right
  instructions 8076 left, 8076 right, 8076 equal
  score        1
  data         0 differing symbol(s): 146 identical, 0 changed, 0 only left, 0 only right
```

Getting there found three real defects, each one a difference that was not in the bytes:

1. **Names with no size were dropped.** A linked image's symbol table says where a variable is and
   not how big it is, so those names claimed no bytes and were never written — and every reference
   to them then resolved to an address instead of a name (`.bss+104` instead of `___mingw_app_type`),
   which the compare engine reads as a difference. They are now emitted as labels at their own
   addresses: 142/142 functions exact, 146/146 data symbols identical.
2. **Every symbol in the relinked image looked like data.** COFF spells a typed symbol
   `.def`/`.endef`, and without it the nine functions that only the symbol table knows about
   (`___gcc_register_frame`, `dispatch.cold`, …) were missing from the relinked image's inventory.
3. **Assembler-generated sections landed in ours.** GNU as writes a `.debug_line` for the file it is
   assembling, and the linker drops an input section it cannot place into the output section of the
   same name — putting the assembler's 29 bytes in front of the section's real contents, which
   shifted it by 29 bytes. Our debug sections now keep their own names (which is also what gives
   them a one-byte alignment, so they are not padded), and the names an assembler writes itself are
   prefixed and discarded.

### The relink is mechanical, so it is not tied to the toolchain that built the image

An MSVC-ABI binary — built by clang and lld-link, with a PDB — delinks and relinks identically using
a MinGW GNU ld driver, because the plan says where every byte goes and the ABI never enters into it:

```
$ recon link --project /tmp/msvc
  toolchain   gcc-13-mingw (the only profile here that can assemble and place sections)
link msvc-sample: 17 piece(s) at 0x400000
  wrote build/relinked.exe
  header copied from the original: timestamp 0x00000000 (…); debug directory -> 0x202c+0x1c; section .text size 0x1f2 (the assembler padded it to its alignment); …
  relinked identical: 4 section(s), 812 byte(s), header included
```

Note the **debug directory** among the copied entries: it is what ties the image to its PDB, and with
the timestamp copied too, the relinked image still matches the debug info that describes it.

## What is deliberately not reproduced

Two differences remain, both outside the image:

* **The COFF symbol table.** The relinked file carries one (446 symbols against the original's 1784);
  it is not loaded, not in `SizeOfImage`, and it names the pieces *this* plan emitted rather than the
  objects the original was linked from. Reproducing it would mean inventing symbols, which is the
  opposite of what this tool does.
* **The checksum**, which covers the whole file and therefore differs with the symbol table.

Everything inside the image — headers, every section's name, address, size, flags and bytes, all
sixteen data directories, the entry point, the image base, the subsystem, `SizeOfImage` — is
identical, and the verifier checks all of it.

## One limitation, and why it is one

`recon link` places sections with a GNU ld **linker script**, which is the only way to say "this
section starts at this address". `lld-link` and `link.exe` have no equivalent: `/section:` sets
attributes, not addresses. A profile whose linker writes `/OUT:` instead of `-o` is therefore refused
with that reason rather than handed a command line it cannot obey:

```
no toolchain that can relink pe32/x86 is installed: relinking needs a compiler that reads GNU-as
syntax and a linker that takes a linker script (GCC or Clang driving GNU ld).
```

`recon delink` is unaffected — the plan is toolchain-independent, and the MSVC corpus's binaries
delink fine. Only the relink needs a linker that can be told where a section goes, and since the
relink is mechanical, a GNU ld driver is enough for any PE image regardless of the ABI that produced
it (proven above).

## Tests

`DelinkTests` (20 — 16 when M5 landed, four more with the rebuilt provider), over the synthetic
fixture and over the corpus:

* every section is tiled with no gap and no overlap, to its virtual size rather than to the size the
  file rounds it up to;
* the relocation table is planned rather than left to the linker;
* a name with no size becomes a label at its own address, inside the piece that covers it;
* a name used at two addresses is emitted twice, with the address in the second one's name, and the
  plan says so;
* section flags come from the original's characteristics (`.text` → `xr`, `.bss` → `b`);
* debug sections keep their own names; the ones an assembler writes itself are prefixed, and the
  script discards them;
* the assembly places every piece at its offset, types every function, and emits addresses that are
  not in the file as `.space`;
* the plan matches `delink.schema.json`;
* header entries the linker filled are left alone, and the ones it could not know are copied;
* the verdict says *identical* for an unchanged copy and names the differing byte for a damaged one;
* end to end: a corpus binary is delinked and relinked with a real cross compiler, the verdict says
  identical, and `recon diff` then scores 1.0 with nothing missing on either side.

### Since M5: the other half of `provider`

M5 planned a piece's provider — `original` or `rebuilt` — and reported how many of each, and `recon link`
then wrote the original's bytes for both, because a piece's bytes came out of the plan rather than out of
a build. A unit with `provider = "rebuilt"` is now filled from that unit's own compiled object, so the
delink → replace → relink loop runs end to end; the whole corpus source, rebuilt as one unit and
relinked, is the original image byte for byte. The provider, the refusals and the measurements are in
[`docs/m7-status.md`](m7-status.md) ("A unit's own build takes a piece's place").

Two numbers in the demo above changed with it, and both were the same defect: the project has five
covers and claimed **seven** pieces. `Covers.Resolve` falls back to an unambiguous substring, and the
delinker asked it about one function at a time — where nothing is ever ambiguous — so `symbol = "add"`
also claimed `___w64_mingwthr_add_key_dtor` and `__multadd_D2A`. A cover is now resolved against every
name the image has, the demo claims the five pieces its covers name, and the assembly is 44 bytes shorter
because two pieces no longer name a unit they were never the responsibility of.

## Gates

* `dotnet test tests/Recon.Tests/Recon.Tests.csproj` — 277 passed, 0 failed (261 before M5).
* `bash tools/relink-corpus.sh` — 6 identical, 0 differing, 0 skipped.
* `bash tools/validate-corpus.sh` — ok (9 binaries, PDB oracle on), now including the relink sweep.
* `recon delink --check-schema` — 0 violations against `delink.schema.json`.
* `dotnet publish src/Recon.Cli/Recon.Cli.csproj -c Release -r linux-x64` — no warnings, and the
  published NativeAOT executable delinks, relinks and diffs the corpus itself: 16 sections,
  192851 byte(s) identical, `recon diff` score 1 with 142/142 functions exact.

## One flake found and removed

The suite passed when it was finished and failed two tests on a later run, with the output of one
test showing up in another's assertion. Cause: the tests drive the CLI in process, so they capture
`Console.Out`, and `Console.Out` is one stream for the whole test process — `DelinkTests` had no
`[Collection("cli")]`, so it ran beside `CliTests`, `BuildTests` and `CompareCliTests` and the four
of them captured each other's text. Fixed twice over: `DelinkTests` is in the `cli` collection, and
`CliRun.Run` now takes a lock around the capture, so a class that forgets the attribute cannot
corrupt another's output. Five consecutive runs afterwards: 277 passed, 0 failed, five times.

## Files

| Path | What it is |
| --- | --- |
| `src/Recon.Core/Delink/DelinkPlanner.cs` | the plan: claims, tiling, fixups, labels, providers |
| `src/Recon.Core/Delink/AsmWriter.cs` | the assembly: bytes, references, labels, typed symbols |
| `src/Recon.Core/Delink/LinkerScriptWriter.cs` | `link.ld`: every section at its address |
| `src/Recon.Core/Delink/HeaderCompletion.cs` | the header entries a linker cannot know |
| `src/Recon.Core/Delink/RelinkVerifier.cs` | the verdict: the relinked image against the original |
| `src/Recon.Core/Delink/DelinkModel.cs`, `Schemas/delink.schema.json` | the document and its contract |
| `src/Recon.Cli/Commands.cs` | `recon delink`, `recon link` |
| `tools/relink-corpus.sh` | delinks and relinks every binary of a corpus and reports the verdict |
