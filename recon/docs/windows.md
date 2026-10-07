# Running Recon on Windows 10

The question this answers is narrow and worth stating exactly: does this tool work on a Windows 10
machine as it stands, or only on the Linux box it has been developed and measured on?

**The tool itself: yes, and the reason is auditable rather than hopeful.** The command line is one
managed .NET program with no platform-specific code paths, so the same commands work on both. Every
claim below is checkable from the source in this repository, and the two defects that audit *did* turn
up were Windows-only, real, and are fixed (the commit that fixed them is named where they are
described).

**The gates around the tool: not as they stand.** Everything under `tools/` is `bash`, and the corpus
scripts name the tools the way a Debian box spells them. Those are the developer's harness, not the
tool, and the section [The gates, on Windows](#the-gates-on-windows) says exactly what has to change —
in practice, Git Bash or WSL, and a profile or `local.toml` entry per compiler.

## What is verified where

| | verified on |
| --- | --- |
| every command, every test, the corpus harnesses, the relink round-trip | Linux (Debian 12, .NET SDK 10.0.400), 613 tests, 0 failed |
| the Windows-specific paths named below | the source, this file's own commands, and the tests that pin them |
| anything that needs Windows to answer | **not tested anywhere yet** — [listed honestly](#what-has-not-been-tested-anywhere-yet) |

This machine has never run Windows, and nothing in this document claims otherwise. What follows is the
audit that can be done without one, plus the two things it found.

## What runs unchanged

* **No native interop.** `grep -rn "DllImport\|LibraryImport" src/` finds nothing: there is no P/Invoke
  to a Unix library, so there is nothing to port. The one place a platform is asked about at all is a
  path comparison — `OperatingSystem.IsWindows()` picks the case-insensitive comparison — and it asks
  correctly.
* **No POSIX-only APIs.** No `chmod`, no symlinks, no `/proc`, no `File.SetUnixFileMode`; paths go
  through `Path.Combine`/`Path.GetRelativePath` throughout, and the one place a separator is rewritten
  is the Ninja export, which normalizes to `/` because that is what Ninja wants on both.
* **No shell.** Tools are started through `ProcessStartInfo` with `ArgumentList` and
  `UseShellExecute = false`, so there is no `sh`, no quoting rules and no `cmd.exe` in the path. A
  compiler named `cl.exe` is started as `cl.exe`.
* **The viewer binds a socket, not a URL ACL.** `recon serve` uses a raw `TcpListener`, so it does not
  need `http.sys` reservation (`netsh http add urlacl`) the way `HttpListener` does on Windows.
* **The documents are ASCII.** `System.Text.Json`'s source-generated contexts escape everything outside
  ASCII, so a `--json` document is safe to pipe through anything. Measured: `recon strings
  inputs/VISDATA.EXE --json` over 4,009 strings is 708,847 bytes with **0** non-ASCII bytes, and the
  text views of `strings`, `inspect`, `diff`, `report` and `vb6` also print none — the scanner's
  printable set is ASCII by construction.
* **NativeAOT publishing is already AOT-clean**, which is what makes a Windows binary possible without
  installing a runtime: JSON goes through source-generated contexts, and the release publish has no
  `IL2026`/`IL3050` left. `dotnet publish -r win-x64 -c Release` produces `recon.exe` (needs the MSVC
  toolchain and Windows SDK that the SDK's AOT publish requires — that is a prerequisite, not a code
  change).

## What you have to install

* **The .NET SDK**, at the version `global.json` pins: `10.0.401` with
  `"rollForward": "latestFeature"`. Installing a different feature band means editing that file or
  passing a newer version — `rollForward` moves forward, never back.
* **A compiler and linker, per what you want to build.** Nothing in the tool *requires* one: `strings`,
  `inventory`, `diff`, `disasm`, `sigs`, `report`, `serve`, `lib`, `vb6`, `pcode`, `opcodes` and `hash`
  are analysis only and need none.
* **To relink (`recon link`), GNU `as` and `ld`** — MinGW's, from MSYS2 or a standalone MinGW-w64
  install. This is a design constraint, not an omission: the linker has to place a section at an
  address, and `link.exe` and `lld-link.exe` cannot, which is why the delink side targets GNU `as`/`ld`
  syntax. Point at them with `local.toml`:

  ```toml
  [toolchains.gcc-13-mingw]
  root = "C:/msys64/mingw32"
  cc = "C:/msys64/mingw32/bin/gcc.exe"
  link = "C:/msys64/mingw32/bin/gcc.exe"
  ```

  Paths may be written with forward slashes; `local.toml` is not committed and is the file that is
  supposed to hold machine-specific paths.

## What does not work as it stands

* **`tools/env.sh` needed the extension.** It looked for `${DOTNET_ROOT}/dotnet`, which does not exist
  on Windows — `dotnet.exe` does — so the one file the README tells you to source found nothing under
  Git Bash even with the SDK installed. Fixed in `728f933`: both names are looked for, and
  `C:\Program Files\dotnet` and `%LOCALAPPDATA%\Microsoft\dotnet` are in the search list.
* **A bare tool name in a profile never resolved.** `ToolResolver.FindOnPath` looked for `gcc`, and
  `File.Exists("...\bin\gcc")` is false on Windows while `gcc.exe` sits there; the shipped GCC and
  Clang profiles all state the Unix convention (`exe = "gcc"`, `exe = "clang"`), so on Windows every
  one of them reported its compiler missing. Fixed in `728f933`: the name and the name plus `.exe` are
  both tried, and a test pins both branches because only one of them can run on any one machine.
* **A tool that filled both pipes hung the build.** `ProcessRunner` read stderr to the end before
  starting on stdout, which deadlocks as soon as a tool writes more than a pipe buffer to the stream
  nobody is reading — and Windows' pipe buffers are smaller than Linux's. Fixed in `728f933` by
  draining both at once; the old code was put back on purpose to check the new test catches it, and it
  hangs (killed at 120 s) where the fixed code completes with 220 KB on each stream.
* **One cosmetic difference, left alone deliberately.** `recon --help` prints 39 non-ASCII bytes — the
  em dashes in the command summaries. A Windows console in a legacy code page (437/850) shows those as
  `?` unless the console is in UTF-8 mode. Nothing else does: the documents are ASCII, and so is every
  text view measured above. Setting `Console.OutputEncoding` was considered and rejected as a fix that
  could not be tested here and changes the user's console code page for the session.

## The gates, on Windows

The corpus builders and the harness scripts are `bash` and assume permission bits and Debian tool
names. Under **Git Bash (Git for Windows) or MSYS2** they run, with these caveats:

* `-x` tests on `dotnet`/`dotnet.exe` are handled now (see above); before that, `tools/env.sh` was
  silently finding nothing.
* `clang-19-macho64`, `gcc-14-elf64` and friends name tools as a Linux distribution does
  (`clang-19`, `ld64.lld-19`, `x86_64-linux-gnu-gcc`). On Windows those become `clang.exe`,
  `ld64.lld.exe`, `gcc.exe` — use the profiles that name Windows tools
  (`gcc-13-mingw`, `gcc-14-mingw`, `msvc-*`, `clang-19-msvc`) or name the compiler in `local.toml`.
* `tools/build-msvc-corpus.sh` already looks for `lld-link.exe` and friends, so it was written with
  this in mind; `tools/build-corpus.sh` (MinGW) wants `i686-w64-mingw32-gcc`, which MSYS2's
  `mingw-w64-i686-gcc` package installs under a different name — the script takes `CC` from the
  environment, which is the way around it.
* `tools/env.sh` is the only setup the project itself needs; the repository root also has a
  `bootstrap.sh` that installs the SDK, plus clang/lld/llvm and MinGW with `apt`, and builds the CLI —
  it is **for the Linux sandbox** (that is what `apt-get` and `/opt/dotnet` mean in it), and it is not
  a Windows setup script. On Windows the equivalent is: install the SDK, then set `DOTNET_ROOT`.

## Running the tests on Windows

The generated corpora are git-ignored and built by `tools/build-*.sh`, and the ELF one cannot be built
on Windows at all — its builder compiles with the *host* compiler, which on Windows produces PE. Three
test classes drive commands over `examples/elf-project`, whose inputs are those corpus binaries:
`AgentInterfaceTests`, `PermuteRunnerTests` and `StringsTests`. They carry
`[Trait("requires", "elf-corpus")]`, so a Windows run leaves them out by trait rather than watching them
fail:

```powershell
dotnet test tests\Recon.Tests\Recon.Tests.csproj --filter "requires!=elf-corpus"
```

That is what `.github/workflows/ci.yml`'s Windows job runs. Everything else runs: the PE, Mach-O and
COFF readers, the disassembler, the inventory, the compare engine, the VB6 and p-code readers, the
schemas and the toolchain resolver — over the corpus files that *are* in the checkout (`tests/corpus/macho`,
`tests/corpus/pdata-seh`) and over fixtures the tests build themselves. Without the filter, those three
classes fail on purpose, with a message naming the script that would produce the corpus.

## What has not been tested anywhere yet

Stated plainly, because a status of "ready" that hides these would be worth nothing:

* **The MSVC-family compile through the shipped profile.** `clang-19-msvc` names `bin/clang-cl.exe`
  and `bin/lld-link.exe`: right for a Windows install, and unrunnable in this sandbox, which has no
  `clang-cl`. The MSVC path is proven with an equivalent profile pointing at this machine's clang, so
  what is untested is the profile's flag set against the real tool, not the code path.
* **A PDB written by `link.exe`.** Reading is proven against clang/lld-produced PDBs; `docs/m1-status.md`
  says the same thing and has said it since M1.
* **Ninja actually running `build/build.ninja`.** Every edge is expanded back into the command line the
  runner would have executed, so the file is verified, but no machine here has had `ninja` to run it.
* **Anything Windows-specific in the tool's own behaviour** — long paths, a UNC working directory, a
  console in a legacy code page beyond the em-dash note, antivirus holding a freshly written `recon.exe`
  or the build output. None of it is expected to be a problem, and none of it has been observed on
  Windows, so none of it is claimed.

## Checking it on your own machine

```powershell
. tools\env.sh                  # Git Bash; or install the SDK yourself and skip this
$env:DOTNET_ROOT                # must point at a directory holding dotnet.exe
dotnet build src\Recon.Cli\Recon.Cli.csproj
dotnet test  tests\Recon.Tests\Recon.Tests.csproj      # needs the corpora built first (tools\build-*.sh)
dotnet publish src\Recon.Cli\Recon.Cli.csproj -c Release -r win-x64
```

Then, on a real input — a PE is the interesting case on this platform:

```powershell
recon doctor --project <dir>                # what is missing, and what would fix it
recon toolchain check gcc-13-mingw          # whether the resolved compiler is actually there
recon strings <your.exe> --encoding utf16 --limit 40
recon inventory --project <dir> --json --check-schema
recon delink --project <dir> --check-schema && recon link    # the round trip, needs GNU as/ld
```

If a run fails on Windows, the message names the file it expected and the key that sets it — that is
the pattern every toolchain failure follows, and `doctor` is the command that collects them.

## References

* `docs/toolchains.md` — the profiles, what each declares, and the evidence each detects on.
* `docs/m1-status.md` — the "Same commands on Windows and Linux" claim this file is the long form of.
* `docs/agent-interface.md` — the JSON contracts, and the exit codes a script can rely on.
* Commits `e60807e` (the schema, checksum and `--check-schema` defects), `6b59d54` (`strings`) and
  `728f933` (the three Windows-only defects above).
* `AGENTS.md` — the gates and conventions this document is written against.
