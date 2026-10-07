using Iced.Intel;
using Recon.Inventory;
using Recon.Toolchains;

namespace Recon.Compare;

/// <summary>What a comparison run is allowed to do. Everything a profile could later override.</summary>
public sealed class ComparisonOptions
{
    /// <summary>Two bodies whose aligned instructions score below this are not paired.</summary>
    public double SimilarityThreshold { get; set; } = 0.80;

    public bool IgnorePadding { get; set; } = true;

    /// <summary>Data words shown per symbol, so a document stays readable.</summary>
    public int DataWordLimit { get; set; } = 16;

    /// <summary>Differences reported per function, so a document stays readable.</summary>
    public int DifferenceLimit { get; set; } = 24;

    /// <summary>
    /// When set, the function with this name gets its whole aligned body in the document. Asking for
    /// one function is what makes the extra detail affordable.
    /// </summary>
    public string? AlignedFunction { get; set; }

    /// <summary>Mnemonics that count as alignment padding, from the toolchain's padding bytes.</summary>
    public HashSet<Mnemonic> PaddingMnemonics { get; } = [Mnemonic.Int3, Mnemonic.Nop];

    /// <summary>The profile these settings came from, recorded in the document.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Called as each stage of a comparison finishes, with its wall time. A comparison of two real
    /// programs is minutes, and which stage those minutes are in is not something to guess at: the
    /// phase report is what said the similarity prefilter was not the cost, the normalizing was.
    /// </summary>
    public Action<string, TimeSpan>? Phase { get; set; }

    /// <summary>
    /// Takes the compare settings from a toolchain profile, falling back to the defaults for anything
    /// the profile leaves unset. Section 3.1 of the plan asks for exactly this: the noise a toolchain
    /// produces is a profile setting, not a special case in the engine.
    /// </summary>
    public static ComparisonOptions FromProfile(ToolchainProfile? profile)
    {
        var options = new ComparisonOptions();
        if (profile is null)
        {
            return options;
        }

        options.Profile = profile.Id;
        options.SimilarityThreshold = profile.Compare.SimilarityThreshold ?? options.SimilarityThreshold;
        options.IgnorePadding = profile.Compare.IgnorePadding ?? options.IgnorePadding;
        options.DifferenceLimit = profile.Compare.DifferenceLimit ?? options.DifferenceLimit;

        var padding = new HashSet<Mnemonic>();
        foreach (byte fill in profile.Codegen.PaddingBytes)
        {
            switch (fill)
            {
                case 0x90:
                    padding.Add(Mnemonic.Nop);
                    break;
                case 0xCC:
                    padding.Add(Mnemonic.Int3);
                    break;
                default:
                    break;
            }
        }

        if (padding.Count > 0)
        {
            options.PaddingMnemonics.Clear();
            options.PaddingMnemonics.UnionWith(padding);
        }

        return options;
    }
}

/// <summary>
/// Builds the comparison document: pairs the functions of two binaries, normalizes each pair's code
/// until only real differences remain, and scores the result.
/// </summary>
/// <remarks>
/// Pairing runs in the order that gives the most trustworthy answer first: the symbol name the two
/// builds share, then the aliases a build may present the same function under, then a body that is
/// byte-for-byte identical once relocated operands are symbolic, and only then similarity. A pair
/// found by similarity is never allowed to steal a function that a name already claimed.
/// </remarks>
public static class ComparisonBuilder
{
    public static ComparisonDocument Build(ComparisonSide left, ComparisonSide right, ComparisonOptions options)
    {
        var document = new ComparisonDocument
        {
            Generator = new GeneratorInfo
            {
                Tool = "recon",
                Version = Recon.ToolVersion.Current,
                Command = "diff",
            },
            Left = Describe(left),
            Right = Describe(right),
            Model = new ComparisonModelInfo
            {
                SimilarityThreshold = options.SimilarityThreshold,
                IgnorePadding = options.IgnorePadding,
                DifferenceLimit = options.DifferenceLimit,
                Profile = options.Profile,
                Options = options,
            },
        };

        var leftFunctions = left.Index.Functions.Select(f => f).ToList();
        var rightFunctions = right.Index.Functions.Select(f => f).ToList();

        // A body is needed to pair a function and again to compare it, and those two moments can be far
        // apart. So the bodies are made when asked for and dropped when the ask is answered, and what
        // is kept between the two is the summary: a key, a count, and a histogram of mnemonics.
        var leftBodies = new SideBodies(left, options);
        var rightBodies = new SideBodies(right, options);
        ReturnWhatTheDocumentsLeft();
        var leftSummaries = Measure(options, "summarize L", () => SummarizeAll(left, leftBodies, leftFunctions, document));
        var rightSummaries = Measure(options, "summarize R", () => SummarizeAll(right, rightBodies, rightFunctions, document));
        var matching = Measure(options, "pair", () => Match(leftSummaries, rightSummaries, options, leftBodies, rightBodies));
        Measure(options, "compare", () =>
        {
            foreach (var pair in matching.Pairs)
            {
                document.Functions.Add(Compare(pair, leftSummaries, rightSummaries, leftBodies, rightBodies, options, document));
                document.Model.MatchedBy[pair.Match] = document.Model.MatchedBy.GetValueOrDefault(pair.Match) + 1;
            }
        });


        // Whatever no stage paired is reported as unpaired. A comparison that quietly dropped
        // functions would look better than it is, which is the one thing a score must not do.
        // Before calling one missing, check whether identical-code folding is the explanation: the
        // other build may have kept one body under two names, so this function's body is present
        // even though nothing is free to pair with it by name.
        var pairedBodiesOnLeft = BodyIndex(matching.Pairs.Select(p => leftSummaries[p.Left]).Where(s => s.InstructionCount > 0));
        var pairedBodiesOnRight = BodyIndex(matching.Pairs.Select(p => rightSummaries[p.Right]).Where(s => s.InstructionCount > 0));

        for (int index = 0; index < leftSummaries.Count; index++)
        {
            if (!matching.LeftTaken[index])
            {
                document.Functions.Add(Unmatched(leftSummaries[index], isLeft: true, pairedBodiesOnRight));
            }
        }

        for (int index = 0; index < rightSummaries.Count; index++)
        {
            if (!matching.RightTaken[index])
            {
                document.Functions.Add(Unmatched(rightSummaries[index], isLeft: false, pairedBodiesOnLeft));
            }
        }

        document.Functions.Sort(OrderFunctions);
        Summarize(document, left, right, matching);

        var data = CompareData(left, right, options);
        document.Data = data;
        document.Summary.DataSymbols = data.Symbols.Count;
        document.Summary.DataIdentical = data.Identical;
        document.Summary.DataChanged = data.Changed;

        foreach (var problem in left.Inventory.Problems.Concat(right.Inventory.Problems))
        {
            document.Problems.Add(problem);
        }

        foreach (var function in document.Functions)
        {
            foreach (var note in function.Notes.Where(n => n.StartsWith("unreadable", StringComparison.Ordinal)))
            {
                document.Problems.Add($"{function.Left?.Name ?? function.Right?.Name}: {note}");
            }
        }

        return document;
    }


    /// <summary>
    /// Hands back what reading the two inventories left behind, before the passes that walk every
    /// instruction start.
    ///
    /// Reading a 68 MB document is two large transient buffers — the file, and the copy with what a
    /// comparison does not read dropped from it — and by this point both are garbage the collector has
    /// had no reason to look at. Measured on the 11.8 MB client: **515 MB committed before this call
    /// and 50 MB after it, in 4 ms**, and 85 MB off the peak of the whole run. It is the one place a
    /// comparison knows it has just thrown away hundreds of megabytes and is about to allocate them
    /// again.
    /// </summary>
    private static void ReturnWhatTheDocumentsLeft()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Runs a stage and tells the caller how long it took, when the caller asked to be told.</summary>
    private static T Measure<T>(ComparisonOptions options, string name, Func<T> stage)
    {
        if (options.Phase is null)
        {
            return stage();
        }

        long started = Environment.TickCount64;
        T result = stage();
        options.Phase(name, TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
        return result;
    }

    private static void Measure(ComparisonOptions options, string name, Action stage)
        => Measure(options, name, () => { stage(); return true; });

    // ------------------------------------------------------------------ pairing

    private static List<BodySummary> SummarizeAll(
        ComparisonSide side,
        SideBodies bodies,
        IReadOnlyList<FunctionInfo> functions,
        ComparisonDocument document)
    {
        var normalizer = new FunctionNormalizer(side.Index, side.Bytes, document.Model.Options);
        var result = new List<BodySummary>(functions.Count);
        foreach (var function in functions)
        {
            // The body is never built: this walk measures it as it reads it, and keeps a count, a
            // histogram, the pending run of trailing padding and a running hash. It is the only pass
            // that reads every instruction of every body, so the reference tally of the model section
            // is counted here — and it is why nothing here holds a body: on the 11.8 MB client the
            // largest function is 1.08 MB, and building it just to count it was the peak of the run.
            result.Add(BodySummary.Of(function, normalizer.Summarize(function, document.Model.ReferenceClasses)));
        }

        document.Model.ReferencesNamed += normalizer.NamedReferences;
        document.Model.ReferencesUnnamed += normalizer.UnnamedReferences;
        document.Model.ReferencesIdentified += normalizer.IdentifiedReferences;

        // Relocation kinds are a property of the container, not of the functions: count them once,
        // and say what each kind means in the abstract model, so a PE fixup and an ELF one can be
        // seen to have been understood as the same thing.
        foreach (var relocation in side.Index.Relocations)
        {
            document.Model.RelocationClasses[relocation.Kind] =
                document.Model.RelocationClasses.GetValueOrDefault(relocation.Kind) + 1;
            document.Model.RelocationModel[relocation.Kind] = RelocationModel.Classify(relocation).ToString();
        }

        return result;
    }

    /// <summary>
    /// The identity a function is looked up by, in order of trust: the real symbol name, the
    /// demangled name, then each alias the inventory recorded for it.
    /// </summary>
    private static List<string> Identities(FunctionInfo function)
    {
        var keys = new List<string>();
        AddKey(function.Name);
        AddKey(function.Demangled);
        foreach (string alias in function.Aliases)
        {
            AddKey(alias);
        }

        if (function.ImportThunk is not null)
        {
            AddKey(function.ImportThunk);
        }

        return keys;

        void AddKey(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            // A placeholder is not an identity: two builds give their unnamed functions different
            // addresses, so "sub_1010" says nothing about what a function is.
            if (!SideIndex.IsRealName(name))
            {
                return;
            }

            string normalized = NormalizeSymbol(name);
            if (!keys.Contains(normalized, StringComparer.Ordinal))
            {
                keys.Add(normalized);
            }
        }
    }

    /// <summary>
    /// True when a function is a stub that only forwards somewhere else: an import thunk or the
    /// jump thunks a compiler emits. Such a stub is never the same thing as a real body that happens
    /// to share its name, so the two are not allowed to pair by name.
    /// </summary>
    private static bool IsStub(FunctionInfo function)
        => function.ImportThunk is not null
           || function.Flags.Contains("import_thunk")
           || (function.Name?.StartsWith("__imp_", StringComparison.Ordinal) ?? false)
           || (function.Name?.StartsWith("_imp__", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Symbol spellings that differ between builds but mean the same function: the leading
    /// underscore a C toolchain adds, the <c>@8</c> a stdcall decoration carries, and the
    /// <c>__imp_</c> an import thunk is wrapped in.
    /// </summary>
    public static string NormalizeSymbol(string name)
    {
        string result = name.Trim();
        if (result.StartsWith("__imp_", StringComparison.Ordinal))
        {
            result = result["__imp_".Length..];
        }
        else if (result.StartsWith("_imp__", StringComparison.Ordinal))
        {
            result = result["_imp__".Length..];
        }

        while (result.StartsWith('_'))
        {
            result = result[1..];
        }

        int at = result.LastIndexOf('@');
        if (at > 0 && int.TryParse(result[(at + 1)..], out _))
        {
            result = result[..at];

            // A fastcall name is decorated on both ends: @sub_fast@8.
            if (result.StartsWith('@'))
            {
                result = result[1..];
            }
        }

        return result;
    }

    /// <summary>
    /// Pairs left and right functions. Returns every pair with the stage that found it; unpaired
    /// functions are reported as such rather than guessed at.
    /// </summary>
    /// <summary>
    /// The bodies of one side, made when a stage asks for one and not kept. A comparison asks for each
    /// body twice — once to pair it, once to compare it — and for a real program those two asks are far
    /// apart, so keeping every body in between means keeping gigabytes to answer questions about one
    /// function at a time. `recon diff` was killed by the kernel on an 11.8 MB Visual Basic 6 client
    /// (4.4 million instructions) while doing exactly that, with nothing printed.
    /// </summary>
    private sealed class SideBodies
    {
        /// <summary>
        /// How many instructions of bodies are worth keeping. Below it, a body made for the summary is
        /// kept and the comparing stage asks for it again for nothing; above it, keeping everything is
        /// how the kernel ends up deciding the question, so bodies are made and dropped instead. On an
        /// 11.8 MB client (4.4 million instructions) the streaming path is the only one that finishes;
        /// on the corpus and on anything a person compiles, keeping them halves the comparison.
        /// </summary>
        private const int KeepInstructionBudget = 100_000;

        private readonly FunctionNormalizer _normalizer;
        private readonly Dictionary<FunctionInfo, NormalizedFunction> _kept = [];
        private int _keptInstructions;

        public SideBodies(ComparisonSide side, ComparisonOptions options)
        {
            _normalizer = new FunctionNormalizer(side.Index, side.Bytes, options);
        }

        /// <summary>How many bodies were kept, for the record.</summary>
        public int KeptCount { get; private set; }

        public void Keep(NormalizedFunction body)
        {
            if (_keptInstructions + body.Instructions.Count > KeepInstructionBudget)
            {
                return;
            }

            _kept[body.Function] = body;
            _keptInstructions += body.Instructions.Count;
            KeptCount++;
        }

        public NormalizedFunction Body(FunctionInfo function)
        {
            if (_kept.TryGetValue(function, out var kept))
            {
                return kept;
            }

            var body = FunctionNormalizer.TrimPadding(_normalizer.Normalize(function));

            // Keep what this made, under the same budget: the one thing that asks for a body twice is
            // the pipeline itself — the similarity stage decodes a pair to score it, and the comparing
            // stage decodes the same pair again to describe it — so a body made for the first of those
            // is worth keeping for the second. `Keep` is what enforces the ceiling, so this changes
            // what a comparison costs and not what it can hold.
            Keep(body);
            return body;
        }
    }

    /// <summary>
    /// Everything the comparison keeps about one function: the body's key, how many instructions it
    /// has, and what mnemonics it is made of. Nothing here grows with the size of the body, which is
    /// what makes pairing 304 large functions possible in the memory two of them take.
    /// </summary>
    private sealed class BodySummary
    {
        public required FunctionInfo Function { get; init; }

        public required string BodyKey { get; init; }

        public int InstructionCount { get; init; }

        public int TrimmedPadding { get; init; }

        public int InvalidBytes { get; init; }

        public int RelocatedCount { get; init; }

        public required Dictionary<Mnemonic, int> Mnemonics { get; init; }

        public uint Rva => Function.Ranges.Count > 0 ? Function.Ranges[0].Rva : 0;

        public uint Size => Function.Ranges.Count > 0 ? Function.Ranges[0].Size : 0;

        public static BodySummary Of(FunctionInfo function, BodyStatistics stats) => new()
        {
            Function = function,
            BodyKey = stats.BodyKey,
            InstructionCount = stats.InstructionCount,
            TrimmedPadding = stats.TrimmedPadding,
            InvalidBytes = stats.InvalidBytes,
            RelocatedCount = stats.RelocatedCount,
            Mnemonics = new Dictionary<Mnemonic, int>(stats.Mnemonics),
        };
    }

    private sealed class Matching
    {
        public List<(int Left, int Right, string Match)> Pairs { get; } = [];

        public bool[] LeftTaken { get; init; } = [];

        public bool[] RightTaken { get; init; } = [];
    }

    private static Matching Match(
        List<BodySummary> left,
        List<BodySummary> right,
        ComparisonOptions options,
        SideBodies leftBodies,
        SideBodies rightBodies)
    {
        var matching = new Matching
        {
            LeftTaken = new bool[left.Count],
            RightTaken = new bool[right.Count],
        };

        var result = matching.Pairs;
        bool[] leftTaken = matching.LeftTaken;
        bool[] rightTaken = matching.RightTaken;

        // A right function can be known by several keys at once (name, demangled, aliases), and
        // several functions may share one alias after folding. First writer wins, in RVA order.
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < right.Count; index++)
        {
            foreach (string key in Identities(right[index].Function))
            {
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = [];
                    byKey[key] = list;
                }

                list.Add(index);
            }
        }

        for (int index = 0; index < left.Count; index++)
        {
            foreach (string key in Identities(left[index].Function))
            {
                if (!byKey.TryGetValue(key, out var candidates))
                {
                    continue;
                }

                int target = candidates.FirstOrDefault(
                    c => !rightTaken[c] && IsStub(right[c].Function) == IsStub(left[index].Function), -1);
                if (target < 0 || leftTaken[index])
                {
                    continue;
                }

                leftTaken[index] = true;
                rightTaken[target] = true;
                result.Add((index, target, "name"));
                break;
            }
        }

        MatchByBody(left, right, leftTaken, rightTaken, result);
        MatchBySimilarity(left, right, leftTaken, rightTaken, result, options, leftBodies, rightBodies);
        return matching;
    }

    private static void MatchByBody(
        List<BodySummary> left,
        List<BodySummary> right,
        bool[] leftTaken,
        bool[] rightTaken,
        List<(int, int, string)> result)
    {
        var byBody = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < right.Count; index++)
        {
            if (rightTaken[index] || right[index].InstructionCount == 0)
            {
                continue;
            }

            if (!byBody.TryGetValue(right[index].BodyKey, out var list))
            {
                list = [];
                byBody[right[index].BodyKey] = list;
            }

            list.Add(index);
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (leftTaken[index] || left[index].InstructionCount == 0)
            {
                continue;
            }

            if (!byBody.TryGetValue(left[index].BodyKey, out var candidates))
            {
                continue;
            }

            int target = candidates.FirstOrDefault(
                c => !rightTaken[c] && IsStub(right[c].Function) == IsStub(left[index].Function), -1);
            if (target < 0)
            {
                continue;
            }

            leftTaken[index] = true;
            rightTaken[target] = true;
            result.Add((index, target, "body"));
        }
    }

    /// <summary>
    /// Pairs what is left by how similar the bodies are, best pair first, so a good pair is never
    /// lost to a worse one that happened to be considered earlier.
    /// </summary>
    private static void MatchBySimilarity(
        List<BodySummary> left,
        List<BodySummary> right,
        bool[] leftTaken,
        bool[] rightTaken,
        List<(int, int, string)> result,
        ComparisonOptions options,
        SideBodies leftBodies,
        SideBodies rightBodies)
    {
        var candidates = new List<(double Score, int Left, int Right)>();

        // What each body is made of is a summary, so the filters cost nothing and hold nothing: two
        // bodies that do not share enough mnemonics to fill the score cannot match, and the shared
        // count is an upper bound on how many instructions could possibly line up. Only a pair that
        // gets past every filter is worth a body, and only one is alive at a time.
        for (int i = 0; i < left.Count; i++)
        {
            if (leftTaken[i] || left[i].InstructionCount == 0)
            {
                continue;
            }

            NormalizedFunction? leftBody = null;
            for (int j = 0; j < right.Count; j++)
            {
                if (rightTaken[j] || right[j].InstructionCount == 0)
                {
                    continue;
                }

                if (IsStub(left[i].Function) != IsStub(right[j].Function))
                {
                    continue;
                }

                // Cheap filters before the O(n·m) alignment, so a large binary stays fast.
                int shorter = Math.Min(left[i].InstructionCount, right[j].InstructionCount);
                int longer = Math.Max(left[i].InstructionCount, right[j].InstructionCount);
                if (longer - shorter > 8 && (double)shorter / longer < options.SimilarityThreshold)
                {
                    continue;
                }

                if (Shared(left[i].Mnemonics, right[j].Mnemonics) < options.SimilarityThreshold * longer)
                {
                    continue;
                }

                leftBody ??= leftBodies.Body(left[i].Function);
                double score = FunctionDiffer.Diff(leftBody, rightBodies.Body(right[j].Function)).Score;
                if (score >= options.SimilarityThreshold)
                {
                    candidates.Add((score, i, j));
                }
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach (var (score, i, j) in candidates)
        {
            if (leftTaken[i] || rightTaken[j])
            {
                continue;
            }

            _ = score;
            leftTaken[i] = true;
            rightTaken[j] = true;
            result.Add((i, j, "similarity"));
        }
    }

    // ------------------------------------------------------------------ comparing

    /// <summary>Body key to one function with that body, for the folded-function check.</summary>
    /// <summary>What a body is made of: how many instructions of each mnemonic.</summary>
    private static Dictionary<Mnemonic, int> Count(NormalizedFunction function)
    {
        var counts = new Dictionary<Mnemonic, int>();
        foreach (var instruction in function.Instructions)
        {
            counts[instruction.Mnemonic] = counts.GetValueOrDefault(instruction.Mnemonic) + 1;
        }

        return counts;
    }

    /// <summary>
    /// The most instructions two bodies could ever match, which is what the similarity stage
    /// prefilters on: for each mnemonic, the smaller of the two counts. No alignment can make more
    /// instructions equal than this, so a pair below the threshold cannot be worth aligning.
    /// </summary>
    public static int SharedMnemonics(NormalizedFunction left, NormalizedFunction right)
        => Shared(Count(left), Count(right));

    private static int Shared(Dictionary<Mnemonic, int> left, Dictionary<Mnemonic, int> right)
    {
        int shared = 0;
        foreach (var (mnemonic, count) in left)
        {
            if (right.TryGetValue(mnemonic, out int other))
            {
                shared += Math.Min(count, other);
            }
        }

        return shared;
    }

    private static Dictionary<string, BodySummary> BodyIndex(IEnumerable<BodySummary> functions)
    {
        var index = new Dictionary<string, BodySummary>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            index.TryAdd(function.BodyKey, function);
        }

        return index;
    }

    private static FunctionComparison Compare(
        (int Left, int Right, string Match) pair,
        List<BodySummary> leftSummaries,
        List<BodySummary> rightSummaries,
        SideBodies leftBodies,
        SideBodies rightBodies,
        ComparisonOptions options,
        ComparisonDocument document)
    {
        var leftSummary = leftSummaries[pair.Left];
        var rightSummary = rightSummaries[pair.Right];
        bool wantsAlignment = WantsAlignment(pair, leftSummary, rightSummary, options);

        // Two bodies with the same key are the same body: the key is the hash of exactly the
        // normalized instructions the differ would compare, which is what made the pairing trust it in
        // the first place. So the comparison is already decided — every instruction equal, no
        // differences, score 1 — and neither body has to be decoded again. This is the second decode of
        // the pair, and for a comparison where the two builds agree it is nearly all of them: on the
        // 11.8 MB client, where 301 of 304 functions are unchanged, it is the difference between a
        // compare stage that costs 27 s and one that costs nothing.
        //
        // The one thing it cannot answer is the aligned listing, which is a *reading* of the two
        // bodies rather than a statement about them; when the caller asked for that function by name,
        // the bodies are decoded and the alignment is built as usual.
        bool identical = !wantsAlignment
            && leftSummary.BodyKey.Length > 0
            && leftSummary.BodyKey == rightSummary.BodyKey;
        if (identical)
        {
            document.Model.BodiesProvenIdentical++;
            return Identical(pair, leftSummary, rightSummary, options);
        }

        // The two bodies this comparison is about, made now and dropped when this method returns: the
        // document keeps the differences and the counts, which are bounded, not the instructions.
        var left = leftBodies.Body(leftSummary.Function);
        var right = rightBodies.Body(rightSummary.Function);
        var diff = FunctionDiffer.Diff(left, right);
        var comparison = new FunctionComparison
        {
            Match = pair.Match,
            Score = Math.Round(diff.Score, 4),
            Moved = left.Rva != right.Rva,
            Left = Side(leftSummary),
            Right = Side(rightSummary),
            Instructions = new InstructionCountInfo
            {
                Left = diff.LeftCount,
                Right = diff.RightCount,
                Equal = diff.Equal,
                Relocated = left.RelocatedCount + right.RelocatedCount,
            },
        };

        comparison.Status = diff.Exact ? "exact" : "changed";
        comparison.Differences = diff.Differences.Take(options.DifferenceLimit).ToList();
        if (wantsAlignment)
        {
            comparison.Aligned = Aligned(left, right, diff.Alignment);
        }

        if (diff.DifferenceCount > comparison.Differences.Count)
        {
            comparison.Notes.Add($"{diff.DifferenceCount - comparison.Differences.Count} further differences not listed");
        }

        Notes(comparison, pair, leftSummary, rightSummary, left.TrimmedPadding, right.TrimmedPadding,
            left.InvalidBytes, right.InvalidBytes);
        return comparison;
    }

    /// <summary>Whether the caller asked for this pair's aligned listing by name.</summary>
    private static bool WantsAlignment(
        (int Left, int Right, string Match) pair,
        BodySummary left,
        BodySummary right,
        ComparisonOptions options)
    {
        if (options.AlignedFunction is not { } wanted)
        {
            return false;
        }

        return string.Equals(left.Function.Name, wanted, StringComparison.Ordinal)
            || string.Equals(right.Function.Name, wanted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The comparison of two bodies that are already known to be identical, written from the summary
    /// the pairing used. Every field means what the differ would have said about the same two bodies —
    /// equal counts, no differences, score 1 — and the values that come from the bodies rather than
    /// from the tokens (padding trimmed, unreadable bytes, relocation count) are the ones the summary
    /// pass measured when it read them.
    /// </summary>
    private static FunctionComparison Identical(
        (int Left, int Right, string Match) pair,
        BodySummary left,
        BodySummary right,
        ComparisonOptions options)
    {
        var comparison = new FunctionComparison
        {
            Match = pair.Match,
            Score = 1,
            Moved = left.Rva != right.Rva,
            Status = "exact",
            Left = Side(left),
            Right = Side(right),
            Instructions = new InstructionCountInfo
            {
                Left = left.InstructionCount,
                Right = right.InstructionCount,
                Equal = left.InstructionCount,
                Relocated = left.RelocatedCount + right.RelocatedCount,
            },
        };

        Notes(comparison, pair, left, right, left.TrimmedPadding, right.TrimmedPadding,
            left.InvalidBytes, right.InvalidBytes);
        return comparison;
    }

    /// <summary>
    /// What the report says about a pair beyond its differences. Both routes through the comparison
    /// write their notes here, so a pair decided by its key and a pair that had to be read say the same
    /// things about padding, unreadable bytes and how they were paired.
    /// </summary>
    private static void Notes(
        FunctionComparison comparison,
        (int Left, int Right, string Match) pair,
        BodySummary left,
        BodySummary right,
        int leftPadding,
        int rightPadding,
        int leftInvalid,
        int rightInvalid)
    {
        if (leftPadding != rightPadding)
        {
            comparison.Notes.Add($"padding trimmed: left {leftPadding}, right {rightPadding}");
        }

        if (leftInvalid > 0 || rightInvalid > 0)
        {
            comparison.Notes.Add($"unreadable bytes in body: left {leftInvalid}, right {rightInvalid}");
        }

        if (pair.Match == "similarity")
        {
            comparison.Notes.Add("paired by similarity, not by name: verify the pairing");
        }

        if (pair.Match == "body")
        {
            comparison.Notes.Add($"paired by identical body; left name {left.Function.Name ?? "-"}, right name {right.Function.Name ?? "-"}");
        }
    }

    /// <summary>
    /// A function no stage paired. When its body is identical to a function of the other side that
    /// did pair (identical-code folding gave one body two names), that is what it is; otherwise it is
    /// simply not there.
    /// </summary>
    private static FunctionComparison Unmatched(
        BodySummary only,
        bool isLeft,
        Dictionary<string, BodySummary> pairedBodies)
    {
        var comparison = new FunctionComparison
        {
            Status = isLeft ? "only_left" : "only_right",
            Score = 0,
            Left = isLeft ? Side(only) : null,
            Right = isLeft ? null : Side(only),
            Instructions = isLeft
                ? new InstructionCountInfo { Left = only.InstructionCount }
                : new InstructionCountInfo { Right = only.InstructionCount },
        };

        if (only.InvalidBytes > 0)
        {
            comparison.Notes.Add($"unreadable bytes in body: {only.InvalidBytes}");
        }

        if (only.InstructionCount > 0 && pairedBodies.TryGetValue(only.BodyKey, out var twin))
        {
            comparison.Status = "folded";
            comparison.Score = 1.0;
            comparison.Notes.Add(
                $"identical body to {twin.Function.Name ?? "0x" + twin.Rva.ToString("x")} (0x{twin.Rva:x}), " +
                "which is paired by name: identical-code folding, not a missing function");
        }

        return comparison;
    }

    /// <summary>
    /// The aligned body of one pair, found by the function ids the document uses. The viewer asks for
    /// one function at a time, which is why this exists separately from the whole-document build: a
    /// comparison of ten thousand functions would not fit in a browser otherwise.
    /// </summary>
    public static List<AlignedInstruction>? AlignedFor(
        ComparisonSide left,
        ComparisonSide right,
        string leftId,
        string rightId)
    {
        var leftFunction = left.Inventory.Functions.FirstOrDefault(f => f.Id == leftId);
        var rightFunction = right.Inventory.Functions.FirstOrDefault(f => f.Id == rightId);
        if (leftFunction is null || rightFunction is null)
        {
            return null;
        }

        var options = ComparisonOptions.FromProfile(left.Profile);
        var leftBody = FunctionNormalizer.TrimPadding(new FunctionNormalizer(left.Index, left.Bytes, options).Normalize(leftFunction));
        var rightBody = FunctionNormalizer.TrimPadding(new FunctionNormalizer(right.Index, right.Bytes, options).Normalize(rightFunction));
        return Aligned(leftBody, rightBody, FunctionDiffer.Diff(leftBody, rightBody).Alignment);
    }

    /// <summary>Every instruction of both bodies, in reading order.</summary>
    private static List<AlignedInstruction> Aligned(
        NormalizedFunction left,
        NormalizedFunction right,
        List<(int? Left, int? Right)> alignment)
    {
        var rows = new List<AlignedInstruction>(alignment.Count);
        foreach (var (leftIndex, rightIndex) in alignment)
        {
            NormalizedInstruction? leftInsn = leftIndex is null ? null : left.Instructions[leftIndex.Value];
            NormalizedInstruction? rightInsn = rightIndex is null ? null : right.Instructions[rightIndex.Value];
            rows.Add(new AlignedInstruction
            {
                Status = leftInsn is null ? "added"
                    : rightInsn is null ? "removed"
                    : string.Equals(leftInsn.Text, rightInsn.Text, StringComparison.Ordinal) ? "equal" : "changed",
                LeftRva = leftInsn?.Rva,
                LeftText = leftInsn?.Text,
                RightRva = rightInsn?.Rva,
                RightText = rightInsn?.Text,
            });
        }

        return rows;
    }

    private static FunctionSideInfo Side(BodySummary function) => new()
    {
        Id = function.Function.Id,
        Name = function.Function.Name,
        Rva = function.Rva,
        Size = function.Size,
        Section = function.Function.Section,
        CallingConvention = function.Function.CallingConvention.Value,
        Flags = function.Function.Flags,
    };

    private static int OrderFunctions(FunctionComparison a, FunctionComparison b)
    {
        int rank = Rank(a).CompareTo(Rank(b));
        if (rank != 0)
        {
            return rank;
        }

        uint leftRva = a.Left?.Rva ?? a.Right?.Rva ?? 0;
        uint rightRva = b.Left?.Rva ?? b.Right?.Rva ?? 0;
        return leftRva.CompareTo(rightRva);
    }

    private static int Rank(FunctionComparison comparison) => comparison.Status switch
    {
        "changed" => 0,
        "folded" => 1,
        "only_left" or "only_right" => 2,
        _ => 3,
    };

    // ------------------------------------------------------------------ summary

    private static void Summarize(
        ComparisonDocument document,
        ComparisonSide left,
        ComparisonSide right,
        Matching matching)
    {
        var summary = document.Summary;
        summary.FunctionsLeft = left.FunctionCount;
        summary.FunctionsRight = right.FunctionCount;
        summary.Matched = matching.Pairs.Count;
        summary.Exact = document.Functions.Count(f => f.Status == "exact");
        summary.Changed = document.Functions.Count(f => f.Status == "changed");
        summary.Folded = document.Functions.Count(f => f.Status == "folded");
        summary.OnlyLeft = document.Functions.Count(f => f.Status == "only_left");
        summary.OnlyRight = document.Functions.Count(f => f.Status == "only_right");
        summary.InstructionsLeft = document.Functions.Sum(i => i.Instructions.Left);
        summary.InstructionsRight = document.Functions.Sum(i => i.Instructions.Right);
        summary.InstructionsEqual = document.Functions.Where(f => f.Right is not null).Sum(i => i.Instructions.Equal);

        int longer = Math.Max(summary.InstructionsLeft, summary.InstructionsRight);
        summary.Score = longer == 0 ? 1.0 : Math.Round((double)summary.InstructionsEqual / longer, 4);
    }

    // ------------------------------------------------------------------ data

    /// <summary>
    /// Compares the named data symbols both sides know about. A word that a relocation covers is
    /// reduced to the symbol it points at, so a vtable full of moved function pointers still
    /// compares equal to the same vtable in another build.
    /// </summary>
    private static DataComparisonSummary CompareData(ComparisonSide left, ComparisonSide right, ComparisonOptions options)
    {
        var summary = new DataComparisonSummary();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var data in left.Index.NamedData.Concat(right.Index.NamedData))
        {
            if (IsComparableData(data))
            {
                names.Add(data.Name!);
            }
        }

        foreach (string name in names)
        {
            bool hasLeft = left.Index.TryData(name, out var leftData);
            bool hasRight = right.Index.TryData(name, out var rightData);

            var comparison = new DataComparison
            {
                Name = name,
                LeftRva = hasLeft ? leftData.Rva : null,
                RightRva = hasRight ? rightData.Rva : null,
                LeftSize = hasLeft ? leftData.Size : null,
                RightSize = hasRight ? rightData.Size : null,
            };

            if (hasLeft)
            {
                comparison.LeftWords = Words(left, leftData, options.DataWordLimit);
            }

            if (hasRight)
            {
                comparison.RightWords = Words(right, rightData, options.DataWordLimit);
            }

            if (!hasRight)
            {
                comparison.Status = "only_left";
                summary.OnlyLeft++;
            }
            else if (!hasLeft)
            {
                comparison.Status = "only_right";
                summary.OnlyRight++;
            }
            else if (leftData.Size == rightData.Size &&
                     comparison.LeftWords!.SequenceEqual(comparison.RightWords!, StringComparer.Ordinal))
            {
                comparison.Status = "identical";
                summary.Identical++;
            }
            else
            {
                comparison.Status = "changed";
                summary.Changed++;
            }

            // Only the symbols that differ are worth listing: a large binary has hundreds that do not.
            if (comparison.Status != "identical")
            {
                summary.Symbols.Add(comparison);
            }
        }

        return summary;
    }

    /// <summary>
    /// A data symbol worth comparing: it has a real name, and it is not the section-start
    /// pseudo-entry the loader records so that the section itself has an address. Those are not
    /// things a reconstruction writes, and comparing them reports the section table as a difference.
    /// </summary>
    private static bool IsComparableData(DataInfo data)
        => data.Name is not null
           && SideIndex.IsRealName(data.Name)
           && !data.Name.StartsWith('.')
           && !string.Equals(data.Source, "section", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first words of a data symbol, each as a symbol name when a relocation says the word is an
    /// address inside this image, and as a plain number otherwise.
    /// </summary>
    private static List<string> Words(ComparisonSide side, DataInfo data, int limit)
    {
        var words = new List<string>();
        int? offset = side.Image.RvaToOffset(data.Rva);
        if (offset is null)
        {
            return words;
        }

        int count = (int)Math.Min(data.Size / 4, (uint)limit);
        for (int index = 0; index < count; index++)
        {
            int at = offset.Value + (index * 4);
            if (at + 4 > side.Bytes.Length)
            {
                break;
            }

            uint value = BitConverter.ToUInt32(side.Bytes, at);
            bool relocated = side.Index.RelocationsIn(data.Rva + (uint)(index * 4), 4)
                .Any(r => r.RawValue == value);

            ulong imageBase = side.Image.ImageBase;
            bool inside = value >= imageBase && value - imageBase <= uint.MaxValue
                && side.Image.ContainsRva((uint)(value - imageBase));
            words.Add(relocated && inside
                ? side.Index.Reference((uint)(value - imageBase), out _)
                : $"0x{value:x}");
        }

        if (data.Size / 4 > (uint)count)
        {
            words.Add($"... {data.Size / 4 - (uint)count} more");
        }

        return words;
    }

    private static ComparisonSideInfo Describe(ComparisonSide side) => new()
    {
        Label = side.Label,
        Project = side.ProjectName,
        ProjectFile = side.ProjectFile,
        Binary = side.BinaryPath,
        Sha256 = side.Sha256,
        Format = side.Inventory.Binary.Format,
        Arch = side.Inventory.Binary.Arch,
        ImageSize = side.Image.SizeOfImage,
        EntryRva = side.Inventory.Binary.EntryRva,
        Functions = side.FunctionCount,
        DebugKind = side.Inventory.Binary.Debug.Kind,
        Toolchain = side.ToolchainId,
        AnalysisMs = side.AnalysisMs,
        InventorySource = side.InventorySource,
    };
}
