# M7 — the permuter, and what comes after it

The plan's last milestone is *"Permuter, plugin API (WASM), agent-friendly interface; non-x86
instruction sets (VB6 p-code); Borland, Delphi and Watcom profiles"* (§6). It is being taken in the
order that order implies, and — like M6 — one slice at a time, each landing with its own tests.

## Why the permuter is first

The order is decided by what each item needs:

| item | what it needs | |
| --- | --- | --- |
| **Permuter** | nothing that is not already here | first |
| Agent-friendly interface | the commands the permuter adds, so agents can drive them | after it |
| Non-x86 ISAs (VB6 p-code) | a p-code VB6 binary to measure and test against | blocked |
| Borland, Delphi, Watcom profiles | binaries of each to read evidence from | blocked |
| Plugin API (WASM) | the plan says not to invent a plugin ABI yet (§3.1) | last |

Two of the five would be profiles full of guesses: `AGENTS.md` says no number goes into a profile
unless a command printed it, and this machine has no Delphi, Watcom, Borland or p-code VB6 file to
print one from. The VB6 input set that closed M6 contained one native program and the Visual C++ 6
tools — nothing p-code. Both items stay open in `TODO.md` saying exactly that, rather than being
shipped as something that sounds right.

The permuter, on the other hand, is item 10 of the plan's own architecture table — *"Permuter that
mutates source to find matches"* — and it sits on top of everything M1-M6 built: build orchestration
(M3), per-function scoring (M2), delink/relink (M5), toolchain profiles (M6). It is the piece that
answers the question the whole tool exists to answer, which is not "what is in this binary" but
"would this source have produced it".

## What it does

```
recon permute [--function NAME] [--unit NAME] [--budget N] [--flags] [--json] [-o DIR]
```

1. Resolve the unit: the one named, or the one whose `[[unit.covers]]` names the function.
2. Generate variants of that unit's source — the same program written a different way.
3. Build and relink each variant with the unit's own toolchain, one at a time.
4. Score the covered functions against the original with the compare engine, unmodified.
5. Report the ranking, and write the best variant's source where it can be adopted.

The score is `recon diff`'s own number for that function, and `exact` means byte equality after
relocation normalization — the only result that means the source reproduces the original. A run
reports what it tried, what each scored and why it stopped, because a search that reports only its
winner cannot be checked.

## Variants: the part that has to be provably safe

`src/Recon.Core/Permute/VariantGenerator.cs` produces the candidates, and it lives by one rule: a
variant must *provably* mean the same thing as the source. A permuter whose variants sometimes mean
something else is worse than no permuter, because one may match the bytes and still be the wrong
source. Anything the generator cannot prove safe it does not emit — working from C text rather than
an AST (the tool has no C front end) is paid for in coverage, never in soundness.

| kind | what it does | when it is offered |
| --- | --- | --- |
| `statement-swap` | two consecutive statements change places | the identifiers they touch are disjoint and neither calls anything — a call could reach shared state through a name this cannot see |
| `operand-swap` | the operands of `+ * == != & \| ^` change places | never for `- / % < >` and never across precedence: the operands of `+` in `a + b * c` are `a` and `b * c` |
| `increment-form` | `i++`, `++i`, `i += 1` | only in statement position |
| `declaration-swap` | two uninitialised locals of one type swap places | what this really explores is stack layout |
| `branch-inversion` | `if (c) A else B` becomes `if (!(c)) B else A` | only with an `else`, and never on a condition that is already a negation |

The same source always yields the same variants in the same order, so a run can be reproduced and a
larger budget extends the same search instead of starting a different one.

Nineteen tests in `tests/Recon.Tests/PermuteTests.cs` pin that: each kind is produced where it is
safe, refused where it is not, and the refusals are the point — statements that share a variable, a
pair of calls, a `return` next to a declaration, `a - b`, a unary `*`, a declaration with an
initialiser, an `if` with no `else`.

Five bugs the tests caught, all of which would have produced wrong variants rather than none:

* the scanner called every statement after a `{` compound, which silently disabled the generator;
* `==` was read as `=`, leaving a stray assignment operator behind every comparison;
* `i++` was read as the binary operator `i + +`, offering `+ + i`;
* `int *p = &a;` read `*` as multiplication, offering `p * int = &a`;
* operands were cut at the next operator regardless of precedence, turning `a + b * c` into
  `b + a* c`, which does not mean the same thing at all.

## The loop: `recon permute`

`src/Recon.Core/Permute/Permuter.cs` is the loop around the generator, and it measures with the
tool's own machinery rather than its own: `BuildPlanner`/`BuildRunner` for the build and
`ComparisonBuilder` for the score. A permuter that had its own idea of "the same bytes" would be
measuring something other than what `recon diff` prints, and the number would not be worth anything.

Three decisions are worth writing down:

**Every unit is rebuilt, not just the one being permuted.** A permutation changes the linked image,
so a build filtered to one unit would relink nothing and the comparison would be against a stale
binary. There is no `UnitFilter` in the loop's `BuildOptions`, and there should not be one.

**The baseline comes first, and it decides whether the run is worth having.** The source as it
stands is built and scored before any variant, because a variant is only interesting if it beats it.
When the baseline is already `exact` the run stops there and says so: 1.0 is the ceiling, so a
budget's worth of builds could not find anything better.

**The source is restored in a `finally`.** The loop writes each variant over the unit's source file
— that is the only way to build it — so a failed build, a budget stop or an exception would
otherwise leave someone's reconstruction silently permuted. The winning variant is written *beside*
the source (`build/permute/` by default, `-o` to choose), never over it: adopting it is a decision
for the human or the agent driving the tool, not for the search.

A run stops on the budget (`--budget N`, default 24), on an exact match (`--no-stop-on-exact` to
search the whole space anyway), when the candidates run out, or when the project does not build —
and it always says which, in `stopped_because`. Build chatter is suppressed by default because N
variants mean N builds; `--verbose` puts the compiler back.

## The agent-facing contract

`recon permute --json` prints the whole run — the baseline, every variant with its score, why the
run stopped — and `--check-schema` checks it against
`src/Recon.Core/Schemas/permute.schema.json` (embedded, like every other schema the tool ships).
The field an agent should branch on is `best_beats_baseline`: `best` is the best-scoring variant
even when it only *ties* the source, and an agent that read `best.score == 1` as "found it" would
be wrong half the time. `best_source` says where the winning source was written, or
`best_write_error` says why it was not.

```
best_beats_baseline == true   -> adopt best_source (or read it and decide)
best_beats_baseline == false  -> the source as it stands is the best known answer
stopped_because               -> budget reached / exact / all candidates tried / does not build
```

## What the tests found

Nine tests in `tests/Recon.Tests/PermuteRunnerTests.cs` run the loop for real, over the ELF corpus
with the machine's own gcc (they pass having done nothing where there is no compiler, as the other
toolchain tests do). One of them is the whole point of the tool: from a source whose `add` is
written `b + a`, the search finds `a + b`, scores it exact, beats the baseline and writes it — and
leaves the unit's source exactly as it was.

Six things they caught, none of which the generator's tests could have:

* `--budget` was missing from the argument parser's set of value-taking options, so `--budget 1`
  was parsed as a flag and silently ignored; only `--budget=1` worked.
* the JSON came out `PascalCase` while its own schema — and every other command's — says
  `snake_case`, so `--check-schema` failed on every document.
* `best` was not in the schema at all, and `additionalProperties: false` rejected it.
* a project whose linked executable is `build/permute` (a project named `permute`) made the default
  output directory a *file*, and the run died with a raw `IOException`. It now reports it in
  `best_write_error` and continues.
* an exact baseline still burned the whole budget, and reported "a variant reproduced the function
  exactly", which is not what happened.
* the run was as noisy as N builds, with the table at the bottom.

The per-variant build-failure path is written but `Built = false` is hard to reach on purpose: the
generator only emits variants it can prove equivalent, and gcc even accepts `i + p` for `p + i`.
What is tested is the same code reached through a baseline that does not compile — a project half
edited, which is the ordinary case for a reconstruction.

## The agent interface

[`docs/agent-interface.md`](agent-interface.md) is the contract; this is why it is shaped the way it
is.

**One document per run.** `--json` puts exactly one JSON document on stdout, and that has to hold
when the run fails too, because a failed run is when a program most needs an answer and is usually
given prose. So a command with something to report prints its own document — `verify` prints its
report even when a hash does not match — and a command that never got started prints an `error`
document instead: the exit code, `error.kind` (`usage`, `configuration`, `check_failed`, `internal`),
every line the run wrote to stderr, and for a configuration failure the file, the line and the key
to fix. Two documents on stdout would not be a contract, so it never happens: the entry point emits
the error document only when nothing else was printed.

**The contract is checked by the thing that publishes it.** Every document goes out through
`EmitWithSchemaCheck`, so `--check-schema` validates it against the schema the binary carries, and
the schemas can be read back (`recon schema list`, `recon schema show permute`). `init` and
`migrate` gained `--json` — a program that sets a project up needs the paths afterwards, and the
one thing `init` must say is that the binary was hashed but not copied. `verify` and `link` gained
schemas.

**One bug worth naming, because it was the kind that only an agent would hit.** `recon verify`
reported `ok: false` — FAILED — for `examples/elf-project`, whose every input hashes correctly and
which builds cleanly. The unit check was treating "no toolchain named" as a broken project, while
`recon build` asks the binary who made it and gets on with it. So `verify` now asks the same
question the build asks, and says which profile it will use. A person reading the table would have
seen "no toolchain and the target has no default" and shrugged; a program reading `ok` would have
gone and fixed something that was not broken.

## Visual Basic 6: native, or p-code

A VB6 program is one of two things. Visual Basic 6 compiles either to native x86, or to p-code that
MSVBVM60 interprets at run time — and it is the *same* project either way: same runtime, same Visual
Studio 98 linker, same imports. The one field that says which is `ProjectInfo.aNativeCode`, zero
when there is no native code in the image at all.

That matters here because a p-code program looks like any other VB6 binary. Hand it to an x86
decoder and it will decode the interpreter's tokens as instructions: 6,702 plausible-looking
instructions out of a stream that is not machine code. That output is worse than no output, because
it looks like an answer.

So `src/Recon.Core/Pe/Vb6Header.cs` reads the structure: the entry point of a VB program pushes the
address of its VB5! header before calling `ThunRTMain`, and ProjectInfo hangs off that header. What
comes out is published as `binary.vb6` in the inventory:

```
"vb6": { "signature": "VB5!", "header_rva": 5664, "language_dll": "VB6DE.DLL",
         "template_version": 500, "code_start_rva": 10448, "code_end_rva": 15824,
         "exception_handler_rva": 4662, "native_code_rva": 0, "is_pcode": true }
```

Three things follow from `is_pcode`:

1. `binary.isa` becomes `vb6-pcode`, which is what `CodeDecoder.CanDecode` is false for. The
   inventory reports zero instructions and says why — `the decoder speaks x86 and x86-64, not
   vb6-pcode` — and `recon disasm` refuses rather than inventing.
2. The `vb6-native` profile is withdrawn for it. Its rules match — a p-code program imports
   MSVBVM60 and carries the 6.0 linker — but it describes a compilation that did not happen, and a
   suggestion is a claim. The VB5! header is recorded as producer evidence instead, so
   `inspect producers` says what the program is:
   `VB5! header: VB6 VB6EN.DLL, p-code (no native code in the image: aNativeCode is 0)`.
3. Everything that is not the code still works: sections, imports, exports, relocations, data,
   xrefs, the compare engine.

The layout is the published one (Ionescu's *Visual Basic Image Internal Structure Format*, Geddon's
*VISUAL BASIC REVERSED*, the structure tables at vb-decompiler.org), and the reader was checked
against a real VB6 program rather than only against itself — a native-compiled one, which is what
was obtainable:

```
image_base=0x400000  entry=0x13c4  sections .text/.data/.rsrc  i386
entry: 68 20 16 40 00 e8 ee ff ff ff      push <VB header>;  call ThunRTMain
VB header at rva 0x1620: 'VB5!'  language dll 'VB6DE.DLL'  language id 0x407
ProjectInfo: template version 0x1F4 (500 = VB6)  code 0x28D0..0x3DD0
             exception handler 0x1236  native code 0x5000
==> native  (aNativeCode != 0: this one is not p-code)
```

Every pointer in the structure is stored as a virtual address and is converted to an RVA on the way
in, so a field named `rva` holds one. A pointer that does not point into the image is not a pointer:
the reader checks rather than trusts, because the p-code answer depends on that one field.

The p-code branch is tested on a synthetic image (`tests/Recon.Tests/Vb6PcodeTests.cs`, 5 tests):
the smallest PE32 carrying a VB5! header, built twice — once with `aNativeCode` set, once cleared.
The native form is the control: it still gets `vb6-native`, which is what shows the p-code test is
measuring the rule and not a fixture that never matched.

What is *not* done is decoding p-code. Recognising it is one field; reading it is the interpreter's
opcode table, and with no p-code binary to check a disassembler against, writing one would be
guessing. The tool says what the program is and declines to lie about its contents.

## What a big binary costs

`ElementEvil.exe` is a Visual Basic 6 game client with an 11.2 MB `.text`: 4,417,764 instructions,
304 functions, 414,282 cross-references. It is the reason this milestone has a performance section.
Handed to the tool as it stood, it was **killed** — RSS climbed to 1.06 GB in nine seconds and the
kernel took the process, with nothing printed. It now inventories in 36 seconds at a peak of 163 MB.

The fix is a change of shape rather than a change of diet. The analysis used to decode every
instruction of the program and keep all of them, because several passes ask things of them. It now
decodes a window at a time and keeps what it learned:

| what a pass asks | what it keeps |
|---|---|
| is this address an instruction boundary? | one bit per byte of the image — 1.4 MB for 2.5 M instructions |
| does anything branch here? | the set of branch targets, and it in ascending order for range queries |
| what points at what? | the xrefs themselves, which are the answer and not a means to one |
| where are the jump tables? | the tables, found once |

An instruction that a pass needs again is decoded again. Decoding is the cheap half of the work:
five trips over 4.4 M instructions cost seconds, where retaining them cost gigabytes.

Three costs were found by measuring, and none of them was where the profile said to look first:

1. **Which function does this address belong to?** was answered by walking the function list, per
   instruction: 304 functions x 4.4 M instructions. Addresses are asked for in ascending order, so
   the answer is now a forward sweep — retire the ranges that have ended, admit the ones that have
   started. The xref pass went from 27.5 s to 7.9 s.
2. **`--check-schema`** parsed the document into a `JsonNode` graph — an object per value. It was
   killed by the 51 MB inventory of a 52,000-function program whose analysis takes 3 seconds. The
   validator walks a `JsonDocument` now, which is read-only and costs a fraction of the document,
   and checks that same inventory in 5.4 s. This is the check that proves a document is well-formed;
   it could not be run on the documents most worth running it on.
3. **Operands no pass read.** Every instruction paid for the registers it touched and the memory it
   referenced: a string per register, a list per instruction with an operand. Registers and memory
   operands are now per-pass options, register names come from one table built once, and a pass that
   asks only where branches go pays for nothing else.

What the streaming rewrite found on the way is the reason the tests exist. Handed a buffer that ends
in the middle of an instruction, the decoder reports a length — the length of the bytes it could see.
A five-byte `call` cut after two is reported as a two-byte instruction, and the walk then resumed
inside that call and decoded everything after it from the wrong address: a program assembled out of
another program's misaligned bytes, in a tool whose whole claim is that it does not invent. Windows
now leave a fifteen-byte margin — the width of the longest x86 instruction — and decode what is left
again in the next window.

Measured on four Visual Basic 6 binaries, all attributed to `vb6-native`, all checking against the
published inventory schema:

```
ElementEvil.exe      11.2 MB .text   4,417,764 instructions   36 s   163 MB peak
XiasporaServer.exe                      77,906 instructions   1.7 s
Basic Server.exe                         6,771 instructions   0.5 s
crackme-vb6.exe                          6,702 instructions   0.5 s
```

The three smaller ones report instruction, function, xref and jump-table counts **identical** to the
counts before the rewrite, which is the evidence that streaming changed the cost and not the answer.

## What a big binary costs, second pass: where the time actually went

The streaming rewrite above took the 11.8 MB client from being killed to being analysed, and left it at
36.5 s. That is still the wrong number for a tool someone uses: half a minute to ask a question. What
made the next round possible was refusing to guess — the analysis now **says what it spent its time on**,
phase by phase, and `--verbose` prints it:

```console
$ recon inventory --project . --verbose
debug:   scan            4781 ms
debug:   jump_tables     5255 ms
debug:   seeds             11 ms
debug:   prologues       1450 ms
debug:     functions/entries    1825 ms
debug:     functions/filter       4 ms
debug:   functions       1834 ms
debug:   conventions     5091 ms
debug:   data               6 ms
debug:   xrefs            489 ms
debug:   statistics         2 ms
debug: inventory built in 19079 ms
debug: serialized 68 MB in 19631 ms
debug: written to /tmp/big/build/inventory.json in 19692 ms
```

That transcript is one run verbatim; runs of the same binary on this machine vary by about ten per cent,
which is why every number below is quoted as before-and-after from a pair compared in the same sitting.

Six phases, and the two most expensive are the two that read every byte: **scan** and **jump_tables**.
Three things followed from seeing that, and each is worth stating as a rule rather than as an anecdote:

**A pass over every instruction is the only cost that matters.** Building one entry per seed looked like
the expensive part of `functions` — it iterates over 17,000 seeds and estimates each one's size by
decoding its body — but the seed pass *before* it was doing two more full passes of its own: one to find
`jmp [IAT]` thunks, and one to collect call targets that the scan had already collected. Both were
avoidable. The thunk pass is a look at every unconditional jump's memory operand, which the scan now
records because it is reading them anyway; the call targets were in a set the scan already builds. The
seed phase went from **6.2 s to 21 ms**, and the numbers it produces are bit-for-bit what they were — the
whole inventory was compared against the pre-change one, timestamps aside, and is identical.

**Ask the decoder only for what you read.** `functions` decoded every function's body with registers and
memory operands tracked, to read one boolean per instruction — whether it ends a block. It now asks for
neither, and the phase went from **5.4 s to 1.6 s**. The same treatment on the conventions pass, which
needed only the last instruction, the first eight instructions' registers and whether any reads an
argument off the frame, took it from **5.0 s to 4.5 s** by streaming instead of materialising a list of
every instruction of every function.

**Parallel is not automatically faster, and on this machine it was much slower.** The two whole-image
phases are independent — neither reads what the other writes — so they were run at the same time, with
the conventions loop split across cores by function. The analysis went from 18.7 s to **41.4 s**: two
cores and a container's share of them decode one stream faster than they decode two, and the phases the
work was moved off lost more than the overlap gained (the conventions pass alone went 4.5 s → 28.9 s
under `Parallel.For`). The reverted change is recorded in the code where the phases run, with the
number, because "this looks parallel" is a hypothesis and the phase report is what tests it.

**18.7 s, from 36.5 s**, on the same input with the same output. The remaining cost is three passes over
every instruction — the scan, jump tables, and the conventions — and the honest next step there is to
stop making them separate passes rather than to make a pass cheaper. *(That step was taken: the three are
two, and the section "One pass over every instruction, where there were three" has the numbers — and the
one pass that stayed separate, with the measurement that says why.)*

### One listing is not one analysis

With the analysis fast, the interactive path stood out: `recon disasm 0x471000 --count 4` took **18
seconds** on the same binary, because it built the whole inventory — functions, xrefs, conventions, data
— to answer a question about four instructions. A reversing session is one build and then hundreds of
listings, and that is the loop the tool is for.

A listing needs three things from an inventory: where the functions are, what the imports are called, and
what the data is called. So `recon disasm` now reads those three from the inventory on disk when there is
one, and runs the analysis when there is not. The same four instructions take **0.6 s** — and the fast
route is only allowed to be a different route to the same document, which is checked two ways: a test
that compares a cached listing against a fresh analysis of the same binary byte for byte, and `--json`
from both routes over three addresses of the real client, one of them a thunk with an import annotation.

What is *not* done is trusting the file. The cache is used only when it says it was written by **this
build of this tool** about **this binary's sha256**, and when the document is not older than the binary;
otherwise the analysis runs. An inventory of another build is exactly the plausible-looking wrong answer
this project exists to replace, and `--verbose` says which route a listing took, so a listing that came
from a stale document cannot be mistaken for a fresh one.

## One pass over every instruction, where there were three

The section above ends with the honest next step it could see: the remaining cost was three passes over
every instruction — the scan, the jump tables and the conventions — and the fix was to stop making them
separate passes rather than to make a pass cheaper. Two of the three were whole-image walks over the same
chain, and they are one walk now.

**What the three cost.** On the 11.8 MB client: `scan` 3374–3509 ms, `jump_tables` 3311–3389 ms and
`prologues` 871–1001 ms — **7.7 s of a 12.9 s analysis**, spent decoding the same bytes three times for
four questions about each instruction: where it starts, which addresses it names, whether it indexes a
jump table, and whether a prologue pattern begins at it. Two facts made one walk enough. Registers are
needed only on the three mnemonics that compute an address (`lea`, and the `call`/`add` pair of the
position-independent idiom) — what the table finder wants and what the other two never paid for — and
what an instruction *is* does not depend on what was recorded about it, so the three walks were always
the same chain.

**What had to stay split.** A prologue is only a *candidate* in the scan: an instruction start that
begins with a prologue pattern. Which candidates are function entries is a question about the seeds,
which do not exist yet, so that decision stays where it was and now costs **3 ms instead of 871–1001 ms**
— a few dozen candidates where a byte walk tested 4.4 million positions, most of them already covered.

And the table **reading** had to move after the walk. An entry counts only when it lands on a decoded
instruction, and the scan is still marking instruction starts as it goes, so a table read mid-walk breaks
at its first entry: measured, **95,063 candidates and zero tables**. It showed up as `jump_tables 0` in
the statistics while the phase report showed the work being done, which is what a phase report is for.
The finder now spots candidates inline — that part is sequential, because it follows which register holds
which address — and reads the tables once the instruction-start map is complete.

**The phase report changed shape with the code.** `jump_tables` is not a phase beside the scan any more;
it is reported underneath it, as the share of the pass it is:

```console
$ recon inventory --project . --verbose
debug:     scan/jump_tables     624 ms
debug:   scan            5581 ms
debug:   seeds              7 ms
debug:   prologues          3 ms
debug:     functions/entries    1247 ms
debug:     functions/filter       2 ms
debug:   functions       1247 ms
debug:   conventions     3097 ms
debug:   data               4 ms
debug:   xrefs            272 ms
debug:   statistics         1 ms
debug: inventory built in 9810 ms
```

`StreamingAnalysisTests` asserts that shape now, including the absence of a top-level `jump_tables` line,
because a report that named a pass which no longer exists would be worse than no report — and the next
person to time an analysis would be timing something imaginary.

**The numbers, from a pair of runs in the same sitting.** The phase report exists because this machine
varies by about ten per cent between runs, so before and after are quoted from one sitting:

| | three passes | one pass |
|---|---|---|
| `scan` (the tables included) | 3374–3509 ms | 5581–5841 ms (624–677 ms of it tables) |
| `jump_tables` | 3311–3389 ms | — (a share of the scan) |
| `prologues` | 871–1001 ms | 3 ms |
| the three together | **7.7–7.9 s** | **5.6–5.8 s** |
| the whole analysis | 12.9–13.0 s | 9.8–10.4 s |

The merged pass costs more than the old `scan` alone — register recording on every `lea`/`call`/`add`, and
a prologue pattern test at every instruction start — and less than the three put together: **about two
seconds off a thirteen-second analysis**, and the answer is unchanged. *Unchanged* in the strong form: the
inventory was compared against the pre-change document field by field and **zero paths differ** — 304
functions with their sizes and evidence, 25 jump tables, 414,282 cross-references, 4,417,764
instructions.

**The third pass was measured too, and left alone.** The conventions pass is per function rather than
whole-image, and folding it into the walk that sizes the functions would mean asking that walk for
registers and memory operands. Measured: the size walk goes from **1.25 s to 3.4 s**, so the merge would
replace 1.25 + 3.1 s with at most 3.4 s — under a second, in exchange for re-deriving three facts (the
last instruction of the body, the first eight instructions' registers, whether any of them reads an
argument off the frame) over a range that is *wider* than the body the convention is about, and for
deciding which of those facts fall inside the body only after the walk that was supposed to establish the
body's end has already finished. A second is not worth that, and the measurement is recorded here rather
than the idea, because "we could merge this one too" is exactly the sort of plausible step this document
exists to check.

## A unit's own build takes a piece's place

Delink and relink landed in M5 with one thing missing, and it was the thing the milestone exists for: a
piece claimed by a unit with `provider = "rebuilt"` was planned and reported, and `recon link` then
wrote **the original's bytes** for it anyway. The plan said which unit was responsible; nothing asked
that unit's build. The delink → replace → relink loop did not run.

It runs now. `recon link` reads each claiming unit's compiled object and places the function the object
holds at the piece's address, with the compiler's own addresses written out as references to the names
the image defines — so the bytes at that address are the unit's, not the original's.

```
$ recon link --project /tmp/rebuild-whole
  toolchain   gcc-13-mingw (target.default_toolchain = "gcc-13-mingw")
link whole: 296 piece(s) at 0x400000
  wrote build/relinked.exe
  rebuilt     13 of 13 claimed piece(s) from 1 unit(s)
    sample (src/sample.c): 13 piece(s), 859 byte(s), 30 reference(s)
  relinked identical: 16 section(s), 192851 byte(s), header included
```

The unit there is the corpus's own `sample.c` — the source that produced `sample-release.exe` — built by
the same compiler with the same flags. **Thirteen functions, 859 bytes, thirty references, and the image
that comes out is the original, byte for byte.** No byte of those thirteen functions is the original's:
every one came out of the object, and the image matches because the object does.

`src/Recon.Core/Archive/CoffObject.cs` is what reads one — sections with their bytes and relocations,
symbols with their names undecorated, the string table for the long section names — deliberately
separate from `CoffArchive.cs`, which catalogues the members of a *library* and never opens a `.o`.
`src/Recon.Core/Delink/RebuiltPieces.cs` is the policy: which pieces can be filled from a build, from
which bytes, and which must keep the original's instead.

### What a rebuild is made of

| part | how it is decided |
| --- | --- |
| the bytes | the object's, from the symbol the piece is named after, to the next symbol in that section with the compiler's alignment padding trimmed |
| the size | it must fit the piece. A longer rebuild cannot be placed — the plan fixes every address — and a shorter one leaves the original's bytes after it |
| an address | `.long <name>`, the four bytes the compiler left being the addend (measured: on i386-PE the field's value *is* the addend, so `.long <name>` and `.long <name> - . - 4` reproduce the bytes) |
| a call | `.long <name> - . - 4`, a COFF `rel32` |
| the name | resolved through whatever the object called it: `_add` is the plan's `add`, `.bss + 0x3C` is the object's `g_counter` at exactly that offset, and `.text + 0x80` is the object's `_add` at 0x80 — whose offset has then been *spent*, so the expression is the name and not the name plus the distance that found it |
| a table or a string | the object has no name for it — a compiler writes those as a section and an offset, `.rdata + 0x14` — so the plan's own answer for that *address* is taken: the fixup it wrote where the original held an address in that same slot. A rebuild is the original's code, so the address fields are in the slots they were in the first time, and the question "what does this address point at" was already answered for this address. Where the plan has no answer either — a field the original did not have an address in — the piece is refused rather than guessed at |

### What it refuses, and why that is the point

A rebuild that cannot be placed produces a warning naming the reason and leaves the piece with the
original's bytes. The image is then the original at that address — honest — rather than a plausible
image with a wrong call in it. The refusals:

* **the unit's object is not its current build.** Reusing the build system's own answer (a cache key
  over the source, the flags, the toolchain and the recorded headers, plus the object being there),
  not a second opinion about staleness. This is the one failure that would produce an image that
  *looks* reconstructed, so it is refused first: `unit[arith] is claimed as rebuilt but its object is
  not its current build (changed dependency: src/arith.c)`.
* **the object does not define the function** the piece is named after, or defines it in a section that
  is not there, or has only padding where it should be.
* **the rebuild is longer than the piece.**
* **a name sits inside the rebuilt range** — a rebuild moves it, and where it went is the compiler's
  business.
* **a reference that is not `dir32`/`rel32`**, whose target the relinked image has no name for, or that
  points at an address neither the object's names nor the plan's own fixups explain.
* **a reference the assembler resolved inside the object.** A call between two functions of the same
  object is four finished bytes with no relocation naming it, so nothing in the object says what it
  points at. It is placed only if the object and the image agree about where the unit's names live; where
  they disagree, the rebuilt bytes are searched for a field that is a reference to the moved name, and
  the piece is refused if one is found — `the object puts _bump at +0x0 of .text, which would be 0x1710
  in the image, where bump is at 0x14F0; loop_sum+0x1D refers to it as a distance the assembler already
  resolved, which the relink cannot fix`. The test that pins this is a unit that lays its functions out
  the other way round: the leaf is placed and the caller is not.

### Three bugs the loop found, which the small case could not

The first test was one function with no references, and it passed while three real defects stood behind
it:

1. **A cover resolved one function at a time.** `Covers.Resolve` falls back to an unambiguous substring,
   and with a single candidate every substring is unambiguous — so `symbol = "add"` also claimed
   `___w64_mingwthr_add_key_dtor` (0x2180) and `__multadd_D2A` (0x6F50). The planner now resolves a cover
   against every name the image has, which is what the fallback was always for: `add` claims `add`, and
   the test that used to work around it says so.
2. **A relocation names a *record*, not a symbol.** A COFF symbol with auxiliary records is followed by
   them, and the reader collects symbols by skipping those records — so a relocation's symbol index,
   which counts records, named some other symbol. `main`'s reference to the format string read as a
   reference to `.bss + 0xE7`; it reads as `.rdata + 0x0` now.
3. **The section-symbol addend was spent twice.** A compiler writes a reference to another function as
   `.text + 0x80`; the name at exactly that offset is `_add`, and the emitted expression was
   `add + 0x80` — off by the object's own offset of the function, which the whole-unit relink showed as
   fourteen bytes in the wrong place, one per call, each off by exactly the callee's offset. With the
   addend spent, the image is identical.

### Evidence

```
$ recon link --project /tmp/rebuilt            # one function, symbol cover
  rebuilt     1 of 1 claimed piece(s) from 1 unit(s)
    arith (src/arith.c): 1 piece(s), 9 byte(s), 0 reference(s)
  relinked identical: 16 section(s), 192851 byte(s), header included

$ recon link --project /tmp/rebuild-whole      # the corpus's own source, one unit
  rebuilt     13 of 13 claimed piece(s) from 1 unit(s)
    sample (src/sample.c): 13 piece(s), 859 byte(s), 30 reference(s)
  relinked identical: 16 section(s), 192851 byte(s), header included

$ recon link --project /tmp/rebuilt            # the same unit laid out the other way round
  rebuilt     1 of 2 claimed piece(s) from 1 unit(s)
    arith (src/arith.c): 1 piece(s), 9 byte(s), 0 reference(s)
  relinked identical: 16 section(s), 192851 byte(s), header included
  warning: unit[arith]: the object puts _bump at +0x0 of .text, which would be 0x1710 in the
  image, where bump is at 0x14F0; loop_sum+0x1D refers to it as a distance the assembler already
  resolved, which the relink cannot fix; the original's bytes are kept
```

`recon link --json` now says what was placed as well as what was claimed — `rebuilt` is the claim,
`rebuilt_pieces` is the supply, with each piece's unit, source, byte count, reference count and how many
bytes of the original were kept after it, and `problems` says why a claimed piece kept the original's
bytes. The difference between the two numbers is the most useful thing in the document: a claim is not a
supply, and a unit can claim a piece and have nothing that fits it.

Checked by 4 new tests in `DelinkTests` (20 in all): one piece rebuilt while every other section stays
the original's, the corpus's own source as one unit relinked into the original image, the moved-function
refusal, and the stale-object refusal. **595 tests, 0 failed, 0 warnings** at that point.

## A p-code program in the inventory

The tool has read p-code for a while: `recon pcode` decodes every procedure of a Visual Basic 6 program
compiled to p-code, against the lengths measured out of the runtime that will interpret it. What it could
not do was put any of that in the inventory. Handed a p-code program, `recon inventory` reported
`isa: vb6-pcode`, **one function named `entry` covering the whole section**, zero instructions, and a
problem line saying the decoder speaks x86 and not this. That was honest — reading interpreter tokens as
machine code would be inventing a program — but it made the entire binary invisible to everything built on
the inventory: `inspect functions`, `report`, a comparison, and any question an agent asks that is not
`recon pcode` itself.

Now the instruction set is an axis rather than a wall. Measured over the 42 programs of the DeForm6 corpus:

```
$ recon inventory --project . --json
  binary.isa                  vb6-pcode
  functions                   680        (one per procedure, all 42 programs)
  instructions                55,135
  functions_high              680
  functions_with_unknown_size 0
  problems                    []          (none: this is read, not refused)
```

Those are the same numbers the p-code reader measures for the corpus (680 procedures, 55,135 instructions
in 171,916 bytes of code), and a test checks the two commands agree procedure by procedure — same
addresses, same extents, same object-and-method identity — because they read the same table through
different doors.

**What a p-code function is.** Its extent is not estimated: the compiler wrote the stream length into the
procedure's descriptor, which is why every one of them is `high` confidence where an x86 function's extent
is often a guess. Its `found_by` is a new evidence kind, `pcode_procedure`, added to the schema as a value
rather than smuggled in as a string, because the schema is the contract. Its name is the program's own
identity for it — `FrmHex[3]`, the object and the method index — and every one carries
`name_is_object_and_method_index` in `unknowns`, because VB6 does not store procedure names in the binary
and a report that looked like it had found names would be lying.

**The runtime is asked for, and its absence is a state rather than a failure.** Instructions inside a
p-code stream can only be measured against the interpreter that will run them, so `inventory` looks for
`msvbvm60.dll` (named by `--runtime`, beside the program, or in the project's input directory). With it,
the counts above. Without it, the program's own method tables still give every procedure's extent — that
needs no interpreter — so the functions are published with their ranges and **zero** instructions, and one
problem line says exactly what to do:

```
$ recon inventory --project . --json          # no runtime anywhere
  functions                   680
  instructions                0
  pcode_runtime_used          0
  problems  ["the runtime that interprets this program was not found, so each procedure's byte stream is
              published from the program's own method tables and its instructions are not measured: pass
              --runtime=<msvbvm60.dll>, put one beside the program, or put one in the project's input
              directory"]
```

Nothing is claimed about a stream that was not read: the `no_exit_instruction` flag and the
`stream_reads_N_ways` note are attached only when the bytes were actually decoded, which is a distinction
the first version got wrong by flagging all 680 procedures as lacking an exit instruction.

**And the refusal that remains points at what works.** `recon disasm` still will not print p-code as
machine code, but it no longer stops at saying so:

```
$ recon disasm 0x3300
vb6-pcode: the decoder speaks x86, x86-64 and arm64, not vb6-pcode
nothing to disassemble as machine code; `recon pcode` lists this program's procedures and their p-code
streams, and `recon inspect functions` lists them from the inventory
```

Checked by 2 tests over a real corpus program (`HexScroll`): the procedure table against `recon pcode`
including the `--check-schema` run, and the no-runtime path with its problem line and its unclaimed flags.
The synthetic p-code image in the older test now covers the third case — a program whose method table is
empty — which is where "no procedures" is the right answer and is still not a decode.

## A profile for a program that was never compiled to machine code

`vb6-pcode` is a toolchain profile, and the first one written from evidence that a *program's own
header* supplies rather than from what a compiler left in the image. It exists because the tool had a
wrong answer for these programs: handed one of the 42 p-code programs of the corpus, `recon inspect
producers` said **`msvc-6: medium via linker_version`** — a true statement about a wrong thing. These
programs were not compiled by Visual C++ 6.0. They were not compiled to machine code at all.

The profile is small because the toolchain is the same one `vb6-native` describes: same IDE, same
Visual Studio 98 linker, same runtime. What is new is that the toolchain has two compilation modes and
the evidence cannot tell them apart:

```
$ recon inspect producers "Hex Scroll.exe"          # one of the 42 p-code programs
rich_header          vb60    9782  1   prod_id=0x000D build=9782 (last entry)    vb6-pcode
linker_version       link    6.0       optional header = 6.0                      msvc-6, vb6-pcode
import_dll           runtime           MSVBVM60.DLL                              vb6-pcode
vb_header            Visual Basic      VB5! header: VB6 *, p-code (no native code in the image: aNativeCode is 0)

toolchain suggestions (evidence, not conclusions):
  vb6-pcode: medium via rich_header, linker_version, import_dll, vb_header
  msvc-6:    medium via linker_version
```

Every one of the first three rows is exactly what a *native* VB6 program produces. What differs is in
the fourth: the program's own `VB5!` header says `aNativeCode` is 0, which is what p-code means. So the
rule that reads it is a **gate**: a profile that names a kind of Visual Basic program is suggested for
that kind only, and the profile naming the other kind is withdrawn from every piece of evidence it
matched — which is why `vb6-native` is absent from that list rather than merely out-ranked. The
withdrawal used to be a hard-coded removal of one profile id in the detector; it is now the rule in the
profile file, and the same reason applies the other way round. `msvc-6` stays below, and that is the
honest answer: the linker stamp really is 6.0, and the evidence table calls its rows suggestions rather
than conclusions.

**This field was measured, not assumed.** The last paragraph of this document's p-code section used to
say no `vb6-pcode` profile existed because a profile's numbers have to come off a real binary and none
had been measured. They have now, over 42 of them:

```
$ recon vb6 --json <program>          # all 42 programs
  header.signature          VB5!
  header.runtime_build      9782
  project.template_version  500         VB6
  project.native_code_rva   0           p-code

$ recon inspect producers --json <program>    # all 42
  rich_header   prod_id=0x000D (vb60)  count 1  build 9782    (the only record in the header)
  linker_version 6.0
  import_dll    MSVBVM60.DLL            (the only import)
```

Three things that came out of it, one of which changed a draft:

- **A p-code program's Rich header holds one record.** A native VB6 program's holds three — the
  assembler's `0x000E`, one `0x0009` per Basic object (36 of them in VISDATA.EXE) and the same
  `0x000D`. That is a real difference, and the profile deliberately does *not* match on it: the header
  rule is the statement that means what it says, and the Rich record would be a second reading of the
  same fact.
- **The `vb60` record's build number is not the runtime's build number in general.** In all 42 it is
  9782, the same number the program's own header gives as `wRuntimeBuild` — which is why a draft of the
  profile comment said the two are one number. Then the native control: VISDATA.EXE carries a `vb60`
  record at **8167** while its header says **8169**. Two numbers, not two spellings of one, so the rule
  matches the product id and the build numbers are recorded as measurements. This is the kind of claim
  that only a second program disproves.
- **The runtime the programs were built against is not the one on this machine.** The corpus asks for
  runtime build 9782 and refuses to say which service pack that is — the corpus's own record is a
  VB6 SP6 IDE on a Windows XP host and nothing here was executed on that host. The `msvbvm60.dll` in
  the inputs directory is **6.00.9848**, and `recon pcode` now prints both numbers for that reason,
  each named for where it came from (the header's `wRuntimeBuild`, the file's version resource):

  ```
  instructions read against msvbvm60.dll 6.00.9848; the program was built against runtime build 9782
  ```

  Two numbers are what make the next claim checkable, and the check is that decoding does not depend on
  the service pack: with this runtime the corpus's **680 procedures all decode to their own
  descriptor** — 222 exactly and 458 with the compiler's own padding, none with two readings and none
  unreadable. A profile could name 9782; that would describe 42 programs from one host.

The profile, then, is:

```toml
id = "vb6-pcode"
extends = "vb6-native"          # same IDE, same linker, same runtime, other compilation mode

[[detect.vb6_header]]
kind = "pcode"                  # the gate: this profile is for p-code programs and no others
```

with `[compile]` deliberately absent: no command here can rebuild one of these programs, because what
produced it is a Visual Basic 6 IDE on Windows and what it emits is interpreter input for a DLL. That
absence is also what `recon permute --flags` reports — no levels to try — which is the honest answer
rather than a guess at flags a compiler that is not on this machine might accept.

**And a bug the measuring found.** Building this evidence meant running `recon inventory` over 42
programs in a loop, and the loop passed both `--project <dir>` and the program's path. The project's
declared input wins, so the command inventoried *the same program 42 times* and the run reported
`966 procedures, 37,590 instructions` — 42 × 23 and 42 × 895, exactly — as if it were the corpus's
totals. Nothing warned; the hand-off between a project and a positional file was simply ignored. It is
a usage error now (exit 2) with the declared input named in the message, because a command that reads
one file and is handed another must say so. The corrected loop reproduces the corpus numbers exactly:

```
$ recon inventory --project <one project per program> --json    # all 42 p-code programs
  42 programs, 680 procedures, 55,135 instructions, 0 problem lines, 93 s
  configured_toolchain: vb6-pcode   (all 42; before this work it was msvc-6, all 42)
```

Two smaller things came with it, both about the same numbers being readable rather than implied:
`recon toolchain show` prints `code_kind` (`pcode` or `native`, null for every profile that is not about
Visual Basic) so a caller can check it against `binary.isa` without parsing a display name; and a profile
that named both kinds is now a load-time error, because a file that did would be chosen for a Visual
Basic program whichever way it had been compiled. Checked by 6 new tests in `Vb6ToolchainTests` — the
profile's inheritance and its absent compiler, the p-code attribution with the native profile's
withdrawal, the native control, the both-kinds rejection, the 42-program corpus loop (each program's
single Rich record, its product id, and its build number against the header's), and the two runtime
builds through the CLI — which took that class to 12 tests, plus the VISDATA attribution test, which
now also asserts the fourth kind of evidence. The suite was **579 tests, all passing** at that point
(591 now, with the operand and listing tests, the comparison reading what is on disk, the one
version string, and the comparison no longer decoding a body it does not have to).

## The comparison had the same disease as the analysis, and it was still terminal

Fixing the build test made a second thing visible, and this one was worse than a wrong score: `recon diff`
on the 11.8 MB Visual Basic 6 client was **killed by the kernel, with nothing printed and exit code 137**.
Two minutes of work, then silence. The tool that says *what a rebuild reproduced* could not be asked the
question about the largest binary it could analyse, and a user would have had no way to know why.

The cause is the shape the analysis itself used to have, and it was still here: `ComparisonBuilder`
normalized **every function of both sides into one list of objects and kept them all** — 4.4 million
instructions per side, each with its text, its references and two `List<>` instances — which is a couple of
gigabytes before the comparison has decided anything. Decoding, not comparing, is what a binary this size
costs, and the comparison was paying to hold the decode of both sides at once.

The fix is the one the analysis already has: **keep the summary, make the body when it is asked for, and
let it go.** What a comparison needs between the moment a function is paired and the moment it is compared
is small — a key over its normalized instructions, how many there are, and a histogram of mnemonics — so
that is what is kept, and the body is rebuilt for the pair being compared. Pairing still needs bodies for
the similarity stage, but only for the pairs that get past filters that are all computed from the summary,
and one body of each side is alive at a time.

Measured on the client against itself, with the stage report this work also added to `diff --verbose`:

```
$ recon diff --project . original.exe --summary --verbose
debug:   summarize L    17188 ms
debug:   summarize R    16216 ms
debug:   pair               4 ms
debug:   compare        33524 ms
  functions    304 left, 304 right, 304 matched (304 exact, 0 changed), 0 folded, 0 only left, 0 only right
  score        1
```

**Killed by the kernel → 100 s and a peak of 1.0 GB**, with the answer a self-comparison must give:
304 of 304 functions exact and 4,443,084 of 4,443,084 instructions equal. (An earlier run of the same
command showed 140 s; that one was sharing two cores with the test suite, which is why the stage report
exists and why numbers in this document are quoted from runs that were not competing with anything.)
The same comparison is **37.6 s at a peak of 684 MB** now, and *The bodies a comparison decodes twice*,
below, is where that came from.

Two smaller things came out of the same measurement:

- **A comparison pass was paid twice.** The summary pass builds each body to key it, and the comparing
  pass built it again — *The bodies a comparison decodes twice*, below, is where that second ask stopped
  costing a decode. `SideBodies` keeps bodies while the total stays under a budget of 100,000
  instructions — the corpus and anything a person compiles are compared in one pass — and streams above
  it. The budget is deliberately far below what a large binary needs: it exists to save work on inputs
  that fit, not to raise the ceiling on inputs that do not, and the first version of it (400,000
  instructions) turned a run that *finished* into a run that was killed, which is how the number was
  chosen.
- **Two empty lists per instruction.** `References` and `ReferenceClasses` were allocated for every
  instruction although most instructions refer to nothing: on this input, 822,000 of 4,443,084 carry a
  reference. They are allocated on the first reference now, which is what made the keeping of bodies
  affordable at all. The peak was about a gigabyte at this point, and where it went was one function body
  at a time: this binary's 304 functions are large (their extents are not known, so each is decoded to
  the end of what it covers), and a body plus its alignment is the unit of work. That — not the
  documents — is what set the ceiling, and *The bodies a comparison decodes twice*, below, is the step
  that lowered it.

### The inventory a side already had

The item that asked for this was one line long: *the comparison still builds an inventory it may already
have on disk*. Half of it is done, and the numbers are why it was worth doing. On the 11.8 MB client, a
comparison of a pair whose two inventories exist takes **72.8 s** where the same pair with neither takes
**108.8 s**, and the two reports are equal — every function, every difference, every field — apart from
the two fields that are about the run rather than about the comparison.

| the sides' inventories | the report says | wall | `analysis_ms` left / right | peak RSS per side |
|---|---|---|---|---|
| neither exists | `built` / `built` | 108.8 s | 17,615 / 18,674 | 1.02–1.08 GB |
| both exist | `read` / `read` | 72.8 s | 725 / 488 | 837 MB |

The peak column read `977 MB` in both rows when this table was written. That number was the instrument
reading a *cumulative* figure — the largest child so far, which was the inventory — rather than the
child being measured, and *The bodies a comparison decodes twice*, below, is where that was found and
where the corrected peaks come from. The wall and `analysis_ms` columns are that run's own and stand.

The pair is the client and a copy with three two-byte edits inside three functions of `.text` — the
first attempt at making a pair put the edits at the start of the section, at `0x1040`, `0x1080` and
`0x10C0`, all before the first function at `0x267A`, and the comparison scored it 1, which is how a
difference nothing looks at was noticed. Both routes score **0.9996** over the same 304 functions, and
the comparison of the two reports is field by field over the whole document with `analysis_ms` and
`inventory_source` zeroed. Each side's `inventory_source` is in the report so that a reader can tell a
cache hit from an analysis: they cost 17.6 s and 0.7 s and produce the same answer, and a report that
did not say which happened would be hiding the cost rather than saving it.

**What makes reading safe is everything it refuses.** A document is used only when it says it was
written by `recon`, at this version, declaring the inventory contract, carrying a `functions` array,
about the hash of the binary in hand, and not older than that binary. Every one of those is a refusal a
test covers, and one of them was found by measuring rather than by thinking: the first version compared
the document against the hash the *project records* for that input, which is a statement about the file
as it was when `recon init` read it — so a comparison against a rebuilt binary refused its own document
and quietly analysed it again. What the document has to match is the bytes being compared, which the
reader has in hand already. The other refusals matter for the same reason: a project's build directory
holds the comparison and the report as well as the inventory, and a comparison document carries a
generator and a version too, so "written by this tool, about this binary" would accept one, deserialize
it into an empty inventory and compare two empty sides — the plausible-looking wrong answer this whole
guard exists to stop.

**A document read for a comparison is not the whole document.** The first read route on the client was
killed by the kernel after 229 s, where the built route finished in 108 s — the same failure the
comparison itself had had, one layer down. The reason was the document's own shape: an inventory of
this client is **71.7 MB and 49.9 MB of that is the cross-reference list**, and a comparison never reads
it. It pairs functions by their bodies and compares an operand as what it points at; *who references
whom* is not one of its questions, and the listing is the only reader that wants it. Two documents with
their cross-references in them were enough to put the run over this machine's 2 GB. The cache now drops
what the comparison does not read as it reads, at the token level — a copy of the spans it keeps, not a
parse, so nothing becomes an object on the way — and the same pair finishes in 72.8 s. The peak is the
comparison's own bodies rather than the documents — the read route ends at 837 MB where the built route
is over a gigabyte — and *The bodies a comparison decodes twice*, below, is what that measurement led
to.

**One version string, found by this work.** Reading a document is guarded by a version, so the versions
had to agree about which build wrote a file, and they did not. The inventory command asked the *entry*
assembly — the CLI when a person runs `recon`, the test host when a test runs the same command
in-process — the two library defaults said `0.1.0`, and the comparison read
`Assembly.GetName().Version`, which is `1.0.0.0`. The five golden files in this repository carried two of
those three answers, about one tool, and they had for months: that is what a host-dependent version
string looks like after a while. `src/Recon.Core/ToolVersion.cs` is the one answer now — the
informational version of the assembly that defines the document format, without any `+<commit>`
metadata — and everything that writes or checks a version asks it: the CLI's `--version`, both document
defaults, the comparison's generator, and the guard that decides whether to read. The goldens were
regenerated deliberately (`version` is `1.0.0` in all five, plus the new `inventory_source` field), and
a test drives the CLI, the library and a comparison and holds all three to the same string.

### The bodies a comparison decodes twice

The read route wrote one conclusion down twice — *comparing instruction streams rather than materialized
bodies is the deeper fix* — and measuring before implementing turned it into something narrower and
better. The stage report says where a comparison spends its time on the client: **summarize
13.6–14.4 s per side, pair 12–20 ms, compare 27–29 s**. Pairing is free; comparing is the whole cost.
And what the comparing stage was buying with those 27 seconds was a **second decode of bodies the
summary pass had already decoded to key them**: the summary pass needs each body, and the comparing
stage asked for the same body again to answer a question that, for most pairs, was already answered.

**A pair whose two keys agree is decided without decoding either body.** `BodyKey` is a hash over
exactly the normalized instructions the differ compares — which is why the *pairing* trusts it to decide
which functions are the same — so two functions with one key have nothing left to differ about: every
instruction is equal, there are no differences, the score is 1, and every other field of the answer (the
instruction count, how much padding was trimmed, how many bytes were unreadable, how many instructions
were relocated) is something the summary pass measured when it read the body. The comparing stage writes
that answer from the summary and decodes neither body, and says how many pairs it decided this way in
`model.bodies_proven_identical`. On the client's edited pair, where **301 of the 303 matched functions
are unchanged**, the compare stage goes from **27–29 s to 4 ms**; the two pairs that really changed are
decoded as before. The one thing a key cannot answer is the aligned listing, because a listing is a
*reading* of two bodies rather than a statement about them: a function the caller asked for by name
(`diff --function NAME`) is decoded and aligned exactly as it was.

**What the pairing pass decodes, the comparing pass keeps.** The similarity stage has to decode both
bodies of a candidate pair to score it, and the comparing stage used to decode the same pair again.
`SideBodies.Body` now hands what it made to the same `Keep` that enforces the 100,000-instruction
budget, so one decode serves both moments. That changes what a comparison *costs* and not what it can
*hold*: above the budget the second pass still decodes, which is the honest behaviour for a binary whose
bodies do not fit.

**The resolver had been holding 118 MB for nothing.** Every question about a symbol goes through
`SideSymbolResolver`, and it kept every reference it had ever answered in a map: measured on the client,
**822,000 entries, about 118 MB**, alive for the rest of the run and never asked again, because a
reference is answered at the moment it is read and the reader moves on. `TakeReferences` hands the list
over and forgets it, and live memory at the point it was measured fell **258 → 140 MB**.

**The summary stopped materializing a body.** This is the change that lowered the ceiling rather than
the time. The peak was never the documents and never the pairing: forced collects between the stages
moved nothing, a dump of what was kept showed 76 bodies live at the 100,000-instruction budget, and the
run's peak was set *inside* a summarize pass by the largest function in the program — 1.08 MB of
listing, about 430,000 instructions, held for as long as it took to hash and count it.
`FunctionNormalizer.Summarize` walks the decoded stream once and accumulates what a summary is made of
— the instruction count, the incremental SHA-256 the key is made of, a histogram of mnemonics, a tally
of reference classes — so the pass that visits every function of both sides holds nothing bigger than
the instruction in front of it. Two tests hold the two ways of measuring a body together:
`Measuring_a_body_and_building_it_agree`, and
`Trailing_padding_is_trimmed_the_same_way_whichever_way_the_body_is_read`, the padding case being what
the streaming walk had to learn — the summary and the differ must trim a body's trailing alignment
identically, or every padded function would become a difference.

**One collect, in the one place the code knows it is worth it.** Reading a 68 MB document is two large
transient buffers — the file, and the copy with the cross-references dropped from it — and by the time
both documents are loaded, both are garbage the collector has had no reason to look at yet.
`ReturnWhatTheDocumentsLeft()` is the comparison's only forced collect and it is not a guess about where
memory is: **515 MB committed before it, 50 MB after, in 4 ms**, and 85 MB off the peak of the whole
run. The collects that were tried between the other stages had no such justification and are gone.

**The number this work started from had been measured wrong.** The table above said 977 MB, and
`resource.getrusage(RUSAGE_CHILDREN).ru_maxrss` is cumulative — it reports the largest child so far,
which for these runs is the inventory, not the comparison. The instrument now measures each child with
`os.wait4` and reports that child's own peak, which is where the corrected 1.02–1.08 GB built and 837 MB
read in the table come from. A measurement whose subject is not what it says it is measures nothing.

Measured on the client against a copy with two bytes changed inside each of three functions, with
`tools/big-diff-pair.py` — the instrument this work left in the repository, because the edit has to land
*inside* a function (an earlier pair put the edits in the padding before the first function, and a pair
that scores 1 proves nothing) and because the two reports have to be shown to be the same comparison:

```
$ python3 tools/big-diff-pair.py /home/user/vb6-wild/ElementEvil.exe --binary-recon /tmp/recon
ElementEvil.exe: 304 functions, three two-byte edits inside functions at 0x2742, 0x3da3, 0x481a
neither on disk: 49.2 s, peak RSS 612 MB, score 0.9996, routes built/built, analysis_ms 12629/13292
    debug:   summarize L    11748 ms
    debug:   summarize R    11224 ms
    debug:   pair              20 ms
    debug:   compare            4 ms
both on disk: 24.4 s, peak RSS 590 MB, score 0.9996, routes read/read, analysis_ms 642/371
    debug:   left         inventory read (642 ms)  0e25d8a0a06d
    debug:   right        inventory read (371 ms)  3d93dca3aab8
    debug:   summarize L    11832 ms
    debug:   summarize R    11280 ms
    debug:   pair              60 ms
    debug:   compare            8 ms
the two reports are equal apart from timing and route: True
```

| the client pair | before | after the key shortcut | after the streaming summary |
|---|---|---|---|
| built / built | 83.6 s / 1.08 GB | 55.7 s / 804 MB | **49.2 s / 612 MB** |
| read / read | 57.4 s / 837 MB | 29.4 s / 699 MB | **24.4 s / 590 MB** |
| summarize, both sides | 27.3–28.8 s | 26–28 s | 23.0 s |
| pair | 12–20 ms | 12 ms | 20–60 ms |
| compare | 27.1–28.5 s | 4 ms | 4–8 ms |

The same program compared against itself — the probe's shape — is **37.6 s at a peak of 684 MB**, with
the compare stage at **0 ms**, 304 of 304 functions exact and 4,443,084 of 4,443,084 instructions equal,
score 1: it was 100 s at 1.0 GB before any of this, and 39.8 s at 742 MB after the key shortcut alone.
The corpus pair is unchanged, which is the point of measuring a pair whose answer is already known:
142 of 142 functions, 139 matched (127 exact, 12 changed), score 0.9705, compare 12 ms — and it is what
the goldens are made of, so the two comparison documents gained only the new field, with **127** and
**23** pairs proven identical in them. Checked by two tests in `CompareCliTests`
(`A_pair_of_identical_bodies_is_decided_without_decoding_it_again` and
`A_function_asked_for_by_name_is_still_listed_instruction_by_instruction`) and the two summary tests
above; the suite stood at **591 tests, 0 failures** then.

### The two scoring bugs that fixing the build test uncovered

**A function that moved was reported as a function that changed.** With the recipe fixed, the same source
compiled at the same flags and with one thing added — `-falign-functions=32`, which changes no statement —
compared at **0.8638: 78 of 142 functions changed**. Every difference was one shape:

```
addressing  mov dword ptr [esp],4019B0h   vs   mov dword ptr [esp],401A50h
reference   jmp near ptr .text+0x5D7      vs   jmp near ptr .text+0x657
```

The first is a function's *address* held in an immediate (`0x4019B0` is `_matherr`, which the symbol table
names), and the second is a branch target inside the function being compared. Both were being compared as
numbers, so both moved when the linker put the code somewhere else — which says a reconstruction is wrong
when it is only elsewhere. Both are identities now: an immediate that lands on something this side can
*name* is an address whether or not the container relocated it, and a branch that stays inside the function
being read is named by its offset from that function's entry, which is the one address two builds of a
function agree on. The same comparison is now **140 of 142 exact, score 0.9998**, and the two that remain
are real: one is an extra `jmp` the alignment produced, and one is a branch into `dispatch` at a genuinely
different offset.

**And the corpus says the same thing at scale.** `sample-debug.exe` against `sample-release.exe` — two
builds of one source at two optimization levels — is the comparison the M2 milestone is built on:

| | before | after |
|---|---|---|
| functions matched | 139 | 139 |
| exact | 56 | **127** |
| changed | 83 | 12 |
| instructions equal | 6,802 | 7,884 |
| score | 0.8373 | **0.9705** |
| the pair the similarity stage had to pair | 1 | 0 (it is body-identical now, which is the stronger statement) |

The pairing is unchanged — the same 139 functions, the same 3+3 unpaired — and 71 of them stopped being
"changed" because their difference was only ever where they sat. Both golden comparison documents were
regenerated for that, deliberately and not to make a run green: the whole difference in the C++ self
comparison is `references_named: 22 → 26` and `reference_classes.Absolute: 48 → 52`, which is the named
immediate becoming a reference at all.

## What the end-to-end build test was hiding

`A_rebuilt_image_compares_against_the_original_it_came_from` is the test that closes the loop the whole
tool is for: compile a reconstruction, compare it against the original, and require every function to
match. It returns early when no cross compiler is installed, and there was none on this machine, so it
had been passing in 0 ms for as long as this container has existed. Installing mingw made it run, and it
failed — both times for reasons that had nothing to do with the code it was testing.

**It asked for an input the project does not declare.** The harness copies the corpus binary into the
project as `inputs/sample-release.exe`, and the project's `[[input]]` says `file = "sample.exe"`. A
project names its own inputs; the copy helper used the corpus's name. The tool said so plainly — `input
"main" (original) is missing` — as a configuration error, which is why the failure was an exit code of 3
and not a comparison. `CopyInput` now takes the name to copy as, and the assertion that was hiding the
message behind `Assert.Equal(0, ...)` now carries the report: a failing comparison has to say what
differed.

**It built a different program from the one it compared against.** The project it constructs compiles at
`-O0`, and the corpus binary was built by `tools/build-corpus.sh` at `-O2 -g -Wall`. The corpus is
reproducible: running the script with this box's compiler regenerates `sample-release.exe` **byte for
byte** (`84668014ea9b…`), which is what says the recipe and not the compiler was the difference. With the
recipe's own flags the comparison is clean:

```
$ recon build --project …
  1 unit(s): 1 compiled, 0 cached in 117 ms; link linked; build/sample.exe (235676 bytes)
$ recon diff --project … build/sample.exe --summary --min-score 1.0
  functions    142 left, 142 right, 142 matched (142 exact, 0 changed), 0 folded, 0 only left, 0 only right
  instructions 8076 left, 8076 right, 8076 equal
  score        1
```

**It asked two opposite things of one file.** The comparison came *after* the test edited the source and
rebuilt, so it required an image the test had just asserted was *different* from the original to be the
original's code, function for function. The comparison now comes first, on the image built from the source
as it stands, and the edit-and-rebuild check follows it, claiming nothing about that image's code, which
is what it is for.

### What the fix turned up: a moved address is not a difference

With the test running for real, the interesting measurement became possible. Take the same source, the
same compiler, the same flags, and change *nothing* about the program — add `-falign-functions=32`:

```
$ recon diff --project … build/sample.exe --summary
  functions    142 left, 142 right, 142 matched (64 exact, 78 changed), 0 folded, 0 only left, 0 only right
  instructions 8076 left, 8077 right, 6977 equal
  score        0.8638
```

No function was rewritten; the functions moved. Every difference is the same shape:

```
addressing  mov dword ptr [esp],4019B0h   vs   mov dword ptr [esp],401A50h
```

and it is the same string literal at two addresses, with **no reference on either side**. The engine
normalizes the references it can name — imports, named data, functions; 26 of that function's
instructions are counted as relocated — but a bare `.rdata` address with nothing symbolizing it is
compared as a number, so a reconstruction that is right scores 0.86 for having put its strings somewhere
else. That is the number this tool exists to produce, so it is the next thing to fix rather than a
curiosity to note: pair unnamed data by what is at the address, and report an address that cannot be
paired as a difference.

*Footnote, added when this was fixed: this figure is from an older gcc. Re-measured with the compiler
this repository builds against, the same `-falign-functions=32` pair already scores **0.9998** — the
moved functions are the same functions, and only `fprintf.constprop.0` (an extra `jmp short add`) and
`dispatch.cold` (a real offset change) differ. Quote 0.9998 for the align pair, not 0.8638, which is
fixed twice over: once by the compiler, once by the work below.*

## An address that moved is not a difference, part two: what is at the address

The section above ended on the sentence "pair unnamed data by what is at the address, and report an
address that cannot be paired as a difference." This is that work.

The first thing measuring it properly did was correct its own premise. With the compiler this repository
builds against, the `-falign-functions=32` pair no longer scores 0.8638 — that figure was measured with
an older gcc and was fixed under us. What is still wrong is data that moved. Same source, same flags, one
more literal declared:

```c
const char *g_message = "corpus";
const char g_extra[32] = "one literal the second build has and the first does not";
```

```
$ recon diff --project /tmp/align test-shifted.exe --summary
  functions    142 left, 142 right, 142 matched (127 exact, 15 changed), 0 folded, 0 only left, 0 only right
  score        0.9934
```

15 functions "changed" for one added string. Every difference is a pair of addresses:

```
addressing  mov dword ptr [esp],40A044h   vs   mov dword ptr [esp],40A0A0h
reference   [.rdata+1232]                 vs   [.rdata+1344]
```

Both addresses are in `.rdata`, both hold the same bytes, and neither is named. Dumping all 52 differing
operands and classifying what sits at each address with a separate PE reader gave the shape of the
problem: strings, float constants, pointer tables and jump tables; **all 52 addresses carry a relocation**
(none is a raw number), and a window at the address pairs 50 of the 52 across the two builds. So the
information to pair them was there, and the comparison had thrown it away by comparing the number
instead.

The fix is a content identity, computed in `SideIndex` — the one file this round touched in production —
and substituted for an address that has no name:

* **`"literal"`** when there are at least four printable bytes at the address. The string itself, not its
  address, is what the two builds have in common.
* **`[w0 w1 w2]`** otherwise: the words at the address, each itself resolved by the same rules — a name
  (imports, exports, named data, functions), a nested identity one level deep, `->` for an address that
  has no name and nothing readable at it, or `0x%08x` for a plain word. The nesting is capped at one
  level and 40 characters, which is what lets a table of pointers to literals be identified by the
  literals rather than by whatever a further hop lands on — and code sections are never entered, so a
  pointer table can never be "identified" by the bytes of a function that happens to sit there.
* The **window is the bytes the instruction reads**, not a constant. This was a measurement bug of its
  own before it was a fix: a fixed 16-byte window paired 31 of 52, because the two builds pad the space
  after a constant differently. Reading exactly the operands' width took 0.9994 to **0.9999**: constants
  whose windows had reached into the word after them were being compared to words that were not theirs.
  One exception is register-indexed memory (`fld qword ptr [eax*4+X]`): iced's `MemorySize.GetSize()`
  truncates that to the register width — 4 — so the *instruction* addresses the table, not the word, and
  those operands get a fixed 12-byte window.

An identity replaces both the address and the positional tail the resolver had already spelled out —
`[.rdata+1232]` becomes the literal it points at — **except** `jump_table[...]` labels, which keep saying
which slot of which table is meant. A slot index is a fact about the code; where the table landed is not.
And a name is never overwritten by an identity: a reference with a symbol keeps the symbol.

Two things it deliberately refuses, and both are the point:

* **An unnamed immediate is admitted only when an identity exists.** A relocation pointing at an address
  is not evidence that the two addresses hold the same thing; if nothing is readable there, the two
  builds still differ there, and the comparison says so.
* **A branch target is admitted when it names something or stays inside its own function.** A target that
  neither names nor stays put is a real difference.

```
$ recon diff --project /tmp/align test-shifted.exe --summary
  functions    142 left, 142 right, 142 matched (141 exact, 1 changed), 0 folded, 0 only left, 0 only right
  instructions 8076 left, 8076 right, 8075 equal
  score        0.9999
  references   1158 named, 110 identified by content, 2372 unnamed
```

The one function left is a genuine difference, and it is worth naming because it is the counter-example
the fix has to keep: `__pow5mult_D2A` reads a mantissa word that is a function of the number being
converted (`0x0` versus `0x3f000000`), and `__gdtoa` reorders by bit length, so the two builds really do
read different words at the same shape of address. An engine that paired those would be wrong.

The count is in the model, not only in the summary: `model.references_identified` joins the two counters
that were already there (`references_named`, `references_unnamed`), the schema and its description say
what it means, and the summary line reads `references N named, M identified by content, K unnamed`. A
reviewer — or an agent — can now tell a comparison that paired addresses by what is at them from one that
was handed two identical builds. `recon diff --check-schema` on the pair prints `comparison matches
schema 0.1`.

Checked by 3 new tests: 2 in `CompareTests` — the synthetic fixture's literal moved (and, in the same
run, `Summary.Matched == Summary.Exact`, the score is 1.0 and `references_identified > 0`, so the test
cannot pass by comparing nothing) and the control that an address with nothing identifiable at it is
still a difference — and 1 in `MovedCodeTests`, which is the corpus's own `sample.c` with one literal
added: `printf.constprop.0`, `fprintf.constprop.0`, `fail` and `main` come back exact, the score is above
0.999, and the model's identified count is positive. **598 tests, 0 failed, 0 warnings.**

## `.pdata`: the compiler's own table of function extents

Every PE image built for a 64-bit Windows target carries a table in `.pdata` with one entry per
function: where it begins, where it ends, and where the unwinder's data for it lives. It is the only
thing in a binary that states where a function **ends** — a symbol table says where one begins and
leaves the end to the next symbol or to a guess — so a stripped image that has the table does not have
to estimate every size. This round made the analysis read it.

The item was filed as unprovable here, and that was wrong in a way worth keeping: **the corpus is
entirely 32-bit.** All 24 binaries in it are PE32, and x86 MinGW builds DWARF-based SEH, so no
`.pdata` appears anywhere in the repository's inputs and nothing in the suite could have noticed the
table, or its absence. A 64-bit MinGW build has it — 96 entries for a five-line C file, measured with
DWARF on the same source as the oracle. The measurement is in "What is next" #5 and re-makes from
`tests/corpus/pdata-seh/build.sh`; this is what was built on it.

**What changed.** The loader reads the table and keeps it faithful (`PeRuntimeFunction`: begin, end,
and the 64-bit form's unwind address; both widths — 8 bytes per entry on PE32, 12 on PE32+) and
`BinaryFunctionRange` carries it across the format boundary so the analysis never asks which format it
is holding. The analysis seeds a function at every entry that begins in a code section, with the
extent as its size, and records `pdata` in the function's `found_by`. Three things it deliberately does
not do: an entry that begins outside code seeds nothing (a table is a statement about functions); a
zero end means **no extent stated**, not "ends where the next entry begins"; and `pdata` is not in the
list of sources that outrank a pattern, because it names nothing — a signature is still free to name a
function the table only sized.

**What it was worth**, on the same source with `-s` against the same source with `-g`, DWARF as the
independent oracle:

| | before | after |
|---|---|---|
| functions found in the stripped build | 96 | **126** |
| of the 128 starts DWARF names | 96 | **126** (2 leaf functions, no unwind data, no call to them) |
| starts invented (not named by DWARF) | 0 | **0** |
| of the file's 96 table entries used | — | **96**, each with its extent as the size |
| functions with an unknown size | 96 of 96 | **30 of 126** |
| the build with DWARF | 128 functions, 127 named | unchanged: 128, 127 named |

**The bug underneath it.** The first run of the new code found nothing, and the reason was not in the
new code: `NumberOfRvaAndSizes` was read at the 32-bit offset for every image, and in a PE32+ header
that offset holds the high half of `SizeOfHeapReserve` — zero — so **no data directory of any 64-bit
image had ever been read**. No imports, no exports, no exception directory, no debug directory: every
directory on every PE32+ image this tool has ever loaded read as absent, and the tool said so quietly,
because an absent directory is a normal thing for an image to have. Nothing in the corpus could see it
— the corpus is all PE32 — and nothing else in the suite used a 64-bit binary. It is two lines of
offset arithmetic and a test that counts a real 64-bit image's imports now, but it is the reason this
round's own feature did not work on the first try, and the reason it is worth having a 64-bit binary in
the suite at all.

**What the fixture caught, and what it confirmed.** The synthetic 32-bit fixture writes its table in
the 8-byte form on purpose, and the first version of the reader read a third word anyway — taking the
next entry's begin for an unwind address, which is the kind of wrong answer that looks like a fact. Two
more things the fixture settled rather than assumed: an extent is not always the function's size (a
compiler pads to the next function, so a symbol that states 25 bytes beats a table that states 32 — the
real build agrees, 95 of its 96 extents equal DWARF's size and the one that differs is padding), and an
entry with no stated end must not quietly become one.

**Where this is not proven.** The table's 64-bit form is proven on one small MinGW build and the
loader's reading of it is compared entry by entry against a table the test parses itself, but there is
no large 64-bit PE in this workspace to run the analysis over — the 11.8 MB client is PE32 — and an
MSVC-produced x64 image (which is where `.pdata` is most complete: it carries unwind data for every
function, leaf ones included) has not been read. The 32-bit form is proven on the fixture only, for the
same reason: no PE32 binary here carries the table at all. What the round did verify end to end on a
real 11.8 MB program is that nothing else moved: `recon inventory` on the VB6 client still reports
**304 functions, 4,417,764 instructions, 414,282 xrefs**, and `recon vb6` still 73 objects (39 forms,
9 classes, 25 modules) — the same numbers as before the round.

Checked by 4 new tests in `PdataTests` (602 in the suite, 0 failed): every entry of a real 64-bit table
read out of the file's own bytes and compared field by field with the loader's reading; the analysis
seeding, sizing and refusing exactly as above; the fixture's four entries — the entry point, a thunk
whose size nothing else states, an entry in `.rdata`, and one with no end; and the stripped/unstripped
pair that carries the numbers in the table above. The corpus was re-validated after the loader change:
`tools/validate-corpus.sh` → **ok (24 binaries, PDB oracle on)**, and the six MinGW binaries relink
byte-identical.

## What is next, in the order the evidence points

*Written when these were the next things to do; each entry is marked done or open as it is taken, and
the open ones are re-measured before they are started — one of them (below) had a premise that was
simply wrong.*

**1. Pair unnamed data addresses.** *Done — see "An address that moved is not a difference, part two".*
An address with no name is compared by what is at it (the literal, the words, a nested identity), the
window is the width the instruction reads, `jump_table[...]` keeps its slot, and an address that cannot
be paired stays a difference: `0.9934` to **`0.9999`** on the same source with one literal added.

**2. Carry p-code into the inventory.** *Done — see "A p-code program in the inventory".* The p-code reader decodes all 680 procedures of the corpus, and
`recon inventory` still reports a p-code program as `isa: vb6-pcode` with one function named `entry`.
Until those meet, a p-code program is invisible to `diff`, `report`, the listings, and every question an
agent asks that is not `recon pcode` itself.

**3. The analysis makes three passes over every instruction where one would do.** *Done — see "One pass
over every instruction, where there were three".* The scan, the jump-table search and the prologue search
were three whole-image walks and are one; the conventions pass stayed a pass of its own, and the
measurement that says why (0.7 s at best, for facts re-derived over a range the body does not cover) is in
that section.

**4. A `vb6-pcode` toolchain profile**, now that 42 real p-code binaries exist to measure it off. The
rule is unchanged: no version, no linker and no Rich number unless a command printed it from a real
binary. *Done — see "A profile for a program that was never compiled to machine code".*

**5. `.pdata`: the compiler's own table of function extents, as seeds.** *Done — see "`.pdata`: the
compiler's own table of function extents".* The stripped 64-bit build went from 96 functions, every
size estimated, to **126 of DWARF's 128** with the 96 extents as sizes and no start invented; the
reading underneath it fixed a bug that made every data directory of every 64-bit image read as absent.
The measurement that opened the item is below, kept as it was written. It was filed as unprovable — "no binary in this
environment has a `.pdata` section" — and that was wrong in a specific way worth recording: it is true
of the 32-bit corpus, because x86 MinGW builds DWARF-based SEH, and false of a **64-bit** MinGW build,
where GCC emits the Windows x64 SEH tables. Measured with `x86_64-w64-mingw32-gcc -O2` on a five-line
C file — the file, the compiler line and the reading of the directory are `tests/corpus/pdata-seh/`,
where `bash build.sh /tmp/pdata` rebuilds the pair and prints the numbers below; two runs are
byte-identical and the binaries are build output, not repository material — `.pdata` is 0x480 bytes and
`.xdata` 0x448, the loader already reads both as ordinary sections, and the directory holds **96
`RUNTIME_FUNCTION` entries**: every one inside `.text`, every one 16-byte aligned, every one with its
unwind info in `.xdata`, extents from 1 to 6,024 bytes summing to 96.0% of `.text`. What they are worth,
with DWARF as the oracle on the same source: all 96 starts are DWARF function starts too, and against
the **stripped** build — DWARF truth 128 starts, the analyser's own seeds 96, every one genuine — the
entries cover 66 of those and add **30 that nothing else finds**, so the union is **126 of 128** with no
false start introduced; and they carry the extent, where the stripped inventory today reports all 96
functions with an unknown size. The work is to parse the entries (12 bytes each on PE32+, 8 on PE32),
seed from them with their own source name, use the extent as the size, and hold the result to both
builds: 96 → 126 on the stripped one, 128 → 128 on the other. The pair, its two project files and the
entry reading re-make in under a second from that script.

**6. The last two, and why they stay open.** A PDB written by Visual Studio's own `link.exe` needs
Windows, and every PDB this repository has read came from `lld-link`; `.pdata` was in that pair and is
now a measurement (above), which leaves this one genuinely waiting for a host. Relinking an image
without GNU ld is a documented refusal, not a gap: `recon link` says why instead of failing later, and
the relink is proven mechanical (an MSVC-ABI binary relinked by a GNU ld driver comes back identical),
so nothing is blocked today.

## Which build made these bytes: `--flags`

Two things decide a binary's bytes: what the source says, and what the compiler was told to do with
it. The permuter began with the first — write the same function a different way, build it, and let the
score say which way the original was written. The second is the larger lever, and until now it was not
pulled at all: a reconstruction whose source is right but whose optimization level is wrong is wrong
in every function at once, and no edit to one function will ever fix it.

`recon permute --flags` adds a candidate for every optimization level the unit's toolchain offers,
tried before the edits for exactly that reason. The levels are profile data:

```toml
[compile.optimization]
levels = ["-O0", "-O1", "-O2", "-O3", "-Os", "-Og"]   # gcc-base, which the clang profiles extend
levels = ["/Od", "/O1", "/O2", "/Ox"]                 # msvc-6, msvc-2008, clang-19-msvc
```

Measured with the machine's own gcc on `fib` from the ELF corpus, from a project whose `[defaults]`
say `-O0` while the original was built `-O2 -g`:

```
baseline  0.182   -O2  0.905   -O3  0.905   -Os  0.429   -O1  0.381   -Og  0.333
```

The level the original was built at is the winner, found by the compare engine's own score rather
than by any rule about what `-O2` looks like. `-O3` ties for `fib`, which it would: it emits the same
code for that function. The winner is named in `best_optimization`, and `best_source` is deliberately
left null with `best_write_error` saying why — a flag is not a source edit, and writing the file out
unchanged would look like a result while teaching nothing.

Two things about this were wrong the first time and are worth the space:

- **The last level in a flag list is the one in force**, because that is the one a compiler acts on.
  Asked what level `["-c", "-g", "-O2", "-O0"]` builds at, the answer is `-O0`; reporting `-O2`
  describes a build that is not happening, and substituting into it changes a flag that `[defaults]`
  had already overridden — a candidate that builds the same bytes as the baseline and silently
  proves nothing.
- **A profile with no levels is left alone.** Appending `-O0` to a compiler this profile has never
  claimed to understand is a guess with a compiler invocation wrapped around it, and `vb6-native`
  has no `[compile]` section at all.

`add` from the same corpus cannot be used to check any of this: it is a single `lea` at every level
from `-O1` up, and its only difference is padding, which the compare engine ignores — so `-O1` scores
1.0 against an `-O2` original and the test would pass for the wrong reason. `fib` recurses, and gcc
lays it out differently at every level.

## Reading ARM

The plan's line for this milestone is *non-x86 instruction sets*, and it is the item the previous
milestone left open with a reason rather than a gap: an AArch64 image was inventoried and not decoded,
because reading ARM bytes as x86 invents a program out of them. There is now an AArch64 decoder in
`src/Recon.Core/Analysis/Arm64Decoder.cs`, and what changed is not that a second decoder appeared but
that a **corpus binary did** — `sample-macho-arm64`, built against the same source as the rest of the
corpus, with a `.dSYM` bundle and a linker `.map` beside it. That gives two records of what the code
says that were produced by the linker rather than by this tool, which is the difference between
checking a decoder and taking its word.

**Why a partial decoder is safe here and would not be on x86.** Every AArch64 instruction is four
bytes. So:

* where the next instruction starts is `address + 4`, which is arithmetic and not decoding — a word
  this decoder does not recognise is four bytes that are skipped, not a boundary that is lost;
* a window boundary cannot cut an instruction in half. `StreamingAnalysisTests` caught a five-byte
  `call` straddling a window on x86, after which the rest of the section was read from inside it. That
  class of bug does not exist at a fixed instruction size, which is why the windowed pass needed no
  change for ARM;
* an unrecognised word costs a mnemonic and nothing else. It is printed as `.inst 0x…` — the form a
  disassembler uses for a word it will not name — and counted, never as `(bad)`, because calling it
  invalid would be a claim about the program rather than about this decoder's vocabulary.

What it decodes is chosen by what a call graph and a data reference need: every form of branch
(`b`, `bl`, `b.cond`, `cbz`/`cbnz`, `tbz`/`tbnz`, `br`, `blr`, `ret`), the `adr`/`adrp` pair that a
position-independent binary uses for every global it touches, the `stp`/`ldp` pairs that open and
close a frame, the immediate and shifted-register arithmetic, the `movz`/`movk`/`movn` triple that
builds a constant too large for one instruction, `madd`/`msub`/`mul`/`mneg`, and the load/store
group in its register-offset and unsigned-immediate forms. The bitmask immediate (`and w8, w21,
#0x7`) is reconstructed from `immr`/`imms`/`N` rather than printed as those fields.

**How it was checked, since no ARM disassembler exists on this machine** — `objdump` will not read
Mach-O and there is no `capstone`:

* **Every call is a target the linker named.** Each of the 14 `bl` instructions in `__text` is
  decoded to a target, and each target is an address the `.map` lists as the start of a function
  (`0x6ec` → `_fib` at `0x6c4`, `0x750` → `_bump` at `0x780`, `0x838` → `_add` at `0x5b0`, …). A
  single bit misplaced in a branch offset lands in the middle of some other function and is caught
  here, which is why this is the assertion and not the instruction count.
* **The globals are where the relocation records put them.** `adr x22` in `_loop_sum` decodes to
  `0x100008010`, which is `_g_table`; the `adrp`/`str` pair at its end decodes to `0x100008040`,
  which is `_g_counter`. Both come from the dSYM's own relocation list, not from this tool.
* **Coverage**: 206 of 206 instructions in `__text`, none falling through to `.inst`.
* **The whole inventory** of that binary: 224 instructions, 14 functions, 46 xrefs, no jump table,
  and an empty `problems[]` — the image is no longer reported as something the tool refused to read.

Two bugs came out of writing it, and both are the kind a corpus cannot catch, because **the corpus
does not contain a case that reaches them**:

* **A sign extension written in unsigned arithmetic.** Branch offsets are 26 bits, sign-extended; the
  shift that should have carried the sign did not, so a backward branch decoded to a target four
  gigabytes above the section. All 14 calls in the corpus are forward, and every one of them was
  right; the loops, which are not, would all have been wrong.
* **A bitmask immediate rotated the wrong way.** `ROR`, not `ROL`. Every logical immediate in the
  corpus sits at `immr == 0`, where a rotation and its mirror image agree, so the corpus reads
  correctly either way. The regression test supplies the case the corpus lacks: four ones rotated
  right by four places, which is `0xf0000000` and not `0x000000f0`.

Not decoded: the sign-extending loads (`ldrsb`, `ldrsw`), the SIMD and floating-point groups, and
32-bit ARM (A32/T32) — the last is still refused with the reason in `problems[]`, because in Thumb the
instructions are two or four bytes and the fixed-size argument that licenses all of the above does
not hold. That is the honest remainder, and it is a remainder rather than a failure: a decoder that
says which words it did not name can be improved one encoding at a time, which is exactly how this one
reached 206 of 206.

## The Visual C++ 5.x profile

The runtime DLLs beside a Visual Basic 6 program used to be the tool's silence: they stamp linker
versions from the 5.x line and the tool named no toolchain for them. The TODO item that recorded this
said why — a profile wants one binary carrying *both* a 5.x stamp and product ids measured from a
real file, "and none of these does". That turned out to be false, and the way it turned out is worth
writing down because it is the argument for measuring instead of reasoning.

**VBA6.DLL carries a Rich header after all.** Four records: 970 objects with no product id, nine
assembler objects, `prod_id=0x0013 build=8078`, and — last — the resource converter. 0x0013 is
`linker_5.12` in the tool's own table, and the optional header stamps 5.12. Two independent
measurements of one tool, agreeing without either being assumed. Nothing about the file had changed;
the claim that it had no Rich header had simply never been checked against the bytes.

So `msvc-5` exists, and it is shaped by the four files it was measured on:

* **Two rules that fire separately.** VBA6.DLL carries the stamp and the product id; VB6.EXE,
  VB6IDE.DLL and MSVBVM60.DLL stamp 5.2 and carry no Rich header at all. A rule demanding both would
  look at three real binaries it can name and report nothing, so either is enough.
* **No overlap with `msvc-6`.** 5.0–5.99 against 6.0–6.99, and 0x0002/0x0010/0x0013 against 0x0004.
  LINK.EXE, C2.EXE and CVPACK.EXE carry 0x0013 from import libraries built before their own toolset
  shipped, so `msvc-5` is reported beside `msvc-6` for them — which is information, not a
  contradiction, and `msvc-6` still comes first because it has more kinds of evidence.
* **What is still unknown is still unknown.** MSO97RT.DLL stamps 3.10, older than the 5.x line and
  older than any product id measured here, so no profile claims it.
* **No invented paths.** The profile gives `cl.exe`'s flags, which are the same in every version of
  Visual C++, and deliberately omits the INCLUDE directory, because the layout of a Visual C++ 5
  installation is not something this machine can read. A profile may leave a path out; it may not
  make one up.

## Reading a `.LIB`: the objects a link can pull in

The libraries beside a binary are where a reconstruction's runtime objects come from, and until now
the tool answered them with a magic number: *"it starts with 0x213C, which is neither ELF, MZ nor
Mach-O"*. `src/Recon.Core/Archive/CoffArchive.cs` reads them, and `recon lib <file>` lists what is
inside.

**An archive is not a program, and is not loaded as one.** It has no entry point, no sections to
decode, no addresses. Making it another `IBinaryImage` would have meant widening the inventory
schema's `format` enum — the contract every consumer validates against — with something that is not a
program, so that `inventory` could emit a document with no functions in it. Instead it is read on its
own, by path, which is also how a library actually arrives: beside the binary, not as it. The loader
says so when handed one:

```
$ recon inventory            # a project whose input is a .lib
VBAEXE6.LIB is a COFF archive, not a program: `recon lib "VBAEXE6.LIB"` lists its members
```

**What makes a library worth reading is `@comp.id`.** Every object Microsoft's compilers produce
carries an absolute symbol of that name whose value is `(product id << 16) | build` — the same
encoding, and the same product ids, as the PE Rich header. So one library yields a bill of materials:
which compiler built each object in it. That is attribution for the parts of a program that were not
compiled for it.

```
$ recon lib VBAEXE6.LIB
VBAEXE6.LIB: 4 member(s), 1 object(s), 0 import record(s)
member                                             kind       size  what it is
first linker member                                linker     54    symbol index
second linker member                               linker     56    symbol index
long name table                                    longnames  50    long name table
d:\vbadev\R6W32ND\presplit\vbarun\obj\natsupp.obj  object     664   masm_6.13 build 7299
  built by masm_6.13 build 7299 (1 object)
```

**Measured, on three archives from three producers.** The one above is the small case, and it carries
its own cross-check: the object's `@comp.id` is `0x000E1C83` — product id 14, build 7299 — and 7299 is
the number in the banner the same object carries as a string, *"Microsoft (R) Macro Assembler Version
6.13.7299"*. Two records of one fact, written by one tool, read here by two different paths; a reader
that disagreed with itself would be wrong in a way no amount of unit testing would show.

The two others cover what the small file cannot:

* **`windows.0.52.0.lib`** — a real Microsoft import library, 21,667 members. Every one is classified
  and the arithmetic closes: 2 symbol indexes, 1 long-name table, 1,113 import-descriptor objects and
  20,551 short import records. A walk that loses its place anywhere in five megabytes shows up as a
  count that does not add up. The import symbols are then checked against the archive's *own*
  big-endian symbol index — a second, separately written list of what the library offers, read by the
  test rather than by the parser, so the two are not the same code agreeing with itself.
* **`libkernel32.a`** — GNU ar, 1,716 COFF objects, names held in a 37 KB long-name table and
  referred to as `/N`. Two librarians, two conventions for the same container.

**The bug worth finding was the container's other tenant.** `!<arch>` is one format with two possible
contents, and the first archive to hand on a Linux machine — `libgcc.a` — is a Unix `.a` full of ELF
objects. Read as COFF, its members report a machine called **0x457F** and twenty-six sections. That
number is the first two bytes of `\x7FELF`, little endian; the sections are the ELF class and
endianness fields. It is a confident, entirely wrong answer, of exactly the kind this tool exists not
to give: the reader did not fail, it succeeded at reading the wrong format. ELF members are now named
as ELF and left alone, and the archive says it is a Unix `.a`.

Two details that cost less to get right than to get wrong: a member's padding byte is not part of the
member, and a long name from the table ends in the same slash a short name does — both librarians use
it as a terminator, not as part of the name.

## A VB6 program, read as the structure it is

`recon vb6 <file>` walks the chain a Visual Basic 5/6 program is made of: the entry point pushes the
address of the VB header, the header names the project data, the project data names the object table,
and the object table lists one descriptor per form, class and standard module.

```
$ recon vb6 "Basic Server.exe"
/home/user/vb6-wild/Basic Server.exe: VB5! header: VB6 *, native code at rva 0x5000
  template 0x1F4, 2 object(s), compiled from "*\AC:\Program Files\Microsoft Visual Studio\VB98\Basic Server\Basic Server.vbp"
  code rva 0x34B0..0x49F0 (5440 bytes); native x86
object  kind             methods  type flags
Main    form             6        0x00018083
Local   standard module  10       0x00018001
```

**Checked against programs VB6 actually built, not against a fixture.** Five of them, and the object
tables come out as:

| program | objects | forms / classes / modules |
| --- | --- | --- |
| `ElementEvil.exe` | 73 | 39 / 9 / 25 |
| `VISDATA.EXE` | 36 | 34 / 1 / 1 |
| `XiasporaServer.exe` | 10 | 1 / 0 / 9 |
| `Basic Server.exe` | 2 | 1 / 0 / 1 |

A wrong stride or a wrong offset could not produce that: 121 objects, every one with a name that reads
as the identifier a programmer wrote, every one a kind the compiler emits, in the order the project
declared them. The values are pinned in the tests, so a change that starts reading a different field
shows up as a different program.

**The build path is still in the file.** The compiler records where it was run from, UTF-16, and
VISDATA — Microsoft's own sample, from the VB6 CD — says it was built from
`d:\sources\vb98\vbsamp\samples\visdata\visdata.vbp`. That is a check on the field's offset and
its encoding in one, and it is also the most human thing in any binary this tool reads.

**Two things that looked like checks and are not.** The object table also carries a *compiled* count,
which is 76 where the declared count is 73 in ElementEvil — so the two are not one number written
twice, and insisting they agree would be inventing a rule the compiler does not follow; it is reported
as the file's own number. And the declared object count is authoritative: a file holding fewer
descriptors than it declares gets the count it asked for, read out of whatever follows. In the test
that constructs exactly that, the phantom object comes back called **"MZ"** — the start of the DOS
header, four bytes past a zeroed pointer — and no reader can tell that from a project with a
three-character object name. What *is* a check is the compile-state word, `0x000A` in all five
programs, which says whether the pointer landed on an object table at all.

### What is not done, and why it is not a guess

The other half of "read a p-code program" is the procedure table and the instruction stream. A
method's code would be found through the object's method table — `ObjectInfo+0x24` — and in every real
program here that field is not a pointer: Basic Server's form has `0x00624830` there, four megabytes
past the end of a 32 KB file, with the method count in the same structure reading zero. The reason is
legible: only the p-code compiler needs that table, so in a native build the linker simply never
filled it in. The other candidate in the descriptor, `+0x20`, turned out on inspection to be a pool of
*object* names — "Main", "Local", "Server" — followed by type-library GUIDs.

So reading procedures needs a p-code program, and this machine has none: every VB6 file available is a
native compilation. That is recorded as the item it is, rather than worked around with a fixture that
would only agree with the reader that wrote it — the same reason no `vb6-pcode` profile has been
written.

### A discrepancy with an independent reconstruction

The Rust `visualbasic` crate publishes an opcode table taken from MSVBVM60.DLL 6.00.9848, and models
**six** dispatch tables where this tool measures **five** in the runtime here. Their handler addresses
come from a different build, so addresses cannot be compared — but the table count can, and it
disagrees. Either their build has a sixth table or one of their leads is a split of one of these. It
needs that build's runtime to settle; it is in `TODO.md` rather than guessed at.

## The p-code opcode table, read out of the runtime

Visual Basic 6 compiles to native x86 or to p-code, and p-code is not machine code: it is a byte
stream for the interpreter in `MSVBVM60.DLL`. The format was never published, which is why the opcode
tables in circulation are reconstructions of it, and why the note here used to be that decoding
p-code wants a specimen this machine does not have — it arrived later, 42 programs of it, and *A p-code
program, decoded* below is what reading them says.

That was true about *programs* and beside the point about *tables*. The table is not in a p-code
program; it is in the runtime, and `MSVBVM60.DLL` ships with Visual Basic 6, which is on this machine.

**The interpreter dispatches with twelve bytes, everywhere in it:**

```asm
33 c0               xor  eax, eax
8a 06               mov  al, [esi]              ; the opcode byte
46                  inc  esi                    ; past it
ff 24 85 <imm32>    jmp  dword ptr [eax*4+table]
```

Searching the runtime for that sequence finds **294 dispatch sites naming 6 distinct tables**, and the
imbalance between them is the useful part:

| table | lead byte | entries | distinct handlers | sites that use it | most-shared handler |
| --- | --- | --- | --- | --- | --- |
| `0x10AA24` | — (primary) | 256 | **252** | **289** | 2 slots |
| `0x10AE24` | `0xFB` | 256 | 165 | 1 | 63 slots |
| `0x10B224` | `0xFC` | 256 | 188 | 1 | 31 slots |
| `0x10B624` | `0xFD` | 256 | 204 | 1 | 16 slots |
| `0x10BA24` | `0xFE` | 256 | 168 | 1 | 59 slots |
| `0x10BE24` | `0xFF` | **71** | 71 | 1 | 1 slot |

**289 sites share one table** because every handler ends by dispatching the next opcode — which is what
a threaded interpreter looks like from the outside, and it identifies the main table without anyone
having to decide which is which. The other five are reached once each, from five consecutive sites at
the interpreter's entry, each with its own handler twelve bytes from the last.

**Six, not five, and the sixth is why the scan was rewritten.** It asserted five for two revisions of
this work: the scan matched the eleven-byte dispatch sequence exactly, and 0xFF's site is not that
sequence — it is a *guarded* dispatcher, `cmp eax, 46h` / `ja <unhandled>` in front of the same jump,
so the interpreter rejects everything above 0x46 as a case before indexing. A scan matching the jump
itself (`ff 24 85` and the table address after it, with a `mov al, [esi]` `inc esi` in the sixteen bytes
before it) finds it, and finds nothing that is not a dispatch: 294 sites, 6 tables. The guard is worth
more than the extra site, because it is the interpreter's own statement about the table's length — the
table is **71 entries**, and the 185 slots after it in the file are the next thing the assembler wrote:
bytes that point into `.data` and, read as handlers, measure strings and relocation tables. The bound is
read from the guard (`cmp eax, 46h` → 71) and a slot only counts as a handler if it points into a
section the file marks as code.

**Counting is what says the main table is the opcode table.** Its 256 entries hold **252 distinct
handlers**: an opcode and a handler are the same thing there. The five others hold 165, 188, 204, 168
and 71, and the four *unguarded* ones each send a large block of their slots to a shared handler — and
all four agree on *which* handler, so it is where a case the interpreter does not handle goes. Two
shapes, then: one entry per operation, and separately a dispatch on something with cases to reject.

So the Visual Basic 6 p-code instruction set is **256 opcodes**, and each opcode's handler is an
address in the runtime's own `ENGINE` section — read out of the shipped file rather than taken on
anybody's word. Checked by 4 tests against the real `MSVBVM60.DLL`, with an ordinary DLL as the control
that finds nothing.

### What each opcode does, read out of its own handler

A handler address is a place to look. `recon opcodes` looks, for all 256 at once: it decodes the
handler the runtime dispatches to, follows its branches, and stops where the handler dispatches the
next opcode again or leaves for its caller — the handler's own code is the bound on reading it.

What comes back is what the handler calls, how far it moves the instruction pointer, how many slots of
the operand stack it moves, and whether it can raise — the last two read out of the same handlers and
written up under *What each opcode does to the stack, and which of them can raise*.

```console
$ recon opcodes ../vb6-inputs/msvbvm60.dll
inputs/msvbvm60.dll: 6.00.9848 — 6 table(s), 1536 opcodes, 159 named, 373 unhandled
  dispatch table at rva 0x10AA24, reached from 294 site(s)
  lead byte 0xFB: table at rva 0x10AE24, 254 of 256 opcodes measured, claimed by 0xFB
  ...
  raises through rva 0x3852C
  table 0x10AA24 (256 opcodes)
opcode     handler   size  name                calls                       stack  raise
0x00       0x108D56  2     (no name)                                       0
0x0D       0x10A43B  5     (no name)                                       ?      raise
0x2A       0x10A8A8  1     vbaStrCat           __vbaStrCat                 -1
0x4A       0x10A8D5  1     vbaLenBstr          __vbaLenBstr                0
0xFE 0x8C  0x10E3F0  5     vbaNextEachVar      via __vbaNextEachVar        ?
0xFB 0x00  -         1     (unhandled)                                     ?      raise
```

(the sample is abridged: `stack` is in dwords of operand stack, `?` where the handler's paths do not
fix an amount, and `raise` marks an opcode that can leave through the runtime's raiser.)

**Names are evidence or they are nothing.** A name is derived from the call a handler makes, and only
when there is exactly one call that is not shared with the rest of the runtime: a call most handlers
make is scaffolding — the interpreter's own housekeeping — and cannot be what distinguishes one opcode
from another. **159 of the 1,536 rows are named**, 122 of them differently. The name is the runtime function's own
name with the C decoration taken off — `__vbaStrCat` is reported as `vbaStrCat`, `rtcMidCharBstr
(MSVBVM60.DLL)` as `rtcMidCharBstr` — and what that produces is a readable instruction set read out of
the file: `vbaInStr`, `vbaUbound`, `vbaFileOpen`, `vbaVarLike`, `vbaObjIs`, `vbaLineInputStr`,
`vbaForEachCollVar`, `vbaNextEachVar`. The reference's mnemonics for the same rows — `FnLBound`,
`ConcatStr`, `MidStr` — are a naming tradition rather than evidence from this file, so they are not
adopted, and where the two disagree the report keeps its own. The report says why in words about the
1,004 rows it leaves unnamed besides the 373 unimplemented ones — *the handler calls nothing that is
not shared with other opcodes: what it does is in its own code* — because a name taken from the wrong
call is exactly the plausible reconstruction this reader exists to replace. An import the file names only by ordinal is reported as
the number it is, `OLEAUT32.dll!#150`, and not turned into a name.

**Three kinds of evidence, kept apart.** A handler can call an export; it can call a routine it loaded
into a register first (`mov ebx, 66111940h` … `call ebx`), which the walk resolves because the address
is right there in the code; or it can call a helper that is a preface around the export that does the
work, which is followed **one level** — straight-line and through unconditional jumps, never through a
conditional one, because a helper that tests something has an error path and the first thing on the
runtime's error path is the unwind. That last distinction is not hypothetical: following conditional
branches named 188 opcodes `RtlUnwind`, and named 15 rows `__vbaStrComp` besides. The report separates
what a handler *calls* from what it *reaches* (`calls` and `reached_calls` in the JSON, `via` in the
table), and a name is only taken from the second kind when the handler calls no export itself: a
second-hand observation never replaces a first-hand one.

**The names were cross-checked against the other reconstruction, and it agrees.** The reference table
names 159 of the same rows, and it names them *its own way*: `vbaStrCat` there is `ConcatStr`,
`vbaLbound` is `FnLBound`, `vbaI4Str` is `CI4Str`. Comparing the two means stripping the conventions
(the `vba`/`Fn`/`C` prefixes) and asking whether one name contains the other or the two share a
distinctive word: of the 159, **76 are the same function name** and **70 more share the word** — 92% —
and the 13 that share no word are the same operations under different words: `vbaCastObj`/`CastAd`,
`vbaGet3`/`GetRec3`, `vbaPut4`/`PutRec4`, `vbaLsetFixstr`/`StFixedStr`, `vbaPowerR8`/`PwrR8R8`. Two
reconstructions made independently, by different means, naming the same function for the same row is
the kind of agreement this project asks for; `tools/compare-opcodes.py` prints this comparison and the
operand one beside it.

**A slot the runtime does not implement is not an instruction.** 373 of the 1,536 rows are unhandled,
and they are two different things: **185** slots with no address in them at all, and **188** the
runtime sends to the one handler that raises (`0x1105C2`: `push 33h` … into the unwind). Those 188 all
share one handler, and the name the evidence gives them is `RtlUnwind` — which is the error path
talking, not the opcode. They get no name and a basis that says which of the two kinds they are, and
the reader takes "the runtime has no case for this byte" from the dispatch table's own guard (`cmp
eax, 46h; ja`) rather than inferring it. A cross-check fell out of this: the independent table marks
**187** rows `InvalidExcode`, and they are the same rows.

**Lengths come from the interpreter's own arithmetic.** The fetch reads the next opcode from `[esi+K]`
and moves `esi` past that byte, so the bytes the instruction consumed are what the path consumed before
the fetch, plus `K`, plus the opcode byte itself. Shapes that need more than that:

- a handler that reads its operand *into* `esi` — a branch, which goes wherever the operand says —
  never moves the pointer past it, so there the operand's width, which the extent of what was read
  states, is the length;
- a handler that hands its last instructions to shared code (a dispatcher several handlers reach) is
  classified by what it did before the hand-off: a path whose *only* act is the jump is a body two
  opcodes share, and the operand is read in the code it lands in — that is what makes `FStVarNoPop`
  three bytes where its own code is four instructions long, and it is the same relationship as the
  prefix at 0x23 in front of 0x31's handler. A path that hands off after it has read its operands is
  measured from what it read, not from where the shared code dispatches, which is what the 25 variable
  operations of the first lead table are: `ImpVar`, `EqvVar`, `ModVar` and the comparisons all read a
  word at `[esi]` and then fetch at `[esi+2]`, and counting the fetch's position would count the
  operand twice;
- a handler that moves the pointer by something the walk cannot follow — `sub esi, ecx`, restoring a
  saved pointer — has a fetch whose position means nothing, so the length is the operand it read: that
  is how the completion-callback opcodes of the sixth table come out at 5 and 7;
- a handler that leaves through the interpreter's exit — `mov esp,esi` … `jmp ecx`, the end of a
  procedure — consumed whatever it read and nothing more. An exit reached by a hand-off counts only
  when the handed-off code read or moved something for this instruction; the shared code also leaves
  for reasons of its own, and one of those paths is a byte that belongs to nobody.

The running total travels with the path the walk took rather than being summed afterwards, because two
paths through one handler can consume different amounts, and adding them up would be adding up bytes
that were never consumed one after another. **Three rows of the 1,536 are left with two lengths and no
single one** — `0xFB 0x87`, `0xFB 0x88` and `0xFC 0x85`, each a conditional jump that may or may not
have moved the pointer over a byte, with a dispatch on each side — and every other row has exactly one.
Two rows that used to have two are now one, and both were this reader's fault: 0x09, the API call, was
measured at 5 and 13, the 13 being the *next* handler's operands reached by falling through a call that
does not return; 0x0D, the late-bound call, was 5 and 6, the 6 being an error path followed into shared
code. The file, and the reference, say 5 for both.

**Six tables, and each row says which one it came from.** The report publishes every table with the
lead byte that selects it, the primary opcodes whose handlers dispatch through it (`claimed_by`, so a
reader can check the pairing rather than trust it), its length where the interpreter states one, and
how many of its rows were measured. A lead instruction is not a byte of the primary table: `ff 2e` is
the lead byte 0xFF and the sub-opcode 0x2E, and reading 0x2E in the primary table would answer with a
different instruction — which is what publishing one table used to do.

**The lengths were cross-checked against somebody else's table — row by row, all of them.** The Rust
`visualbasic` crate's `opcodes.csv` is the same shape this table is: a primary table and five more, one
per lead byte 0xFB to 0xFF, 1,536 rows. It states a length for **1,340** of them, and this reader states
one for **1,351** — 1,348 rows with a single length and 3 with two readings. **Of the 1,340 rows where
both state a number, 1,327 agree — 99.0%.** The thirteen that do not are listed rather than smoothed
over. The comparison is reproducible rather than remembered: `tools/compare-opcodes.py --ours
opcodes.json --theirs opcodes.csv` prints this table and exits non-zero if a difference is not one of the
shapes below.

Thirteen disagreements, then:

| row | this reader | the crate | the bytes, read by hand |
| --- | --- | --- | --- |
| `0xFB 0x87`, `0xFB 0x88`, `0xFC 0x85` | `1` or `2` | 1 | a conditional jump that may or may not have moved the pointer, with a dispatch on each side: this reader states both, the crate states the shorter |
| `0xFC 0x64` | 3 | 2 | reads a word operand; the crate counts it as two bytes |
| `0xFE 0x7E`, `0x86` | 3 | 5 | the sub-handler hands off before reading, so only the shared code's own reads count here |
| `0xFE 0x95`, `0x96` | 3 | 7 | **variable by construction**: `movsx eax,[esi]` / `add esi,2` / `add esi,eax` skips a table whose length is in the stream, so 3 is the part the handler always consumes and the crate's 7 is one instance of a table with four bytes of entries |
| `0xFE 0x8C` | 5 | 9 | `mov ebx,imm` / `movsx eax,[esi]` / `add esi,2` / `call ebx`, and the code it calls reads a second word (`0x10975D: movzx eax,[esi]` / `add esi,2`) — four operand bytes, five with the lead |
| `0xFE 0x9F` | 9 | 5 | reads a word, a dword and a byte on one path, and leaves through an exit |
| `0xFE 0xAB` | 11 | 1 | reads `[esi]` `[esi+2]` `[esi+4]` `[esi+8]` before dispatching |
| `0xFE 0xC3`, `0xC5` | 11 | 1 | `mov ebx,6` / `mov ebx,7` then `jmp 0x109B0B`, which reads a word and two dwords and does `add esi,10h`: eleven with the lead byte, and `mov [edi+ebp],bx` writes 6 and 7 into the variant's type field — `VT_CY` and `VT_DATE`, which is the crate's own naming for these two rows, confirmed by the code that implements them |

Where the two tables differ, the file is the tiebreak, and on four of these rows it settles the question
against the crate: 0xC3, 0xC5 and 0xAB read more than one byte of operand (a currency literal is not one
byte long, and the code that reads it adds ten to the pointer), and 0x8C reads four, in two words, in the
two routines it is made of. Two rows — 0x95 and 0x96, `OnGosub` and `OnGoto` — have no single length at
all, because they skip a table whose size is written into the stream they are read from; the report gives
the fixed part, and calling either number "the length" would be wrong for the other's program. The
remaining three rows are this reader stating both readings where the crate states one. That is the shape
of evidence this project asks for — a number measured out of the file, and a number somebody else
independently wrote down agreeing with 99% of it — and every place they differ has been read rather than
assumed.

Checked by 20 tests (`tests/Recon.Tests/OpcodesTests.cs`), most of them against the real runtime. The
version the report prints — **6.00.9848** — is read out of the file's own version resource, so the
numbers here are attributable to a build rather than to "some MSVBVM60".

What is *not* claimed: what the secondary tables dispatch on — the type of an operand, or the shape of
the value on the stack — is not decided, and guessing would be the same substitution of a plausible
table for a measured one that this exists to avoid. Nor is the sub-opcode byte of a lead instruction
given a second name: 0xFF's table is 71 instructions selected by the byte after the lead, and each is
reported as the instruction it is under that lead byte. And 1,383 of the 1,536 rows are unnamed in the
sense that matters — the handler says which runtime function the opcode calls and how long it is, which
is where a reading of what it *means* starts, not where it ends.

### What each opcode does to the stack, and which of them can raise

A listing needs three more things about an opcode: how many slots it takes off the operand stack, how
many it leaves, and whether it can leave through an error instead. `recon opcodes` states all three per
row — `stack_effect` where there is one number, `stack_effects` where the paths disagree, `raises`,
`outside_jumps` for the addresses outside the handler its paths leave by, and a `stack_basis` sentence
that says what the row's number is made of. The raiser those flags are measured against is in the
summary as `raiser_rva` (0x3852C here, 0x6603852C loaded), so the flags can be checked rather than
trusted.

**The number is the machine stack, in dwords, on the paths that hand the next opcode back.** In this
interpreter the operand stack *is* the machine stack: a handler that concatenates two strings is
`call __vbaStrCat` / `push eax` / fetch / dispatch, and the two operands were pushed by earlier
instructions and are removed by the call's own `ret 8`. So the effect is read out of the handler's
code: `push` and `pushfd` add one, `pop` and `popfd` take one, `pushad` and `popad` move eight,
`add esp, imm` and `sub esp, imm` move what they state, and a call removes what the callee's own
`ret N` pops — which is a fact about the callee, read once and remembered. Anything that moves the
stack pointer by something the walk cannot see — a call through a pointer, `mov esp`/`leave`, a callee
whose own returns disagree — ends that path's arithmetic, and the row says so in words instead of
stating a number.

The effect travels with the path, for the same reason the consumed count does: two paths through one
handler can leave the stack differently, and adding them up would be adding up two instructions that
were never executed one after the other. **Of the 1,163 implemented rows, 806 have a single effect and
2 have two** (−2 and 0, −2 and 1: a handler that reads an operand and may or may not push a result),
and the other 355 have none, each with a reason rather than a blank:

| why a row has no effect | rows |
| --- | --- |
| every path leaves the interpreter — it raises, or calls a routine that does not come back | 167 |
| a call through a pointer | 96 |
| a called routine whose own returns disagree | 66 |
| the stack pointer written by something the walk cannot follow (`mov esp`, `leave`) | 26 |

*Every path leaves the interpreter* is the interesting one, and it is the same thing that makes 330 of
the rows say they can raise: an instruction that raises never runs the opcode after it, so asking what
it leaves on the stack is asking about a stack nobody reads.

**A handler can raise if the runtime's raiser is reachable from it.** The raiser is one function, at
**0x6603852C** in 6.00.9848 — it takes the error code pushed to it, compares it against `9C68h` (the
"object variable not set" code) among others, and unwinds. The evidence for that address is the code
itself rather than a name: the blocks handlers jump to when they have an error code loaded all begin by
pushing it and calling this function.

```asm
66 10A479: mov eax, 9C68h        ; opcode 0x0D's handler, loading the code
66 10A47E: jmp 66108D50h         ; …and leaving with it
66 108D50: push eax              ; the block it lands in
66 108D51: call 66385 2Ch        ; …which calls the one function that raises
```

Two rules were tried before this one and both were wrong in ways worth recording, because each looked
right until it was read against the file:

- **"the address is jumped to by many handlers, so it is the error path"** — a shared body that stores
  a value is jumped to by many handlers too. It called 43 handlers raising that jump to a plain store;
- **"a called function whose first instruction is a push"** — true of almost every function in the
  file. It made **652** rows able to raise, the exits among them, and it was caught by looking at one of
  the candidates: `66385 2Ch` starts with two pushes, and so does everything else.

What is asked now is that the jump be taken *with an error code loaded* (an immediate the path just put
in a register, leaving the handler's neighbourhood) and that the code it lands in actually calls the
raiser — a block reached that way which reaches the raiser inside a few instructions (through its own
short jumps and conditional branches) is the error path, and a block that dispatches back to the
interpreter instead is a shared body. **142 of the implemented rows can raise**, 52 of them with no
stack effect at all. **A handler raises directly too** when it calls the raiser itself,
`push eax` / `call 66385 2Ch` — 188 more rows do, and they are the 188 slots the runtime does not
implement: its "no case for this byte" handler does nothing but raise, which is the same fact that
gives them no name.

**Tail calls are where most of the effect would otherwise have been lost.** The runtime is a run of
small thunks laid one after another, each ending in its own `ret N`, and a walk that follows jumps
without a bound reads the *next* thunk's `ret` and concludes that the routine's returns disagree with
themselves. That bound is now the first `ret` read straight through from the entry: a jump above it is
a tail call, and what the code over there removes is what this call removes. With it the number of
implemented rows with a measured effect went from 555 to 806; without it, 592 handler paths were
reported as "returns disagree" because a `ret 0Ch` at one address and a `ret 4` sixty bytes later
belonged to two different functions. **A call whose callee has no `ret` at all returns nowhere**: the
path ends at the call and contributes no effect, which is what the raiser is, and what a handler that
calls the raiser does for a living.

This is the third place in this slice where a rule had to be narrowed to the code's own statement
rather than a pattern that usually means it, and the fourth bug of the same family: **the decoder never
filled in `ret N`.** `DecodedInsn.RetPopBytes` was read by three callers and written by nobody, so
every call removed nothing, every `ret 8` read as `ret 0`, and the inventory's `ret N` evidence — a
line M1–M6 has been reporting since the beginning — was never printed. Populating it at the decode
changed the stated effect of **282 rows** and took 0x2A to **−1**, which is the number the published
opcode tables state as well (two pops, one push).

**The effects were cross-checked against the same independent table as the lengths.** Where both this
reader and the crate state an effect — 777 rows, the other 119 being rows the crate marks as depending
on the operand — **532 agree (68%)**. The 245 that differ are mostly a difference in *units* rather
than in reading: the crate counts a variant as four slots, so `LitVarI2` is "0 pops, 4 pushes" there
and "+1" here, because the handler writes a variant through the stack (sixteen bytes of frame) rather
than pushing one dword four times. Where the two are counting the same thing they agree exactly, and the
place they are both counting the same thing is a call that pops its own arguments: 0x2A is −1 on both
sides and 0x1E `Branch` is 0 on both.

Two of the differences, read by hand, which are conventions rather than corrections:

| row | the crate | this reader | the bytes |
| --- | --- | --- | --- |
| `LitVarI2` (0x28) | 0 pops, 4 pushes = +4 | +1 | the handler writes a variant into a frame slot and pushes one pointer, so +1 dword of machine stack; the crate counts the variant's four slots |
| `CRec2Ansi` (0x1F) | 0 pops, 0 pushes = 0 | −2 | `push [edx+eax*4]` then `call __vbaRecUniToAnsi`, whose own `ret 0Ch` pops three — one pushed here, two pushed before this opcode ran; the crate counts what the opcode is defined to touch, this reader counts what the code moves |

`tools/compare-opcodes.py` prints this comparison beside the length one, so the number in this
paragraph has a command behind it.

### What the bytes after an opcode are, read out of the handler

An opcode's name and length say what an instruction is called and how long it is. Neither says what its
operand *is*, and that is the reading `recon pcode` renders: **four kinds, each measured off the
handler's own instructions**, with the number of operand bytes and *where* in them the word the kind is
about sits. This is the last piece between "this program has 23 procedures" and "this procedure opens a
file and loops over its records", because a stream of bytes and lengths is not yet a listing.

| kind | what the bytes after the opcode are | rows |
| --- | --- | --- |
| `none` | nothing: the instruction is the opcode byte alone | 485 |
| `data` | a value — an index, a length, a pool reference, an immediate | 330 |
| `slot` | a word the handler sign-extends and addresses the frame by | 303 |
| `target` | a word the handler adds to the instruction stream's base and goes to | 45 |

**A branch is a shape in the handler, never a name in a table.** Three shapes end in the same act —
the word plus the stream's base replaces the instruction pointer — and they are read from the
handlers, which is why the same reader can also say *where in the operand* the word is:

- `movzx esi, word ptr [esi]` … `add esi, [ebp-58h]`, which is the fetch at 0x108F35 and the tail at
  0x108F5C behind it: **0x1C, 0x1D, 0x1E and 0xFD 0x03/0x04/0x07/0x0C** share it;
- `mov esi, [ebp-58h]` … `add esi, eax`: **0x64, 0xFD 0x0A, 0xFD 0xCB–0xCF, 0xFE 0x79** and the
  family that arrives at it;
- `add eax, [ebp-58h]` … `xchg esi, eax`: **0x65, 0x67, 0x68, 0x69, 0x6A and 0xFE 0x80–0x85**.

**The offset of the word is the pointer's advance, not the displacement written in the
instruction.** `movzx eax, word ptr [esi+2]` reads the word *second* only after the handler has moved
`esi` by two; a handler that does `add esi, 2` first and then reads `[esi]` reads the second word as
well. The first version of this reader used the displacement alone and put every `For Each`/`Next`
branch on the counter's frame slot; the offsets it now reports were each checked against the
instruction that does the moving, and they are the reason a `Next` is reported as two words:

```
0x32FA  66 e4 fe 98 01                 0x66       5     frame[-0x11C] then +0x198 -> 0x3288
```

That is a `Next` of a `For` loop: the first word (`fe e4`) is the frame slot of the counter it counts,
the second (`98 01`) is how far it branches back. Reporting only one of them would be reporting half of
the instruction's operand, so `operand_slot_at` is a field of its own beside `operand_at`.

**A frame slot is a shape too**, and there are three of them: the word sign-extended and then used as
a memory address (`mov [edi+ebp], bl`, `cmp [edi+ebp+4], ecx`), the word sign-extended and then added
to the frame base (`movsx eax,[esi]` / `add eax,ebp`), and — a memory form with the two registers the
other way round — `[ebx+ebp]`. The scan stops where the register that holds the word is written again,
which is what keeps a later use of the same register from being read as an address built from this
operand; an earlier version used a fixed six-instruction window instead and missed a `Next` whose
counter is compared seven instructions later.

**A counted instruction's operand is a list, and says so.** 0x29, 0x32, 0x36 and 0xFE 0xB2–0xB4 are the
instructions that free a *list* of variables: their handlers read a count and then loop over that many
two-byte entries. The measured length is one pass through that loop, so `operand_bytes` is **−1** — no
number describes the operand — and the kind is read from the loop body.

**The kinds were checked three ways, and where they are weaker than the independent reconstruction the
report says so.** `tools/compare-opcodes.py` prints this comparison beside the length, stack and name
ones:

- **The corpus says the targets are targets.** Of the **1,878 branch operands** of the 42 programs —
  over 680 procedure listings and 55,135 instruction rows — **1,877 resolve to an address that is an
  instruction start of the same procedure**, inside its code. A 16-bit word read as an address and
  landing on an instruction boundary is not a coincidence, and it is the check that catches a
  mis-kinded operand: the first classifier this work shipped had 184 `target` rows and this measurement
  is what refused them. (One of the 1,878 has the word zero — a `0x1E` — and lands on the procedure's
  own first instruction, which the check counts but does not weigh: 1,877 words are non-zero and 1,876
  of those land.) The one operand that does not land is a **sentinel, and its handler says so**:
  `TFTPClient.exe` `Form1[3]` at `0x37AE` is `FD 0C` with the word `0xFFFF`, and the handler compares
  the sign-extended word against −2 and −1 *before* it is ever used as an offset — `0x10F236`
  `movzx esi,word[esi]`, `0x10F239` `movsx eax,si`, `0x10F23C` `add esi,[ebp-58h]`, `0x10F23F`
  `cmp eax,0FFFFFFFEh`, `0x10F242` `jb`, `0x10F244` `mov esi,ebx` — and for those two values continues
  from a *different* pointer (with a byte-or-table indirection off it) rather than from the stream's
  base, so there is nothing in this procedure for the word to land on. It is the only one of the 1,878
  words that is not an offset. The earlier 26-program run of this same check measured 1,448 operands,
  all of them landing, and every one of the pre-reset run's opcode counts is inside this run's — the
  0x1C 928 of 1,267, the 0x1E 311 of 373 — so the two agree and the program with the sentinel is simply
  one the earlier set did not walk. The independent table names `FD 0C` **`Resume`** and calls it a
  branch, and the runtime's own export table documents the same family of sentinels one instruction
  over: `__vbaOnError` takes an `nHandler` whose `0xFFFFFFFD` is `Resume Next`. What the second stream
  pointer *is* — the saved resume point is the likely answer — is not traced, so the listing states what
  it knows and no more: `+0xFFFF -> 0x136DB (not an instruction start)`.
- **The corpus says the slots are slots.** Every frame slot a listing names lies inside the frame the
  procedure's own descriptor declares — 25,812 slots over the 680 procedure listings of the corpus, all
  of them inside `[-frame_size - 132, argument_size + 4]`. That bound is a measurement, not a rule from
  a specification: the deepest slot in the corpus is exactly 132 bytes below `frame_size` and the
  highest argument exactly 4 above `argument_size`, which is what says the field counts the frame's
  fixed part and the compiler keeps its temporaries below it.
- **The independent table agrees on the slots.** Of the 303 rows this reader calls `slot`, **292 are
  `%a` in the crate's `operand_format`** — a reconstruction that does not know what this one measured,
  naming a frame slot for the same rows.

Where the two disagree, the report lists the rows rather than averaging them away. **Forty rows this
reader calls `data` carry `%a` in the other table**, and they are the `Redim`/`Late`/`WMem`/`ExitProcCb`
families, whose handlers hand the word to a runtime function rather than using it as an address in
their own code — the word is an offset the callee interprets, and this reader states only what the
handler it read does with it. **Five rows this reader calls `data` carry `%l`**: `OnErrorGoto`,
`BranchFVar` and `BranchFVarFree` (0x4B, 0x5C, 0xFD 0x05, 0xFD 0x06, 0xFD 0x0B), whose handlers jump
*away* with an error code and let the interpreter's own code do the branching, so the word is never
added to the stream's base in a handler at all. And **eleven rows this reader calls `slot`** the other
table leaves empty or fills with `%2` — the six counted free-lists, whose operand is a list of slots
and not one slot, plus `LitVarCy`/`LitVarDate`, which write a currency or a date through the slot.

Both of the `%l` rows that are branches are worth one more word, because they are the shape of
evidence this project prefers: **0x4B and 0x5C are branches in the reference and `data` here**, and
what settles them is not the reference but the corpus — 0x4B and 0x5C do not appear as `target`
operands in the corpus at all (they are `OnErrorGoto` and `BranchFVar`, whose targets are resolved by
the interpreter's error path and by a variable's control-flow value), so there is nothing here to
measure a landing on. Where the corpus cannot say, the reader does not guess.

### A procedure, listed like machine code

`recon pcode <program> --object <name> --procedure <n>` prints the selected procedure's stream the way
`disasm` prints machine code — the bytes, the opcode, the operand rendered as what it means:

```console
$ recon pcode ../pcode-inputs/vb6-code/Mandelbrot/Mandelbrot.exe --runtime ../vb6-inputs/msvbvm60.dll \
      --object frmFractal --procedure 1
rva     bytes                          opcode     size  operand           name
0x30F0  fa 00 00 00 00 65 cd dd 41     0xFA       9
0x30F9  74 14 ff                       0x74       3     frame[-0xEC]
0x32BC  1c d8 01                       0x1C       3     +0x1D8 -> 0x32C8
0x32FA  66 e4 fe 98 01                 0x66       5     frame[-0x11C] then +0x198 -> 0x3288
```

Every column is a measurement: `bytes` is the stream as it lies in the file, `opcode` is the opcode and
its lead byte where it has one (`0xFE 0x8E`), `size` is the length measured from the handler, and
`operand` is the operand as the handler reads it — a frame slot, or a distance and where it lands. The
same rows are in `--json` as `operand`, `operand_bytes`, `operand_at`, `operand_slot_at`, `target`,
`target_is_an_instruction_start` and `frame_slot`, with `bytes` and `size`, and the p-code schema
(`pcode.schema.json`) requires them, so an agent reading the document is reading the same facts as a
person reading the table.

**A target is printed as the interpreter resolves it**, not as the raw word: the procedure's code
address plus the word, which is what `+0x1D8 -> 0x32C8` says, and a target that did *not* land on an
instruction start would say so in the listing (`(not an instruction start)`) rather than be printed as
if it were fine. That no listing in the corpus prints it is the measurement above.

**The operand's basis travels with it.** Every row of `recon opcodes` carries an `operand_basis`
sentence naming the instruction the reading came from — ``` `movzx eax, word ptr [esi+2]` at
0x1095E5 ``` — and a `data` row cites the read it is about too, which it did not until this pass: a
kind is a claim about an instruction, and a claim that names no instruction is the one a reader cannot
check. Where the operand is read only in code the handler hands off to, the basis says that instead of
attributing the read to the handler's own code.

Checked by 3 new tests: the kinds, offsets and the `operand_bytes = size − 1` arithmetic over all 1,163
handled rows of the runtime's tables, two rendered listings against rows that were disassembled by
hand (the `Next` above, the branch at 0x32BC, the slot at 0x30F9), and a fifth of the corpus held to
"every branch lands on an instruction start" so the suite carries part of the measurement rather than
only the prose.

## Three bugs found by using the tool

Each of these was found by typing a command and being surprised by what came back, not by reading
code. They are written down together because they share a shape: in all three the tool said something
that sounded like a fact about the program or the toolchain, and was really a fact about itself.

**`recon init --project somewhere` wrote the project into the current directory.** Every other command
resolves `--project` to a project file and opens it; `init` has no file yet, so the option fell
through to `Directory.GetCurrentDirectory()`. This is how it was found: a project appeared in the
repository root, and a test that runs `recon report` on two binaries with no project started failing
— the CLI's upward search found that project, concluded history was wanted, and wrote a
`history.jsonl` the test asserts is absent. The command now treats `--project` as the directory to
create the project in, whether it is given as a directory or as the path of the file to be written
(two tests).

**`recon toolchain show <id>` printed the file, not the profile.** The documentation says the command
shows "what one profile resolves to"; the default path loaded the raw file, so everything a profile
inherits read as its default: `mangling: none` for a compiler that mangles every symbol,
`eh / debug: none / none` for a toolchain whose exception model and PDB are defined one file up, an
empty prologue list, and `inherits: msvc-6` — a profile inheriting from itself. Absence was being
printed in the shape of a fact. The default is now the resolved profile, and `--raw` prints the file's
own contents with each inherited field marked `(inherited)` rather than rendered as its default (two
tests).

**The example project's profiles had gone stale, and a golden was holding them there.**
`recon init` copies the shipped profiles into a project so they can be edited, and a project that has
its own copies never sees the shipped set again. `examples/sample-project` had been created before
`msvc-6` existed — so it was missing two profiles — and seven of its thirteen copies were older than
the files they were copied from, including `gcc-base` and `vb6-native`. Two consequences:

* `recon toolchain show msvc-6` inside the example project answered *unknown profile*.
* The corpus golden was generated through that project, so it pinned `msvcrt.dll` matching
  `gcc-13-mingw`, `gcc-14-mingw` and `gcc-4_8-mingw` — the behaviour M6 fixed by teaching the GCC
  profiles to name MinGW's own runtime instead. The stale copy had been quietly undoing that fix for
  every test that ran through the example project.

The copies are refreshed, and the golden with them: the compilers are now attributed from their PDB
and DWARF producers at `high` confidence, and `msvcrt.dll` matches nothing. Whether a project's own
profiles *replace* the shipped set is left as it is — a test pins that, and it is defensible: a
project's copies are its own. What is no longer true is that the staleness is silent:
`recon doctor` compares the project's copies with the shipped set and warns about the ones that are
missing and the ones that differ, so the next project to fall behind says so itself (one test).

## A p-code program, decoded

The opcode tables made a disassembler possible; a program made it checkable. Both halves arrived at once
when the corpus below turned up, and what follows is what reading 42 of them says.

**Where the code is.** A p-code build fills in what a native build leaves empty. Each object's
`ObjectInfo` carries a method count at `+0x20` and a dispatch table at `+0x24`; each entry of that table
is a `ProcDscInfo`, the structure that trails a procedure, and `wPCodeBackOffset` says how many bytes
before it the code starts. That single field is how a stream's end is known — a number the compiler
wrote down rather than one inferred from the bytes — and it is also the check: a stream that does not
reach its own descriptor is a stream this does not understand, and it says so.

```console
$ recon pcode Mandelbrot.exe --runtime msvbvm60.dll
Mandelbrot.exe: p-code, 8 procedure(s) in 1 object(s)
  instructions read against msvbvm60.dll 6.00.9848
  1036 instruction(s) in 2960 bytes: 3 exact, 5 padded
object      method  code    bytes  instructions  status  ends with exit
frmFractal  1       0x30F0  968    324           exact   yes
...
```

**Lengths come from the runtime, and the runtime is named.** `recon opcodes` measures the tables — the
primary one and the five the lead bytes select — out of `MSVBVM60.DLL`'s own handlers, and `recon pcode`
decodes against those. The report prints the runtime's path and its version resource, because the same
program decoded against a different build's interpreter would be a plausible listing of the wrong thing.

**What the tables say about the lead bytes, from the file.** The handler for 0xFB dispatches through
`0x10AE24`, 0xFC through `0x10B224`, 0xFD through `0x10B624`, 0xFE through `0x10BA24`, and 0xFF
through `0x10BE24` — five secondary tables, each lead handler twelve bytes from the last. That pairing
is read out of the runtime, not assumed, and each is published with the primary opcodes that claim it.
A stream with a lead instruction in it needs the sub-table's lengths: the sizes of the 1,536 rows are
what `recon pcode` decodes against.

**A procedure ends with an exit, and the alignment after it is not an instruction.** Three opcodes end a
procedure — 0x13 `ExitProcHresult`, 0x14 `ExitProc`, 0x15 `ExitProcI2` — and each is one byte. Between
the last instruction and the descriptor sit up to four bytes of alignment, and reading them as
instructions is what they look like: `00 00 00` decodes as three of something. The decoder therefore
ends a stream at the last exit in that window; where the exit is earlier or absent, the bytes are code
reached by a branch and are decoded as code, which is what an independent implementation of the same
format records about the same corpus.

**The corpus, and what it is.** 42 Visual Basic 6 programs, each with its source, built to p-code by the
Visual Basic 6 IDE on a Windows XP host, published (MIT) as `corpus-pcode/` in the DeForm6 project by
Stiven Gjekaj and taken at one pinned commit. Every one of the 42 confirms itself as p-code under this
tool's own reader — `aNativeCode` zero, `isa` `vb6-pcode` — before anything is decoded, and the sources
are there so the decode can be checked against what the project *should* contain: Mandelbrot's form
declares seven Subs and one Function, and the program reads as eight procedures.

**The measured result, over all 42 programs.** 680 procedures, **all of them read**: 222 end exactly on
their descriptor, 458 have one to four bytes of alignment before it, none admits more than one reading,
and none is unreadable. Getting there took the four measurement fixes below; before them 100 of the 680
had no reading at all and a further seven were read two ways. A stream that ends in the compiler's
alignment is read as ending there even where the alignment does not decode — `13 ff ff ff` is an exit
and three bytes of fill, and the fill is not a lead instruction. Two kinds of slot are told from a
procedure and counted rather than guessed at: 89 hold no address at all (events a project declares and
never writes), and 102 entries do not name their own object, so they are not that object's procedures —
which is how a method table longer than an object's code is settled by the file instead of by a choice.
The whole corpus is re-measurable rather than remembered: `tools/pcode-corpus.py` walks a directory of
p-code builds and prints every count in this section.

Checked by 9 tests, and the whole corpus is in the workspace under `pcode-inputs/` with a manifest
recording the repository, the commit and each file's SHA-256.

## Three more bugs, found by reading the names back

The naming pass found its own bugs, and all three had the same shape: an answer that was uniform,
plausible, and about the interpreter rather than about the opcode.

**188 opcodes were all named after the error path.** A slot the runtime does not implement is a slot
the interpreter sends to the one handler that raises — `0x1105C2`, `push 33h` and into the unwind — and
188 of the 1,536 rows are exactly that. Naming them from what that handler calls produced 188 rows
named `RtlUnwind`, one per unimplemented opcode, and it looked like evidence: the call is real, the name
is real, and it is the name of the error, not of the operation. The row is not an instruction and gets
no name now: the report says which of the two kinds of unimplemented slot it is — 185 have no address
at all, 188 go to the handler that raises — and the reader takes that from the dispatch table's own
guard rather than inferring it. The independent table marks 187 rows `InvalidExcode`, and they are the
same rows, which is the cross-check that this rule is the right one.

**A helper's branch is not the opcode's operation.** Following a branch inside a helper — to reach the
code the helper hands off to — also followed it into the *error* path, because the first thing a
runtime helper does is test something and call the unwind. It named 15 rows `__vbaStrComp` for the same
reason. Only unconditional jumps are followed now: a tail call or a hand-off is the work, a conditional
jump is a test, and the error path behind it belongs to the interpreter. That change also removed the
15 wrong names, which is what made it visible.

**A helper is a run of instructions, and the reader stopped at the first one.** The code that follows a
helper read one instruction and stopped unless that instruction was a call — a mistake that made the
whole mechanism look useless, because a helper whose first instruction is `mov eax,[esp+4]` yielded
nothing. Reading the helper as a straight run until it returns or branches is what found the four
`ForEach`/`NextEach` names, each of which is a preface of pushes around the export that does the work.

## Four measurements that were wrong, and what they cost

The corpus is the instrument that found these. Each was a length derived from the file that was
*plausible* and wrong, and each made streams run past their own descriptor — 100 of the 680 procedures
had no reading at all until all four were fixed, and seven more were read two ways. They are the same
kind of mistake as the three bugs above, from the other direction: not the tool saying something about
itself, but the tool saying something about the runtime that the runtime does not say.

**Five tables where the file has six.** The dispatch scan matched the interpreter's eleven-byte dispatch
sequence exactly, and 0xFF's site is a *guarded* dispatcher — `cmp eax, 46h` before the same jump — so a
scan that only matched the plain form could not see it. Every stream with a `ff` in it was decoded with
no lengths for the byte after it, which is the "0xFF 0xFF at 0x3AC2 is not measured" family. The scan now
matches the jump (`ff 24 85` and the table address after it) and requires a `mov al, [esi]` before it,
which finds six tables and nothing that is not a dispatch. The guard turned out to be the interpreter's
own statement of the table's length — 71 — which is also what keeps the 185 slots after it in the file
from being read as handlers.

**A lead byte's own length is not the count of bytes its handler skips.** The first revision added one
byte per lead instruction, which made every such stream a byte short; the second added the lead's *own*
measured consumption, which is 2 for 0xFF and made 50 streams a byte long. What is actually true is that
a lead instruction's length is the number of bytes the sub-handler skips as its operand, and the only
place that is written down is the code: the sixth table's handlers read their operand and then restore
`esi` from a register (`sub esi, ecx`) — a pointer adjustment a walk cannot follow — so the length is
what they read, and 0xFF 0x2F measures 5, one lead byte and four of operand.

**`esi` restored from a register invalidates everything measured after it.** `sub esi, ecx`, where the
count came out of a call, moves the pointer by an amount the walk does not know, so a length taken from a
fetch after it is taken from the wrong place. The walk marks such a path and sizes it from what it read
instead. This one fix turned 100 unmeasured procedures into 51.

**A stream that ends in alignment does not decode as code.** `13 ff ff ff` is an exit and three bytes of
fill; the fill is a lead instruction with no row, and the stream was reported as unmeasured because of
it. Where a stream cannot be decoded further, the path so far is still a reading if it already ended at
an exit within the padding window — which is the same rule the decoder already used for a stream whose
last bytes are all instructions. That took the corpus from 49 unmeasured to none.

**And a fifth, from the cross-check rather than the corpus.** The 25 variable operations of the first
lead table — `ImpVar`, `EqvVar`, `ModVar`, the text comparisons — read their operand in the shared code
they hand off to, at `[esi]` and at `[esi+2]`, and a rule that matched the fetch against the dispatch
threw all 25 away: they measured the one byte they read on their own. They are measured from what the
handed-off code read now, which is what took the row-by-row agreement with the
independent table from 1,285 rows to **1,327 of the 1,340** rows both tables state a length for.

Two more came out of the same rewriting, and they are the interesting kind: fixes that made a row *worse*
in a way only the independent table could show. Following an unconditional jump into another handler's
entry — the shared-body case above — was right for `FStVarNoPop` (1 → 3) and wrong for 0x0D, which then
read an error path's five bytes and reported 5 *and* 6 where the file says 5; the rule is now "a jump
before the handler has read or consumed anything", which keeps the shared body and drops the error path.
And the exit rule, written to drop an incomplete path in `AryInRecLdPr`, was at first wide enough to drop
the one-byte row `AddI2` as well; it now asks whether the handed-off code read the operand for *this*
instruction, not merely whether this one had consumed something.

## Bugs found on the way through the opcode reader

Four more, three of them older than this slice, all of them the same shape as the three above: the
tool answered something that was not about the file.

**The parser kept a list of which options take a value, and it was out of date.** `--inventory`,
`--length`, `--min-fixed` and `--opcode` are documented in the usage lines, read by their commands,
and were absent from that list — so the option was read as *not given*, and the command carried on with
its default. `recon validate --inventory=audit.json` read a different file; `recon sigs build
--length=16` built at the default length; `recon opcodes --opcode 0x2A` printed all 256. Three options
in the same list were the other way round — registered, documented nowhere, read by nobody — and an
option that takes a value eats the argument after it, so those were a positional argument waiting to
disappear. The four are registered, the three are gone, and a test now reads the usage lines this build
generates and holds the two lists to each other (2 tests). It failed on the first run, on `--opcode`
being documented as a flag.

**A conditional jump was a jump in one decoder and not in the other.** The x86 decoder set `IsJump`
for `jmp` and left it clear for `jcc`, with `IsConditionalJump` marking the conditional ones; the
AArch64 decoder sets both. Code that asks "is this a branch" therefore got a different answer per
architecture, and the opcode reader — asking `IsJump` — quietly skipped every conditional branch inside
every handler. Handlers that decide something and then dispatch measured as if the branch were not
there. Both flags now mean the same thing in both decoders, `IsJump` documented as "a branch of either
kind", and the reader asks for either (the handler walk is what found it: one opcode's length was
wrong until the branch through `jmp edi` was followed).

**The version resource walk read nothing at all.** Three mistakes in the same function: the *first
entry* of a directory was taken from the directory header rather than from its first entry; a leaf
entry was resolved to itself instead of to the offset the entry names; and the resource's address was
treated as an offset into the section rather than as the image RVA it is. Between them they turned
every runtime's version into the empty string — the report said `msvbvm60.dll: — 256 opcodes`, with the
build's name read as absent rather than as unknown. It now prints **6.00.9848**, which is the number
this project's p-code findings are stated for, and the reader was checked by hand against a walk of the
same file before it was believed.

**The dispatch sequence was measured as eleven bytes and is eleven plus the address.** The reader
slices the last bytes of a handler to see whether it dispatches, and the guard checked eleven while the
sequence it matches is `33 c0 | 8a 06 | 46 | ff 24 85` — twelve with the table address. Every handler
whose dispatch was the last thing in the file's section threw `ArgumentOutOfRangeException`. The
reader now matches the jump itself, seven bytes, which is also what keeps the walk inside a handler
rather than over its edge.

## Status

Done: the variant generator (19 tests), the permutation loop and the `recon permute` command (9
more), the schema and `--json` for a run, the agent contract around all of it (7 more), schemas for
every command that emits JSON (17 more, one per command), VB6 p-code: recognised, reported and
refused rather than decoded as x86 (5 more), the memory and performance work above (2 more), the
`--flags` slice above (6 more), ARM: an AArch64 decoder whose every call target is checked against
the linker's own map (14 more), the Visual C++ 5.x profile above (13 more), `recon init --project`
writing where it was told (2 more), `recon toolchain show` showing what a profile resolves to (2
more), `recon doctor` warning about stale profile copies (1 more), COFF archives: `recon lib` over
three real archives (15 more), the p-code opcode tables measured out of the runtime (5 more), the
opcodes themselves — named from what their handlers call, with their lengths measured, all six tables
published, and the measurements held to an independent table row by row (17 more), the parser's option
registry held to its own usage lines (2 more), and p-code *programs*: every procedure of a p-code
build, decoded instruction by instruction, all 680 of them over 42 programs (9 more) — plus `recon vb6`,
a Visual Basic program read as the structure it is, over five real programs (12 more), and the stack
each opcode moves and whether it can raise, read out of the same handlers (20 more, the opcode tests
grown from 17), and a second round on the analysis itself: the analysis reports what each phase cost, the
passes that read every instruction were folded together, and a listing reuses the inventory on disk
(2 more, 569 in all), the comparison reads an inventory that is already on disk when it is this build's
reading of the binary being compared — which also settled the three different version strings, into one
identity every document and every reader asks for — 4 more, counted class by class: 3 in
`CompareCliTests` (the route, its refusals, and a stale declared hash) and 1 in `CliTests` (one version
string, driven through the CLI, the library and a comparison), and the bodies-twice work 4 more — 2 in
`CompareCliTests` (the key shortcut, and the listing a named function still gets) and 2 in
`CompareTests` (the summary held to the body, trailing padding included) — and the analysis reading every
instruction once instead of three times **no new tests** (it is a cost, not an answer: the answer is held
by every corpus test that counts jump tables, and by `StreamingAnalysisTests`, which now also pins the
report's shape) — and the rebuilt provider 4 more, in `DelinkTests`: one piece taken from a unit's
compiled object while every other section stays the original's, the corpus's own source as one unit
relinked into the original image (13 of 13 pieces, 859 bytes, 30 references, identical), the refusal of a
rebuild whose own layout would address the wrong call, and the refusal of a unit whose object is not its
current build — and the unnamed-data-address fix 3 more: 2 in `CompareTests` (a literal that moved, with
the identified count asserted so the test cannot pass by comparing nothing, and the control that an
address with nothing identifiable at it is still a difference) and 1 in `MovedCodeTests` (the corpus's own
source with one literal added: the functions using it come back exact, score above 0.999, the model's
identified count positive), and the exception-directory round 4 more, in `PdataTests`: a real 64-bit
table read out of the file's own bytes and compared entry for entry with the loader's reading of it,
the analysis seeding functions and their extents and refusing an entry that is not in code, the fixture's
four entries in their 8-byte form, and the stripped/unstripped pair that carries 96 → 126 starts and
96 → 30 unknown sizes — which is **602** in the suite as it stands. The Visual Basic 6 area — the six test classes for the
runtime, the p-code programs, the VB6 structures and the compiler inputs — is **64 tests in all**
(OpcodesTests 21, PcodeProgramTests 11, RealVb6Tests 7, PcodeRuntimeTests 5, Vb6PcodeTests 5,
Vb6ProgramTests 15), and the suite passes both with the real inputs on this machine and with them
absent. Every count in this document is from a run; the ones in this paragraph were printed by
`dotnet test --filter` one class at a time. The corpus counts are re-measurable rather than
remembered: `tools/pcode-corpus.py` walks a directory of p-code builds and prints every number in the
sections above, and `tools/pcode-landings.py` is the check behind the branch and slot numbers — every
`target` operand proved to land on an instruction of its own procedure, every `frame_slot` proved to
lie in the frame the procedure declares — with the operands that do not land printed as they are seen.

The order the measurements point is at the end of this document; the one-line item that used to sit
here — *the comparison builds an inventory it may already have on disk* — is closed in both halves. The
first is the section above; the second is that it still decoded each side's bodies twice, once to key
them and once to compare them, and that too is done: a pair whose two keys agree is decided from the
summaries without a second decode, what the pairing's similarity stage decodes the comparing stage keeps,
and the summary pass walks the stream and stores what a summary is made of rather than a body — which is
what took the client's self-comparison from **100 s at 1.0 GB to 37.6 s at 684 MB**. Meanwhile the *lists* are in and a
procedure lists: every row has a length, a name where its handler names one, the slots it moves, whether
it can raise, and now **what its operand is** — a value, a frame slot, or a branch, with the offset the
word sits at — so a selected procedure prints as bytes and meanings rather than as bytes and lengths,
and the 45 branch rows are held to the corpus' own check (1,877 of the 1,878 branch operands of the 42
programs land on an instruction start, and the one that does not is a `Resume` sentinel the handler
dodges, named in the section above) and the 303 slot rows to the frame the procedure declares. What is left of the opcode reader is the reading
that needs the programs rather than the runtime: taking the decoded streams of `corpus-pcode/` and
checking each instruction against the source in `corpus/` that produced it, which is where a wrong
length, effect or operand stops being a number in a report and becomes a line of decompiled code that
does not match what the program did. Three of the six tables still raise a question worth one more
pass — the secondary tables' dispatch has not been tied to the operand's type with evidence, only its
lengths, effects and operands measured — and the 1,383 rows that carry no name are a list to work
through against the independent table's mnemonics, one row at a time, rather than something to name by
pattern, and the 45 rows whose operand this reader calls `data` while the other table calls it a slot or
a label are the same kind of list: each is a handler to read, one at a time. The Borland, Delphi and
Watcom profiles and the WASM plugin API stay out of scope by request.
