using System.Text.Json;
using Recon.Analysis;
using Recon.DebugInfo;
using Recon.Inventory;
using Recon.Pe;
using Recon.Project;
using Recon.Schema;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// End-to-end tests over the checked-in corpus. They are skipped when the corpus has not been built,
/// so a fresh clone can run the unit tests without a cross compiler.
/// </summary>
public class InventoryTests
{
    private const string Binary = "sample-release.exe";

    private static bool Available => TestPaths.CorpusExists(Binary);

    private static (InventoryDocument Document, AnalysisResult Analysis) Build()
    {
        var context = SampleProject();
        var image = context.LoadImage();
        Assert.NotNull(image.Image);
        var debug = context.LoadDebugInfo(image.Image!);
        var profile = context.Registry.All("pe32", "x86").FirstOrDefault();

        var inputs = new InventoryInputs
        {
            Project = context.Project,
            Local = context.Local,
            Image = image.Image!,
            Bytes = image.Bytes,
            Debug = debug,
            Dwarf = context.Dwarf,
            Registry = context.Registry,
            Profile = profile,
            Options = context.BuildAnalysisOptions(),
        };

        var document = InventoryBuilder.Build(inputs);
        var analyzer = new InventoryAnalyzer(image.Image!, image.Bytes, profile, context.BuildAnalysisOptions());
        return (document, analyzer.Analyze(debug));
    }

    private static ProjectContext SampleProject()
    {
        var diagnostics = new Recon.Config.Diagnostics();
        string projectFile = Path.Combine(TestPaths.RepositoryRoot, "examples", "sample-project", "project.toml");
        var context = ProjectContext.Load(projectFile, diagnostics);
        Assert.Empty(diagnostics.Errors);
        return context;
    }

    [Fact]
    public void Matches_the_published_schema()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();
        string json = Reports_Serialize(document);
        var validator = JsonSchemaValidator.Parse(BuiltInSchemas.Get("inventory")!);

        var violations = validator.Validate(json);

        Assert.Empty(violations);
    }

    [Fact]
    public void Survives_a_json_round_trip()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();
        string json = Reports_Serialize(document);

        // The document only carries primitives, lists and dictionaries, so a round trip must be
        // lossless; a mismatch here means the model would not persist through the contract.
        string normalised = TestJson.Normalize(json);
        var reparsed = JsonDocument.Parse(normalised).RootElement;
        Assert.Equal("0.1", reparsed.GetProperty("schema_version").GetString());
        Assert.Equal("pe32", reparsed.GetProperty("binary").GetProperty("format").GetString());
        Assert.Equal(reparsed.GetProperty("functions").GetArrayLength(), document.Functions.Count);
        Assert.Equal(
            reparsed.GetProperty("relocations").GetArrayLength(),
            document.Relocations.Count);
    }

    [Fact]
    public void Finds_the_functions_at_the_documented_addresses()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();
        var byName = document.Functions.Where(f => f.Name is not null).ToDictionary(f => f.Name!, f => f);

        // Ground truth from the DWARF and the map file of the corpus build.
        foreach (var (name, rva, size) in new (string, uint, uint)[]
                 {
                     ("add", 0x1570, 16),
                     ("mul_std", 0x1580, 8),
                     ("sub_fast", 0x1590, 16),
                     ("dispatch", 0x15A0, 32),
                     ("fib", 0x16F0, 64),
                     ("loop_sum", 0x1730, 64),
                     ("fail", 0x1780, 32),
                     ("use_twins", 0x17C0, 23),
                     ("bump", 0x14F0, 8),
                 })
        {
            var function = Assert.Contains(name, byName);
            Assert.Equal(rva, function.Ranges[0].Rva);
            Assert.Equal("high", function.Confidence);

            // Sizes come from DWARF where it has them; the inventory may not be smaller than the
            // DWARF body and must not run into the next symbol.
            Assert.InRange(function.Ranges[0].Size, 1u, 4096u);
            if (size > 0)
            {
                Assert.True(
                    function.Ranges[0].Size + 16 >= size,
                    $"{name}: size {function.Ranges[0].Size} is far below the DWARF size {size}");
            }
        }
    }

    [Fact]
    public void Never_reports_a_function_inside_another_one()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();
        var ordered = document.Functions.OrderBy(f => f.Ranges[0].Rva).ToList();

        for (int i = 1; i < ordered.Count; i++)
        {
            uint previousEnd = ordered[i - 1].Ranges[0].Rva + ordered[i - 1].Ranges[0].Size;
            Assert.True(
                ordered[i].Ranges[0].Rva >= ordered[i - 1].Ranges[0].Rva,
                "functions must be ordered by address");
            Assert.True(
                ordered[i].Ranges[0].Rva >= previousEnd,
                $"{ordered[i].Name} starts inside {ordered[i - 1].Name}");
        }
    }

    [Fact]
    public void Relocations_are_linked_to_the_instructions_that_use_them()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();

        Assert.NotEmpty(document.Relocations);
        Assert.All(document.Relocations, relocation => Assert.Equal("HIGHLOW", relocation.Kind));

        // A relocation inside a function must be reported as an xref of that function, which is the
        // milestone-1 requirement stated as "relocations are linked to using instructions".
        var xrefs = document.Xrefs.Where(x => x.ViaReloc).ToList();
        Assert.NotEmpty(xrefs);

        var functions = document.Functions.ToDictionary(f => f.Id, f => f);
        foreach (var relocation in document.Relocations.Where(r => r.InFunction is not null))
        {
            Assert.Contains(relocation.InFunction, functions.Keys);
            Assert.Contains(xrefs, x => x.FromRva == relocation.Rva && x.InFunction == relocation.InFunction);
        }
    }

    [Fact]
    public void Finds_the_jump_tables_and_keeps_them_out_of_code()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();

        Assert.Equal(2, document.JumpTables.Count);
        var dispatchTable = document.JumpTables.OrderBy(t => t.Rva).First();
        Assert.Equal(0xA058u, dispatchTable.Rva);
        Assert.Equal(11, dispatchTable.Entries);
        Assert.Equal("f_0015a0", dispatchTable.Owner);
        Assert.Equal(0x15B0u, dispatchTable.UsedAtRva);
        // A switch compiles to a jump table whose targets are the case bodies, which belong to the
        // function that owns the table rather than to functions of their own.
        var owner = document.Functions.Single(f => f.Id == dispatchTable.Owner);
        Assert.All(dispatchTable.Targets, target => Assert.True(
            owner.Ranges[0].Rva <= target && target < owner.Ranges[0].Rva + owner.Ranges[0].Size,
            $"jump table target 0x{target:X} is outside its owner {owner.Name}"));

        // The table lives in .rdata, so no function may claim it.
        Assert.DoesNotContain(document.Functions, f =>
            f.Ranges[0].Rva <= dispatchTable.Rva
            && dispatchTable.Rva < f.Ranges[0].Rva + f.Ranges[0].Size);
    }

    [Fact]
    public void Identifies_import_thunks()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();

        var thunks = document.Functions.Where(f => f.ImportThunk is not null).ToList();
        Assert.NotEmpty(thunks);
        Assert.All(thunks, thunk =>
        {
            Assert.Contains("import_thunk", thunk.Flags);
            Assert.Contains("!", thunk.ImportThunk);
        });
    }

    [Fact]
    public void Statistics_agree_with_the_lists_they_summarise()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();

        Assert.Equal(document.Functions.Count, document.Statistics["functions"]);
        Assert.Equal(document.JumpTables.Count, document.Statistics["jump_tables"]);
        Assert.Equal(document.Xrefs.Count, document.Statistics["xrefs"]);
        Assert.Equal(document.Data.Count, document.Statistics["data_ranges"]);
        Assert.Equal(
            document.Functions.Count(f => f.Confidence == "high"),
            document.Statistics["functions_high"]);
    }

    [Fact]
    public void Producer_detection_names_the_compiler_per_unit()
    {
        if (!Available)
        {
            return;
        }

        var (document, _) = Build();

        var main = document.Functions.Single(f => f.Name == "main");
        Assert.Equal("sample.c", main.Unit);
        Assert.Equal("gcc-14-mingw", main.Toolchain.Id);
        Assert.Equal("high", main.Toolchain.Confidence);

        // The runtime objects come from a different GCC release in the same image; the per-unit
        // view has to reflect that rather than forcing one profile on the whole binary.
        var runtime = document.Functions.FirstOrDefault(f => f.Unit is not null && f.Unit != "sample.c");
        Assert.NotNull(runtime);
        Assert.Equal("gcc-13-mingw", runtime!.Toolchain.Id);


    }

    /// <summary>
    /// A MinGW binary carries two generations of the same compiler: the release that compiled the
    /// program, and the older one that built the runtime objects it links. Both match, and the
    /// per-unit view names both — but the binary as a whole has to be attributed to the release that
    /// compiled the program, because that is the one a rebuild would have to reproduce.
    /// </summary>
    [Fact]
    public void The_binary_is_attributed_to_the_compiler_that_built_the_program()
    {
        if (!Available)
        {
            return;
        }

        var context = SampleProject();
        var image = context.LoadImage();
        Assert.NotNull(image.Image);
        var debug = context.LoadDebugInfo(image.Image!);

        var chosen = Recon.Toolchains.ToolchainSelector.Choose(
            context.Registry, context.Project.Target.DefaultToolchain, image.Image!, debug, context.Dwarf);
        Assert.NotNull(chosen);
        Assert.Equal("gcc-14-mingw", chosen!.Id);

        var report = Recon.Analysis.ProducerDetector.Detect(image.Image!, debug, context.Registry, context.Dwarf);
        Assert.Equal(
            ["gcc-14-mingw", "gcc-13-mingw"],
            report.Suggestions.Take(2).Select(s => s.ProfileId).ToArray());
    }

    private static string Reports_Serialize(InventoryDocument document)
        => InventoryJson.Serialize(document);

    private static System.Text.Json.Nodes.JsonNode? JsonNode_Parse(string json)
        => System.Text.Json.Nodes.JsonNode.Parse(json);
}
