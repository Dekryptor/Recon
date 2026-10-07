# Milestone 3 — status (build orchestration)

**Goal (plan §6, M3):** *build orchestration: toolchain definitions, per-unit toolchain lookup, Ninja
generation, caching; MSVC and MinGW/GCC (32-bit PE) profiles.* In other words: `recon` stops being
only a reader of binaries and becomes the thing that runs the reconstruction's own build, so that
`recon diff` can be the automatic check on every change (plan §7.3).

Everything below was produced by running the tool. The commands are given so every number can be
re-checked.

## What is implemented

| Part | Code | What it does |
| --- | --- | --- |
| Tool resolution | `Build/ToolResolver.cs` | Turns a profile into the commands *this machine* runs: `compile.exe` relative to `local.toml`'s `root`, overridable by `cc`/`link`, with `wine` in front when the install says so and `{root}` substituted in the profile's environment |
| The plan | `Build/BuildPlanner.cs` | One compile command per unit plus one link; per-unit toolchain (`unit.toolchain`, else `target.default_toolchain`); a content-hash cache key per unit |
| The cache | `Build/BuildManifestStore.cs`, `build/build.json` | The manifest is both the report and the cache: the next run compares every unit's key and only recompiles what changed |
| Depfiles | `Build/DepFile.cs` | Reads the Make-syntax files GCC writes, so a changed header invalidates only the units that included it |
| The runner | `Build/BuildRunner.cs` | Compiles in parallel (`--jobs`), keeps going or not (`--keep-going`), records the tail of each compiler's output, links when something changed |
| Ninja export | `Build/NinjaWriter.cs` | Writes `build/build.ninja`: the same commands, with `deps = gcc` where the toolchain has a depfile |
| The command | `Commands.Build()` | `recon build [--unit] [--jobs] [--dry-run] [--force] [--keep-going] [--no-ninja] [--check-schema] [--json]` |
| Environment check | `Doctor.CheckBuildTools` | `recon doctor` reports, per toolchain the units ask for, whether the compiler and linker are actually there |
| The document | `build/build.json`, `Schemas/build.schema.json` | Schema `0.1`, validated by `--check-schema` and by a test |

## How a unit is built

`project.toml` names one unit per source file:

```toml
[[unit]]
name     = "sample"
source   = "src/sample.c"
toolchain = "gcc-13-mingw"     # optional; defaults to target.default_toolchain
flags    = ["-fno-inline"]      # appended after [defaults]
```

`local.toml` says where that toolchain lives here. Because Linux has no `C:/tools`, the machine gets
the last word: `cc` and `link` override the profile's own executables, which is how this repository
points a MinGW profile at `/usr/bin/i686-w64-mingw32-gcc` without a second profile for the same GCC:

```toml
[toolchain.gcc-13-mingw]
root = "/usr"
cc   = "/usr/bin/i686-w64-mingw32-gcc"
link = "/usr/bin/i686-w64-mingw32-gcc"
```

**Flag merge order** (plan A.2, later wins): the profile's `compile.default_flags`, then
`[defaults].flags`, then the unit's own `flags`. Defines and include directories go through the
profile's `define_flag` / `include_flag` templates. Objects land in `build/obj/<unit name>.o`
(`.obj` for the `msvc` family), with path separators folded into the file name.

## The cache

A unit is recompiled when any of these change, and not otherwise:

| Input | How it is measured |
| --- | --- |
| The source | SHA-256 of `unit.source` |
| Every header it includes | The depfile the compiler wrote (`-MMD -MF {dep}`), hashed; a toolchain with no `depfile_flag` falls back to hashing its include directories |
| The flags and defines | The merged list, hashed in order |
| The toolchain | The profile's own fingerprint (`ToolResolver.Fingerprint`: tool paths, flags, templates, environment) **and** the identity of the installed compiler (path, size, mtime), so an upgrade rebuilds |
| The environment | The profile's `compile.env`, with `{root}` substituted |

The key is a hash over those parts, and the parts themselves are written to the manifest as
`units[].inputs`, so "why did this rebuild?" is answerable from the file. `touch`ing a source does
**not** trigger a rebuild: the cache keys are content hashes, not timestamps.

A link step is skipped (recorded as `link: cached`) when nothing was compiled and the image is still
there — because a linker stamps its output, re-linking would produce a different file without
producing a different program.

## Evidence

### A build, a rebuild and a rebuild after a change

`sample.c` from the test corpus, with the original `sample-release.exe` as the project's input
(MinGW GCC 13, profile `gcc-13-mingw`):

```
$ recon build --check-schema
build sample: 1 unit(s), output build/sample.exe
  toolchain gcc-13-mingw     cc /usr/bin/i686-w64-mingw32-gcc (ok)
  CC     src/sample.c (gcc-13-mingw -> build/obj/sample.o)
1 unit(s): 1 compiled, 0 cached in 77 ms; link linked; build/sample.exe (235676 bytes, sha256 4bbe35ef6805)
  wrote build/build.json
  wrote build/build.ninja
build manifest matches schema 0.1

$ recon build                                   # nothing changed
1 unit(s): 0 compiled, 1 cached in 0 ms; link cached; build/sample.exe (235676 bytes, sha256 4bbe35ef6805)

$ recon build                                   # src/sample.c gained one function
  CC     src/sample.c (gcc-13-mingw -> build/obj/sample.o)
1 unit(s): 1 compiled, 0 cached in 77 ms; link linked; build/sample.exe (235707 bytes, sha256 0e5a0bfc8630)
```

### The rebuilt image against the original

The build before that last edit was byte-comparable to the original, and the comparison says so:

```
$ recon diff orig/sample-release.exe build/sample.exe --summary --min-score 1.0
diff left 84668014ea9b vs right 4bbe35ef6805
  functions    142 left, 142 right, 142 matched (142 exact, 0 changed), 0 folded, 0 only left, 0 only right
  instructions 8076 left, 8076 right, 8076 equal
  score        1
  matched by   body 1, name 141
  data         0 differing symbol(s): 146 identical, 0 changed, 0 only left, 0 only right
  references   1034 named, 2324 unnamed
  model        threshold 0.8, ignore padding true, profile gcc-13-mingw
```

That is M3's point: the build ran, the comparison ran, and the answer is a number a user can act on.

### What the same comparison says after the source changed

Adding one function to the source moves every absolute address in the image, and 73 of the 142
functions then report differences — every one of them an address constant no relocation covers and
no named symbol resolves:

```
$ recon diff orig/sample-release.exe build/sample.exe --summary
  functions    142 left, 143 right, 142 matched (69 exact, 73 changed), 0 folded, 0 only left, 1 only right
  instructions 8076 left, 8078 right, 6991 equal
  score        0.8654
```

```json
{ "kind": "addressing",
  "left_text":  "mov dword ptr [esp],4019B0h",
  "right_text": "mov dword ptr [esp],4019C0h" }
```

This is the tool being right rather than being quiet: the code is the same, the addresses moved, and
the difference is reported in the one place it cannot be attributed to a symbol. Closing it
completely is the delink/relink milestone (M5), not a comparison tweak.

### The other toolchain family

The shipped `clang-19-msvc` profile names `bin/clang-cl.exe`, which a Linux box does not have, so an
MSVC-family build is shown with the profile a user with another install would write: same family,
this machine's binaries. `family = "msvc"` is what changes the object extension and the output flag:

```
$ recon build --check-schema
build sample: 1 unit(s), output build/sample.exe
  toolchain msvc-test        cc /usr/bin/clang-19 (ok, no install root in local.toml)
  CC     src/main.c (msvc-test -> build/obj/main.obj)
1 unit(s): 1 compiled, 0 cached in 249 ms; link linked; build/sample.exe (1024 bytes, sha256 db798467ffdc)
build manifest matches schema 0.1
```

`.obj`, no depfile (clang writes none), `/out:` for the linker — and the Ninja file follows: with no
`depfile_flag` in the profile, the `cc` rule has no `deps = gcc` line at all.

### The Ninja export

`build/build.ninja` for the MinGW project, verbatim:

```ninja
rule cc
  command = $cc $flags $in $out_flag $dep_flag
  description = CC $in
  deps = gcc
  depfile = $dep_file

rule link
  command = $link $flags $in $out_flag
  description = LINK $out

build obj/sample.o: cc ../src/sample.c
  cc = /usr/bin/i686-w64-mingw32-gcc
  flags = -c -g -O2 -I../include
  out_flag = -o obj/sample.o
  dep_flag = -MMD -MF obj/sample.d
  dep_file = obj/sample.d

build sample.exe: link obj/sample.o
  link = /usr/bin/i686-w64-mingw32-gcc
  flags = -Wl,--no-insert-timestamp
  out_flag = -o sample.exe

default sample.exe
```

Ninja is not installed in this environment, so the export is checked the other way round: a test
expands each edge back into a command line and compares it with the one `recon build` would run.
Paths in the file are relative to its own directory, so the file survives the tree being moved.

### Environment check

```
$ recon doctor
OK      project               /home/user/m3demo/project.toml
OK      input main            SHA-256 matches (sample-release.exe)
OK      toolchain gcc-13-mingw  gcc-13-mingw at /usr
OK      build gcc-13-mingw    1 unit(s): sample: cc /usr/bin/i686-w64-mingw32-gcc, link /usr/bin/i686-w64-mingw32-gcc
```

The build's own inputs are checked by `verify` too, so a project that cannot be built does not
verify clean:

```
$ recon verify
input  role      file   sha256   note
main   original  found  hash ok
unit    source        file   toolchain     note
sample  src/sample.c  found  gcc-13-mingw  buildable
verify: ok
```

Delete `src/sample.c` and the same command exits 1 with `source not found: src/sample.c`.

### Checks that run on every change

* `dotnet test tests/Recon.Tests/Recon.Tests.csproj` — **261 tests** (the suite as it stood at M3), of which 38 are M3's: 36 in
  `BuildTests` (depfile parsing, tool resolution, flag order, planning problems, cache hits and
  misses, the Ninja export edge by edge, the command end to end, `verify` over the units' inputs) and
  2 in `ToolchainProfileTests` (the new profile keys, and key-by-key inheritance of `[compile]`).
* The end-to-end tests skip when `i686-w64-mingw32-gcc`, `clang-19` or `lld-link-19` is missing, so a
  fresh checkout without a cross compiler still runs green.
* `recon build --check-schema` validates the manifest against `Schemas/build.schema.json`; a test
  does the same.

## Known gaps and honest unknowns

* **Ninja itself is not run here.** The generated file is verified by expanding its edges back into
  command lines, not by executing it, because this sandbox has no `ninja` binary. A machine with
  ninja installed can run `ninja -C build` and should get the same image; that is worth one run when
  one is available.
* **The MSVC-family compile is not the shipped profile's.** `clang-19-msvc` names `clang-cl.exe` and
  `cl`-style flags, which is right for a Windows install and unrunnable here. The MSVC path is
  proven with an equivalent profile pointing at this machine's clang and lld-link.
* **Link flags are the profile's, plus `[defaults].link_flags`.** There is no per-unit link input
  yet, and a unit with `provider = "original"` is left out of the link — its bytes come from the
  original image at relink time, which is M5.
* **A profile without `[compile]` or `[link]` is a plan problem, not a crash.** Both are reported in
  `problems[]` and the command exits 1.
* **Parallel compiles share no state**, but two units may still write the same output through a
  dependency they both include; that is a source-layout question, not a tool one, and the plan does
  not ask for a fix.
