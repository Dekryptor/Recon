# Toolchain profiles

A profile says two things about a binary: **what made it** (which is detection) and **how that thing
behaves** (which is the ABI, the padding, the alignment, the exception model, and how the tool would
invoke the same compiler again). Both live in one TOML file in
`src/Recon.Core/Toolchains/profiles/`, and both are data: nothing in the code knows that GCC pads with
`0x90` and `0xCC` both count as padding where MSVC counts only `0xCC` except the profile that
 says so.

A project's `paths.profiles` is where it looks; `recon init` and `recon toolchain builtins` write the
shipped set into that directory so it can be edited there, and `examples/sample-project/toolchains/` is
that generated copy (gitignored — it is not the project's source). The golden inventories pin what the
tool produces with the full shipped set, which is why adding a profile can change one.

`recon toolchain list` shows what a project can see, `recon toolchain show <id>` what one profile
resolves to, `recon toolchain detect` what one binary's evidence points at, and
`recon toolchain builtins` writes the shipped set into a project directory so it can be edited there.

## Optimization levels

A profile that can compile names the optimization levels its compiler accepts, spelled the way that
compiler spells them:

```toml
[compile.optimization]
levels = ["-O0", "-O1", "-O2", "-O3", "-Os", "-Og"]   # gcc-base, and clang through it
levels = ["/Od", "/O1", "/O2", "/Ox"]                 # msvc-5, msvc-6, msvc-2008, clang-19-msvc
```

They are there for `recon permute --flags`, which tries each one and scores the result: which
`-O`/`/O` setting reproduces a binary is a question about the build, and no edit to the source can
answer it. Three things about the list are deliberate:

- **It is data, not a rule about which compiler is in front of us.** MSVC's levels are separate flags
  with a different spelling, and guessing that from `family = "msvc"` would be the kind of knowledge
  that rots.
- **`-Ofast` is not offered.** It is allowed to change floating-point results, so a rebuild at
  `-Ofast` would not prove the same program, and a permuter's candidates all have to mean the same
  thing.
- **The last level in a flag list is the one in force**, because that is the one a compiler acts on.
  A project whose `[defaults]` say `-O0` over a profile that says `-O2` is built at `-O0`; a search
  that reported `-O2` would then change a flag that was already overridden, and nothing would happen.
  A profile that declares no levels has none: `vb6-native` has no `[compile]` section at all, and
  appending a flag to a compiler this profile has never claimed to understand would be a guess.

## Inheritance

`extends = "…"` chains profiles: `msvc-2010` extends `msvc-2008`, which extends `msvc-base`. A child
carries only its differences; everything else — detection rules included — comes from its parent.
Abstract profiles (`abstract = true`) exist to be extended and are never chosen for a binary, though
their rules reach the profiles that extend them.

## Detection

Evidence is collected first, then matched against the rules, so a rule is a statement about the
evidence rather than about a file format:

| kind | where it comes from | confidence on its own |
| --- | --- | --- |
| `pdb_compiland` | the producer strings in a PDB | high |
| `dwarf_producer` | `DW_AT_producer` | high |
| `comment_section` | `.comment` | medium |
| `rich_header` | a product id in the PE Rich header | medium |
| `linker_version` | the optional header's `MajorLinkerVersion.MinorLinkerVersion` | medium |
| `import_dll` | the DLLs (or, on ELF, the `DT_NEEDED` libraries) a binary imports | low |
| `section` | a section by name | low on its own; with an import, medium |
| `vb_header` | a Visual Basic program's own `VB5!` header | a gate, not a confidence: see below |

A suggestion's confidence is the strongest kind it matched, and a single weak kind stays weak however
many times the same kind agrees — a binary that imports three Microsoft DLLs has one kind of evidence,
not three. Ties are broken by how many *different* kinds a profile matched, then by which release of
the compiler that evidence names, and only then by profile id.

That last tie-break is why a MinGW binary is attributed to GCC 14 and not to GCC 13. Both are in the
image: the program was compiled by one, and the runtime objects it links were built by the other, and
they match exactly the same kinds of evidence. Counting compilands does not decide it either — the
runtime usually has more of them. What decides it is that the release a rebuild has to reproduce is
the one that compiled the program, and that is the newer one, so the newer one wins and the other is
still reported.

One quirk worth knowing: `section` evidence is recorded for sections that are **present**. A rule such
as "`no .eh_frame`" — the rule that separates Microsoft output from GNU output — narrows which
profiles may match once some other evidence exists, but it produces no evidence of its own, so it
cannot raise a profile's confidence by itself.

## Visual Basic 6

`vb6-native` is the profile for a Visual Basic 6 program compiled to native code. It extends
`msvc-base` because VB6 compiles with the Visual C++ back end, and everything it adds was measured on
a real VB6 program — VISDATA.EXE, the data-manager sample that shipped with Visual Basic 6 — rather
than assumed:

```toml
[[detect.import_dll]]
contains = "msvbvm60"

[[detect.linker_version]]
min = "6.0"
max = "6.99"

[[detect.rich_header]]
id = 13
role = "compiler"

[[detect.rich_header]]
id = 9
role = "compiler"
```

What that file actually says is:

```
linker_version      6.0
rich_header         0x000E  masm_6.13  build 7299  count   1
rich_header         0x0009  vb6_basic  build 8041  count  36   <- the program's objects
rich_header         0x000D  vb60       build 8167  count   1   <- the link step, last record
import_dll          MSVBVM60.DLL
```

MSVBVM60.DLL is the Visual Basic 6 virtual machine, and only a Visual Basic program imports it. VB5's
runtime is MSVBVM50.DLL and the VB3/VB4 ones were `VBRUN*.DLL`, so the `60` is what makes the
identification VB6 rather than "some version of Visual Basic"; each of the others is its own profile
when a target needs one. Thirty-six Basic objects is a program's worth of forms and modules, and the
single `vb60` record is the link step — that shape, not any one number, is what identifies a Visual
Basic program.

Only the two product ids are written down, never their build numbers. Three measured builds are not a
range: a service pack moves them, and a rule that names 8041 would then quietly stop matching.

Two consequences worth stating plainly:

* The attribution is `medium`, from three kinds of evidence, and `msvc-6` is reported beside it on the
  strength of the 6.0 linker stamp alone — which is true, not a mistake: a VB6 program *is* built with
  that toolset. The more specific profile has more kinds of evidence and comes first.
* A p-code VB6 program imports the same DLL but its code is interpreted bytecode, not x86. That is
  a profile of its own now, `vb6-pcode`, and the section below is what separates them.

The DLLs that sit beside a VB6 program are a different question, and they are answered by
`msvc-5` — see the next section. One of them is still reported as unknown, and rightly so:
MSO97RT.DLL, the Office 97 runtime, stamps linker version 3.10, which is older than the 5.x line and
older than any product id this tool has measured, so nothing claims it.

## Visual Basic 6, p-code

`vb6-pcode` is the other compilation mode of the same toolchain: the program holds a stream of tokens
for MSVBVM60.DLL to interpret at run time instead of compiled x86. It extends `vb6-native`, so the ABI,
the padding, the linker and the (absent) debug information are the ones that profile describes, and what
it adds is one statement — `[[detect.vb6_header]] kind = "pcode"` — plus a `code_kind` of `pcode` for
anything that asks.

The rule is a **gate**, not a score. A profile that names a kind of Visual Basic program is suggested
for that kind only, and the profile naming the other kind is withdrawn from every piece of evidence it
matched, because the evidence itself is true and merely says less than the header does:

```
recon inspect producers "Hex Scroll.exe"           # one of the corpus's 42 p-code programs
rich_header          vb60    9782  1   prod_id=0x000D build=9782 (last entry)   vb6-pcode
linker_version       link    6.0       optional header = 6.0                     msvc-6, vb6-pcode
import_dll           runtime           MSVBVM60.DLL                             vb6-pcode
vb_header            Visual Basic      VB5! header: VB6 *, p-code (no native code in the image: aNativeCode is 0)

toolchain suggestions (evidence, not conclusions):
  vb6-pcode: medium via rich_header, linker_version, import_dll, vb_header
  msvc-6:    medium via linker_version
```

`vb6-native` is not in that list at all — it matched the first three rows exactly, and the header took
it out. `msvc-6` stays below, and that is the honest answer: the program's linker really does stamp 6.0,
which is all that suggestion is made of. What is *not* claimed any more is the native profile for a
program that has no native code in it.

Three things about that evidence are worth writing down, all of them measured over the 42 programs of
the DeForm6 corpus (VB6 SP6 on a Windows XP host, built with `CompilationType=0`):

* **A p-code program's Rich header holds exactly one record** — product id 0x000D (`vb60`), count 1. A
  native VB6 program's holds three (the assembler's 0x000E, one 0x0009 per Basic object, and the same
  0x000D). That is a real difference, and it is *not* what the profile matches on: in all 42 the
  record's build number (9782) is also the program's own `wRuntimeBuild`, but the two are not one
  number — VISDATA.EXE carries a `vb60` record at 8167 while its header says 8169 — so the rule matches
  the product id and the builds stay out of the file.
* **The runtime the program was built against is not the one on this machine.** The corpus asks for
  runtime build 9782; `msvbvm60.dll` here reports 6.00.9848, `recon pcode` prints both numbers and says
  which is which. Its instruction lengths come from the interpreter in front of it, and with this one it
  decodes all 680 procedures of the corpus exactly — 222 exactly and 458 with the compiler's own
  padding, none with two readings and none unreadable.
* **There is no `[compile]` section**, and it is not missing work: no command here can rebuild a p-code
  program, because what produced it is a Visual Basic 6 IDE on Windows and what it emits is interpreter
  input. A profile with no optimization levels is what `recon permute --flags` reports when it has no
  flags to try, which is the honest answer.

The evidence in full, and every number above, is in `docs/m7-status.md` ("A profile for a program that
was never compiled to machine code").

## Visual C++ 5.x

`msvc-5` is the toolset of Visual Studio 97. It was measured on VBA6.DLL, the Basic runtime from a
Visual Studio 98 installation, which carries **both** halves of the evidence:

```
linker_version       link         5.12   optional header MajorLinkerVersion.MinorLinkerVersion = 5.12
rich_header          masm_6.13    7299   prod_id=0x000E build=7299  count 9
rich_header          linker_5.12  8078   prod_id=0x0013 build=8078  count 1   <- the linker
rich_header          cvtres_5.0   1735   prod_id=0x0006 build=1735  count 1   <- the last record
```

Product id 0x0013 is `linker_5.12` in the table below — the Visual Studio 97 linker — and 5.12 in the
optional header is the same tool, so the two agree without either being assumed. The file also holds
970 objects with no `@comp.id` at all, and its last Rich record is the resource converter rather than
the linker, which is why a linker is matched by product id and never by position.

Three more files from the same installation — VB6.EXE, VB6IDE.DLL and MSVBVM60.DLL — stamp 5.2 and
carry **no** Rich header. That is why the profile has two rules that fire independently rather than
one that demands both: a build may carry either, and a rule requiring both would look at three real
binaries it can name and report nothing.

The files this was measured from ship inside Visual Studio 98, whose Visual Basic components were
linked with this 5.x linker while the C++ tools of the same release stamp 6.0. The two profiles do not
overlap: 5.0–5.99 against 6.0–6.99, and 0x0002/0x0010/0x0013 against 0x0004. What they can do is both
appear for one file — LINK.EXE, C2.EXE and CVPACK.EXE carry 0x0013 from import libraries built before
their own toolset shipped, so `msvc-5` is reported beside `msvc-6` there. `msvc-6` has more kinds of
evidence and comes first; the second entry is information, not a contradiction.

## Visual C++ 6.0

`msvc-6` is the toolset of Visual Studio 98: the compiler behind Visual Basic 6, and the one that
built the tools in a Visual Studio 98 installation. It was measured on LINK.EXE, C2.EXE and
CVPACK.EXE from 1998-2000, all three of which stamp linker version 6.0 and carry product id 0x0004 —
the Visual Studio 98 linker — at builds 8447, 8047 and 8168.

It exists for two reasons. The Visual Basic profile needs somewhere for the 6.0 stamp to point when a
binary is not a Visual Basic program, and these three files are why: before it, every one of them was
attributed to MinGW, because all three import MSVCRT.dll and the GCC profiles used to count that as
evidence. Every 32-bit Windows binary imports MSVCRT.dll. A rule that matches everything identifies
nothing, so `gcc-base` now names MinGW's own runtime (`libgcc*`, `libstdc++*`, `libwinpthread*`)
instead.

## The product-id table

Everything from `DanS` up to `Rich` is XORed with the key that follows `Rich` — the entries included,
not just the marker and its padding. A real file settles it: read the entries without the key, the
counts come out in the billions and not one product id is known. Each entry is
`(id << 16 | build, count)`: a product id, the low 16 bits of that tool's build number, and how many
object files that tool contributed.

The ids this tool names are the Visual Studio 97/98 ones — the era a Visual Basic 6 target lives in —
and the Visual Studio 2008 ones the fixture and the `msvc-2008` profile speak about. Their names come
from `comp_id.txt` in the public `richprint` project
(https://github.com/dishather/richprint), whose entries for these releases are observed rather than
interpolated, and every id here has also been read back out of a real 1998-2004 binary. Ids outside
the table are reported as `tool_0xNNNN` rather than guessed:

| id | tool | id | tool |
| --- | --- | --- | --- |
| `0x0000` | objects with no `@comp.id` | `0x000D` | Visual Basic 6 (`prodidVisualBasic60`) |
| `0x0001` | imported symbols | `0x000E` | MASM 6.13 |
| `0x0002` | linker 5.10 | `0x0012` | MASM 6.14 |
| `0x0004` | linker 6.0 — Visual Studio 98 | `0x0013` | linker 5.12 |
| `0x0006` | cvtres 5.0 (resources) | `0x0018` | MASM 6.15 |
| `0x0009` | VB6 Basic compiler | `0x002A` | MASM 6.20 |
| `0x000A` | `cl`, C | `0x0083` / `0x0084` | `cl` 9.0, C / C++ |
| `0x000B` | `cl`, C++ | `0x0091` | linker 9.0 — Visual Studio 2008 |
| `0x000C` | alias objects 6.0 | `0x0093` | import library 9.0 |

The last entry is **not** always the linker, which the files above show: a Visual Studio 98 binary
ends with the resource converter (`cvtres_5.0`) and a Visual Basic 6 program ends with the Basic
compiler, with no linker record anywhere in the header. Which entry is the linker is therefore read
from the product id (`RichHeader.LinkerProdIds`), never from an entry's position, and a header with no
linker record reports `LinkerEntry` as null. A profile rule that wants the linker asks for
`role = "linker"`; one that wants a compiler's objects asks for `role = "compiler"`.

