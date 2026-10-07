# Naming library code from patterns

Plan §3.1, in one sentence: patterns, "so effort isn't wasted reconstructing a standard CRT".

A released binary has no symbols, no debug information and no map file. Most of what is in it is nobody's
code: a C runtime, a static library, the pieces a compiler generated. Reconstructing those by hand is
work that buys nothing, and the tool can already say what they are — the same runtime linked into a
binary it *can* read has the same bytes, minus the bytes a link is free to change.

`recon sigs` is that mechanism. It is the FLIRT idea in the one form this tool can check: take the head
of every function something else named, mark every byte a link may change as a wildcard, and remember
the rest.

## What it does

```bash
# from a binary whose functions something else named
recon sigs build --library mingw-w64-crt --unit crt,mingw,cygming -o signatures.json

# against a binary whose functions nothing named
recon sigs apply --signatures signatures.json
recon inventory --signatures signatures.json -o build/inventory.json
```

`build` takes the head of every named, non-data function, `--unit` restricting it to the units you
name (a comma-separated list of substrings). `apply` reports what those patterns recognize; the same
file works on `recon inventory` with `--signatures`, or from `project.toml` as
`[analysis] signatures = "signatures.json"` so the whole project uses it.

## Measured on the corpus

Every number below comes from the MinGW corpus that `tools/build-corpus.sh` produces, run through the
published NativeAOT binary.

| step | result |
| --- | --- |
| `sigs build` over every named function of `sample-release.exe` | **135 patterns** from 141 named functions |
| `sigs build --unit crt,mingw,cygming` | **65 patterns**, the runtime without the program's own code |
| `sigs apply` of those 65 to `sample-stripped.exe` with its map out of reach | **24 of 107 functions named**, all of them attributed to `mingw-w64-crt` |
| the same binary without patterns | 0 named by signature, 107 functions |

The third row is the point: a binary with no symbols, no debug information, no PDB and no map, whose
`mainCRTStartup`, `atexit`, `__mingw_vfprintf`, `_fpreset` and twenty others are named anyway — and
whose `fib`, `loop_sum` and `dispatch` are not, because those are the program's own code and the
`--unit` filter kept them out of the patterns. What is left unnamed is what has to be reconstructed.

## The rules, and why each one exists

1. **Relocations first.** A relocation says outright which bytes a link will rewrite, so those become
   wildcards. Failing that — and a release build often has no relocation table — a four-byte word that
   points inside the image is an address, checked both as an RVA and as `base + rva`, because a linked
   PE stores virtual addresses and a linked ELF stores RVAs.
2. **Not every word inside the image is an address.** A small constant lands there too, so below one
   page (`0x1000`, where the headers live and no linker puts a section) a value stays a constant
   unless a relocation says otherwise. Without that floor the immediates of half the functions in a
   file get wildcarded for nothing.
3. **A pattern needs enough bytes a link cannot touch** (16 of 32 by default). A pattern of all
   wildcards matches everything, which is worse than no pattern.
4. **A pattern shared by two addresses is dropped**, with the reason in `problems[]`. It cannot say
   which function it is, so it says neither. Two names at *one* address are not that: identical-code
   folding gives one body two entries in the symbol table, and the pattern is kept.
5. **A pattern never invents a function.** It names addresses the analysis already found. The test
   `Never_invents_a_function` compares the function list with and without patterns: same addresses,
   only named.
6. **A pattern is the weakest evidence the tool accepts.** It yields to every symbol source — PDB,
   DWARF, COFF, ELF, Mach-O, map, export, config, import thunk — so it names only what nothing else
   named. Where it does name something the confidence is `medium`, `found_by` gains `signature`, and
   the library the pattern came from becomes the function's `unit`.
7. **Two patterns that disagree name nothing.** Different names matching one address is recorded as a
   `signature: …` problem rather than a guess dressed as a fact.

## Files

| file | what it holds |
| --- | --- |
| `src/Recon.Core/Signatures/SignatureModel.cs` | `FunctionSignature` (bytes, mask, name, library, unit), `SignatureDatabase`, the source-generated JSON context and `SignatureFile` |
| `src/Recon.Core/Signatures/SignatureBuilder.cs` | the pattern builder: masking, the fixed-byte floor, the ambiguity rule and the `problems[]` that explain what was dropped |
| `src/Recon.Core/Signatures/SignatureMatcher.cs` | the matcher: indexed by first byte, including patterns whose first byte is a wildcard; disputes recorded, never guessed |
| `src/Recon.Core/Schemas/signatures.schema.json` | the schema for the pattern file, so `recon schema show signatures` describes it and a hand-written one can be checked |
| `tests/Recon.Tests/SignatureTests.cs` | 19 tests: the fixture ones pin the rules byte by byte, the corpus one names runtime code in a binary nothing else named |

The wire format is a JSON file (`schema_version`, `pattern_length`, `min_fixed_bytes`, `entries[]` of
`name`/`size`/`library`/`unit`/`bytes`/`mask`), serialized through the same source-generated context as
every other document, so the NativeAOT build reads and writes it without reflection.

## Tests

`SignatureTests` (19) runs against `SyntheticPe`, whose every byte is known: the relocation over
`func_a`'s absolute address becomes four wildcards and nothing else does; `func_b`'s jump-table address
is wildcarded without any relocation saying so; two identical heads are dropped; two names at one
address are not; raising the fixed-byte floor drops patterns and says why; a pattern whose first byte
is a wildcard still matches; two patterns that disagree name nothing; the round-trip through JSON is
lossless. One test runs on the corpus and skips without it, as every corpus test does.

`CliTests` adds five cases: `sigs build` on the sample project, the file it writes, `sigs apply` naming
nothing where symbols already name everything, `sigs apply` without a pattern file being a
configuration error (exit 3), an unknown `sigs` action being a usage error (exit 2), and
`schema list` naming the new file.

Suite: **390 tests**, `tools/validate-corpus.sh` ok over 24 binaries, `tools/relink-corpus.sh`
6 identical.
