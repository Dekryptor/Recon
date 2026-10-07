using System.Text.Json;
using Recon.Analysis;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;
using Recon.Project;
using Recon.Signatures;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M6 — naming functions from patterns, the FLIRT idea: the first bytes of a function, with the bytes
/// a link can change marked as wildcards. The corpus test at the end is the reason any of it exists —
/// a released binary has no symbols, and the runtime code in it is code nobody has to reconstruct —
/// but the rules are easier to pin down against a fixture whose every byte is known, and a fixture
/// needs no toolchain to run.
/// </summary>
public class SignatureTests
{
    private const string CorpusBinary = "sample-release.exe";
    private const string CorpusStripped = "sample-stripped.exe";

    // ------------------------------------------------------------------ the fixture

    private static (IBinaryImage Image, byte[] Bytes, PeImage Pe) Load(SyntheticPeOptions? options = null)
    {
        byte[] bytes = SyntheticPe.Build(options);
        var result = ImageLoader.LoadBytes(bytes, "synthetic-pe32.exe");
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        var loaded = PeLoader.LoadBytes(bytes, "synthetic-pe32.exe");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Problems));
        return (result.Image!, bytes, loaded.Image!);
    }

    private static DebugInfoResult Symbols(PeImage image, byte[] bytes)
        => CoffSymbols.Read(image, bytes);

    private static SignatureDatabase Build(IBinaryImage image, byte[] bytes, DebugInfoResult debug, SignatureBuilder.Options? options = null)
        => SignatureBuilder.Build(image, bytes, debug, options);

    private static (InventoryDocument Document, AnalysisResult Analysis) Inventory(
        IBinaryImage image,
        byte[] bytes,
        DebugInfoResult? debug,
        SignatureDatabase? signatures)
    {
        var project = new ProjectConfig
        {
            Project = new ProjectMeta { Name = "signatures" },
            Target = new TargetSpec { Format = image.Format, Arch = image.ArchName },
        };
        var options = new AnalysisOptions { BuildXrefs = false, MinFunctionConfidence = "low" };

        var document = InventoryBuilder.Build(new InventoryInputs
        {
            Project = project,
            Image = image,
            Bytes = bytes,
            Debug = debug,
            Signatures = signatures,
            Options = options,
        });

        var analyzer = new InventoryAnalyzer(image, bytes, null, options);
        return (document, analyzer.Analyze(debug, signatures));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static uint Start(FunctionInfo function) => function.Ranges[0].Rva;

    // ------------------------------------------------------------------ building

    [Fact]
    public void Takes_a_pattern_from_every_named_function()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes));

        Assert.Contains(database.Entries, e => e.Name == "func_a");
        Assert.Contains(database.Entries, e => e.Name == "func_c");
        Assert.All(database.Entries, e =>
        {
            Assert.Equal(SignatureDatabase.DefaultPatternLength * 2, e.Bytes.Length);
            Assert.Equal(e.Bytes.Length, e.Mask.Length);
        });
    }

    [Fact]
    public void Marks_the_bytes_a_link_can_change_as_wildcards()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes));

        // func_a's body is `mov eax, [abs]; ret` and the fixture records a base relocation over the
        // address: a link that moves the image rewrites those four bytes, so a pattern cannot use them.
        var entry = Assert.Single(database.Entries, e => e.Name == "func_a");

        // `mov eax, [abs]`: the opcode and the `ret` are the function, the four bytes between them
        // are where the address goes. func_a's head is long enough to reach into func_b, whose jump
        // table address is wildcarded by the same rule without a relocation saying so.
        Assert.Equal("a1", entry.Bytes[..2]);
        Assert.Equal("00", entry.Mask[2..4]);
        Assert.Equal("ff", entry.Mask[10..12]); // the ret
        Assert.Equal(8, FunctionSignature.Wildcards(entry.Mask));
        Assert.Equal(24, entry.FixedBytes);
    }

    [Fact]
    public void Marks_a_word_that_points_inside_the_image_as_a_wildcard_even_without_a_relocation()
    {
        var (image, bytes, pe) = Load(new SyntheticPeOptions { BaseRelocations = false });
        var database = Build(image, bytes, Symbols(pe, bytes));

        // func_b jumps through a table the fixture gives no relocation for. Nothing says the address
        // moves, but it is an address all the same: any 4-byte word that lands inside the image is one,
        // because a link that moves the image rewrites it whether the table admits it or not.
        var entry = Assert.Single(database.Entries, e => e.Name == "func_b");
        Assert.Equal("00", entry.Mask[6..8]);
        Assert.True(FunctionSignature.Wildcards(entry.Mask) >= 4);
    }

    [Fact]
    public void Takes_no_pattern_from_a_function_most_of_whose_bytes_a_link_can_change()
    {
        var (image, bytes, pe) = Load();
        var debug = Symbols(pe, bytes);

        // The bar is how many bytes a link cannot change. Raised high enough, most functions fall
        // below it: what is left of them after the addresses are wildcarded is not enough to be
        // unlikely by chance, and a pattern that is not unlikely is a pattern that will lie.
        var loose = Build(image, bytes, debug, new SignatureBuilder.Options { MinFixedBytes = 16 });
        var strict = Build(image, bytes, debug, new SignatureBuilder.Options { MinFixedBytes = 32 });

        Assert.True(strict.Entries.Count < loose.Entries.Count);
        Assert.DoesNotContain(strict.Entries, e => e.Name == "func_a"); // 24 bytes survive the wildcards
        Assert.Contains(strict.Problems, p => p.Contains("bytes a link cannot change", StringComparison.Ordinal));
    }

    [Fact]
    public void Drops_a_pattern_two_functions_share()
    {
        var (image, bytes, _) = Load();

        // Two functions whose first bytes are the same, which is what a static library full of small
        // functions does to you sooner than you would think. The bytes cannot say which one they are,
        // so they say neither. (The fixture's 0xCC padding is a run of identical bytes long enough to
        // be two functions' heads, which is all the test needs.)
        const uint TwinOne = 0x10C0;
        const uint TwinTwo = 0x10E0;
        var debug = new DebugInfoResult
        {
            Symbols =
            [
                new DebugSymbol { Name = "twin_one", Rva = TwinOne, Size = 0x20, Source = SymbolSource.Coff },
                new DebugSymbol { Name = "twin_two", Rva = TwinTwo, Size = 0x20, Source = SymbolSource.Coff },
            ],
        };

        var database = Build(image, bytes, debug);

        Assert.Empty(database.Entries);
        Assert.Contains(
            database.Problems,
            p => p.Contains("twin_one", StringComparison.Ordinal) && p.Contains("twin_two", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_a_pattern_two_names_share_at_one_address()
    {
        // The same function under two names is folding, not ambiguity: the linker gave one body two
        // entries in the symbol table. Dropping the pattern here would throw away good evidence for
        // a rule that exists to prevent guessing.
        var (image, bytes, pe) = Load(new SyntheticPeOptions { FoldedAlias = true });
        var database = Build(image, bytes, Symbols(pe, bytes));

        Assert.Contains(database.Entries, e => e.Name == "func_b");
        Assert.DoesNotContain(database.Problems, p => p.Contains("is shared by", StringComparison.Ordinal));
    }

    [Fact]
    public void Takes_patterns_only_from_the_units_it_was_asked_for()
    {
        var (image, bytes, pe) = Load();
        var debug = Symbols(pe, bytes);

        var everything = Build(image, bytes, debug);
        var filtered = Build(image, bytes, debug, new SignatureBuilder.Options { UnitFilters = ["nothing.c"] });

        Assert.NotEmpty(everything.Entries);
        Assert.Empty(filtered.Entries);
    }

    [Fact]
    public void Records_what_the_patterns_belong_to()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes), new SignatureBuilder.Options { Library = "msvcrt" });

        Assert.All(database.Entries, e => Assert.Equal("msvcrt", e.Library));
        Assert.Equal(1, database.SchemaVersion);
        Assert.Equal(SignatureDatabase.DefaultPatternLength, database.PatternLength);
    }

    // ------------------------------------------------------------------ applying

    [Fact]
    public void Names_the_function_whose_bytes_it_carries()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes));

        // No debug information on purpose: this is a released binary, and the only thing that can name
        // anything in it is what the patterns remember.
        var (document, _) = Inventory(image, bytes, debug: null, signatures: database);

        var named = document.Functions.Where(f => f.FoundBy.Contains("signature")).ToList();
        Assert.NotEmpty(named);
        Assert.Contains(named, f => f.Name == "func_a" && Start(f) == SyntheticPe.FuncARva);
        Assert.All(named, f => Assert.Equal("medium", f.Confidence));
    }

    [Fact]
    public void Never_invents_a_function()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes));

        var (withPatterns, _) = Inventory(image, bytes, debug: null, signatures: database);
        var (without, _) = Inventory(image, bytes, debug: null, signatures: null);

        // The same addresses, only named. A pattern that matched the middle of another function would
        // add one, which is the failure this whole feature has to avoid.
        Assert.Equal(without.Functions.Count, withPatterns.Functions.Count);
        Assert.Equal(
            without.Functions.Select(f => Start(f)).ToList(),
            withPatterns.Functions.Select(f => Start(f)).ToList());
    }

    [Fact]
    public void Counts_what_it_named()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes));
        var (document, _) = Inventory(image, bytes, debug: null, signatures: database);

        int carried = document.Functions.Count(f => f.FoundBy.Contains("signature"));
        Assert.True(carried > 0);
        Assert.Equal(carried, document.Statistics["functions_named_by_signature"]);
    }

    [Fact]
    public void Yields_to_a_symbol_that_names_the_same_address()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes));

        var debug = new DebugInfoResult
        {
            Symbols =
            [
                new DebugSymbol { Name = "renamed_by_a_symbol", Rva = SyntheticPe.FuncARva, Size = 0x20, Source = SymbolSource.Config },
            ],
        };

        var (document, _) = Inventory(image, bytes, debug, database);
        var function = Assert.Single(document.Functions, f => Start(f) == SyntheticPe.FuncARva);

        Assert.Equal("renamed_by_a_symbol", function.Name);
        Assert.DoesNotContain("signature", function.FoundBy);
    }

    [Fact]
    public void Names_nothing_when_two_patterns_disagree()
    {
        var (image, bytes, pe) = Load();
        int offset = image.RvaToOffset(SyntheticPe.FuncARva)!.Value;

        byte[] head = bytes.AsSpan(offset, SignatureDatabase.DefaultPatternLength).ToArray();
        byte[] full = new byte[head.Length];
        Array.Fill(full, (byte)0xFF);

        // One pattern that insists on every byte, and one that ignores one of them: both match, and
        // they do not agree on what the bytes mean. Saying either would be a guess, so the tool says
        // neither and records why.
        byte[] loose = (byte[])full.Clone();
        loose[5] = 0x00;
        byte[] other = (byte[])head.Clone();
        other[5] ^= 0xFF;

        var database = new SignatureDatabase
        {
            Entries =
            [
                new FunctionSignature { Name = "first_name", Bytes = Hex(head), Mask = Hex(full) },
                new FunctionSignature { Name = "second_name", Bytes = Hex(other), Mask = Hex(loose) },
            ],
        };

        var (document, analysis) = Inventory(image, bytes, debug: null, signatures: database);
        var function = Assert.Single(document.Functions, f => Start(f) == SyntheticPe.FuncARva);

        Assert.DoesNotContain("signature", function.FoundBy);
        Assert.Contains(analysis.Problems, p => p.StartsWith("signature:", StringComparison.Ordinal));
    }

    [Fact]
    public void Matches_a_pattern_whose_first_byte_is_a_wildcard()
    {
        var (image, bytes, pe) = Load();
        int offset = image.RvaToOffset(SyntheticPe.FuncARva)!.Value;

        byte[] head = bytes.AsSpan(offset, SignatureDatabase.DefaultPatternLength).ToArray();
        byte[] mask = new byte[head.Length];
        Array.Fill(mask, (byte)0xFF);
        mask[0] = 0x00; // a relocation on the very first byte: the index must not lose the pattern for it
        head[0] = 0x00;

        var database = new SignatureDatabase
        {
            Entries =
            [
                new FunctionSignature { Name = "wild_first", Bytes = Hex(head), Mask = Hex(mask) },
            ],
        };

        var matcher = new SignatureMatcher(database);
        var match = matcher.At(image, bytes, SyntheticPe.FuncARva);

        Assert.NotNull(match);
        Assert.Equal("wild_first", match!.Name);
    }

    [Fact]
    public void Does_not_match_bytes_that_differ()
    {
        var (image, bytes, pe) = Load(new SyntheticPeOptions { MutateFuncA = true });
        int offset = image.RvaToOffset(SyntheticPe.FuncARva)!.Value;

        byte[] head = bytes.AsSpan(offset, SignatureDatabase.DefaultPatternLength).ToArray();
        byte[] mask = new byte[head.Length];
        Array.Fill(mask, (byte)0xFF);
        head[0] = 0x00; // not what is there
        mask[0] = 0x00;

        var database = new SignatureDatabase
        {
            Entries =
            [
                new FunctionSignature { Name = "other_function", Bytes = Hex(head), Mask = Hex(mask) },
            ],
        };
        var matcher = new SignatureMatcher(database);

        // MutateFuncA rewrote that first instruction, so the pattern from the unmutated build must
        // still match: it is a wildcard there. Change a byte the pattern does insist on and it must not.
        Assert.NotNull(matcher.At(image, bytes, SyntheticPe.FuncARva));

        mask[1] = 0xFF;
        head[1] = (byte)(head[1] ^ 0xFF);
        database.Entries[0] = new FunctionSignature { Name = "other_function", Bytes = Hex(head), Mask = Hex(mask) };
        var strict = new SignatureMatcher(database);
        Assert.Null(strict.At(image, bytes, SyntheticPe.FuncARva));
    }

    // ------------------------------------------------------------------ the file

    [Fact]
    public void Survives_a_round_trip_through_json()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes), new SignatureBuilder.Options { Library = "msvcrt" });

        string json = SignatureFile.Serialize(database);
        var back = SignatureFile.Deserialize(json);
        Assert.NotNull(back);

        Assert.Equal(database.SchemaVersion, back!.SchemaVersion);
        Assert.Equal(database.PatternLength, back.PatternLength);
        Assert.Equal(database.MinFixedBytes, back.MinFixedBytes);
        Assert.Equal(database.Entries.Count, back.Entries.Count);
        Assert.Equal(database.Entries[0].Name, back.Entries[0].Name);
        Assert.Equal(database.Entries[0].Bytes, back.Entries[0].Bytes);
        Assert.Equal(database.Entries[0].Mask, back.Entries[0].Mask);
        Assert.Equal(database.Entries[0].Library, back.Entries[0].Library);
    }

    [Fact]
    public void Writes_a_file_that_matches_its_schema()
    {
        var (image, bytes, pe) = Load();
        var database = Build(image, bytes, Symbols(pe, bytes), new SignatureBuilder.Options { Library = "msvcrt" });

        string? schema = Recon.Schemas.BuiltInSchemas.Get("signatures");
        Assert.NotNull(schema);

        var validator = Recon.Schema.JsonSchemaValidator.Parse(schema!);
        var problems = validator.Validate(SignatureFile.Serialize(database));

        Assert.Empty(problems);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_pattern_file()
    {
        Assert.ThrowsAny<JsonException>(() => SignatureFile.Deserialize("{ this is not json"));
    }

    // ------------------------------------------------------------------ the corpus

    /// <summary>
    /// The case the feature is for. The corpus is built by <c>tools/build-corpus.sh</c>; without a
    /// cross compiler it is not there and this test skips, as every corpus test does.
    /// </summary>
    [Fact]
    public void Names_runtime_code_in_a_binary_nothing_else_named()
    {
        if (!TestPaths.CorpusExists(CorpusBinary) || !TestPaths.CorpusExists(CorpusStripped))
        {
            return;
        }

        var (reference, referenceBytes, referencePe) = LoadCorpus(CorpusBinary);
        var database = Build(reference, referenceBytes, Symbols(referencePe, referenceBytes), new SignatureBuilder.Options
        {
            Library = "mingw-w64-crt",
        });
        Assert.NotEmpty(database.Entries);

        // The stripped binary, with its map file out of reach: no symbols, no debug information, no
        // side map. What a release looks like from the outside.
        using var scratch = new TempDir();
        string copy = Path.Combine(scratch.Path, CorpusStripped);
        File.Copy(TestPaths.Corpus(CorpusStripped), copy, overwrite: true);

        var result = ImageLoader.Load(copy);
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        var (document, _) = Inventory(result.Image!, result.Bytes, debug: null, signatures: database);

        var named = document.Functions.Where(f => f.FoundBy.Contains("signature")).ToList();
        Assert.NotEmpty(named);
        Assert.Contains(named, f => IsRuntime(f.Name));
        Assert.All(named, f => Assert.Equal("mingw-w64-crt", f.Unit));
    }

    /// <summary>The MinGW runtime's own names, which is what nobody should have to reconstruct.</summary>
    private static bool IsRuntime(string? name)
        => name is not null
           && (name.StartsWith("__mingw", StringComparison.Ordinal)
               || name.StartsWith("__pformat", StringComparison.Ordinal)
               || name.StartsWith("_", StringComparison.Ordinal) && name.Contains("CRTStartup", StringComparison.Ordinal)
               || name == "atexit"
               || name == "main"
               || name == "WinMainCRTStartup"
               || name == "mainCRTStartup");

    private static (IBinaryImage Image, byte[] Bytes, PeImage Pe) LoadCorpus(string name)
    {
        var result = ImageLoader.Load(TestPaths.Corpus(name));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        var loaded = PeLoader.Load(TestPaths.Corpus(name));
        Assert.True(loaded.Ok, string.Join("; ", loaded.Problems));
        return (result.Image!, result.Bytes, loaded.Image!);
    }
}
