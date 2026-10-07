# Reversing & Reconstruction Tool: Full Plan

A reusable, project-agnostic toolkit for matching decompilation and binary reconstruction. **Windows first, Linux second. First target: 32-bit (x86) binaries.**

*Revision 3: adds the multi-toolchain design (section 3.1) with a support priority order, VB6 as a test case, matching changes to the roadmap, M1 scope, task list, schema and risks, and Appendix A with draft formats for `project.toml`, `local.toml` and toolchain profiles, including profile inheritance.*

Reference for similar tooling: `git.riotzed.lol/decomp/lol-decomp-mac`. The goal is to learn from it and build something more general.

---

## 1. What the reference repo does

It is a byte-matching decompilation project. The workflow is:

1. Start from the original binary plus its debug info.
2. Generate a project skeleton (sources, headers, struct layouts) from the debug info.
3. Write natural C/C++ and compile it with an exact, content-locked toolchain.
4. Compare the compiled output to the original at instruction level.
5. Report progress (instruction-exact, verified, similarity), with a progress site.
6. Delink the original into per-file pieces and relink original and rebuilt parts at fixed addresses, so a half-reconstructed image still links.

Useful ideas to keep:

- Hash-locked inputs and a `verify-inputs` command
- A `doctor` command to diagnose a broken environment
- Separate metrics with explicit scopes, where unknown stays unknown
- Delink/relink with a per-function "provider" decision
- Generated command reference and progress page

Limitations to fix:

- Tied to one game, one format (Mach-O i386), one host (Linux x86-64), and one compiler (Clang 3.3)
- Toolchain is a mix of Make, Ruby, C++ and Node
- Original binaries and SDKs live in the repository tree (2.3 GiB)
- No license or contribution policy

## 2. Goals and principles

- **Project-agnostic.** Everything specific to a program lives in a project config, never in the core.
- **Windows-native.** MSVC-built binaries are common, and running the real compiler natively beats emulating it.
- **Artifact-free.** The tool never ships or stores binaries. Inputs are located and verified by hash. A project repository contains only your own source and config.
- **Single executable** with subcommands, instead of a pile of scripts.
- **Honest metrics.** Report what is proven, what is estimated, and what is unknown separately.
- **Scriptable.** Every command has machine-readable JSON output.
- **Cross-platform core**, even when the first target is Windows.
- **Toolchain-aware.** Binaries come from many compilers, and often several in one file. Toolchain is a first-class concept in the data model, not a global setting (see 3.1).

## 3. Architecture

| # | Module | Responsibility |
| --- | --- | --- |
| 1 | Project config | `project.toml`: inputs with hashes, target format and arch, toolchain, per-file flags, source-to-address mappings |
| 2 | Loader | PE/COFF first, then ELF and Mach-O. Sections, symbols, imports, exports, relocations, debug info (PDB, DWARF, map files) |
| 3 | Disassembly and analysis | Function boundaries, cross-references, jump tables, code vs. data |
| 4 | Skeleton generator | Stub sources, headers, struct layouts and a function inventory from debug info or symbols |
| 5 | Build runner | Toolchain definitions (MSVC versions, Clang, GCC), Ninja generation, content-hash caching |
| 6 | Compare engine | Relocation-aware function diffing, per-instruction results, scoring |
| 7 | Delink / relink | Split the original into per-file objects, replace function by function, relink at fixed addresses |
| 8 | Reports | Versioned JSON schema, history snapshots, static progress site with treemap |
| 9 | Diff viewer | Local web UI served by the tool, in plain HTML and TypeScript or Web Components (no UI framework). Project state in plain files or an embedded SQLite file |
| 10 | Assist tools | Permuter that mutates source to find matches; JSON/CLI interface so scripts or agents can drive the tool |
| 11 | Doctor / verify | Check inputs, toolchains, environment |

### 3.1 Toolchain model (multiple compilers and runtimes)

The tool must handle binaries built by different toolchains, and binaries that mix several. Treat "toolchain" as its own concept, not one global setting.

**Three independent axes.** Key everything on these separately:

1. **Container format**: PE, ELF, Mach-O
2. **Instruction set / architecture**: x86, x64, ARM, and later non-native ones such as VB6 p-code
3. **Toolchain / ABI**: compiler, runtime and linker with their conventions (MSVC, GCC, Clang, MinGW, Borland, Watcom, Delphi, VB6...)

A PE32 built by MSVC and one built by MinGW share the first two axes but behave very differently on the third.

**Toolchain profiles.** A profile is a data file (TOML) read by the core, not code scattered through the tool. It describes:

- Name mangling scheme (MSVC, Itanium, Watcom, Borland)
- Calling conventions and the default for each language
- Prologue/epilogue and padding patterns, function alignment
- Exception handling model (MSVC SEH and C++ EH, Itanium/DWARF unwinding)
- RTTI and vtable layout
- Linker behavior: identical-code folding, COMDAT handling, section ordering
- Jump-table and switch patterns
- Debug info format (PDB, DWARF, map files)
- How to run it: executable path, flags, native or through Wine, and how to hash-lock the install

```toml
[toolchain.msvc-2008]
mangling = "msvc"
default_cc = "cdecl"
function_align = 16
padding = ["int3"]
eh = "msvc-seh"
debug = "pdb"
compiler = { exe = "tools/msvc2008/cl.exe", sha256 = "..." }

[toolchain.gcc-4_8-mingw]
mangling = "itanium"
default_cc = "cdecl"
function_align = 16
padding = ["nop", "lea-nop"]
eh = "dwarf2"
debug = "dwarf"
compiler = { exe = "tools/mingw48/bin/g++.exe", sha256 = "..." }
```

**Mixed toolchains in one binary.** This is the common real-world case: the main code came from one compiler, while statically linked libraries (CRT, middleware) came from others. Assignment is therefore **per compilation unit or address range**, not per binary.

- The inventory records a `toolchain` on each function or unit, plus the evidence for it.
- Compare and build settings are looked up per unit.
- Library code can be identified with a **signature database** (FLIRT-style pattern matching), so effort isn't wasted reconstructing a standard CRT.

**Automatic detection.** Combine several clues and report a confidence level:

- PE **Rich header** (MSVC and linker versions)
- ELF **`.comment`** section and DWARF **`DW_AT_producer`**
- PDB compiland records
- Characteristic idioms: prologues, padding bytes, mangling style, EH tables

Detection is a suggestion. The project config can always override it.

**Compare engine impact.** Normalization rules depend on format and toolchain. Abstract a **relocation model** (absolute, relative, GOT/PLT-style, import thunks) so PE `HIGHLOW`, ELF `R_386_32`/`PC32` and Mach-O relocations reduce to one internal form. Toolchain noise (padding choices, folded functions, section ordering) then becomes a profile setting, not a code change.

**Extensibility.** Start with built-in profiles (data files plus Rust traits) and don't invent a plugin ABI yet. If third-party support is wanted later, WASM plugins are the cleanest cross-platform option: sandboxed and identical on Windows and Linux. Native DLL/SO plugins are a poorer fit because of ABI and trust problems.

**Test case: VB6.** A VB6 program needs at least two profiles:

- **Native-code VB6** gives ordinary x86 PE32 files. The native code generator is `C2.EXE`, the Visual C++ compiler back end, so the output carries VC++ back-end idioms plus heavy calls into the VB runtime (`MSVBVM60.DLL`).
- **P-code VB6** contains bytecode interpreted by the runtime, not x86 for the compiled parts. It needs its own decoder and a non-x86 "instruction set" on axis 2.

Both link with Microsoft's linker, so a signature database for runtime calls helps.

**Support priority after MSVC.**

1. **MinGW/GCC, 32-bit PE.** It changes only the toolchain axis (container and instruction set stay PE32 and x86), so it is the cheapest way to prove the profile system works. GCC versions are freely available, so exact matching is realistic. It exercises Itanium mangling, DWARF instead of PDB, a different exception model and different padding idioms, and most of it carries over to ELF later.
2. **Clang.** Largely a variation on the GCC work, and it covers Mach-O targets such as the reference repo.
3. **VB6 native-code.** Ordinary x86 PE32 from the VC++ back end, so it is cheap to add once MSVC is solid. P-code stays in M7.
4. **Borland/Delphi and Watcom.** They need their own mangling and RTTI handling, and obtaining and licensing the original compilers is harder, so exact matching may not be possible.

The order changes only if a specific target binary needs a different toolchain first. In that case, start with whatever built it.

**Caveat.** Byte-exact matching only works if you can run the original compiler version. For GCC and Clang that is usually possible. For old proprietary compilers, availability and licensing are the limit, and the tool can only do approximate matching there.

## 4. Language and libraries

**Rust** for the core: single static executable on Windows and Linux, mature crates for binary formats and x86, and most existing decomp tooling lives there. A C# NativeAOT build is a reasonable alternative.

Likely crates (as far as I know, verify when you start):

- `object` or `goblin` for PE/ELF/Mach-O parsing
- `iced-x86` for disassembly (pure Rust, no C dependency)
- `pdb` for PDB reading
- `serde` / `serde_json` for output
- `clap` for the CLI

## 5. Prior art to study or reuse

- **objdiff**: relocation-aware diffing and diff UI
- **decomp-toolkit** and **splat**: splitting binaries into per-file units
- **asm-differ** and **decomp-permuter**: diffing and automatic matching
- **decomp.dev**: progress tracking and treemap

## 6. Roadmap

| Milestone | Scope |
| --- | --- |
| **M1** | Foundation: config, PE32 loader, disassembly, function inventory, xrefs, producer/toolchain detection (MSVC profile only) |
| **M2** | Compare engine: relocation normalization, per-function scoring, JSON output, CLI diff |
| **M3** | Build orchestration: toolchain definitions, per-unit toolchain lookup, Ninja generation, caching; MSVC and MinGW/GCC (32-bit PE) profiles |
| **M4** | Diff viewer and progress reports |
| **M5** | Delink/relink |
| **M6** | Portability: ELF, then Mach-O; x64 and ARM; GCC on ELF, Clang and VB6 native-code profiles; library signature database |
| **M7** | Permuter, plugin API (WASM), agent-friendly interface; non-x86 instruction sets (VB6 p-code); Borland, Delphi and Watcom profiles |

---

## 7. Milestone 1: "Load, understand, inventory"

**Goal:** given a PE32 binary, produce a reliable, queryable inventory of its functions, data and relocations. The compare engine later builds on this.

**Out of scope:** building sources, diffing, UI, delinking.

### 7.1 Why 32-bit x86 shapes the design

- **Calling conventions** (`cdecl`, `stdcall`, `fastcall`, `thiscall`) change how arguments appear in code. Record a guessed convention per function.
- **Absolute addresses** are baked into code and fixed up via `.reloc`. The loader must track these, because the compare engine must tell a relocation from a real difference.
- **Imports** go through the IAT, so calls often look like `call [mem]` or jump through thunks.
- **MSVC quirks:** identical-code folding (several functions sharing one body), `int3`/`nop` padding between functions, jump tables inside code, SEH frames. Each can break naive boundary detection.
- **Debug info** is most likely a PDB. A linker map file is the fallback.
- **Mixed toolchains** are likely even in an "MSVC" binary, since statically linked libraries may come from other compilers or versions. Record evidence per function from the start.

### 7.2 Deliverables

1. `project.toml` loader with hash-locked inputs
2. PE32 loader: sections, imports, exports, relocations, entry point
3. Debug-info reader: PDB first, map file second
4. x86 disassembly of all code regions
5. Function boundary detection combining debug info, exports, call targets and prologue patterns, with a confidence level per function
6. Cross-reference table (code to code, code to data, data to code)
7. JSON inventory plus human-readable `inspect` commands
8. Producer detection: Rich header, PDB compilands and similar evidence recorded per binary and per function
9. `ToolchainProfile` data structure and loader, with only the MSVC profile implemented

### 7.3 CLI

```
tool init                                  # create project.toml
tool verify                                # check input hashes and environment
tool inspect sections|imports|relocs|functions|xrefs
tool inventory                             # write build/inventory.json
tool disasm <addr|name>
```

### 7.4 Config sketch

```toml
[project]
name = "sample"

[target]
format = "pe32"
arch   = "x86"

[[input]]
role   = "original"
path   = "inputs/sample.exe"
sha256 = "..."

[[input]]
role   = "debug"
path   = "inputs/sample.pdb"
sha256 = "..."
```

### 7.5 Test plan

- Write a small C/C++ program you control: a few dozen functions using each calling convention, a `switch` with a jump table, a virtual class, and some SEH.
- Compile with MSVC in Debug and Release and generate PDBs.
- Compare the inventory against the PDB symbol list. Debug builds should match about 100%. The Release build exercises the heuristics.
- Keep golden JSON files so regressions are caught automatically.
- Once the MSVC path works, add a detection-only test with a binary from a second toolchain (for example MinGW GCC), and optionally a native-code VB6 sample, to check that producer detection does not report everything as plain MSVC.
- Run the same tests on Windows and Linux.

### 7.6 Definition of done

- Every function in the PDB appears in the inventory at the correct address and size
- Relocations are recorded and linked to the instructions that use them
- Jump tables are not misread as code
- The same commands run on Windows and Linux

---

## 8. Milestone 1 task breakdown

| # | Task | Acceptance |
| --- | --- | --- |
| T1 | Cargo workspace, CLI skeleton, logging, error type | `tool --help` lists all subcommands |
| T2 | `project.toml` parser and `init` | Round-trips a config; validates required fields |
| T3 | `verify`: hash inputs, report mismatches | Clear pass/fail with JSON output |
| T4 | PE32 header and section parsing | `inspect sections` matches a reference tool on the sample |
| T5 | Imports, exports, entry point | `inspect imports` lists every IAT entry |
| T6 | Base relocation parsing | Every `HIGHLOW` entry recorded with its target |
| T7 | Disassembler integration and `disasm` | Disassembles any address; stops cleanly at section ends |
| T8 | PDB reader: public and procedure symbols, sizes | Symbol count matches PDB dump on the sample |
| T9 | Map-file fallback reader | Same inventory shape produced without a PDB |
| T10 | Function detection and confidence scoring | All PDB functions found in Debug; heuristic results labeled in Release |
| T11 | Jump-table detection and ICF handling | Jump tables excluded from code; folded functions share one body with aliases |
| T12 | Xref builder, tied to relocations | Every relocation target appears as an xref |
| T13 | Inventory writer and schema validation | `inventory` output validates against the schema |
| T14 | Test corpus and golden tests, CI on Windows and Linux | Green on both OSes |
| T15 | Producer detection: Rich header parsing and PDB compiland records | `inspect producers` lists tool and version evidence; MSVC test binary reports the expected compiler and linker |
| T16 | `ToolchainProfile` struct, TOML loader, MSVC profile | Profile loads and validates; each function gets a `toolchain` field with confidence and evidence |

Suggested order: T1 to T3, T4 to T6, T7, T8 and T9, T10 to T12, T15 and T16, T13, T14.

---

## 9. Draft inventory JSON schema (v0)

This is the contract the compare engine and reports build on. All addresses are **RVAs** (relative to the image base) as unsigned integers unless a field says `va`.

```json
{
  "schema_version": "0.1",
  "binary": {
    "format": "pe32",
    "arch": "x86",
    "sha256": "...",
    "image_base": 4194304,
    "entry_rva": 4096,
    "debug": { "kind": "pdb", "sha256": "..." },
    "producers": [
      { "kind": "rich_header", "tool": "linker", "version": "6.0.8168", "count": 12 },
      { "kind": "pdb_compiland", "tool": "cl", "version": "...", "unit": "game.obj" }
    ]
  },
  "sections": [
    { "name": ".text", "rva": 4096, "size": 32768, "flags": ["code", "exec", "read"] }
  ],
  "imports": [
    { "dll": "KERNEL32.dll", "name": "GetTickCount", "iat_rva": 53248 }
  ],
  "exports": [
    { "name": "Foo", "rva": 4608 }
  ],
  "relocations": [
    { "rva": 4660, "kind": "HIGHLOW", "target_rva": 45056, "in_function": "f_001200" }
  ],
  "functions": [
    {
      "id": "f_001200",
      "name": "?Update@Game@@QAEXM@Z",
      "demangled": "Game::Update(float)",
      "ranges": [ { "rva": 4608, "size": 128 } ],
      "section": ".text",
      "isa": "x86",
      "toolchain": { "id": "msvc-2008", "confidence": "medium", "evidence": ["rich_header", "pdb_compiland"] },
      "found_by": ["pdb", "call_target"],
      "confidence": "high",
      "calling_convention": { "value": "thiscall", "confidence": "medium" },
      "flags": ["has_seh", "has_jump_table"],
      "aliases": []
    }
  ],
  "data": [
    { "rva": 45056, "size": 16, "name": "g_state", "kind": "data" }
  ],
  "jump_tables": [
    { "rva": 8192, "entries": 6, "owner": "f_001200" }
  ],
  "xrefs": [
    { "from_rva": 4640, "to_rva": 4096, "kind": "call", "via_reloc": false }
  ]
}
```

Field notes:

- `ranges` is an array because a function can be non-contiguous (for example after optimization).
- `found_by` lists every source that identified the function: `pdb`, `export`, `call_target`, `prologue`, `map`.
- `confidence` is `high`, `medium` or `low` and reflects agreement between sources.
- `aliases` holds the names of functions folded into the same body by identical-code folding.
- `xrefs.kind` is one of `call`, `jump`, `data_read`, `data_write`, `data_ref`, `code_ref`.
- Unknown or unresolved information is recorded explicitly, never omitted silently.
- `binary.producers` holds raw evidence of which tools touched the binary (Rich header entries, PDB compilands, `.comment` or `DW_AT_producer` later). It is evidence, not a conclusion.
- `toolchain` on a function is the conclusion drawn from that evidence, with a confidence level and the evidence kinds used. It can be overridden in `project.toml`.
- `isa` is the instruction set of the function. It is `x86` for now. Values such as `vb6-pcode` can be added later without changing the rest of the schema.

---

## 10. Risks and open questions

| Risk | Mitigation |
| --- | --- |
| Function boundary detection is unreliable in optimized builds | Keep confidence levels; prefer debug info; show unknowns instead of guessing |
| Exactly matching an old compiler needs that compiler | Toolchain definitions with hash-locked installs; support running on Windows natively |
| Scope creep (UI, permuter) before the core is solid | Hard milestone gates; M1 and M2 ship before any UI |
| Schema churn breaks later modules | Version the schema from day one; golden tests |
| Toolchain detection is wrong or ambiguous, especially in mixed binaries | Store raw evidence separately from the conclusion; confidence levels; allow config overrides |
| Non-x86 code (VB6 p-code) does not fit the x86 analysis path | Keep `isa` as its own axis in the data model; defer p-code support to M7 |
| Original compilers are unavailable or unlicensed | Support approximate matching and report it separately from exact matches |

Open questions:

1. Which MSVC versions should be supported first?
2. Should the compare engine live in the same binary as the loader, or be a library with several front ends?
3. How much of the progress site should be generated by the tool versus left to a project template?
4. Toolchain order after MSVC is set (MinGW/GCC, Clang, VB6 native-code, then Borland/Delphi/Watcom; see 3.1). Revisit if a specific target binary needs a different one first.

## 11. Hygiene and legal note

The tool should never include or redistribute original binaries, SDKs or captures. Projects hold their own source and config; inputs are provided by the user locally and verified by hash. Decide a license for the tool early, and document it before accepting contributions.

---

## Appendix A. Format specs (v0 draft)

Concrete formats for `project.toml`, `local.toml` and toolchain profiles. They are TOML, strictly validated, and versioned from day one.

### A.1 Design rules

- **Committed vs. local.** `project.toml` is portable and committed. Anything machine-specific (where inputs and compilers live on disk) goes in `local.toml`, which is gitignored. This keeps binaries and compiler installs out of the repository by construction.
- **Strict parsing.** Unknown keys are errors, except keys prefixed with `x-`, which are ignored and reserved for extensions.
- **Versioned.** Every file has `schema_version`. The tool refuses newer major versions and offers a `migrate` command for older ones.
- **Paths** are relative to the project root and use `/` on every OS. Absolute paths are only allowed in `local.toml`.
- **Addresses** are RVAs unless a key says `va`. TOML hex integers (`0x1200`) are allowed.
- **Hashes** are lowercase SHA-256 hex.

### A.2 `project.toml`

```toml
schema_version = 1

[project]
name = "sample"
description = "Reconstruction of sample.exe"

[target]
format = "pe32"            # pe32 | pe64 | elf32 | elf64 | macho32 | macho64
arch = "x86"               # x86 | x64 | arm | arm64
isa = "x86"                # x86 now; later e.g. "vb6-pcode"
default_toolchain = "msvc-2008"

[[input]]
id = "main"
role = "original"          # original | debug | map | reference
file = "sample.exe"        # file name only; the directory comes from local.toml
sha256 = "..."

[[input]]
id = "main-pdb"
role = "debug"
file = "sample.pdb"
sha256 = "..."
for = "main"               # which input this debug info belongs to

[paths]
source = "src"
include = ["include"]
build = "build"
profiles = ["toolchains"]  # directories searched for profile files

[defaults]                 # applied to every unit unless overridden
flags = ["/O2"]
defines = ["NDEBUG"]

[analysis]
min_function_confidence = "low"        # low | medium | high
extra_entry_points = [0x1000]
no_return = ["?Fatal@@YAXPBD@Z"]

[[analysis.data_range]]    # manual override: this range is data, not code
rva = 0x2000
size = 0x40
kind = "jump_table"

[[unit]]
name = "game/update"
source = "src/game/update.cpp"
toolchain = "msvc-2008"    # optional; defaults to target.default_toolchain
flags = ["/GS-"]           # appended after [defaults]
defines = ["GAME_BUILD"]
provider = "rebuilt"       # rebuilt | original
status = "wip"             # not_started | wip | matched | approx | library | skip
covers = [
  { symbol = "?Update@Game@@QAEXM@Z" },
  { rva = 0x1200, size = 0x80 },
]

[report]
output = "build/report"
history = true
```

| Table | Purpose | Required |
| --- | --- | --- |
| `project` | Name and description | yes |
| `target` | Format, architecture, instruction set, default toolchain | yes |
| `input` (array) | Original binary, debug info, map files, with hashes | at least one `original` |
| `paths` | Source, include, build and profile directories | no (defaults shown above) |
| `defaults` | Flags and defines applied to all units | no |
| `analysis` | Overrides for function detection and data ranges | no |
| `unit` (array) | Compilation units: source file, toolchain, flags, what it covers, status | no until M3 |
| `report` | Output location and history | no |

**Flag merge order** (later wins): profile defaults, then `[defaults]`, then the unit's own `flags`.

**Toolchain resolution:** `unit.toolchain`, else `target.default_toolchain`. Detected toolchains (section 3.1) are suggestions shown by `tool toolchain detect`. They never override the config silently.

**Status meanings:** `matched` is byte-exact and verified, `approx` compiles and is close but not exact, `library` is identified third-party or runtime code not being reconstructed, `skip` is intentionally excluded, and `provider = "original"` keeps the original bytes in the relinked image.

### A.3 `local.toml` (not committed)

```toml
schema_version = 1

[inputs]
dir = "C:/re/inputs/sample"          # where the files named in project.toml are found

[toolchain.msvc-2008]
root = "C:/tools/msvc2008"           # where this compiler is installed on this machine
# optional extra environment for the compiler
env = { SDK = "C:/tools/winsdk61" }

[toolchain.gcc-4_8-mingw]
root = "C:/tools/mingw48"
wine = false                         # run through Wine (Linux hosts) instead of natively
```

`tool verify` checks that every input in `project.toml` exists under `inputs.dir` and matches its hash. `tool toolchain check <id>` verifies the install under `root` against the profile's hash list.

### A.4 Toolchain profile (`toolchains/msvc-2008.toml`)

```toml
schema_version = 1
id = "msvc-2008"
display_name = "Microsoft Visual C++ 2008"
family = "msvc"                      # groups profiles that share behavior and detection

[[targets]]
format = "pe32"
arch = "x86"

[abi]
mangling = "msvc"                    # msvc | itanium | watcom | borland | none
default_cc = "cdecl"
member_cc = "thiscall"
pointer_size = 4

[codegen]
function_align = 16
padding_bytes = [0xCC]               # fill between functions
prologue_hints = ["push ebp; mov ebp, esp", "sub esp, imm"]

[linker]
identical_code_folding = "possible"  # never | possible | always
comdat = true

[eh]
model = "msvc-seh"                   # msvc-seh | dwarf2 | sjlj | none

[debug]
format = "pdb"                       # pdb | dwarf | map | none

[[detect.rich_header]]               # illustrative values; fill from a compiler database
product = "cl"
build_min = 21022
build_max = 30729

[detect.pdb_compiland]
producer_contains = "Microsoft (R) Optimizing Compiler"

[compile]
exe = "bin/cl.exe"                   # relative to the install root
default_flags = ["/c", "/nologo", "/Zi"]
include_flag = "/I{path}"
define_flag = "/D{name}"
output_flag = "/Fo{obj}"
env = { INCLUDE = "{root}/include;{root}/atlmfc/include" }

[link]
exe = "bin/link.exe"
default_flags = ["/nologo", "/DEBUG"]

[[install.files]]                    # used by `tool toolchain check`
path = "bin/cl.exe"
sha256 = "..."
```

A second profile only needs to change the relevant tables. For example `toolchains/gcc-4_8-mingw.toml`:

```toml
schema_version = 1
id = "gcc-4_8-mingw"
display_name = "GCC 4.8 (MinGW)"
family = "gcc"

[[targets]]
format = "pe32"
arch = "x86"

[abi]
mangling = "itanium"
default_cc = "cdecl"
member_cc = "thiscall"
pointer_size = 4

[codegen]
function_align = 16
padding_bytes = [0x90]
prologue_hints = ["push ebp; mov ebp, esp"]

[linker]
identical_code_folding = "never"
comdat = true

[eh]
model = "dwarf2"

[debug]
format = "dwarf"

[detect.comment_section]
contains = "GCC: (GNU) 4.8"

[compile]
exe = "bin/g++.exe"
default_flags = ["-c", "-g"]
include_flag = "-I{path}"
define_flag = "-D{name}"
output_flag = "-o {obj}"
```

| Profile table | Purpose |
| --- | --- |
| `targets` | Which format and architecture combinations the profile applies to |
| `abi` | Mangling, calling conventions, pointer size |
| `codegen` | Alignment, padding bytes, prologue idioms used by function detection |
| `linker` | Folding and COMDAT behavior that affects compare and detection |
| `eh` | Exception-handling model |
| `debug` | Which debug-info reader to use |
| `detect.*` | Evidence rules for automatic toolchain detection |
| `compile`, `link` | How to invoke the real compiler and linker (M3) |
| `install.files` | Files and hashes used to verify an install |

### A.4.1 Profile inheritance

A profile may build on another with `extends`, so near-identical compiler versions don't duplicate whole files.

```toml
# toolchains/msvc-2010.toml
schema_version = 1
id = "msvc-2010"
display_name = "Microsoft Visual C++ 2010"
extends = "msvc-2008"

[[detect.rich_header]]               # illustrative values
product = "cl"
build_min = 30319
build_max = 40219

[[install.files]]
path = "bin/cl.exe"
sha256 = "..."
```

Rules:

- **Single inheritance only**, with a maximum chain depth of 4. Cycles and unknown parents are errors.
- **Tables merge key by key.** A child that sets `[codegen] function_align` keeps the parent's other `codegen` keys.
- **Arrays replace, never concatenate.** This applies to `targets`, `detect.*`, `install.files`, `default_flags` and similar lists. Compiler versions differ in exactly these places, and silent concatenation would produce stale detection rules or hash lists.
- **`id` and `display_name` are required** in every child and must be unique.
- **`abstract = true`** marks a base profile that exists only to be extended. It cannot be used as a unit or default toolchain.
- **Inheritance is for variants of one family.** Across families (MSVC vs. GCC) write separate profiles; the shared `family` key already groups them for detection.
- `tool toolchain show <id> --resolved` prints the fully merged profile, so you can see exactly what a unit will use.

### A.5 Rust-side sketch

```rust
pub struct ToolchainProfile {
    pub schema_version: u32,
    pub id: String,
    pub display_name: String,
    pub family: String,
    pub extends: Option<String>,        // resolved into a merged profile at load time
    pub is_abstract: bool,
    pub targets: Vec<TargetSpec>,
    pub abi: Abi,
    pub codegen: Codegen,
    pub linker: LinkerBehavior,
    pub eh: EhModel,
    pub debug: DebugFormat,
    pub detect: DetectRules,
    pub compile: Option<CompileSpec>,   // None until M3
    pub link: Option<LinkSpec>,
    pub install: Option<InstallSpec>,
}

pub struct Evidence { pub kind: EvidenceKind, pub detail: String }

pub trait ToolchainDetector {
    /// Returns (profile id, confidence, evidence) candidates for one function or unit.
    fn detect(&self, binary: &Binary, unit: UnitRef) -> Vec<(String, Confidence, Vec<Evidence>)>;
}
```

M1 only needs parsing, validation and `detect`. The `compile`, `link` and `install` parts are parsed but unused until M3.

### A.6 Validation and CLI additions

```
tool validate                     # check project.toml, local.toml and profiles against their schemas
tool toolchain list               # profiles found in paths.profiles
tool toolchain detect             # evidence-based suggestions per unit or function
tool toolchain show <id> --resolved   # print the merged profile after inheritance
tool toolchain check <id>         # verify the install under local.toml against the profile
tool migrate                      # upgrade older schema_version files
```

Errors should name the file, key path and line, for example `project.toml:18 unit[2].toolchain: unknown profile "msvc-2010" (known: msvc-2008, gcc-4_8-mingw)`.

### A.7 Open decisions for these formats

1. ~~Profile inheritance~~ Decided: profiles may use `extends` (see A.4.1). Revisit only if multiple inheritance turns out to be needed.
2. Should `unit.covers` allow globs on symbol names, or only exact symbols and ranges?
3. Where should the compiler database for Rich header values live: inside the profiles, or as a separate shared data file?