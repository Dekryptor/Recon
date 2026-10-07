using Iced.Intel;
using Recon.Compare;
using Recon.Config;
using Recon.Inventory;
using Recon.Schema;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The compare engine. The unit tests build function bodies by hand so that one difference at a time
/// can be checked; the corpus tests then check the whole pipeline on real builds, where the answer
/// has to survive relocations, padding and two optimization levels.
/// </summary>
public class CompareTests
{
    // ------------------------------------------------------------------ differences

    [Fact]
    public void A_changed_register_is_reported_as_a_register_difference()
    {
        var result = FunctionDiffer.Diff(
            Body(("mov eax, ebx", Mnemonic.Mov)),
            Body(("mov eax, ecx", Mnemonic.Mov)));

        var difference = Assert.Single(result.Differences);
        Assert.Equal("register", difference.Kind);
        Assert.Equal("mov eax, ebx", difference.LeftText);
        Assert.Equal("mov eax, ecx", difference.RightText);
        Assert.Equal(0, result.Equal);
        Assert.Equal(0.0, result.Score);
        Assert.False(result.Exact);
    }

    [Fact]
    public void A_changed_constant_is_reported_as_an_immediate_difference()
    {
        var result = FunctionDiffer.Diff(
            Body(("mov dword ptr [eax], 5", Mnemonic.Mov)),
            Body(("mov dword ptr [eax], 7", Mnemonic.Mov)));

        Assert.Equal("immediate", Assert.Single(result.Differences).Kind);
    }

    [Fact]
    public void A_changed_symbol_is_reported_as_a_reference_difference()
    {
        var result = FunctionDiffer.Diff(
            Body(("mov eax, [g_counter]", Mnemonic.Mov, "g_counter")),
            Body(("mov eax, [g_other]", Mnemonic.Mov, "g_other")));

        var difference = Assert.Single(result.Differences);
        Assert.Equal("reference", difference.Kind);
        Assert.Equal("g_counter", difference.LeftReference);
        Assert.Equal("g_other", difference.RightReference);
    }

    [Fact]
    public void A_changed_opcode_is_reported_as_an_opcode_difference()
    {
        var result = FunctionDiffer.Diff(
            Body(("push ebp", Mnemonic.Push)),
            Body(("mov eax, [esp+8]", Mnemonic.Mov)));

        Assert.Equal("opcode", Assert.Single(result.Differences).Kind);
    }

    [Fact]
    public void An_inserted_instruction_does_not_make_the_rest_look_different()
    {
        var left = Body(
            ("push ebp", Mnemonic.Push),
            ("mov ebp, esp", Mnemonic.Mov),
            ("pop ebp", Mnemonic.Pop),
            ("ret", Mnemonic.Ret));

        var right = Body(
            ("push ebp", Mnemonic.Push),
            ("mov ebp, esp", Mnemonic.Mov),
            ("xor eax, eax", Mnemonic.Xor),
            ("pop ebp", Mnemonic.Pop),
            ("ret", Mnemonic.Ret));

        var result = FunctionDiffer.Diff(left, right);

        Assert.Equal(4, result.Equal);
        Assert.Equal(1, result.DifferenceCount);
        Assert.Equal("added", Assert.Single(result.Differences).Kind);
        Assert.Equal(0.8, result.Score);
    }

    [Fact]
    public void A_removed_instruction_is_reported_once()
    {
        var result = FunctionDiffer.Diff(
            Body(("push ebp", Mnemonic.Push), ("nop", Mnemonic.Nop), ("ret", Mnemonic.Ret)),
            Body(("push ebp", Mnemonic.Push), ("ret", Mnemonic.Ret)));

        Assert.Equal(2, result.Equal);
        Assert.Equal("removed", Assert.Single(result.Differences).Kind);
        Assert.Equal(2.0 / 3.0, result.Score, 4);
    }

    [Fact]
    public void Identical_bodies_are_exact_and_score_one()
    {
        var result = FunctionDiffer.Diff(
            Body(("push ebp", Mnemonic.Push), ("ret", Mnemonic.Ret)),
            Body(("push ebp", Mnemonic.Push), ("ret", Mnemonic.Ret)));

        Assert.True(result.Exact);
        Assert.Equal(1.0, result.Score);
        Assert.Empty(result.Differences);
    }

    // ------------------------------------------------------------------ symbol identities

    [Theory]
    [InlineData("_mul_std@8", "mul_std")]
    [InlineData("@sub_fast@8", "sub_fast")]
    [InlineData("__imp__printf", "printf")]
    [InlineData("_imp__GetLastError@0", "GetLastError")]
    [InlineData("?run@Shape@@UAEHXZ", "?run@Shape@@UAEHXZ")]
    public void Symbol_spellings_that_mean_the_same_function_normalize_alike(string name, string expected)
        => Assert.Equal(expected, ComparisonBuilder.NormalizeSymbol(name));

    // ------------------------------------------------------------------ the whole engine

    [Fact]
    public void A_binary_compared_with_itself_matches_completely()
    {
        if (!MsvcCorpusIsAvailable)
        {
            return;
        }

        var document = CompareMsvcWithItself();

        Assert.Equal(1.0, document.Summary.Score);
        Assert.Equal(document.Summary.FunctionsLeft, document.Summary.Matched);
        Assert.Equal(document.Summary.Matched, document.Summary.Exact);
        Assert.Equal(0, document.Summary.Changed);
        Assert.Equal(0, document.Summary.OnlyLeft);
        Assert.Equal(0, document.Summary.OnlyRight);
        Assert.All(document.Functions, f => Assert.Empty(f.Differences));
        Assert.Empty(document.Problems);

        // The relocation classes must say something: a comparison that normalized nothing would be
        // reporting a match it did not earn.
        Assert.True(document.Model.RelocationClasses.GetValueOrDefault("HIGHLOW") > 0);
        Assert.True(document.Model.ReferencesNamed > 0);
    }

    [Fact]
    public void Every_function_of_either_side_is_accounted_for()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var document = CompareCorpusBuilds();

        Assert.Equal(document.Summary.FunctionsLeft, document.Summary.Exact + document.Summary.Changed + document.Summary.OnlyLeft);
        Assert.Equal(document.Summary.FunctionsRight, document.Summary.Exact + document.Summary.Changed + document.Summary.OnlyRight);
        Assert.Equal(document.Summary.Matched, document.Summary.Exact + document.Summary.Changed);
        Assert.Equal(document.Functions.Count, document.Summary.Exact + document.Summary.Changed + document.Summary.OnlyLeft + document.Summary.OnlyRight);
    }

    /// <summary>
    /// Two optimization levels of one program: much matches, much does not, and the score has to sit
    /// between the two without pretending either way.
    /// </summary>
    [Fact]
    public void Debug_and_release_builds_match_where_they_should()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var document = CompareCorpusBuilds();

        Assert.InRange(document.Summary.Score, 0.5, 1.0);
        Assert.True(document.Summary.Matched > document.Summary.FunctionsLeft / 2);
        Assert.True(document.Summary.Exact > 20);

        // The corpus' own functions must be paired: `add` and `loop_sum` exist in both builds.
        foreach (string name in (string[])["add", "loop_sum", "fib"])
        {
            Assert.Contains(document.Functions, f => f.Left?.Name == name && f.Status != "only_left");
        }
    }

    /// <summary>
    /// A function that only forwards somewhere else is not the same thing as a real body with the
    /// same name: the corpus imports <c>printf</c> and also defines one, and the pair must not be
    /// reported as a match.
    /// </summary>
    [Fact]
    public void A_real_function_is_never_paired_with_an_import_thunk_of_the_same_name()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var document = CompareCorpusBuilds();

        foreach (var function in document.Functions.Where(f => f.Status is "exact" or "changed"))
        {
            bool leftIsThunk = IsThunk(function.Left?.Flags);
            bool rightIsThunk = IsThunk(function.Right?.Flags);
            Assert.True(leftIsThunk == rightIsThunk, $"{function.Left?.Name} / {function.Right?.Name} pairs a thunk with a real function");
        }

        static bool IsThunk(List<string>? flags)
            => flags is not null && flags.Contains("import_thunk");
    }

    [Fact]
    public void Matching_is_deterministic_across_runs()
    {
        if (!MsvcCorpusIsAvailable)
        {
            return;
        }

        var first = CompareMsvcWithItself();
        var second = CompareMsvcWithItself();

        Assert.Equal(first.Summary.Score, second.Summary.Score);
        Assert.Equal(first.Model.MatchedBy, second.Model.MatchedBy);
        Assert.Equal(
            first.Functions.Select(f => $"{f.Status}:{f.Left?.Rva:x}:{f.Right?.Rva:x}:{f.Score}"),
            second.Functions.Select(f => $"{f.Status}:{f.Left?.Rva:x}:{f.Right?.Rva:x}:{f.Score}"));
    }

    [Fact]
    public void The_comparison_document_matches_its_schema()
    {
        string? schemaJson = BuiltInSchemas.Get("comparison");
        Assert.NotNull(schemaJson);

        var document = MsvcCorpusIsAvailable
            ? CompareMsvcWithItself()
            : TestPaths.CorpusExists("sample-release.exe") ? CompareCorpusBuilds() : null;

        if (document is null)
        {
            return;
        }

        var json = Recon.Reporting.Reports.Serialize(document);
        var violations = JsonSchemaValidator.Parse(schemaJson!).Validate(json);
        Assert.Empty(violations.Select(v => v.ToString()));
    }

    /// <summary>
    /// The threshold decides what gets paired by similarity; it must not change what names pair.
    /// </summary>
    [Fact]
    public void The_similarity_threshold_only_affects_the_similarity_stage()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var strict = CompareCorpusBuilds(threshold: 0.99);
        var loose = CompareCorpusBuilds(threshold: 0.5);

        Assert.Equal(strict.Model.MatchedBy.GetValueOrDefault("name"), loose.Model.MatchedBy.GetValueOrDefault("name"));
        Assert.True(loose.Summary.Matched >= strict.Summary.Matched);
        Assert.Equal(0, strict.Model.MatchedBy.GetValueOrDefault("similarity"));
    }

    /// <summary>
    /// One build keeps a function the other no longer has, but the body is still there under another
    /// name: the report says folded, because calling it missing would be wrong and hiding it would be
    /// worse. This is how a dead-stripped duplicate and an identical-code-folded rebuild look from
    /// the outside.
    /// </summary>
    [Fact]
    public void A_body_that_survives_under_another_name_is_reported_as_folded()
    {
        var document = CompareSynthetic(
            new SyntheticPeOptions { DuplicateFuncA = true },
            new SyntheticPeOptions());

        var folded = Assert.Single(document.Functions, f => f.Status == "folded");
        Assert.Equal("func_a_copy", folded.Left?.Name);
        Assert.NotNull(folded.Left);
        Assert.Equal(SyntheticPe.FuncACopyRva, folded.Left!.Rva);
        Assert.Equal(1.0, folded.Score);
        Assert.Contains("identical body to func_a", folded.Notes.Single(), StringComparison.Ordinal);
        Assert.Equal(1, document.Summary.Folded);

        // Every function of either side is still accounted for.
        Assert.Equal(
            document.Summary.FunctionsLeft,
            document.Summary.Exact + document.Summary.Changed + document.Summary.Folded + document.Summary.OnlyLeft);
    }

    // ------------------------------------------------------------------ helpers

    private static bool MsvcCorpusIsAvailable => Recon.Tests.Fixtures.MsvcCorpus.Available("sample-cxx");

    private static NormalizedFunction Body(params (string Text, Mnemonic Mnemonic)[] instructions)
        => Body(instructions.Select(i => (i.Text, i.Mnemonic, (string?)null)).ToArray());

    private static NormalizedFunction Body(params (string Text, Mnemonic Mnemonic, string? Reference)[] instructions)
    {
        var function = new FunctionInfo
        {
            Id = "f_00001000",
            Name = "sample",
            Ranges = [new RangeInfo { Rva = 0x1000, Size = (uint)instructions.Length }],
        };

        var normalized = new NormalizedFunction { Function = function };
        for (int index = 0; index < instructions.Length; index++)
        {
            var (text, mnemonic, reference) = instructions[index];
            var instruction = new NormalizedInstruction
            {
                Rva = 0x1000 + (uint)index,
                Text = text,
                Mnemonic = mnemonic,
            };

            if (reference is not null)
            {
                instruction.AddReference(reference, ReferenceClass.Absolute);
            }

            normalized.Instructions.Add(instruction);
        }

        normalized.BodyKey = FunctionNormalizer.BodyKey(normalized);
        return normalized;
    }

    private static ComparisonDocument CompareMsvcWithItself()
    {
        string binary = Path.Combine(TestPaths.MsvcCorpusDirectory, "sample-cxx.exe");
        var diagnostics = new Diagnostics();
        var side = ComparisonSide.FromBinary("left", binary, diagnostics);
        var other = ComparisonSide.FromBinary("right", binary, diagnostics);
        Assert.Empty(diagnostics.Errors);
        return ComparisonBuilder.Build(side, other, new ComparisonOptions());
    }

    // ------------------------------------------------------------------ what a reference is, when it has no name

    /// <summary>
    /// Two images of one program that put a string literal in two places compare as the same program.
    ///
    /// `func_c` pushes the address of `"recon fixture"` and calls through the import table; the second
    /// image is the same code with the literal twelve bytes later in `.rdata` and the push moved with
    /// it. Nothing about the program changed, so nothing about it may be reported as a difference —
    /// and before the identity work it was: the operand was an immediate holding a relocated address,
    /// and the engine has no name for a string literal, so the two pushes were compared as two numbers.
    /// </summary>
    [Fact]
    public void A_string_literal_that_moved_is_not_a_difference()
    {
        var document = CompareSynthetic(
            new SyntheticPeOptions(),
            new SyntheticPeOptions { StringShift = 0x0C });

        // The synthetic image has three functions; the two that do not touch the literal are exact
        // either way, and the one that pushes it has to be too.
        var funcC = document.Functions.First(f => f.Left?.Name == "func_c");
        Assert.Equal("exact", funcC.Status);
        Assert.Empty(funcC.Differences);

        // And the same for every other function in the image: nothing else moved, so nothing else may
        // be reported, and the score of the whole comparison is the one a comparison of a program with
        // itself produces.
        Assert.Equal(document.Summary.Matched, document.Summary.Exact);
        Assert.Equal(1.0, document.Summary.Score);

        // And the reference is counted as an identity rather than as an unnamed place, because that is
        // what it was compared as: the text of the literal, not the address of it.
        Assert.True(
            document.Model.ReferencesIdentified > 0,
            "the moved literal should be identified by its contents");
    }

    /// <summary>
    /// A reference with nothing at the address worth saying is still compared as the place it is, and
    /// a difference of that kind is reported rather than smoothed over. The fixture's `func_a` reads
    /// a global through an absolute address, and the two images here are the same except that the
    /// *global's* address is written differently — which is a real difference, and the engine says so.
    /// </summary>
    [Fact]
    public void An_address_with_nothing_identifiable_at_it_is_still_a_difference()
    {
        var document = CompareSynthetic(
            new SyntheticPeOptions(),
            new SyntheticPeOptions { MutateFuncA = true });

        var funcA = document.Functions.First(f => f.Left?.Name == "func_a");
        Assert.NotEqual("exact", funcA.Status);
    }

    /// <summary>Compares two synthetic images, which is the only way to build some cases at all.</summary>
    private static ComparisonDocument CompareSynthetic(SyntheticPeOptions left, SyntheticPeOptions right)
    {
        using var temp = new TempDir("recon-compare-synthetic");
        string leftPath = temp.Write("left.exe", SyntheticPe.Build(left));
        string rightPath = temp.Write("right.exe", SyntheticPe.Build(right));

        var diagnostics = new Diagnostics();
        var leftSide = ComparisonSide.FromBinary("left", leftPath, diagnostics);
        var rightSide = ComparisonSide.FromBinary("right", rightPath, diagnostics);
        Assert.Empty(diagnostics.Errors);
        return ComparisonBuilder.Build(leftSide, rightSide, new ComparisonOptions());
    }

    // ------------------------------------------------------------------ toolchain settings

    /// <summary>
    /// Section 3.1 of the plan: what counts as toolchain noise is a property of the toolchain, not a
    /// constant in the engine. A profile that states the settings gets them, and one that does not
    /// keeps the defaults.
    /// </summary>
    [Fact]
    public void Compare_settings_come_from_the_toolchain_profile()
    {
        using var temp = new TempDir();
        string directory = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        BuiltInProfiles.WriteTo(directory);
        var diagnostics = new Diagnostics();
        var registry = ToolchainRegistry.Load([directory], diagnostics);

        var msvc = registry.GetResolved("msvc-2010");
        Assert.NotNull(msvc);
        var options = ComparisonOptions.FromProfile(msvc);

        Assert.Equal(0.80, options.SimilarityThreshold);
        Assert.True(options.IgnorePadding);
        Assert.Equal(24, options.DifferenceLimit);
        Assert.Equal("msvc-2010", options.Profile);

        // MSVC pads with int3 and GCC with nop, which is exactly the kind of difference a profile is
        // supposed to carry: the same engine, told what this toolchain considers noise.
        Assert.Contains(Mnemonic.Int3, options.PaddingMnemonics);
        Assert.DoesNotContain(Mnemonic.Nop, options.PaddingMnemonics);

        var gcc = registry.GetResolved("gcc-13-mingw");
        Assert.NotNull(gcc);
        var gccOptions = ComparisonOptions.FromProfile(gcc);
        Assert.Contains(Mnemonic.Nop, gccOptions.PaddingMnemonics);
        Assert.Equal("gcc-13-mingw", gccOptions.Profile);
    }

    [Fact]
    public void Without_a_profile_the_compare_defaults_apply_and_are_recorded_as_such()
    {
        var options = ComparisonOptions.FromProfile(null);

        Assert.Null(options.Profile);
        Assert.Equal(0.80, options.SimilarityThreshold);
        Assert.True(options.IgnorePadding);
        Assert.Equal(24, options.DifferenceLimit);
    }

    /// <summary>
    /// The settings a profile carries have to reach the document, otherwise a reader cannot tell what
    /// a score was computed under.
    /// </summary>
    [Fact]
    public void A_comparison_records_the_settings_it_ran_under()
    {
        var options = new ComparisonOptions { SimilarityThreshold = 0.9, IgnorePadding = false, DifferenceLimit = 5 };
        var documented = new ComparisonDocument
        {
            Model = new ComparisonModelInfo
            {
                SimilarityThreshold = options.SimilarityThreshold,
                IgnorePadding = options.IgnorePadding,
                DifferenceLimit = options.DifferenceLimit,
            },
        };

        string json = Recon.Reporting.Reports.Serialize(documented);
        Assert.Contains("\"similarity_threshold\": 0.9", json, StringComparison.Ordinal);
        Assert.Contains("\"ignore_padding\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"difference_limit\": 5", json, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the model, and its bounds

    /// <summary>
    /// One table, three containers. This is the §3.1 closure: the engine only ever sees the abstract
    /// classes, and a kind nobody has taught it is admitted as unknown rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData("HIGHLOW", ReferenceClass.Absolute)]
    [InlineData("DIR64", ReferenceClass.Absolute)]
    [InlineData("HIGHADJ", ReferenceClass.Absolute)]
    [InlineData("ABSOLUTE", ReferenceClass.Unknown)]
    [InlineData("R_386_32", ReferenceClass.Absolute)]
    [InlineData("R_386_PC32", ReferenceClass.Relative)]
    [InlineData("R_X86_64_PLT32", ReferenceClass.Relative)]
    [InlineData("R_X86_64_GOTPCREL", ReferenceClass.Import)]
    [InlineData("X86_64_RELOC_UNSIGNED", ReferenceClass.Absolute)]
    [InlineData("X86_64_RELOC_BRANCH", ReferenceClass.Relative)]
    [InlineData("X86_64_RELOC_GOT", ReferenceClass.Import)]
    [InlineData("R_MIPS_26", ReferenceClass.Unknown)]
    public void Every_containers_relocation_kinds_reduce_to_the_abstract_model(string kind, ReferenceClass expected)
    {
        Assert.Equal(expected, RelocationModel.Classify(kind));
        Assert.Equal(expected, RelocationModel.Classify(new RelocationInfo { Kind = kind }));
    }

    /// <summary>
    /// The abstract model has to be visible in the document, otherwise a reader cannot tell whether a
    /// PE fixup was understood at all.
    /// </summary>
    [Fact]
    public void A_comparison_says_what_the_container_relocation_kinds_mean()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var document = CompareCorpusBuilds();

        Assert.Equal("Absolute", document.Model.RelocationModel["HIGHLOW"]);
        Assert.Equal(["HIGHLOW"], document.Model.RelocationModel.Keys);
        Assert.Equal(document.Model.RelocationClasses.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     document.Model.RelocationModel.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// The similarity prefilter is an upper bound on what an alignment could find, so it must never
    /// cut a pair that would have matched: checked against the pair the corpus does pair that way.
    /// </summary>
    [Fact]
    public void The_similarity_prefilter_is_an_upper_bound_on_a_real_pair()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var document = CompareCorpusBuilds();

        // Checked on a pair that genuinely differs, which is the pair a prefilter can throw away by
        // being too tight. This used to be the corpus's one *similarity* pair; that pair is paired by
        // its body now, because normalizing a branch that stays inside a function relative to that
        // function made two bodies that differed only in where they sat compare equal — and a body
        // match is the stronger statement. The bound is the same bound either way.
        var paired = document.Functions.First(
            f => f.Status == "changed" && f.Left is not null && f.Right is not null);
        var (leftBody, rightBody) = NormalizedBodies(paired);

        Assert.True(
            ComparisonBuilder.SharedMnemonics(leftBody, rightBody) >= paired.Instructions.Equal,
            $"the prefilter bound must not be below the {paired.Instructions.Equal} instructions that did match");

        // And the threshold is the only thing standing between that pair and being paired: the bound
        // has to be high enough to have let it through in the first place. A pair matched by *name*
        // never had to clear the similarity stage, so this half is only claimed of a pair that the
        // similarity stage itself paired.
        if (paired.Match == "similarity")
        {
            Assert.True(
                ComparisonBuilder.SharedMnemonics(leftBody, rightBody) >= 0.80 * Math.Max(leftBody.Instructions.Count, rightBody.Instructions.Count),
                "a pair the similarity stage paired must clear its own prefilter");
        }
        else
        {
            Assert.Contains(paired.Match, new[] { "name", "body" });
        }
    }

    /// <summary>
    /// Two bodies that are not the same code do not get aligned at all: the prefilter can see that
    /// from what they are made of, which is what keeps a binary with thousands of unmatched functions
    /// from paying for every pair.
    /// </summary>
    [Fact]
    public void Unrelated_bodies_are_below_the_similarity_bound()
    {
        using var temp = new TempDir("recon-compare-unrelated");
        string path = temp.Write("one.exe", SyntheticPe.Build(new SyntheticPeOptions()));
        var side = ComparisonSide.FromBinary("one", path, new Diagnostics());

        var normalizer = new FunctionNormalizer(side.Index, side.Bytes);
        var a = normalizer.Normalize(side.Index.Functions.First(f => f.Name == "func_a"));
        var b = normalizer.Normalize(side.Index.Functions.First(f => f.Name == "func_b"));

        int longer = Math.Max(a.Instructions.Count, b.Instructions.Count);
        Assert.True(a.Instructions.Count > 0 && b.Instructions.Count > 0);
        Assert.True(ComparisonBuilder.SharedMnemonics(a, b) < 0.80 * longer);
    }

    /// <summary>
    /// The summarizing pass measures a body without building it, and the comparing pass builds one —
    /// so the two ways of reading the same function have to agree about it, field by field, or the
    /// comparison pairs by one description and diffs by another. This walks every function of a real
    /// corpus binary both ways and holds them to each other: the identity a pairing is made on (the
    /// body key), the counts a report states, and the histogram the similarity stage filters with.
    /// </summary>
    [Fact]
    public void Measuring_a_body_and_building_it_agree()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        var diagnostics = new Diagnostics();
        var side = ComparisonSide.FromBinary("side", TestPaths.Corpus("sample-release.exe"), diagnostics);
        var classes = new Dictionary<string, int>();

        Assert.NotEmpty(side.Index.Functions);
        foreach (var function in side.Index.Functions)
        {
            var measured = new FunctionNormalizer(side.Index, side.Bytes).Summarize(function, classes);
            var built = FunctionNormalizer.TrimPadding(new FunctionNormalizer(side.Index, side.Bytes).Normalize(function));

            var mnemonics = new Dictionary<Mnemonic, int>();
            foreach (var instruction in built.Instructions)
            {
                mnemonics[instruction.Mnemonic] = mnemonics.GetValueOrDefault(instruction.Mnemonic) + 1;
            }

            Assert.Equal(built.BodyKey, measured.BodyKey);
            Assert.Equal(built.Instructions.Count, measured.InstructionCount);
            Assert.Equal(built.TrimmedPadding, measured.TrimmedPadding);
            Assert.Equal(built.InvalidBytes, measured.InvalidBytes);
            Assert.Equal(built.RelocatedCount, measured.RelocatedCount);
            Assert.Equal(mnemonics, measured.Mnemonics);
        }

        Assert.NotEmpty(classes);
    }

    /// <summary>
    /// The same two ways over a synthetic image whose bodies end in padding, begin with it, are all
    /// padding, and are empty — the shapes where the trailing run is the whole point: a run that turns
    /// out to be interior counts, and one that reaches the end does not.
    /// </summary>
    [Fact]
    public void Trailing_padding_is_trimmed_the_same_way_whichever_way_the_body_is_read()
    {
        using var temp = new TempDir("recon-summary-padding");
        string path = temp.Write("one.exe", SyntheticPe.Build(new SyntheticPeOptions()));
        var side = ComparisonSide.FromBinary("one", path, new Diagnostics());

        foreach (var function in side.Index.Functions)
        {
            var measured = new FunctionNormalizer(side.Index, side.Bytes).Summarize(function);
            var built = FunctionNormalizer.TrimPadding(new FunctionNormalizer(side.Index, side.Bytes).Normalize(function));

            Assert.Equal(built.BodyKey, measured.BodyKey);
            Assert.Equal(built.Instructions.Count, measured.InstructionCount);
            Assert.Equal(built.TrimmedPadding, measured.TrimmedPadding);
        }
    }

    /// <summary>
    /// The bodies of a paired function, normalized again so a test can ask the same questions the
    /// engine asked.
    /// </summary>
    private static (NormalizedFunction Left, NormalizedFunction Right) NormalizedBodies(FunctionComparison paired)
    {
        var diagnostics = new Diagnostics();
        var left = ComparisonSide.FromBinary("left", TestPaths.Corpus("sample-debug.exe"), diagnostics);
        var right = ComparisonSide.FromBinary("right", TestPaths.Corpus("sample-release.exe"), diagnostics);

        return (
            new FunctionNormalizer(left.Index, left.Bytes).Normalize(left.Index.Functions.First(f => f.Id == paired.Left!.Id)),
            new FunctionNormalizer(right.Index, right.Bytes).Normalize(right.Index.Functions.First(f => f.Id == paired.Right!.Id)));
    }

    private static ComparisonDocument CompareCorpusBuilds(double threshold = 0.80)
    {
        var diagnostics = new Diagnostics();
        var left = ComparisonSide.FromBinary("left", TestPaths.Corpus("sample-debug.exe"), diagnostics);
        var right = ComparisonSide.FromBinary("right", TestPaths.Corpus("sample-release.exe"), diagnostics);
        Assert.Empty(diagnostics.Errors);
        return ComparisonBuilder.Build(left, right, new ComparisonOptions { SimilarityThreshold = threshold });
    }
}
