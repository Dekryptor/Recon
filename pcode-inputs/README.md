# The p-code corpus

42 Visual Basic 6 programs, each compiled **to p-code** by the Visual Basic 6 IDE, with the sources they
were built from. This is the specimen the p-code work had been waiting for: a native VB6 build leaves
the method table empty, so nothing on this machine could be decoded until a p-code build turned up.
42 of them are here, and the sources beside them mean a decode can be checked against what the project
should contain rather than against itself.

## Where they came from

| | |
| --- | --- |
| Repository | <https://github.com/Stiven-Gjekaj/DeForm6> |
| Directory | `corpus-pcode/` (with `corpus/` as the sources) |
| Commit | `f26480b2fa901ed1862a80e463d4c1f758ec0e6f` |
| Licence | MIT (the repository's `LICENSE`; per-program notices in `provenance/NOTICES`) |
| Built by | the Visual Basic 6 IDE on the author's Windows XP host, with `tests/pcode.toml` recording each build |

Fetched by path at that commit and saved unmodified. `provenance/manifest.json` records every file, its
size and its SHA-256, so what is here can be checked rather than trusted.

## What is in it

| | |
| --- | --- |
| Programs | 42 `.exe`, built from 44 projects — two have no p-code build because their upstream source does not compile (`Edge_Detection.exe` names a `.cls` that is not in its directory, `HMM.exe`'s `.frx` is damaged, and the corpus's own NOTICES says a repair would invent source, so neither was repaired) |
| `public-domain/` | 10 program directories, each under The Unlicense |
| `vb6-code/` | 27 program directories under BSD 2-Clause, Copyright (c) 2018 Tanner Helland, whose notice is copied in with the corpus |
| Sources | `source/` holds the `.vbp`/`.frm` of the programs named here, for the same reason: a decode is checked against what the project says it contains |
| Build record | the corpus's NOTICES records each build: the Visual Basic 6 IDE (VB6 SP6, dated 2004-02-23) on a Windows XP host, with only `CompilationType=0` → `-1` changed in each project file. That is the corpus author's record of his own builds, kept as such |

## What was verified here, independently of the package

Every program was read with this tool's own reader before anything was decoded:

* all 42 report `aNativeCode` zero and `isa` `vb6-pcode` — they are p-code builds, measured rather than
  taken on the label;
* the method tables are filled in, which is what distinguishes a p-code build from a native one in the
  image itself;
* **680 procedures decode to their descriptor**: 266 exactly, 407 with one to four bytes of alignment
  before the descriptor, 7 admitting two readings of an opcode the runtime's tables give two lengths
  for, and none unreadable.

`recon pcode <program> --runtime msvbvm60.dll` reproduces those numbers; `RECON_PCODE_INPUTS` points the
tests at this directory.

## What it is not

Not malware: these are public-domain and open-source demo programs from a public corpus, each with its
source. Nothing here is executed — the tool only reads the files.
