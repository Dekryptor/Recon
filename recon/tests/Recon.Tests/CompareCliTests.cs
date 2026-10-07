using Recon.Config;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The <c>recon diff</c> command. These share one xUnit collection because the CLI runs in-process
/// and captures Console: two classes doing that at the same time would capture each other's output.
/// </summary>
[Collection("cli")]
public class CompareCliTests
{

    [Fact]
    public void The_diff_command_reports_a_score_and_writes_the_document()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff");
        string output = temp.PathOf("comparison.json");
        var run = CliRun.Run(
            "diff",
            TestPaths.Corpus("sample-debug.exe"),
            TestPaths.Corpus("sample-release.exe"),
            "--summary", "-o", output);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("score", run.StandardOutput);
        Assert.True(File.Exists(output));

        var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("0.1", document.RootElement.GetProperty("schema_version").GetString());
        Assert.True(document.RootElement.GetProperty("summary").GetProperty("matched").GetInt32() > 0);
    }

    [Fact]
    public void The_diff_command_fails_when_the_score_is_below_the_minimum()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff");
        var run = CliRun.Run(
            "diff",
            TestPaths.Corpus("sample-debug.exe"),
            TestPaths.Corpus("sample-release.exe"),
            "--summary", "--min-score", "0.99", "-o", temp.PathOf("comparison.json"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("below --min-score", run.All);
    }

    [Fact]
    public void The_diff_command_prints_instruction_level_detail_for_one_function()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff");
        var run = CliRun.Run(
            "diff",
            TestPaths.Corpus("sample-debug.exe"),
            TestPaths.Corpus("sample-release.exe"),
            "--function", "add", "-o", temp.PathOf("comparison.json"));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("opcode", run.StandardOutput);
        Assert.Contains("removed", run.StandardOutput);
    }

    [Fact]
    public void The_diff_command_validates_its_output_against_the_schema()
    {
        if (!TestPaths.CorpusExists("sample-debug.exe") || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff");
        var run = CliRun.Run(
            "diff",
            TestPaths.Corpus("sample-debug.exe"),
            TestPaths.Corpus("sample-release.exe"),
            "--summary", "--check-schema", "-o", temp.PathOf("comparison.json"));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("matches schema 0.1", run.StandardOutput);
    }

    /// <summary>
    /// A project over one corpus binary, with the binary where the project's inputs directory points:
    /// what every test below compares with itself, so that the only thing that changes between two
    /// runs is whether an inventory document for that binary is on disk.
    /// </summary>
    private static string ProjectOverCorpusBinary(TempDir temp, string corpusName)
    {
        string inputs = Path.Combine(temp.Path, "inputs");
        Directory.CreateDirectory(inputs);
        string binary = Path.Combine(inputs, corpusName);
        File.Copy(TestPaths.Corpus(corpusName), binary);
        var init = CliRun.Run("init", temp.Path, "--binary", binary, "--force", "--json");
        Assert.Equal(0, init.ExitCode);
        return binary;
    }

    /// <summary>
    /// What a side says about where its inventory came from, and the document itself with the two
    /// fields that are about the run rather than about the comparison taken out.
    /// </summary>
    private static (string Left, string Right, string Report) ReadComparison(string path)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string left = node["left"]!["inventory_source"]!.GetValue<string>();
        string right = node["right"]!["inventory_source"]!.GetValue<string>();
        foreach (string side in new[] { "left", "right" })
        {
            node[side]!["analysis_ms"] = 0;
            node[side]!.AsObject().Remove("inventory_source");
        }

        return (left, right, node.ToJsonString());
    }

    /// <summary>
    /// The comparison is the same comparison whether the inventory was built for it or read back from
    /// the document <c>recon inventory</c> had already written — and the report says which of the two
    /// happened, because a cache hit and a build cost very different amounts and a report that did not
    /// say would be hiding the cost rather than saving it.
    /// </summary>
    [Fact]
    public void A_comparison_reads_the_inventory_that_is_already_on_disk_and_says_so()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff-cache");
        ProjectOverCorpusBinary(temp, "sample-release.exe");

        var built = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("built.json"));
        Assert.Equal(0, built.ExitCode);
        var before = ReadComparison(temp.PathOf("built.json"));
        Assert.Equal("built", before.Left);
        Assert.Equal("built", before.Right);

        var inventory = CliRun.Run("inventory", "--project", temp.Path, "--json");
        Assert.Equal(0, inventory.ExitCode);
        Assert.True(File.Exists(Path.Combine(temp.Path, "build", "inventory.json")));

        var read = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("read.json"));
        Assert.Equal(0, read.ExitCode);
        var after = ReadComparison(temp.PathOf("read.json"));
        Assert.Equal("read", after.Left);
        Assert.Equal("read", after.Right);

        Assert.Equal(before.Report, after.Report);
    }

    /// <summary>
    /// The two ways a document is not usable: it is about a different binary, or it was written before
    /// the binary it claims to describe last changed. Reading either would report the previous build's
    /// names as this one's, which is the failure mode the whole guard exists for, so both fall back to
    /// building the inventory.
    /// </summary>
    [Fact]
    public void A_comparison_ignores_a_document_about_another_binary_or_an_older_one()
    {
        if (!TestPaths.CorpusExists("sample-release.exe") || !TestPaths.CorpusExists("sample-debug.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff-cache");
        string binary = ProjectOverCorpusBinary(temp, "sample-release.exe");
        Assert.Equal(0, CliRun.Run("inventory", "--project", temp.Path, "--json").ExitCode);

        using var other = new TempDir("recon-diff-cache");
        ProjectOverCorpusBinary(other, "sample-debug.exe");
        Assert.Equal(0, CliRun.Run("inventory", "--project", other.Path, "--json").ExitCode);

        string document = Path.Combine(temp.Path, "build", "inventory.json");
        string mine = File.ReadAllText(document);

        // A document about another binary, in the place where ours would be.
        File.Copy(Path.Combine(other.Path, "build", "inventory.json"), document, overwrite: true);
        var wrongBinary = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("wrong.json"));
        Assert.Equal(0, wrongBinary.ExitCode);
        Assert.Equal("built", ReadComparison(temp.PathOf("wrong.json")).Left);

        // And ours, but older than the binary it describes.
        File.WriteAllText(document, mine);
        File.SetLastWriteTimeUtc(binary, File.GetLastWriteTimeUtc(document).AddMinutes(1));
        var stale = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("stale.json"));
        Assert.Equal(0, stale.ExitCode);
        Assert.Equal("built", ReadComparison(temp.PathOf("stale.json")).Left);
    }

    /// <summary>
    /// A build directory holds more than one JSON document, and the decision to read one has to be
    /// about what the document *is*, not about what it is called. A comparison document carries a
    /// generator and a version as well, so "written by this tool, about this binary" would accept one
    /// and compare two empty sides — the plausible-looking wrong answer the guard exists to stop.
    /// Three documents that all name the right binary are refused here: one with no `functions` array,
    /// one claiming another schema version, and one written by another version of the tool.
    /// </summary>
    /// <summary>
    /// What a document has to be about is the bytes being compared, not the hash the project recorded
    /// for that input. A project's `sha256` is a statement about the file as it was when `recon init`
    /// read it; an input that has been edited since — a rebuilt binary, which is the everyday case for
    /// a comparison — still has a document that describes it, and the document says which bytes those
    /// are itself. Checking the recorded hash instead refused the right document for the wrong reason.
    /// </summary>
    [Fact]
    public void A_project_whose_recorded_hash_is_stale_still_reads_its_own_document()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff-cache");
        ProjectOverCorpusBinary(temp, "sample-release.exe");
        Assert.Equal(0, CliRun.Run("inventory", "--project", temp.Path, "--json").ExitCode);

        string project = Path.Combine(temp.Path, "project.toml");
        string text = File.ReadAllText(project);
        string stale = System.Text.RegularExpressions.Regex.Replace(
            text, "sha256 = \"[0-9a-fA-F]+\"", "sha256 = \"" + new string('0', 64) + "\"");
        Assert.NotEqual(text, stale);
        File.WriteAllText(project, stale);

        var run = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("stale.json"));
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("read", ReadComparison(temp.PathOf("stale.json")).Left);
    }

    [Fact]
    public void A_comparison_refuses_a_document_that_only_looks_like_an_inventory()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff-cache");
        string binary = ProjectOverCorpusBinary(temp, "sample-release.exe");
        string document = Path.Combine(temp.Path, "build", "inventory.json");
        Assert.Equal(0, CliRun.Run("diff", temp.Path, temp.Path, "--summary", "-o", temp.PathOf("built.json")).ExitCode);
        Assert.Equal(0, CliRun.Run("inventory", "--project", temp.Path, "--json").ExitCode);
        string real = File.ReadAllText(document);

        // Sorts before inventory.json, so it is the document that gets offered first.
        string decoy = Path.Combine(temp.Path, "build", "aaa-decoy.json");
        string hash = System.Text.Json.Nodes.JsonNode.Parse(real)!["binary"]!["sha256"]!.GetValue<string>();

        // A document about this binary, written by this tool, with no functions in it.
        File.WriteAllText(decoy, $$"""
            {
              "schema_version": "0.1",
              "generator": { "tool": "recon", "version": "{{Recon.ToolVersion.Current}}" },
              "binary": { "path": "{{binary}}", "sha256": "{{hash}}" }
            }
            """);
        var empty = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("one.json"));
        Assert.Equal(0, empty.ExitCode);
        Assert.Equal("read", ReadComparison(temp.PathOf("one.json")).Left);

        // The same document, but claiming a contract this build does not speak.
        File.WriteAllText(decoy, real.Replace("\"schema_version\": \"0.1\"", "\"schema_version\": \"9.9\""));
        File.Delete(document);
        var future = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("two.json"));
        Assert.Equal(0, future.ExitCode);
        Assert.Equal("built", ReadComparison(temp.PathOf("two.json")).Left);

        // And a document from another version of the tool: our own, one version down.
        File.WriteAllText(document, real.Replace($"\"version\": \"{Recon.ToolVersion.Current}\"", "\"version\": \"0.0.1\""));
        File.Delete(decoy);
        var older = CliRun.Run("diff", temp.Path, temp.Path, "--json", "-o", temp.PathOf("three.json"));
        Assert.Equal(0, older.ExitCode);
        Assert.Equal("built", ReadComparison(temp.PathOf("three.json")).Left);
    }

    /// <summary>
    /// Two bodies with the same normalized key are the same body — that is what the key is for, and
    /// what the pairing already trusts when it matches two functions by it — so the comparison of such
    /// a pair is written from the summary instead of decoding both bodies a second time. The field
    /// `model.bodies_proven_identical` is how many pairs took that route, and a comparison of a binary
    /// with itself is the case where it is all of them.
    /// </summary>
    [Fact]
    public void A_pair_of_identical_bodies_is_decided_without_decoding_it_again()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff-key");
        string output = temp.PathOf("comparison.json");
        var run = CliRun.Run(
            "diff", TestPaths.Corpus("sample-release.exe"), TestPaths.Corpus("sample-release.exe"),
            "--summary", "-o", output);

        Assert.Equal(0, run.ExitCode);
        var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(output)).RootElement;
        int matched = document.GetProperty("summary").GetProperty("matched").GetInt32();
        int exact = document.GetProperty("summary").GetProperty("exact").GetInt32();
        int proven = document.GetProperty("model").GetProperty("bodies_proven_identical").GetInt32();

        Assert.True(matched > 0);
        Assert.Equal(matched, exact);
        Assert.Equal(matched, proven);

        // And every pair is reported exactly as the differ would have reported it: equal counts, no
        // differences, score 1, and the counts that come from the bodies rather than from the tokens.
        foreach (var function in document.GetProperty("functions").EnumerateArray())
        {
            Assert.Equal("exact", function.GetProperty("status").GetString());
            Assert.Equal(1.0, function.GetProperty("score").GetDouble());
            Assert.Equal(0, function.GetProperty("differences").GetArrayLength());
            var instructions = function.GetProperty("instructions");
            Assert.Equal(instructions.GetProperty("left").GetInt32(), instructions.GetProperty("equal").GetInt32());
            Assert.Equal(instructions.GetProperty("right").GetInt32(), instructions.GetProperty("equal").GetInt32());
        }
    }

    /// <summary>
    /// The key decides the comparison, and the key is a hash — so the one thing it cannot produce is a
    /// *reading* of the two bodies. When the caller asked for a function by name, the aligned listing
    /// is built as it always was, and this is the test that holds the two routes to the same answer:
    /// the same pair compared with and without `--function` differs in that listing and in nothing else.
    /// </summary>
    [Fact]
    public void A_function_asked_for_by_name_is_still_listed_instruction_by_instruction()
    {
        if (!TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var temp = new TempDir("recon-diff-aligned");
        string plain = temp.PathOf("plain.json");
        string listed = temp.PathOf("listed.json");
        string binary = TestPaths.Corpus("sample-release.exe");

        Assert.Equal(0, CliRun.Run("diff", binary, binary, "--summary", "-o", plain).ExitCode);
        Assert.Equal(0, CliRun.Run("diff", binary, binary, "--summary", "-o", listed, "--function", "add").ExitCode);

        // The pair the name asks for, on both runs: an aligned listing belongs to that function, not to
        // whichever function happens to come first.
        static System.Text.Json.Nodes.JsonNode? Asked(string path)
        {
            var functions = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["functions"]!.AsArray();
            return functions.FirstOrDefault(f =>
                f!["left"]?["name"]?.GetValue<string>() == "add"
                || f!["right"]?["name"]?.GetValue<string>() == "add");
        }

        var withListing = Asked(listed)!;
        Assert.Equal("exact", withListing["status"]!.GetValue<string>());
        Assert.NotNull(withListing["aligned"]);
        Assert.NotEmpty(withListing["aligned"]!.AsArray());
        foreach (var row in withListing["aligned"]!.AsArray())
        {
            Assert.NotNull(row!["status"]);
        }

        Assert.Null(Asked(plain)!["aligned"]);

        // The listing is what `--function` adds. Everything else about the pair is the same on both
        // routes, which is the check that the summary path says what the differ would have said.
        var stripped = withListing.AsObject();
        stripped.Remove("aligned");
        var byKey = Asked(plain)!;
        Assert.Equal(byKey.ToJsonString(), stripped.ToJsonString());
    }

    [Fact]
    public void The_diff_command_needs_two_sides_or_a_project()
    {
        using var temp = new TempDir("recon-diff");
        var run = CliRun.Run("diff", "--project", temp.Path);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("usage: recon diff", run.All);
    }
}
