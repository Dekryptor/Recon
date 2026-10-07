using Iced.Intel;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Pe;
using Recon.Toolchains;

namespace Recon.Analysis;

public sealed class AnalysisOptions
{
    public string MinFunctionConfidence { get; set; } = "low";

    public List<uint> ExtraEntryPoints { get; set; } = [];

    public List<string> NoReturn { get; set; } = [];

    public List<DataRangeSpec> DataRanges { get; set; } = [];

    public bool DetectJumpTables { get; set; } = true;

    public bool BuildXrefs { get; set; } = true;

    public bool DetectCallingConventions { get; set; } = true;

    /// <summary>
    /// Told the name and the wall time of each phase of an analysis, in the order they ran. A caller
    /// that reports it gives a run somewhere to look when it is slow: which phase, on which input,
    /// rather than "the tool takes half a minute". Null means nobody is asking.
    /// </summary>
    public Action<string, TimeSpan>? Phase { get; set; }
}

/// <summary>
/// Turns a PE image plus whatever debug information exists into a function inventory: where the
/// functions are, how confident we are, what calls what, and which bytes are data.
/// </summary>
public sealed class InventoryAnalyzer
{
    private static readonly string[] ConfidenceOrder = ["low", "medium", "high"];

    private readonly IBinaryImage _image;
    private readonly byte[] _bytes;
    private readonly ToolchainProfile? _profile;
    private readonly AnalysisOptions _options;
    private readonly CodeDecoder _decoder;

    /// <summary>How many instructions the code sections hold. Held, because they are not.</summary>
    private int _instructionCount;

    /// <summary>
    /// One bit per byte of the image: set where an instruction starts. This is what the whole
    /// instruction map used to be — a dictionary of every decoded instruction — reduced to the only
    /// question the analysis really asks of it, which is whether a given address is a boundary. An
    /// 11 MB program is 2.5 million instructions; its bitmap is 1.4 MB.
    /// </summary>
    private ulong[] _startBits = [];

    /// <summary>Where to stop reading in one window, so a huge function cannot ask for all of it.</summary>
    private const int DecodeWindowSize = 0x40000;

    /// <summary>The longest an x86 instruction can be, which is how wide a window's margin has to be.</summary>
    private const int MaxInstructionLength = 15;

    /// <summary>
    /// Every address a call goes to, gathered during the scan so that recognising import thunks does
    /// not need a second trip over the code to ask the same question.
    /// </summary>
    private HashSet<uint> _callTargets = [];

    /// <summary>
    /// Every unconditional jump that goes through a memory operand, as (where it is, which slot it
    /// reads), gathered by the scan. The seed pass turns those that read an import slot into thunks.
    /// </summary>
    private List<(uint Rva, uint Slot)> _jumpsThroughMemory = [];

    /// <summary>
    /// The cross-references the scan saw, as candidates: where the instruction is, what it points at,
    /// what kind of reference it is, and whether it sits inside a relocation. They are attributed to
    /// functions and deduplicated in the cross-reference phase, which is the first point at which the
    /// functions exist to attribute them to.
    /// </summary>
    private List<(uint From, uint To, XrefKind Kind, bool ViaReloc)> _directRefs = [];

    private List<(uint From, uint To, XrefKind Kind, bool ViaReloc)> _memoryRefs = [];

    /// <summary>One bit per byte of the image: set where a relocation covers the byte.</summary>
    private ulong[] _relocationBits = [];

    /// <summary>
    /// The kinds of cross-reference this analysis records. They are an enumeration rather than the
    /// strings the inventory publishes because they are held for every reference in the program while
    /// the functions are being built, and a string each is a megabyte of the same eight words.
    /// </summary>
    private enum XrefKind : byte
    {
        Call,
        Jump,
        ImportCall,
        ImportRef,
        DataWrite,
        DataRead,
        DataRef,
        Reloc,
    }

    private static string KindName(XrefKind kind) => kind switch
    {
        XrefKind.Call => "call",
        XrefKind.Jump => "jump",
        XrefKind.ImportCall => "import_call",
        XrefKind.ImportRef => "import_ref",
        XrefKind.DataWrite => "data_write",
        XrefKind.DataRead => "data_read",
        XrefKind.Reloc => "reloc",
        _ => "data_ref",
    };

    /// <summary>A bitmap of the relocation addresses, so asking about one instruction's bytes is a bit test.</summary>
    private ulong[] RelocationBits()
    {
        uint span = _image.SizeOfImage;
        foreach (var section in _image.Sections)
        {
            span = Math.Max(span, section.RvaEnd);
        }

        var bits = new ulong[(span >> 6) + 1];
        foreach (var relocation in _image.Relocations)
        {
            int index = (int)(relocation.Rva >> 6);
            if ((uint)index < (uint)bits.Length)
            {
                bits[index] |= 1UL << (int)(relocation.Rva & 63);
            }
        }

        return bits;
    }

    /// <summary>Whether any byte of an instruction is relocated.</summary>
    private static bool RelocatedAt(ulong[] bits, uint rva, int length)
    {
        for (int offset = 0; offset < length; offset++)
        {
            uint at = rva + (uint)offset;
            int index = (int)(at >> 6);
            if ((uint)index < (uint)bits.Length && (bits[index] & (1UL << (int)(at & 63))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A decoder for one pass over the code, configured to record only what that pass reads. The
    /// operands an instruction has are the expensive half of decoding it — a string per register, a
    /// list per instruction with memory operands — and most passes ask only where branches go.
    /// </summary>
    /// <summary>The only instructions whose registers the jump-table search follows.</summary>
    private static readonly HashSet<Mnemonic> AddressMnemonics = [Mnemonic.Lea, Mnemonic.Call, Mnemonic.Add];

    private CodeDecoder PassDecoder(bool registers = false, bool memory = false, HashSet<Mnemonic>? registerMnemonics = null)
    {
        // Register tracking follows idioms — `lea reg, [address]`, the `call`/`add` pair of the
        // position-independent one — that are x86 instructions with x86 names. On another instruction
        // set there is nothing to look for, and asking anyway would compare Iced's mnemonics against
        // instructions that have none of them.
        bool x86 = _image.Isa is "x86" or "x64";
        return new CodeDecoder(_image)
        {
            Data = _bytes,
            IncludeText = false,
            TrackRegisters = x86 && (registers || registerMnemonics is not null),
            RegisterMnemonics = x86 ? registerMnemonics : null,
            TrackMemory = memory,
        };
    }

    /// <summary>The jump tables of this image, found once and then asked where they are read from.</summary>
    private List<JumpTableEntry> _jumpTables = [];

    /// <summary>Every address some branch points at, used to tell live code from dead tail padding.</summary>
    private HashSet<uint> _referencedTargets = [];

    /// <summary>
    /// The same addresses in ascending order. <see cref="HasReferencedInstruction"/> asks whether any
    /// of them falls in a range, which a hash set cannot answer without walking all of them.
    /// </summary>
    private uint[] _referencedSorted = [];
    private readonly List<BinarySection> _codeSections = [];

    public InventoryAnalyzer(IBinaryImage image, byte[] bytes, ToolchainProfile? profile, AnalysisOptions options)
    {
        _image = image;
        _bytes = bytes;
        _profile = profile;
        _options = options;
        // Every instruction of every code section is decoded here, and none of that text is read:
        // the inventory reports about instructions, not their rendering.
        _decoder = new CodeDecoder(image) { Data = bytes, IncludeText = false };
        _codeSections = image.Sections.Where(s => s.IsCode).ToList();
    }

    public AnalysisResult Analyze(DebugInfoResult? debug, Recon.Signatures.SignatureDatabase? signatures = null)
    {
        var result = new AnalysisResult();

        // Each phase is timed and named as it runs, because a slow analysis is a fact about one of them
        // and the phases are cheap to tell apart: decoding is not the same cost as building xrefs, and
        // which one dominates depends on the input rather than on the tool.
        //
        // The phases run in order, one at a time, and that is a measured decision rather than a
        // default: on the 11.8 MB Visual Basic client below, running the two whole-image phases at the
        // same time — which is possible, since neither reads what the other writes — took the analysis
        // from 18.7 s to 41.4 s. A machine with two cores and a container's share of them does not
        // decode twice as fast for having two decoders, and the phases the work was moved off lost more
        // than the overlap saved. The phase report is what said so.
        //
        // The three things that used to be passes over every instruction — the scan, the jump-table
        // search and the prologue search — are one pass now, because a pass over every instruction is
        // where an analysis of a large program spends its time. `ScanInstructions` says how, and what
        // is reported is that pass and the share of it the tables took.
        Phase("scan", ScanInstructions);
        result.JumpTables.AddRange(_jumpTables);
        result.InstructionCount = _instructionCount;
        if (_decoder.DecodeProblem is { } decodeProblem)
        {
            // Not a reason to stop: the symbols still name the functions, they just cannot be
            // walked. What must not happen is pretending the instructions were read.
            result.Problems.Add(decodeProblem);
        }
        else if (_instructionCount == 0)
        {
            result.Problems.Add("no code sections were decoded");
            return result;
        }

        var tableTargets = _jumpTables.SelectMany(t => t.Targets).ToHashSet();

        Dictionary<uint, Seed> seeds = [];
        Phase("seeds", () => seeds = CollectSeeds(debug, tableTargets));
        if (signatures is { Entries.Count: > 0 } database)
        {
            Phase("signatures", () => ApplySignatures(seeds, database, result));
        }

        Phase("prologues", () => AddPrologueSeeds(seeds));
        Phase("functions", () => result.Functions = BuildFunctions(seeds, tableTargets, debug));

        var functions = result.Functions;

        Phase("conventions", () =>
        {
            if (_options.DetectCallingConventions)
            {
                // Split by function, this is the kind of work that looks parallel and is not: run
                // through `Parallel.For` on the same machine it took 28.9 s against 4.5 s in one thread,
                // because the cost is decoding bytes rather than waiting for anything.
                foreach (var function in functions)
                {
                    function.CallingConvention = DetectCallingConvention(function, debug);
                }
            }

            ApplyAliases(functions);
            ApplyNoReturn(functions);
        });

        result.Functions = functions
            .OrderBy(f => f.Start)
            .ToList();

        var tableRanges = _jumpTables.Select(t => (t.Rva, (uint)(t.Entries * t.EntryWidth))).ToList();
        Phase("data", () =>
        {
            result.Data = CollectData(debug, tableRanges);
            AttachJumpTableOwners(result, _jumpTables, functions);
        });

        if (_options.BuildXrefs)
        {
            Phase("xrefs", () => result.Xrefs = BuildXrefs(result.Functions, result.Data));
        }

        Phase("statistics", () => result.Statistics = BuildStatistics(result));
        return result;
    }

    /// <summary>Reports a stretch of work measured by hand, the way <see cref="Phase"/> reports its own.</summary>
    private void Report(string name, long start)
        => _options.Phase?.Invoke(name, System.Diagnostics.Stopwatch.GetElapsedTime(start));

    /// <summary>
    /// The same, for work whose time was accumulated rather than bracketed by two stamps: a share of a
    /// pass, which is measured around each instruction and summed.
    /// </summary>
    private void Report(string name, TimeSpan elapsed) => _options.Phase?.Invoke(name, elapsed);

    /// <summary>Runs a phase and reports how long it took, to whoever asked in <see cref="AnalysisOptions.Phase"/>.</summary>
    private void Phase(string name, Action work)
    {
        Action<string, TimeSpan>? report = _options.Phase;
        if (report is null)
        {
            work();
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        work();
        report(name, System.Diagnostics.Stopwatch.GetElapsedTime(start));
    }

    // ---------------------------------------------------------------- seeds

    private sealed class Seed
    {
        public uint Rva { get; set; }

        public string? Name { get; set; }

        public uint? Size { get; set; }

        public string Confidence { get; set; } = "low";

        public List<string> FoundBy { get; set; } = [];

        public string? Unit { get; set; }

        /// <summary>Other names the sources gave this address (decorated, folded, public vs local).</summary>
        public List<string> Aliases { get; set; } = [];

        public bool IsThunk { get; set; }

        public string? ThunkTarget { get; set; }
    }

    /// <summary>
    /// True for generated names such as <c>sub_401000</c>. A plain prefix test would also match real
    /// symbols (<c>sub_fast</c>), which is exactly how a legitimate name once got overwritten.
    /// </summary>
    /// <summary>True when the name came from the binary or its debug info rather than from a guess.</summary>
    /// <summary>
    /// The sources that state a function *boundary*: a symbol table, debug information, an export, a
    /// stated entry point. What they have in common is that they were told where a function begins and
    /// did not infer it from a reference — so a jump table pointing at one of their addresses is a
    /// fact about the table, not evidence against the function.
    /// </summary>
    private static readonly string[] SourcesThatStateAFunctionBoundary =
    [
        "pdb", "dwarf", "coff", "elf", "macho", "map", "export", "config", "import_thunk",
        "entry_point", "tls_callback", "runtime_callback", "pcode_procedure",
    ];

    /// <summary>
    /// The sources that outrank a pattern. A signature is the weakest evidence this tool accepts —
    /// the bytes could be shared by two functions, which is why the builder drops a pattern when they
    /// are — so it names only what no symbol, no debug information and no export named.
    /// </summary>
    private static readonly string[] SourcesThatOutrankASignature =
    [
        "pdb", "dwarf", "coff", "elf", "macho", "map", "export", "config", "import_thunk",
    ];

    /// <summary>
    /// Names the functions a pattern file recognizes. It cannot invent one: the address has to be a
    /// function the analysis already found, which is what keeps a pattern that happens to match the
    /// middle of another function from becoming a function of its own.
    /// </summary>
    private void ApplySignatures(Dictionary<uint, Seed> seeds, Recon.Signatures.SignatureDatabase database, AnalysisResult result)
    {
        var matcher = new Recon.Signatures.SignatureMatcher(database);

        foreach (var seed in seeds.Values)
        {
            if (seed.FoundBy.Any(foundBy => SourcesThatOutrankASignature.Contains(foundBy)))
            {
                continue;
            }

            var match = matcher.At(_image, _bytes, seed.Rva);
            if (match is null)
            {
                continue;
            }

            seed.Name = match.Name;
            seed.Unit ??= match.Library;
            // Better than the guess that found the address, not as good as a symbol that names it.
            seed.Confidence = "medium";
            if (!seed.FoundBy.Contains("signature"))
            {
                seed.FoundBy.Add("signature");
            }
        }

        foreach (string problem in matcher.Ambiguous)
        {
            result.Problems.Add($"signature: {problem}");
        }
    }

    private static bool IsRealName(SymbolSource source)
        => source is SymbolSource.Pdb
            or SymbolSource.Dwarf
            or SymbolSource.Coff
            or SymbolSource.Map
            or SymbolSource.Export
            or SymbolSource.ImportThunk;

    /// <summary>
    /// True inside a section the file itself declares to be symbol stubs — Mach-O's <c>__stubs</c>.
    /// Such a section holds nothing but stubs, so the section, and not the call that happens to
    /// reach an entry, is what says an entry is one. A compiler is free to tail-jump to an import
    /// instead of calling it, and a stub jumped to is no less a stub than one that is called.
    /// </summary>
    private bool IsStubSection(uint rva)
        => _image.SectionContainingRva(rva)?.FlagNames.Contains("symbol_stubs", StringComparer.Ordinal) == true;

    private static bool IsPlaceholderName(string name)
        => name.StartsWith("sub_", StringComparison.Ordinal)
           && name.Length > 4
           && name[4..].All(char.IsAsciiHexDigit);

    private Dictionary<uint, Seed> CollectSeeds(DebugInfoResult? debug, HashSet<uint> tableTargets)
    {
        var seeds = new Dictionary<uint, Seed>();

        void Add(uint rva, string? name, uint? size, SymbolSource source, string confidence, string? unit = null)
        {
            if (rva == 0 || !_image.ContainsRva(rva))
            {
                return;
            }

            if (!seeds.TryGetValue(rva, out var seed))
            {
                seed = new Seed { Rva = rva, Confidence = confidence };
                seeds[rva] = seed;
            }

            string wire = SymbolSourceNames.ToWire(source);
            if (!seed.FoundBy.Contains(wire))
            {
                seed.FoundBy.Add(wire);
            }

            if (Rank(confidence) > Rank(seed.Confidence))
            {
                seed.Confidence = confidence;
            }

            if (name is not null)
            {
                if (seed.Name is null || IsPlaceholderName(seed.Name))
                {
                    // A real name replaces no name or a generated one such as sub_401000. What a
                    // source made up on the spot (entry, tls_callback_0, a call target) is not kept
                    // as an alias either: an alias is another name the binary really carries.
                    if (seed.Name is not null
                        && !string.Equals(seed.Name, name, StringComparison.Ordinal)
                        && IsRealName(source))
                    {
                        seed.Aliases.Add(seed.Name);
                    }

                    seed.Name = name;
                }
                else if (!string.Equals(seed.Name, name, StringComparison.Ordinal)
                         && IsRealName(source)
                         && !seed.Aliases.Contains(name))
                {
                    seed.Aliases.Add(name);
                }
            }

            if (size is > 0 && seed.Size is null)
            {
                seed.Size = size;
            }

            seed.Unit ??= unit;
        }

        if (debug is not null)
        {
            foreach (var symbol in debug.Symbols.Where(s => !s.IsData))
            {
                // A symbol in the image or its debug info is authoritative about existence and
                // address; what it may not carry is a size, which the unknowns list reports.
                Add(symbol.Rva, symbol.Name, symbol.Size, symbol.Source, "high", symbol.Unit);
            }
        }

        // The compiler's own table of function ranges, when the file has one: PE's exception
        // directory. It is evidence about existence and about *extent* — the one kind of evidence in
        // a stripped image that says where a function ends rather than where the next one starts, and
        // the reason a stripped 64-bit image is not left with every size estimated. It names nothing:
        // an entry has no name field, so a function this found and nothing named stays `sub_…`. An
        // entry that points outside a code section is not a function's start, and is not one here —
        // the table is a statement about functions, and a reader that seeds from it says so.
        foreach (var range in _image.FunctionRanges)
        {
            if (_image.SectionContainingRva(range.BeginRva) is not { IsCode: true })
            {
                continue;
            }

            Add(range.BeginRva, null, range.Size, SymbolSource.ExceptionDirectory, "high");
        }

        Add(_image.EntryPointRva, "entry", null, SymbolSource.EntryPoint, "high");
        foreach (uint callback in _image.TlsCallbackRvas)
        {
            Add(callback, "tls_callback", null, SymbolSource.TlsCallback, "high");
        }

        AddRuntimeCallbackSeeds((rva, name, size, source, confidence) => Add(rva, name, size, source, confidence));

        foreach (var export in _image.Exports.Where(e => e.Forwarder is null))
        {
            // An export is not always code: ELF exports data objects through .dynsym as well as
            // functions, and naming one after a variable would invent a function out of a pointer.
            if (_image.SectionContainingRva(export.Rva)?.IsCode == false)
            {
                continue;
            }

            Add(export.Rva, export.Name ?? $"ordinal_{export.Ordinal}", null, SymbolSource.Export, "high");
        }

        foreach (var rva in _options.ExtraEntryPoints)
        {
            Add(rva, $"entry_{rva:X}", null, SymbolSource.Config, "high");
        }

        // Import thunks: a stub whose whole body is "jmp [IAT]". They are named after the import.
        var importsByIat = _image.Imports
            .GroupBy(i => i.SlotRva)
            .ToDictionary(g => g.Key, g => g.First());

        // The call targets are already known: the scan that built the instruction map gathered them,
        // because asking "is this thunk called?" used to cost a whole extra pass over the code.
        var callTargets = _callTargets;

        // The jumps that go through an import slot are the thunks, and the scan kept them.
        foreach (var (rva, iatRva) in _jumpsThroughMemory)
        {
            if (!importsByIat.TryGetValue(iatRva, out var import))
            {
                continue;
            }

            bool isCalled = callTargets.Contains(rva);
            if (isCalled || IsStubSection(rva))
            {
                if (!seeds.TryGetValue(rva, out var thunkSeed))
                {
                    thunkSeed = new Seed { Rva = rva, Confidence = "high" };
                    seeds[rva] = thunkSeed;
                }

                thunkSeed.IsThunk = true;
                thunkSeed.ThunkTarget = import.Qualified;
                thunkSeed.Name ??= $"thunk_{import.Display}";
                if (!thunkSeed.FoundBy.Contains("import_thunk"))
                {
                    thunkSeed.FoundBy.Add("import_thunk");
                }
            }
        }

        // Every direct call target is a function unless it is a jump table handler — and the scan
        // already has the call targets, so this asks the set rather than the code.
        foreach (uint target in callTargets)
        {
            if (tableTargets.Contains(target))
            {
                continue;
            }

            Add(target, null, null, SymbolSource.CallTarget, "medium");
        }

        return seeds;
    }

    /// <summary>
    /// Adds low-confidence seeds where a prologue pattern appears in code that no other source covers.
    /// This is what makes a stripped Release build usable at all, and it is labelled as a guess.
    /// </summary>
    /// <summary>
    /// The two places a C runtime is handed the address of a function nothing calls directly.
    ///
    /// The first is the initialiser arrays: every entry of <c>.init_array</c> and <c>.fini_array</c>
    /// (or <c>.ctors</c> and <c>.dtors</c>) is a pointer to code the loader runs before and after
    /// <c>main</c>, and the file says so in a relocation rather than in a call.
    ///
    /// The second is the argument list of <c>__libc_start_main</c>. The entry point of a Linux binary
    /// does not call <c>main</c>; it loads <c>main</c>'s address — and the addresses of the runtime's
    /// own initialisers — into registers and hands them to the C runtime, which calls them. Following
    /// those arguments is the only way a stripped binary gives up its <c>main</c>.
    /// </summary>
    private void AddRuntimeCallbackSeeds(Action<uint, string?, uint?, SymbolSource, string> add)
    {
        foreach (var relocation in _image.Relocations)
        {
            if (relocation.TargetRva is not uint target || !InCodeSection(target))
            {
                continue;
            }

            string? section = _image.SectionContainingRva(relocation.Rva)?.Name;
            if (section is null || !IsInitialiserArray(section))
            {
                continue;
            }

            add(target, null, null, SymbolSource.RuntimeCallback, "medium");
        }

        // The two the loader itself calls: nothing in the file refers to them, and a stripped file
        // does not name them either, so DT_INIT and DT_FINI are the only way in.
        ulong?[] loaderCallbacks = [_image.Elf?.InitAddress, _image.Elf?.FiniAddress];
        foreach (ulong? maybe in loaderCallbacks)
        {
            if (maybe is { } address && address >= _image.ImageBase
                && address - _image.ImageBase <= uint.MaxValue
                && InCodeSection((uint)(address - _image.ImageBase)))
            {
                add((uint)(address - _image.ImageBase), null, null, SymbolSource.RuntimeCallback, "medium");
            }
        }

        if (!_image.Imports.Any(i => i.Name is { } name && name.Contains("libc_start_main", StringComparison.Ordinal)))
        {
            return; // only the GNU C runtime takes main this way; elsewhere there is nothing to follow
        }

        // Everything the entry point loads before its first call is an argument it is about to pass.
        for (uint rva = _image.EntryPointRva; rva > 0 && rva < _image.EntryPointRva + 0x200;)
        {
            if (_decoder.DecodeOne(rva) is not { } insn)
            {
                break;
            }

            if (insn.IsCall)
            {
                break;
            }

            foreach (var memory in insn.MemoryRefs)
            {
                if (memory.Rva is { } address && InCodeSection(address))
                {
                    add(address, null, null, SymbolSource.RuntimeCallback, "medium");
                }
            }

            rva += (uint)Math.Max(insn.Length, 1);
        }
    }

    private static bool IsInitialiserArray(string sectionName)
        => sectionName is ".init_array" or ".fini_array" or ".ctors" or ".dtors"
           || sectionName.Contains(".init_array", StringComparison.Ordinal)
           || sectionName.Contains(".fini_array", StringComparison.Ordinal);

    private bool InCodeSection(uint rva) => _image.SectionContainingRva(rva)?.IsCode == true;

    private void AddPrologueSeeds(Dictionary<uint, Seed> seeds)
    {
        var (patterns, alignment) = PrologueRules();
        if (patterns.Count == 0 || _prologueCandidates.Count == 0)
        {
            return;
        }

        var covered = seeds.Keys.OrderBy(k => k).ToList();

        // The candidates are the instruction starts the scan found a prologue pattern at. What is
        // decided here is which of them are function entries, which is a question about the seeds:
        // either something already covers the neighbourhood at high confidence, or the bytes before the
        // candidate fall through — in which case it is most likely a branch target inside a function.
        foreach (uint rva in _prologueCandidates)
        {
            if (IsCovered(rva, covered) || seeds.ContainsKey(rva))
            {
                continue;
            }

            if (!IsBoundaryBefore(rva, alignment))
            {
                continue;
            }

            seeds[rva] = new Seed
            {
                Rva = rva,
                Confidence = "low",
                FoundBy = ["prologue"],
            };
        }
    }

    /// <summary>
    /// The prologue patterns and the function alignment this image's profile states, or the defaults
    /// for its word size. The pass that finds the candidates and the pass that decides them both ask
    /// here, so both read the same rules.
    /// </summary>
    private (List<byte[]> Patterns, uint Alignment) PrologueRules()
    {
        var hints = _profile?.Codegen.PrologueHints ?? [];
        if (hints.Count == 0)
        {
            // The 32-bit hint is the historical default; on a 64-bit image `push ebp; mov ebp, esp`
            // is not what a compiler emits, so the default follows the image.
            hints = [ProloguePatterns.Default(_image.Is64)];
        }

        var patterns = hints
            .SelectMany(ProloguePatterns.Parse)
            .Where(p => p.Length > 0)
            .ToList();
        uint alignment = (uint)Math.Max(1, _profile?.Codegen.FunctionAlign ?? 16);
        return (patterns, alignment);
    }

    /// <summary>The instruction starts the scan matched a prologue pattern at; decided later.</summary>
    private List<uint> _prologueCandidates = [];

    private bool MatchesPrologue(uint rva, List<byte[]> patterns)
    {
        int? offset = _image.RvaToOffset(rva);
        if (offset is null)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length == 0 || offset.Value + pattern.Length > _bytes.Length)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < pattern.Length; i++)
            {
                byte expected = pattern[i];
                byte actual = _bytes[offset.Value + i];
                if (expected == 0x83 && actual is 0x83 or 0x81)
                {
                    // sub esp, imm8 / imm32
                    bool isSubEsp = actual == 0x83 ? _bytes[offset.Value + i + 1] == 0xEC : _bytes[offset.Value + i + 1] == 0xEC;
                    if (!isSubEsp)
                    {
                        match = false;
                        break;
                    }

                    continue;
                }

                if (expected != actual)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the byte before <paramref name="rva"/> is padding or the end of a terminator.</summary>
    private bool IsBoundaryBefore(uint rva, uint alignment)
    {
        int? offset = _image.RvaToOffset(rva);
        if (offset is null || offset.Value == 0)
        {
            return false;
        }

        // A run of padding before the candidate is the strongest hint.
        var padding = _profile?.Codegen.PaddingBytes ?? [(byte)0xCC];
        int lookBack = 0;
        while (lookBack < 16 && offset.Value - 1 - lookBack >= 0 && padding.Contains(_bytes[offset.Value - 1 - lookBack]))
        {
            lookBack++;
        }

        if (lookBack > 0)
        {
            return true;
        }

        _ = alignment;
        return false;
    }

    // ---------------------------------------------------------------- functions

    private List<FunctionEntry> BuildFunctions(Dictionary<uint, Seed> seeds, HashSet<uint> tableTargets, DebugInfoResult? debug)
    {
        // A table's target is usually a case label *inside* the function that owns the table, which is
        // why a seed there is not a function entry. That reasoning only holds while nothing else has
        // spoken: a table pointing at a function's own first instruction is exactly what a vtable, a
        // dispatch table or an import stub looks like, and a source that states function boundaries has
        // already answered the question. Measured on a real MSVC binary (coreclr.dll, 320 tables): the
        // unconditional version of this rule deleted **2,080 functions the PDB named** — every folded
        // three-byte virtual stub in the image, because a dispatch table under `.rdata` pointed at them.
        var ordered = seeds.Values
            .Where(s => !tableTargets.Contains(s.Rva) || s.FoundBy.Any(SourcesThatStateAFunctionBoundary.Contains))
            .OrderBy(s => s.Rva)
            .ToList();
        var result = new List<FunctionEntry>();
        var minRank = Rank(_options.MinFunctionConfidence);
        long entriesStart = System.Diagnostics.Stopwatch.GetTimestamp();

        for (int i = 0; i < ordered.Count; i++)
        {
            var seed = ordered[i];
            if (Rank(seed.Confidence) < minRank)
            {
                continue;
            }

            var section = _image.SectionContainingRva(seed.Rva);
            var entry = new FunctionEntry
            {
                Id = $"f_{seed.Rva:x6}",
                Name = seed.Name,
                Confidence = seed.Confidence,
                Section = section?.Name ?? string.Empty,
                Isa = _image.Isa,
                ImportThunk = seed.ThunkTarget,
                Unit = seed.Unit,
            };

            foreach (var source in seed.FoundBy)
            {
                entry.FoundBy.Add(source);
            }

            if (seed.IsThunk)
            {
                entry.Flags.Add("import_thunk");
            }

            foreach (string alias in seed.Aliases)
            {
                if (!string.Equals(alias, seed.Name, StringComparison.Ordinal))
                {
                    entry.Aliases.Add(alias);
                }
            }

            uint estimatedEnd = NextBoundary(ordered, i, section, out bool nextIsFunction);
            string sizeReason = "size_estimated";
            uint size = seed.Size ?? EstimateSize(seed.Rva, estimatedEnd, tableTargets, out sizeReason);
            if (seed.Size is null && size > 0)
            {
                entry.Unknowns.Add(sizeReason);
                if (sizeReason != "size_from_terminator" && sizeReason != "size_from_data_boundary")
                {
                    // The end of this function is a guess, unlike the two cases above.
                    entry.Unknowns.Add("size_estimated");
                }
            }

            entry.Ranges.Add(new Range { Rva = seed.Rva, Size = size });

            if (size == 0)
            {
                entry.Unknowns.Add("empty_range");
            }

            if (DetectJumpTableUse(seed.Rva, size))
            {
                entry.Flags.Add("has_jump_table");
            }

            result.Add(entry);
        }

        Report("  functions/entries", entriesStart);
        long filterStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Drop seeds that sit inside a higher-confidence function: they were branch targets, not
        // entries. `filtered` is kept ordered by Start, so binary search finds the only candidates —
        // the few entries immediately before this one — instead of asking every function about every
        // other function. `maxEndThrough[i]` is the highest end seen up to `i`, and once that is
        // behind `entry.Start` no entry at or before `i` can contain it, so the walk stops there.
        var filtered = new List<FunctionEntry>();
        var filteredStarts = new List<uint>();
        var maxEndThrough = new List<uint>();
        foreach (var entry in result.OrderBy(f => f.Start))
        {
            int insert = filteredStarts.BinarySearch(entry.Start);
            int upTo = insert >= 0 ? insert : ~insert;

            FunctionEntry? container = null;
            for (int i = upTo - 1; i >= 0; i--)
            {
                var candidate = filtered[i];
                if (candidate.Start < entry.Start
                    && entry.Start < candidate.Start + candidate.Size
                    && Rank(candidate.Confidence) >= Rank(entry.Confidence))
                {
                    // Keep going: the earliest entry in Start order is the one that wins, which is
                    // what "the container of this seed" meant when the search was a linear scan.
                    container = candidate;
                }

                if (maxEndThrough[i] <= entry.Start)
                {
                    break;
                }
            }

            if (container is not null)
            {
                if (Rank(entry.Confidence) == Rank(container.Confidence) && entry.Name is not null && container.Name is null)
                {
                    container.Unknowns.Add($"overlapping_symbol:{entry.Name}");
                }

                continue;
            }

            filtered.Add(entry);
            filteredStarts.Add(entry.Start);
            uint end = entry.Start + entry.Size;
            maxEndThrough.Add(maxEndThrough.Count == 0 ? end : Math.Max(maxEndThrough[^1], end));
        }

        // Anything left whose range overlaps the next function gets clipped.
        for (int i = 0; i < filtered.Count - 1; i++)
        {
            var current = filtered[i];
            var next = filtered[i + 1];
            if (current.Start + current.Size > next.Start)
            {
                uint clipped = next.Start - current.Start;
                if (clipped > 0)
                {
                    current.Ranges[0].Size = clipped;
                    current.Unknowns.Add("size_clipped_to_next_function");
                }
            }
        }

        Report("  functions/filter", filterStart);
        _ = debug;
        return filtered;
    }

    /// <summary>
    /// Every instruction of every code section, in address order, decoded a window at a time and
    /// handed over one at a time. Nothing is kept: a big program decodes to millions of these, and
    /// holding them all is what killed the process on an 11 MB Visual Basic client. A pass that needs
    /// something from every instruction takes it as the instructions go by and remembers that
    /// instead — a set of branch targets, a list of xrefs, a bit per instruction start.
    ///
    /// Redecoding is the trade: a pass costs another trip over the bytes rather than another few
    /// hundred megabytes. Decoding is the cheap half of the work.
    /// </summary>
    private IEnumerable<DecodedInsn> StreamCode(CodeDecoder decoder, uint? from = null, uint? to = null)
    {
        foreach (var section in _codeSections)
        {
            uint start = Math.Max(section.Rva, from ?? section.Rva);
            uint end = Math.Min(section.Rva + section.RawSize, to ?? section.Rva + section.RawSize);
            uint rva = start;
            while (rva < end)
            {
                int? offset = _image.RvaToOffset(rva);
                if (offset is null)
                {
                    break;
                }

                int length = (int)Math.Min(DecodeWindowSize, end - rva);
                var batch = decoder.DecodeRange(_bytes, offset.Value, length, rva);
                if (batch.Count == 0)
                {
                    break;
                }

                // An instruction the window cuts in half is not an instruction: handed a truncated
                // one, the decoder still reports a length, but it is the length of the bytes it could
                // see. Trusting it would resume the walk in the middle of that instruction and decode
                // everything after it from the wrong address, which is a plausible-looking program
                // made of another program's misaligned bytes. So a trailing instruction that runs
                // past the window is dropped and decoded again, whole, in the next one.
                // Only an instruction the window holds in full is trusted. The decoder reports a
                // length for one the buffer ends in the middle of — the length of the bytes it could
                // see — and walking on from there decodes everything after it from the wrong
                // address: a five-byte call cut after two becomes a two-byte instruction, and the
                // next window starts inside that call. Rather than ask which length the decoder
                // meant, this leaves a margin at the edge wide enough to hold the longest x86
                // instruction, and decodes what is left again in the next window. The last window of
                // a section keeps its tail, which is what decoding the section whole used to do.
                bool lastWindow = rva + (uint)length >= end;
                int margin = !lastWindow && length >= 64 ? MaxInstructionLength : 0;
                uint safeEnd = rva + (uint)(length - margin);
                int usable = batch.Count;
                while (usable > 0 && batch[usable - 1].Rva + (uint)Math.Max(batch[usable - 1].Length, 1) > safeEnd)
                {
                    usable--;
                }

                if (usable == 0)
                {
                    // Nothing in the window is clear of the margin. A window that small only happens
                    // at the end of a section, where the margin is not applied, so this is the
                    // decoder refusing the bytes rather than a window too small to hold them.
                    break;
                }

                for (int i = 0; i < usable; i++)
                {
                    if (batch[i].Rva >= end)
                    {
                        yield break;
                    }

                    yield return batch[i];
                }

                // Continue at the end of the last whole instruction, so one that straddles the
                // window is decoded again from its own start rather than from its middle byte.
                var last = batch[usable - 1];
                uint next = last.Rva + (uint)Math.Max(last.Length, 1);
                if (next <= rva)
                {
                    break;
                }

                rva = next;
            }
        }
    }

    /// <summary>
    /// The one pass over every instruction of every code section.
    ///
    /// Four things are about an instruction rather than about a function — where it starts, which
    /// addresses it names, whether it indexes a jump table, and whether a prologue pattern begins at
    /// it — and they used to be three passes: one decoding the whole image for the starts and the
    /// references, one decoding it again to find the tables, and one walking every byte of it for the
    /// prologues. Each cost what a pass over every instruction costs, which is most of what an analysis
    /// of a large program costs. Measured on the 11.8 MB client in one sitting: the three were
    /// **3374–3509 ms (scan) + 3311–3389 ms (tables) + 871–1001 ms (prologues)**, the one is
    /// **5581–5841 ms**, and the analysis as a whole went 12.9 s → 10.4 s with the inventory byte for
    /// byte what it was.
    ///
    /// Two things make the merge exact rather than a compromise. The decoder is asked for registers
    /// only on the three mnemonics that compute an address (`lea`, and the `call`/`add` pair of the
    /// position-independent idiom) — what the table finder needs and what the other two did not want to
    /// pay for — and what an instruction *is* does not depend on what was recorded about it, so the
    /// three walks were always the same chain. And a prologue is only a **candidate** here: an
    /// instruction start that begins with a prologue pattern. Whether it is a function's entry is a
    /// question about the other seeds, which do not exist yet, so that half stays where it was, in
    /// `AddPrologueSeeds`.
    ///
    /// The last thing collected here is what it was: an import thunk is a stub whose whole body is
    /// <c>jmp [IAT]</c>, so recognising the thunks means looking at every unconditional jump's memory
    /// operand. The decoder records a branch's memory operand whether or not the pass asked for memory
    /// operands in general, so reading it here costs nothing.
    /// </summary>
    private void ScanInstructions()
    {
        uint span = _image.SizeOfImage;
        foreach (var section in _image.Sections)
        {
            span = Math.Max(span, section.RvaEnd);
        }

        _startBits = new ulong[(span >> 6) + 1];
        var referenced = new HashSet<uint>();
        var callTargets = new HashSet<uint>();
        var throughMemory = new List<(uint Rva, uint Slot)>();
        var direct = new List<(uint From, uint To, XrefKind Kind, bool ViaReloc)>();
        var memory = new List<(uint From, uint To, XrefKind Kind, bool ViaReloc)>();
        var relocationBits = RelocationBits();
        var iatSlots = _image.Imports.Select(i => i.SlotRva).ToHashSet();

        // The table finder rides along, fed one instruction at a time. Its share of this pass is
        // measured by stamping the clock around its work, and only when someone asked for the phase
        // report: two `GetTimestamp` calls per instruction over four million instructions is a fifth of
        // a second that a run not reporting phases has no reason to spend.
        var tables = _options.DetectJumpTables ? new TableFinder(this) : null;
        bool timing = _options.Phase is not null;
        long tableTicks = 0;

        var (prologuePatterns, _) = PrologueRules();
        List<uint>? prologues = prologuePatterns.Count > 0 ? [] : null;

        foreach (var insn in StreamCode(PassDecoder(memory: true, registerMnemonics: AddressMnemonics)))
        {
            _instructionCount++;
            MarkInstructionStart(insn.Rva);

            if (tables is not null)
            {
                long started = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                tables.Visit(insn);
                if (timing)
                {
                    tableTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                }
            }

            if (prologues is not null && HasPrologueRoom(insn.Rva) && MatchesPrologue(insn.Rva, prologuePatterns))
            {
                prologues.Add(insn.Rva);
            }

            if (insn.DirectTargetRva is { } target)
            {
                referenced.Add(target);
                if (insn.IsCall)
                {
                    callTargets.Add(target);
                }
            }

            if (insn.IsJump && !insn.IsConditionalJump && insn.BranchMemory?.Rva is uint slot)
            {
                throughMemory.Add((insn.Rva, slot));
            }

            // The cross-references are collected here rather than by a pass of their own. A pass that
            // reads every instruction costs what any pass over every instruction costs — most of the
            // time an analysis of a large program takes — so the analysis reads the stream twice in
            // total instead of four times: once here for everything that does not depend on knowing
            // where the functions are, and once afterwards for what does.
            if (insn.DirectTargetRva is null && insn.MemoryRefs.Count == 0)
            {
                // Nothing to record, which is most instructions: the relocation test is only worth
                // asking of one that produced a candidate.
                continue;
            }

            bool viaReloc = RelocatedAt(relocationBits, insn.Rva, insn.Length);
            if (insn.DirectTargetRva is uint jumpedTo)
            {
                direct.Add((insn.Rva, jumpedTo, insn.IsCall ? XrefKind.Call : XrefKind.Jump, viaReloc));
            }

            foreach (var reference in insn.MemoryRefs)
            {
                if (reference.Rva is not uint memRva || memRva == 0)
                {
                    continue;
                }

                XrefKind kind = iatSlots.Contains(memRva)
                    ? insn.IsCall || insn.IsJump ? XrefKind.ImportCall : XrefKind.ImportRef
                    : reference.Write && !reference.Read ? XrefKind.DataWrite
                        : reference.Read && !reference.Write ? XrefKind.DataRead
                        : XrefKind.DataRef;
                memory.Add((insn.Rva, memRva, kind, viaReloc));
            }
        }

        if (tables is not null)
        {
            long reading = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _jumpTables = tables.ReadTables();
            if (timing)
            {
                tableTicks += System.Diagnostics.Stopwatch.GetTimestamp() - reading;
                Report("  scan/jump_tables", System.Diagnostics.Stopwatch.GetElapsedTime(0, tableTicks));
            }
        }
        else
        {
            _jumpTables = [];
        }
        _prologueCandidates = prologues ?? [];

        _referencedTargets = referenced;
        _callTargets = callTargets;
        _jumpsThroughMemory = throughMemory;
        _directRefs = direct;
        _memoryRefs = memory;
        _relocationBits = relocationBits;
        _referencedSorted = [.. referenced.Order()];
    }

    /// <summary>
    /// Whether a candidate at <paramref name="rva"/> still has the eight bytes in its section that the
    /// byte-walking search required of it, so that finding candidates on instruction starts accepts
    /// exactly the candidates the walk did.
    /// </summary>
    private bool HasPrologueRoom(uint rva)
    {
        foreach (var section in _codeSections)
        {
            if (rva >= section.Rva && rva < section.Rva + section.RawSize)
            {
                return rva + 8 < section.Rva + section.RawSize;
            }
        }

        return false;
    }

    private bool IsInstructionStart(uint rva)
    {
        int index = (int)(rva >> 6);
        return (uint)index < (uint)_startBits.Length && (_startBits[index] & (1UL << (int)(rva & 63))) != 0;
    }

    private void MarkInstructionStart(uint rva)
    {
        int index = (int)(rva >> 6);
        if ((uint)index < (uint)_startBits.Length)
        {
            _startBits[index] |= 1UL << (int)(rva & 63);
        }
    }

    /// <summary>The instructions in <c>[start, end)</c>, decoded on demand.</summary>
    /// <summary>
    /// One function's instructions, from its first, decoded as they are read. The caller takes what it
    /// wants as they go by; the ones that need the whole list are the ones that ask for a list.
    /// </summary>
    private IEnumerable<DecodedInsn> Instructions(uint start, uint end)
    {
        end = Math.Min(end, start + DecodeWindowSize);
        return StreamCode(PassDecoder(registers: true, memory: true), start, end);
    }

    private List<DecodedInsn> InstructionsIn(uint start, uint end, bool ends = false)
    {
        // Both callers ask about one function, from its first instruction. A function larger than
        // the window is not analysed past it: no compiler emits one, and finding out otherwise is
        // not worth holding a program's worth of instructions to do it.
        //
        // `ends` is for the caller that reads nothing but where each instruction ends a block: the
        // decoder then records no registers and no memory operands, which is most of what decoding a
        // program's worth of instructions costs. The chain is the same either way — what an instruction
        // *is* does not depend on what was recorded about it.
        end = Math.Min(end, start + DecodeWindowSize);
        CodeDecoder decoder = ends ? PassDecoder() : PassDecoder(registers: true, memory: true);
        return [.. StreamCode(decoder, start, end)];
    }

    /// <summary>
    /// True when one of the already-known function starts covers this neighbourhood. Only the
    /// greatest start at or below <paramref name="rva"/> can: a smaller one ends 0x4000 bytes earlier
    /// than that, so if this one does not reach, neither does any other.
    /// </summary>
    private static bool IsCovered(uint rva, List<uint> ascendingStarts)
    {
        int index = ascendingStarts.BinarySearch(rva);
        int candidate = index >= 0 ? index : ~index - 1;
        return candidate >= 0 && rva < ascendingStarts[candidate] + 0x4000;
    }

    private uint NextBoundary(List<Seed> ordered, int index, BinarySection? section, out bool nextIsFunction)
    {
        uint sectionEnd = section is null ? _image.SizeOfImage : Math.Min(section.RvaEnd, section.Rva + section.VirtualSize);
        for (int i = index + 1; i < ordered.Count; i++)
        {
            if (ordered[i].Rva > ordered[index].Rva)
            {
                nextIsFunction = true;
                return ordered[i].Rva;
            }
        }

        nextIsFunction = false;
        return sectionEnd;
    }

    /// <summary>
    /// Estimates a function's size when no symbol carries one. The reason is reported to the caller
    /// so the inventory can say how the number was obtained instead of presenting a guess as a fact.
    /// </summary>
    private uint EstimateSize(uint start, uint estimatedEnd, HashSet<uint> tableTargets, out string reason)
    {
        reason = "size_estimated";

        if (estimatedEnd <= start)
        {
            return 0;
        }

        // The last place a block ends inside the range, and this is the function's own instruction chain
        // rather than the section-wide stream: within a range this wide the two walks do not always
        // decode the same bytes — a range that is not really a function has an entry that is not an
        // instruction boundary — and the chain from the entry is what the sizes have always been read
        // from. Only `EndsBlock` and the length are read here, so nothing else is recorded: asking the
        // decoder for registers and memory operands on the way past was two thirds of this phase's cost.
        uint lastTerminatorEnd = 0;
        foreach (var insn in InstructionsIn(start, estimatedEnd, ends: true))
        {
            if (insn.EndsBlock)
            {
                lastTerminatorEnd = insn.Rva + (uint)insn.Length;
            }
        }

        if (lastTerminatorEnd > start && lastTerminatorEnd < estimatedEnd && IsPaddingBetween(lastTerminatorEnd, estimatedEnd))
        {
            reason = "size_from_terminator";
            return lastTerminatorEnd - start;
        }

        // Nothing branches past the last terminator and there is no jump table in the way, so the
        // bytes up to the next boundary are dead space (alignment, cold padding) rather than code.
        if (lastTerminatorEnd > start && lastTerminatorEnd < estimatedEnd
            && !HasReferencedInstruction(lastTerminatorEnd, estimatedEnd, tableTargets))
        {
            reason = "size_to_last_terminator";
            return lastTerminatorEnd - start;
        }

        // A jump table or other known data inside the range ends the function.
        uint dataStart = estimatedEnd;
        foreach (uint target in tableTargets)
        {
            if (target > start && target < dataStart)
            {
                dataStart = target;
            }
        }

        if (dataStart < estimatedEnd)
        {
            reason = "size_from_data_boundary";
            return dataStart - start;
        }

        reason = IsPaddingBetween(lastTerminatorEnd, estimatedEnd) ? "size_from_terminator" : "size_to_next_boundary";
        return estimatedEnd - start;
    }

    /// <summary>
    /// True when the half-open range holds an instruction that some branch targets, which means the
    /// range cannot be dismissed as padding.
    /// </summary>
    private bool HasReferencedInstruction(uint from, uint to, HashSet<uint> tableTargets)
    {
        // Every address a branch points at is an instruction start by construction, so the question
        // is only whether one of them falls inside the range — which the sorted array answers
        // without walking the image.
        int index = Array.BinarySearch(_referencedSorted, from);
        int first = index >= 0 ? index : ~index;
        if (first < _referencedSorted.Length && _referencedSorted[first] < to)
        {
            return true;
        }

        foreach (uint target in tableTargets)
        {
            if (target >= from && target < to)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPaddingBetween(uint from, uint to)
    {
        int? offset = _image.RvaToOffset(from);
        if (offset is null)
        {
            return false;
        }

        var padding = _profile?.Codegen.PaddingBytes ?? [(byte)0xCC];
        int length = (int)(to - from);
        for (int i = 0; i < length; i++)
        {
            int position = offset.Value + i;
            if (position >= _bytes.Length)
            {
                return true;
            }

            byte value = _bytes[position];
            if (!padding.Contains(value) && value != 0x00 && value != 0x90)
            {
                return false;
            }
        }

        return true;
    }

    private bool DetectJumpTableUse(uint start, uint size)
    {
        if (!_options.DetectJumpTables || size == 0)
        {
            return false;
        }

        // The question is whether this function reads a table, not whether its branch names one: a
        // 32-bit absolute branch does (`jmp dword [table + reg*4]`), and every other kind reaches the
        // table through a register and jumps through that. Both are caught by asking the tables the
        // analysis already found where they are read from.
        return _jumpTables.Any(t => t.UsedAtRva >= start && t.UsedAtRva < start + size);
    }

    // ---------------------------------------------------------------- calling conventions

    /// <summary>True when a C symbol carries a decoration that names its calling convention.</summary>
    private static bool IsDecorated(string name)
        => name.StartsWith('_') || name.Contains('@');

    private CallingConventionInfo DetectCallingConvention(FunctionEntry function, DebugInfoResult? debug)
    {
        var info = new CallingConventionInfo();

        // The name of the function first, then the other names the binary carries for the same
        // address: MSVC records __stdcall as _name@N and __fastcall as @name@N, and a PDB stores the
        // undecorated name, so the decoration that states the convention is often only in an alias.
        var candidates = new List<string>();
        if (function.Name is not null)
        {
            candidates.Add(function.Name);
        }

        candidates.AddRange(function.Aliases);

        string? undecorated = null;
        foreach (string candidate in candidates)
        {
            var demangled = Demangler.Demangle(candidate);
            if (string.Equals(candidate, function.Name, StringComparison.Ordinal) && demangled.Text is not null)
            {
                function.Demangled = demangled.Text;
            }

            string? convention = demangled.CallingConvention;
            if (convention is null || convention == "unknown")
            {
                continue;
            }

            // An undecorated identifier states no convention: 'cdecl' there is the default. A
            // decorated name (_name, _name@N, @name@N) really does state one, so it is decisive.
            if (convention == "cdecl" && demangled.Scheme == "c" && !IsDecorated(candidate))
            {
                undecorated ??= convention;
                continue;
            }

            info.Value = convention;
            info.Confidence = "high";
            info.Evidence.Add($"mangling:{demangled.Scheme}");
            if (!string.Equals(candidate, function.Name, StringComparison.Ordinal))
            {
                info.Evidence.Add($"alias:{candidate}");
            }

            return info;
        }

        if (undecorated is not null)
        {
            info.Value = undecorated;
            info.Confidence = "high";
            info.Evidence.Add("mangling:c");
            return info;
        }

        if (function.ImportThunk is not null)
        {
            info.Value = "unknown";
            info.Confidence = "low";
            info.Evidence.Add("import_thunk");
            return info;
        }

        // The function's instructions are streamed rather than listed: what is read here is the last
        // instruction, the registers the first few read, and whether any of them reads an argument off
        // the frame — three questions that a walk answers as it goes. Holding every instruction of every
        // function of an 11 MB program to ask them was the largest remaining cost in an analysis.
        bool readsEcx = false;
        bool readsEdx = false;
        bool readsStackArgs = false;
        DecodedInsn? last = null;
        int seen = 0;
        foreach (var insn in Instructions(function.Start, function.Start + Math.Max(function.Size, 1)))
        {
            last = insn;
            if (seen < 8)
            {
                readsEcx |= insn.ReadRegisters.Contains("ECX");
                readsEdx |= insn.ReadRegisters.Contains("EDX");
            }

            seen++;
            if (!readsStackArgs)
            {
                foreach (var memory in insn.MemoryRefs)
                {
                    if (memory.BaseRegister is "EBP" && !memory.HasIndex && memory.Address >= 8 && memory.Address < 0x1000)
                    {
                        readsStackArgs = true;
                        break;
                    }
                }
            }
        }

        if (last is null)
        {
            return info;
        }

        if (last.IsReturn && last.RetPopBytes is > 0)
        {
            info.Value = "stdcall";
            info.Confidence = "medium";
            info.Evidence.Add($"ret {last.RetPopBytes}");
            return info;
        }

        if (readsEcx && readsEdx)
        {
            info.Value = "fastcall";
            info.Confidence = "low";
            info.Evidence.Add("reads_ecx_early");
            info.Evidence.Add("reads_edx_early");
            return info;
        }

        if (readsEcx)
        {
            info.Value = "thiscall";
            info.Confidence = "low";
            info.Evidence.Add("reads_ecx_early");
            return info;
        }

        if (last.IsReturn && last.RetPopBytes == 0)
        {
            info.Value = readsStackArgs ? "cdecl" : "cdecl";
            info.Confidence = readsStackArgs ? "medium" : "low";
            info.Evidence.Add("ret");
            if (readsStackArgs)
            {
                info.Evidence.Add("reads_stack_args");
            }

            return info;
        }

        info.Value = "unknown";
        info.Confidence = "low";
        info.Evidence.Add("no_terminator_found");
        _ = debug;
        return info;
    }

    // ---------------------------------------------------------------- aliases and flags

    private void ApplyAliases(List<FunctionEntry> functions)
    {
        var byStart = functions.GroupBy(f => f.Start).Where(g => g.Count() > 1);
        foreach (var group in byStart)
        {
            var names = group.SelectMany(f => f.Name is null ? [] : new[] { f.Name }).Distinct(StringComparer.Ordinal).ToList();
            if (names.Count < 2)
            {
                continue;
            }

            var primary = group.First();
            primary.Aliases = [.. names.Where(n => n != primary.Name)];
            primary.Flags.Add("folded");
            foreach (var other in group.Skip(1))
            {
                other.Aliases = [.. names.Where(n => n != other.Name)];
                other.Flags.Add("folded");
            }
        }

        if (_profile?.Linker.IdenticalCodeFolding == "never")
        {
            return;
        }

        // Identical bodies reached through different symbols: MSVC folding, kept visible as a flag.
        var byBody = functions
            .Where(f => f.Size >= 8 && f.Confidence != "low")
            .GroupBy(f => BodyHash(f.Start, f.Size))
            .Where(g => g.Select(f => f.Start).Distinct().Count() > 1 && g.All(f => f.Size == g.First().Size));

        foreach (var group in byBody)
        {
            var entries = group.ToList();
            foreach (var entry in entries)
            {
                entry.Flags.Add("identical_body");
                entry.Unknowns.Add($"identical_body_group:{string.Join(",", entries.Where(e => e != entry).Select(e => e.Id))}");
            }
        }
    }

    private string BodyHash(uint start, uint size)
    {
        int? offset = _image.RvaToOffset(start);
        if (offset is null || offset.Value + size > _bytes.Length)
        {
            return "n/a";
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(_bytes.AsSpan(offset.Value, (int)size)));
    }

    private void ApplyNoReturn(List<FunctionEntry> functions)
    {
        if (_options.NoReturn.Count == 0)
        {
            return;
        }

        foreach (var function in functions)
        {
            if (function.Name is not null && _options.NoReturn.Contains(function.Name, StringComparer.Ordinal))
            {
                function.Flags.Add("no_return");
            }
        }
    }

    // ---------------------------------------------------------------- jump tables

    /// <summary>
    /// Finds the jump tables of this image: a switch a compiler turned into an indexed read of a
    /// table of code addresses.
    ///
    /// Two shapes exist, and a tool that only knows the first sees no jump tables at all in a modern
    /// Linux binary. A 32-bit absolute image indexes the table in the branch itself
    /// (<c>jmp dword [0xa058 + eax*4]</c>), so the table's address is a constant in the instruction. A
    /// position-independent image cannot do that: it loads the table's address into a register first
    /// (<c>lea rdx, [rip+0xdb4]</c>), reads a 4-byte offset out of it
    /// (<c>movsxd rax, [rdx + rdi*4]</c>), adds the two and jumps. That is why this remembers which
    /// register holds which address: without it, every switch in a PIE binary is a jump through a
    /// pointer nobody can follow.
    ///
    /// Both entry encodings are tried — a full address, and an offset from the table's own address —
    /// and the one that accounts for more of the table wins. Which one it was is recorded, because a
    /// table of offsets and a table of addresses are not the same thing to whoever reads the report.
    ///
    /// This is state fed one instruction at a time by `ScanInstructions` rather than a pass of its own:
    /// the same chain, the same decoder options and the same order, at the cost of one decode instead
    /// of two.
    /// </summary>
    private sealed class TableFinder(InventoryAnalyzer owner)
    {
        private readonly HashSet<(uint Table, uint Insn)> _seen = [];
        private readonly Dictionary<string, uint> _addresses = new(StringComparer.Ordinal);

        private readonly List<((uint Rva, uint? Base) Table, uint UsedAt, int Scale, int Minimum)> _candidates = [];

        public void Visit(DecodedInsn insn)
        {
            // The indexed reads, and the registers the two idioms that compute an address without
            // loading it write: `lea reg, [address]`, and the `call`/`add` pair a position-independent
            // binary uses.
            foreach (var memory in insn.MemoryRefs)
            {
                if (!memory.HasIndex || !memory.Read || memory.IsFrameRelative)
                {
                    continue;
                }

                var table = owner.TableAddress(memory, _addresses);
                if (table is null || !_seen.Add((table.Value.Rva, insn.Rva)))
                {
                    continue;
                }

                // A table named in the branch itself is one the compiler meant; one reached through a
                // register is a guess that has to pay for itself with a longer run of real targets.
                _candidates.Add((table.Value, insn.Rva, memory.IndexScale, memory.Rva is not null ? 2 : 3));
            }

            owner.TrackAddressRegisters(insn, _addresses);
        }

        /// <summary>
        /// Reads the tables the scan spotted, **after** the scan has finished.
        ///
        /// The reading cannot happen where the spotting does: an entry counts only when it lands on a
        /// decoded instruction, and the scan is still marking instruction starts as it goes, so a
        /// table read mid-walk breaks at its first entry and reports nothing — measured, 95063
        /// candidates and zero tables. The order, the deduplication and the rules are the walk's; only
        /// the read of the bytes waits for the map it is checked against.
        /// </summary>
        public List<JumpTableEntry> ReadTables()
        {
            var tables = new List<JumpTableEntry>(_candidates.Count);
            foreach (var (table, usedAt, scale, minimum) in _candidates)
            {
                var entry = owner.ReadJumpTable(table, scale, usedAt, minimum);
                if (entry is not null)
                {
                    tables.Add(entry);
                }
            }

            return tables;
        }
    }

    /// <summary>
    /// Where a table is read from, when that can be worked out at all: the table's own address, and
    /// the value of the register it is indexed from when that is known — a table of offsets is
    /// relative to one or the other, and which one is a property of the way the compiler wrote it.
    /// </summary>
    private (uint Rva, uint? Base)? TableAddress(MemoryRef memory, Dictionary<string, uint> addresses)
    {
        if (memory.Rva is uint constant)
        {
            return (constant, null);
        }

        // [reg + index*scale + displacement], where reg holds an address this pass has followed.
        if (memory.BaseRegister is { } baseRegister && addresses.TryGetValue(baseRegister, out uint baseValue))
        {
            return Offset(baseValue, memory.Address);
        }

        // The two operands can be the other way round. `lea rdx, [index*4]` followed by
        // `mov eax, [rdx + rax]` reads the same table with the roles swapped — the register holding
        // the address is the *index* operand here — and that is the form a compiler at -O0 emits.
        if (memory.IndexRegister is { } indexRegister && addresses.TryGetValue(indexRegister, out uint indexValue))
        {
            return Offset(indexValue, memory.Address);
        }

        return null;

        // The displacement is signed, so the addition is done wide and truncated: a negative
        // displacement is a table below the address in the register, which is the usual case.
        (uint Rva, uint? Base)? Offset(uint value, ulong displacement)
        {
            uint address = (uint)(value + displacement);
            return _image.ContainsRva(address) ? (address, value) : null;
        }
    }

    /// <summary>
    /// Follows the two idioms a compiler uses to get an address into a register without ever loading
    /// it from memory, and forgets a register as soon as something else is written to it.
    ///
    /// The first is <c>lea reg, [address]</c>. The second is the 32-bit position-independent idiom:
    /// <c>call __x86.get_pc_thunk.ax</c> pushes the return address into the register and
    /// <c>add $delta, reg</c> turns it into the address of the table. Both are worth following,
    /// because both are what a switch in a position-independent binary reads its table through.
    /// </summary>
    /// <summary>Shared empty list, so scanning an instruction that writes nothing allocates nothing.</summary>
    private static readonly List<string> NoRegisters = [];

    private void TrackAddressRegisters(DecodedInsn insn, Dictionary<string, uint> addresses)
    {
        // Read through the field test rather than the property: touching the property would
        // allocate a list for every instruction scanned, and most instructions write nothing
        // this analysis tracks.
        var written = insn.HasWrittenRegisters ? insn.WrittenRegisters : NoRegisters;

        // The order matters: an instruction that computes an address has to be recognised before the
        // rule that forgets a register that is written, or `add $delta, reg` would clear the very
        // value it is adjusting.
        if (insn.Mnemonic == Mnemonic.Lea && written.Count > 0)
        {
            // A memory operand is a struct, so the "none found" case is a default one whose Rva is
            // null — not a null reference to test for.
            uint? address = insn.MemoryRefs.FirstOrDefault(m => m.Rva is not null).Rva;
            foreach (var register in written)
            {
                if (address is null)
                {
                    addresses.Remove(register);
                }
                else
                {
                    addresses[register] = address.Value;
                }
            }

            return;
        }

        if (insn.IsCall && insn.DirectTargetRva is uint thunkRva && PicThunkRegister(thunkRva) is { } picRegister)
        {
            // What the call left in the register is the address of the instruction after it.
            foreach (var register in written)
            {
                addresses.Remove(register);
            }

            addresses[picRegister] = insn.Rva + (uint)insn.Length;
            return;
        }

        if (insn.Mnemonic == Mnemonic.Add && written.Count == 1 && insn.Immediate is long delta
            && addresses.TryGetValue(written[0], out uint value))
        {
            addresses[written[0]] = (uint)(value + delta);
            return;
        }

        foreach (var register in written)
        {
            addresses.Remove(register);
        }
    }

    /// <summary>
    /// The register a <c>__x86.get_pc_thunk.*</c> stub leaves the return address in, when the call
    /// target really is one of those stubs. The stub is three instructions of a fixed shape
    /// (<c>mov reg, [esp]</c> then <c>ret</c>), so it is recognised by its bytes rather than by a
    /// name the analysis does not carry.
    /// </summary>
    private string? PicThunkRegister(uint rva)
    {
        int? offset = _image.RvaToOffset(rva);
        if (offset is null || offset.Value + 4 > _bytes.Length)
        {
            return null;
        }

        byte[] code = _bytes;
        int at = offset.Value;
        if (code[at] != 0x8B || code[at + 2] != 0x24 || code[at + 3] != 0xC3)
        {
            return null;
        }

        // 8B /r: the register is in the reg field of the modrm byte, `[esp]` being the r/m.
        int register = (code[at + 1] >> 3) & 7;
        string[] names = _image.Is64
            ? ["RAX", "RCX", "RDX", "RBX", "RSP", "RBP", "RSI", "RDI"]
            : ["EAX", "ECX", "EDX", "EBX", "ESP", "EBP", "ESI", "EDI"];

        // ESP/RSP is not a register this idiom ever uses; matching it would be reading noise.
        return register == 4 ? null : names[register];
    }

    /// <summary>
    /// Reads one table at <paramref name="tableRva"/> and returns it if its entries really are code
    /// addresses. Entries are read as full addresses first and as offsets from the table second.
    /// </summary>
    private JumpTableEntry? ReadJumpTable((uint Rva, uint? Base) table, int scale, uint usedAtRva, int minimum)
    {
        int? offset = _image.RvaToOffset(table.Rva);
        if (offset is null)
        {
            return null;
        }

        int width = scale >= 8 ? 8 : 4;

        // Three encodings exist and the file does not say which one it used, so each is read and the
        // one that accounts for most of the table is believed:
        //   absolute       — the entry is the address (a non-relocatable image);
        //   base-relative  — the entry is an offset from the register the table is indexed from,
        //                    which is how 32-bit position-independent code is written;
        //   table-relative — the entry is an offset from the table's own address, which is how the
        //                    64-bit kind is written, and the same thing when base and table coincide.
        var absolute = ReadTableTargets(offset.Value, width, table.Rva, baseRva: null);
        var fromBase = table.Base is { } known && width == 4
            ? ReadTableTargets(offset.Value, width, table.Rva, known)
            : [];
        var fromTable = width == 4
            ? ReadTableTargets(offset.Value, width, table.Rva, table.Rva)
            : [];

        var best = absolute;
        string kind = "absolute";
        if (fromBase.Count > best.Count)
        {
            best = fromBase;
            kind = "relative";
        }

        if (fromTable.Count > best.Count)
        {
            best = fromTable;
            kind = "relative";
        }

        if (best.Count < minimum)
        {
            return null;
        }

        return new JumpTableEntry
        {
            Rva = table.Rva,
            Entries = best.Count,
            Kind = kind,
            EntryWidth = width,
            Targets = [.. best],
            UsedAtRva = usedAtRva,
        };
    }

    /// <summary>
    /// Reads consecutive entries while each one lands on a decoded instruction: the first entry that
    /// does not is where the table ends, which is how an unknown table length is discovered rather
    /// than guessed.
    /// </summary>
    private List<uint> ReadTableTargets(int offset, int width, uint tableRva, uint? baseRva)
    {
        var targets = new List<uint>();
        for (int i = 0; i < 512; i++)
        {
            int position = offset + (i * width);
            if (position + width > _bytes.Length)
            {
                break;
            }

            uint? rva = width == 8
                ? AbsoluteTarget(BitConverter.ToUInt64(_bytes, position))
                : baseRva is { } known
                    ? RelativeTarget((int)BitConverter.ToUInt32(_bytes, position), known)
                    : AbsoluteTarget(BitConverter.ToUInt32(_bytes, position));

            if (rva is null || !IsInstructionStart(rva.Value))
            {
                break;
            }

            targets.Add(rva.Value);
        }

        return targets;
    }

    private uint? AbsoluteTarget(ulong value)
    {
        if (value < _image.ImageBase)
        {
            return null;
        }

        ulong rva = value - _image.ImageBase;
        return rva <= uint.MaxValue && _image.ContainsRva((uint)rva) ? (uint)rva : null;
    }

    /// <summary>A 4-byte entry of a position-independent table: a signed offset from a known address.</summary>
    private uint? RelativeTarget(int offset, uint from)
    {
        long rva = from + (long)offset;
        return rva >= 0 && rva <= uint.MaxValue && _image.ContainsRva((uint)rva) ? (uint)rva : null;
    }

    private void AttachJumpTableOwners(AnalysisResult result, List<JumpTableEntry> tables, List<FunctionEntry> functions)
    {
        foreach (var table in tables)
        {
            var owner = functions.FirstOrDefault(f => f.Covers(table.UsedAtRva));
            table.Owner = owner?.Id;
        }

        _ = result;
    }

    // ---------------------------------------------------------------- data

    private List<DataEntry> CollectData(DebugInfoResult? debug, List<(uint Rva, uint Size)> jumpTables)
    {
        var data = new List<DataEntry>();

        foreach (var (rva, size) in jumpTables)
        {
            data.Add(new DataEntry { Rva = rva, Size = size, Kind = "jump_table", Source = "analysis" });
        }

        if (debug is not null)
        {
            foreach (var symbol in debug.Symbols.Where(s => s.IsData || _image.SectionContainingRva(s.Rva)?.IsCode == false))
            {
                data.Add(new DataEntry
                {
                    Rva = symbol.Rva,
                    Size = symbol.Size ?? 0,
                    Name = symbol.Name,
                    Kind = "data",
                    Source = SymbolSourceNames.ToWire(symbol.Source),
                });
            }
        }

        foreach (var range in _options.DataRanges)
        {
            data.Add(new DataEntry { Rva = range.Rva, Size = range.Size, Kind = range.Kind, Source = "config" });
        }

        // Section-level fallback ranges, so "unknown stays unknown" is visible rather than empty.
        foreach (var section in _image.Sections.Where(s => !s.IsCode))
        {
            if (section.VirtualSize == 0 && section.RawSize == 0)
            {
                continue;
            }

            data.Add(new DataEntry
            {
                Rva = section.Rva,
                Size = Math.Max(section.VirtualSize, section.RawSize),
                Kind = section.IsWritable ? "data" : "read_only",
                Source = "section",
                Name = section.Name,
            });
        }

        return data
            .GroupBy(d => (d.Rva, d.Kind, d.Name))
            .Select(g => g.First())
            .OrderBy(d => d.Rva)
            .ToList();
    }

    // ---------------------------------------------------------------- xrefs

    /// <summary>
    /// The cross-references of the program, built from the candidates the scan collected.
    ///
    /// Nothing is decoded here. What is left to do needs the functions — a reference belongs to the
    /// function that contains it, and points at a function when its target is one — and those do not
    /// exist until the seeds have been built. So the scan records candidates while it is reading the
    /// code anyway, and this turns them into the inventory's `xrefs`: deduplicated by (from, to, kind),
    /// merged where the same instruction was relocated, attributed, and sorted.
    /// </summary>
    private List<Xref> BuildXrefs(List<FunctionEntry> functions, List<DataEntry> data)
    {
        _ = data;
        var xrefs = new Dictionary<(uint From, uint To, XrefKind Kind), Xref>();
        var functionFor = BuildFunctionLookup(functions);

        // Looked up once per xref: scanning the function list for each was a second pass over every
        // function for every cross-reference in the program.
        var functionsByStart = new Dictionary<uint, FunctionEntry>();
        foreach (var function in functions)
        {
            functionsByStart.TryAdd(function.Start, function);
        }

        void Add(uint from, uint to, XrefKind kind, bool viaReloc)
        {
            var key = (from, to, kind);
            if (xrefs.TryGetValue(key, out var existing))
            {
                existing.ViaReloc |= viaReloc;
                return;
            }

            var owner = functionFor(from);
            xrefs[key] = new Xref
            {
                FromRva = from,
                ToRva = to,
                Kind = KindName(kind),
                ViaReloc = viaReloc,
                InFunction = owner?.Id,
                ToFunction = functionsByStart.TryGetValue(to, out var target) ? target.Id : null,
            };
        }

        foreach (var (from, to, kind, viaReloc) in _directRefs)
        {
            Add(from, to, kind, viaReloc);
        }

        foreach (var (from, to, kind, viaReloc) in _memoryRefs)
        {
            Add(from, to, kind, viaReloc);
        }

        // Every relocation target becomes an xref, so nothing is lost when the instruction form hides it.
        foreach (var relocation in _image.Relocations)
        {
            if (relocation.TargetRva is not uint targetRva)
            {
                continue;
            }

            bool inCode = _image.SectionContainingRva(relocation.Rva)?.IsCode == true;
            Add(relocation.Rva, targetRva, inCode ? XrefKind.Reloc : XrefKind.DataRef, true);
        }

        return xrefs.Values.OrderBy(x => x.FromRva).ThenBy(x => x.ToRva).ToList();
    }

    /// <summary>
    /// Whether any byte of an instruction is relocated. Asked of every instruction, so it allocates
    /// nothing: an earlier version built an <see cref="Enumerable.Range"/> per instruction to ask the
    /// same question.
    /// </summary>
    private static bool ViaRelocation(HashSet<uint> relocated, uint rva, int length)
    {
        for (int offset = 0; offset < length; offset++)
        {
            if (relocated.Contains(rva + (uint)offset))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Answers "which function does this address belong to?". The old answer walked the whole
    /// function list for every instruction that asked — a 300-function program over 4.4 million
    /// instructions is 1.3 billion comparisons, which is most of where the time went.
    ///
    /// Addresses are asked for in ascending order, so the answer is found by walking forward: retire
    /// the ranges that have ended, admit the ones that have started, and the owner is whichever
    /// admitted range covers the address. An address asked out of order starts the walk again.
    /// </summary>
    private Func<uint, FunctionEntry?> BuildFunctionLookup(List<FunctionEntry> functions)
    {
        var ordered = functions.OrderBy(f => f.Start).ToList();
        var active = new List<FunctionEntry>();
        int next = 0;
        uint lastRva = 0;

        return rva =>
        {
            if (rva < lastRva)
            {
                // Not the sweep this is built for: restart rather than answer from a stale position.
                active.Clear();
                next = 0;
            }

            lastRva = rva;

            active.RemoveAll(f => f.Start + f.Size <= rva);
            while (next < ordered.Count && ordered[next].Start <= rva)
            {
                active.Add(ordered[next]);
                next++;
            }

            // Admitted in ascending order of start, so the first range that reaches is the lowest
            // start that covers the address — which is what walking the list from the beginning found.
            foreach (var function in active)
            {
                if (rva < function.Start + function.Size)
                {
                    return function;
                }
            }

            return null;
        };
    }

    // ---------------------------------------------------------------- statistics

    private static Dictionary<string, int> BuildStatistics(AnalysisResult result)
    {
        var stats = new Dictionary<string, int>
        {
            ["functions"] = result.Functions.Count,
            ["functions_high"] = result.Functions.Count(f => f.Confidence == "high"),
            ["functions_medium"] = result.Functions.Count(f => f.Confidence == "medium"),
            ["functions_low"] = result.Functions.Count(f => f.Confidence == "low"),
            ["functions_import_thunks"] = result.Functions.Count(f => f.ImportThunk is not null),
            ["functions_named_by_signature"] = result.Functions.Count(f => f.FoundBy.Any(s => string.Equals(s.Value, "signature", StringComparison.Ordinal))),
            ["functions_with_unknown_size"] = result.Functions.Count(f => f.Unknowns.Any(u => u.StartsWith("size_", StringComparison.Ordinal))),
            ["calling_conventions_known"] = result.Functions.Count(f => f.CallingConvention.Value != "unknown"),
            ["jump_tables"] = result.JumpTables.Count,
            ["data_ranges"] = result.Data.Count,
            ["xrefs"] = result.Xrefs.Count,
            ["instructions"] = result.InstructionCount,
        };
        return stats;
    }

    private static int Rank(string confidence) => Array.IndexOf(ConfidenceOrder, confidence) switch
    {
        < 0 => 0,
        var index => index,
    };
}
