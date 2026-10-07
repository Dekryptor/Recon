using Recon.Compare;
using Recon.Config;
using Recon.Inventory;
using Recon.Project;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// One checked-in inventory per corpus binary, compared key by key on every run.
///
/// The document is scrubbed of everything that legitimately differs between machines (absolute
/// paths) before it is compared, so a diff means the analysis changed, not where the checkout is.
/// Regenerate deliberately with <c>RECON_UPDATE_GOLDEN=1 dotnet test --filter Golden</c>.
/// </summary>
public class GoldenTests
{
    private static string GoldenPath => Path.Combine(
        TestPaths.RepositoryRoot, "tests", "Recon.Tests", "golden", "inventory.sample-release.json");

    private static string MsvcGoldenPath => Path.Combine(
        TestPaths.RepositoryRoot, "tests", "Recon.Tests", "golden", "inventory.sample-cxx.json");

    private static string MsvcCGoldenPath => Path.Combine(
        TestPaths.RepositoryRoot, "tests", "Recon.Tests", "golden", "inventory.sample-msvc-c.json");

    private static string ComparisonGoldenPath => Path.Combine(
        TestPaths.RepositoryRoot, "tests", "Recon.Tests", "golden", "comparison.sample-debug-vs-release.json");

    private static string SelfComparisonGoldenPath => Path.Combine(
        TestPaths.RepositoryRoot, "tests", "Recon.Tests", "golden", "comparison.sample-cxx-self.json");

    private static bool Corpus => TestPaths.CorpusExists("sample-release.exe");

    [Fact]
    public void Inventory_of_the_corpus_binary_is_unchanged()
    {
        if (!Corpus)
        {
            return;
        }

        string actual = Scrub(BuildInventoryJson());

        if (Environment.GetEnvironmentVariable("RECON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllText(GoldenPath, actual);
            return;
        }

        Assert.True(File.Exists(GoldenPath), $"the golden file {GoldenPath} is missing");
        string expected = TestJson.Normalize(File.ReadAllText(GoldenPath));
        Assert.Equal(expected, TestJson.Normalize(actual));
    }

    /// <summary>
    /// The same idea for the MSVC-ABI C++ corpus: it pins the whole PDB path, from the names and
    /// sizes the PDB gives through the decorated publics that supply the conventions, the vtables
    /// and RTTI records kept out of the function list, and the toolchain attribution.
    /// </summary>
    [Fact]
    public void Inventory_of_the_msvc_cxx_corpus_binary_is_unchanged()
    {
        if (!MsvcCorpus.Available("sample-cxx"))
        {
            return;
        }

        var (document, projectDirectory) = MsvcCorpus.Build("sample-cxx");
        string actual = Scrub(
            InventoryJson.Serialize(document),
            (projectDirectory.Replace('\\', '/'), "<project>"));

        if (Environment.GetEnvironmentVariable("RECON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MsvcGoldenPath)!);
            File.WriteAllText(MsvcGoldenPath, actual);
            return;
        }

        Assert.True(File.Exists(MsvcGoldenPath), $"the golden file {MsvcGoldenPath} is missing");
        Assert.Equal(TestJson.Normalize(File.ReadAllText(MsvcGoldenPath)), TestJson.Normalize(actual));
    }

    /// <summary>
    /// The MSVC C corpus pins what only a real MSVC-ABI binary can show: stdcall and fastcall read
    /// from decorated publics, data publics kept out of the function list, and the toolchain
    /// attribution that comes from the compiland records.
    /// </summary>
    [Fact]
    public void Inventory_of_the_msvc_c_corpus_binary_is_unchanged()
    {
        if (!MsvcCorpus.Available("sample"))
        {
            return;
        }

        var (document, projectDirectory) = MsvcCorpus.Build("sample");
        string actual = Scrub(
            InventoryJson.Serialize(document),
            (projectDirectory.Replace('\\', '/'), "<project>"));

        if (Environment.GetEnvironmentVariable("RECON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MsvcCGoldenPath)!);
            File.WriteAllText(MsvcCGoldenPath, actual);
            return;
        }

        Assert.True(File.Exists(MsvcCGoldenPath), $"the golden file {MsvcCGoldenPath} is missing");
        Assert.Equal(TestJson.Normalize(File.ReadAllText(MsvcCGoldenPath)), TestJson.Normalize(actual));
    }

    /// <summary>
    /// Two builds of one program: the comparison golden pins the matching, the normalization and the
    /// score. Everything machine-specific (paths, the time the analysis took) is scrubbed first, so a
    /// diff here means the engine changed what it says, not where it ran.
    /// </summary>
    [Fact]
    public void Comparison_of_two_builds_of_the_corpus_is_unchanged()
    {
        if (!Corpus || !TestPaths.CorpusExists("sample-debug.exe"))
        {
            return;
        }

        var left = ComparisonSide.FromBinary("left", TestPaths.Corpus("sample-debug.exe"), new Diagnostics());
        var right = ComparisonSide.FromBinary("right", TestPaths.Corpus("sample-release.exe"), new Diagnostics());
        var document = ComparisonBuilder.Build(left, right, new ComparisonOptions { DifferenceLimit = 6 });
        string actual = ScrubComparison(document);

        if (Environment.GetEnvironmentVariable("RECON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ComparisonGoldenPath)!);
            File.WriteAllText(ComparisonGoldenPath, actual);
            return;
        }

        Assert.True(File.Exists(ComparisonGoldenPath), $"the golden file {ComparisonGoldenPath} is missing");
        Assert.Equal(TestJson.Normalize(File.ReadAllText(ComparisonGoldenPath)), TestJson.Normalize(actual));
    }

    /// <summary>A binary against itself: every function exact, nothing paired by guesswork.</summary>
    [Fact]
    public void Self_comparison_of_the_msvc_cxx_corpus_is_unchanged()
    {
        if (!MsvcCorpus.Available("sample-cxx"))
        {
            return;
        }

        string binary = Path.Combine(TestPaths.MsvcCorpusDirectory, "sample-cxx.exe");
        var left = ComparisonSide.FromBinary("left", binary, new Diagnostics());
        var right = ComparisonSide.FromBinary("right", binary, new Diagnostics());
        var document = ComparisonBuilder.Build(left, right, new ComparisonOptions());
        string actual = ScrubComparison(document);

        if (Environment.GetEnvironmentVariable("RECON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SelfComparisonGoldenPath)!);
            File.WriteAllText(SelfComparisonGoldenPath, actual);
            return;
        }

        Assert.True(File.Exists(SelfComparisonGoldenPath), $"the golden file {SelfComparisonGoldenPath} is missing");
        Assert.Equal(TestJson.Normalize(File.ReadAllText(SelfComparisonGoldenPath)), TestJson.Normalize(actual));
    }

    /// <summary>
    /// A comparison scrubbed of what legitimately differs between runs: the analysis time, and the
    /// paths of the binaries. Hashes stay: they are part of what the golden pins.
    /// </summary>
    private static string ScrubComparison(ComparisonDocument document)
    {
        document.Left.AnalysisMs = 0;
        document.Right.AnalysisMs = 0;
        return Scrub(
            Recon.Reporting.Reports.Serialize(document),
            (TestPaths.CorpusDirectory.Replace('\\', '/'), "<corpus>"),
            (TestPaths.MsvcCorpusDirectory.Replace('\\', '/'), "<corpus>"));
    }

    private static string BuildInventoryJson()
    {
        var diagnostics = new Recon.Config.Diagnostics();
        string projectFile = Path.Combine(TestPaths.RepositoryRoot, "examples", "sample-project", "project.toml");
        var context = ProjectContext.Load(projectFile, diagnostics);
        Assert.Empty(diagnostics.Errors);

        var image = context.LoadImage();
        Assert.NotNull(image.Image);

        var document = InventoryBuilder.Build(new InventoryInputs
        {
            Project = context.Project,
            Local = context.Local,
            Image = image.Image!,
            Bytes = image.Bytes,
            Debug = context.LoadDebugInfo(image.Image!),
            Dwarf = context.Dwarf,
            Registry = context.Registry,
            Profile = context.Registry.All("pe32", "x86").FirstOrDefault(),
            Options = context.BuildAnalysisOptions(),
            Command = "inventory",
        });

        return InventoryJson.Serialize(document);
    }

    /// <summary>
    /// Replaces machine-specific strings with placeholders. Only paths are scrubbed: versions and
    /// hashes are part of what the golden file is meant to pin.
    /// </summary>
    internal static string Scrub(string json, params (string Prefix, string Placeholder)[] extra)
    {
        string root = TestPaths.RepositoryRoot.Replace('\\', '/');
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Scrub(node, root, "<repo>");
        foreach (var (prefix, placeholder) in extra)
        {
            Scrub(node, prefix, placeholder);
        }

        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static void Scrub(System.Text.Json.Nodes.JsonNode node, string root, string placeholder)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                var value = obj[key];
                if (value is System.Text.Json.Nodes.JsonValue jsonValue
                    && jsonValue.TryGetValue(out string? text)
                    && text is not null)
                {
                    obj[key] = text.Replace('\\', '/').Replace(root, placeholder);
                }
                else if (value is not null)
                {
                    Scrub(value, root, placeholder);
                }
            }

            return;
        }

        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    Scrub(item, root, placeholder);
                }
            }
        }
    }
}
