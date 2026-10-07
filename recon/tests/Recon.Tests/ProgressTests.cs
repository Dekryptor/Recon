using System.Text.Json;
using System.Net;
using System.Text.Json.Nodes;
using Recon.Build;
using Recon.Compare;
using Recon.Config;
using Recon.Project;
using Recon.Reporting;
using Recon.Toolchains;
using Recon.Schema;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Progress measurement, which is what the report is for. The distinction these tests exist to keep
/// is the one the plan asks for in its "honest metrics" note: what is proven (a function matches
/// instruction for instruction), what is derived (a share of the original), and what cannot be
/// measured at all (a unit that covers nothing) — three different things, three different numbers,
/// and never a zero standing in for "unknown".
/// </summary>
public class ProgressTests
{
    // ---------------------------------------------------------------- totals

    [Fact]
    public void Progress_is_measured_against_the_original_not_the_rebuild()
    {
        using var temp = new TempDir();
        var comparison = CompareSelf(temp);
        var report = ProgressBuilder.Build(Project(temp, units: string.Empty), comparison, "test");

        // The two sides are the same binary, so every share is 1 — and each of the three numbers
        // means something different, which is why they are reported separately.
        Assert.Equal(1.0, report.Totals.InstructionExact);
        Assert.Equal(1.0, report.Totals.Verified);
        Assert.Equal(1.0, report.Totals.Similarity);
        Assert.Equal(report.Totals.InstructionsOriginal, report.Totals.InstructionsEqual);
        Assert.Equal(comparison.Functions.Count(f => f.Left is not null), report.Totals.FunctionsOriginal);
        Assert.Equal(report.Totals.FunctionsOriginal, report.Totals.FunctionsExact);
    }

    [Fact]
    public void A_rebuild_that_adds_functions_does_not_claim_credit_for_them()
    {
        using var temp = new TempDir();
        var comparison = Compare(temp, new SyntheticPeOptions { DuplicateFuncA = true }, new SyntheticPeOptions());
        var report = ProgressBuilder.Build(Project(temp, units: string.Empty), comparison, "test");

        // The folded copy exists only on one side: it is not part of the original, so it cannot make
        // the result look better.
        Assert.True(report.Totals.InstructionsRebuilt < report.Totals.InstructionsOriginal);

        // The extra copy is recognized as a fold of func_a rather than as missing work: the original
        // has two names for one body, and saying so is more useful than a silent 1/2 score.
        Assert.Equal(1, report.Totals.FunctionsFolded);
        Assert.True(report.Totals.InstructionExact < 1.0);
        Assert.True(report.Totals.Verified < 1.0);
    }

    // ---------------------------------------------------------------- units

    [Fact]
    public void A_unit_is_scored_on_the_original_functions_its_covers_claim()
    {
        using var temp = new TempDir();
        var comparison = Compare(temp, new SyntheticPeOptions { DuplicateFuncA = true }, new SyntheticPeOptions());
        var report = ProgressBuilder.Build(Project(temp, """
            [[unit]]
            name = "a"
            source = "src/a.c"
            toolchain = "msvc-2010"
            status = "matched"

            [[unit.covers]]
            symbol = "func_a"

            [[unit]]
            name = "b"
            source = "src/b.c"
            status = "matched"

            [[unit.covers]]
            symbol = "func_a_copy"
            """), comparison, "test");

        var a = Assert.Single(report.Units, u => u.Name == "a");
        var b = Assert.Single(report.Units, u => u.Name == "b");

        Assert.Equal("msvc-2010", a.Toolchain);
        Assert.Equal(1.0, a.Progress);
        Assert.Equal(a.Instructions.Total, a.Instructions.Equal);
        Assert.Equal(0, a.Functions.Missing);

        // The duplicate option puts a second copy of func_a's code in the original only: the unit
        // that covers it is measured as not reproduced, whatever the rest of the image looks like.
        Assert.Equal(0.0, b.Progress);
        Assert.Equal(1, b.Functions.Missing);
        Assert.Equal(["func_a_copy"], b.MissingFunctions);
    }

    [Fact]
    public void A_unit_that_covers_nothing_reports_no_progress_rather_than_zero()
    {
        using var temp = new TempDir();
        var comparison = CompareSelf(temp);
        var report = ProgressBuilder.Build(Project(temp, """
            [[unit]]
            name = "nowhere"
            source = "src/nowhere.c"
            status = "not_started"

            [[unit.covers]]
            symbol = "_does_not_exist"
            """), comparison, "test");

        var unit = Assert.Single(report.Units);
        Assert.Null(unit.Progress);
        Assert.Equal(0, unit.Instructions.Total);
        Assert.True(Assert.Single(unit.Covers).Unresolved);
        Assert.Contains(unit.Notes, note => note.Contains("matches nothing"));
    }

    [Fact]
    public void A_cover_matches_by_symbol_by_demangled_name_and_by_range()
    {
        using var temp = new TempDir();
        var comparison = CompareSelf(temp);
        var report = ProgressBuilder.Build(Project(temp, """
            [[unit]]
            name = "mixed"
            source = "src/mixed.c"

            [[unit.covers]]
            symbol = "func_a"

            [[unit.covers]]
            symbol = "func_b"

            [[unit.covers]]
            rva = 0x1050
            size = 0x20
            """), comparison, "test");

        var covers = Assert.Single(report.Units).Covers;
        Assert.Equal(3, covers.Count);
        Assert.All(covers, cover => Assert.False(cover.Unresolved));
        Assert.All(covers, cover => Assert.Equal(1, cover.Functions));

        // func_b is covered under its demangled name and func_c by the range it occupies, and the
        // three covers resolve to three different functions.
        var unit = Assert.Single(report.Units);
        Assert.Equal(3, unit.Functions.Total);
    }

    [Fact]
    public void Functions_no_unit_covers_are_reported_separately()
    {
        using var temp = new TempDir();
        var comparison = CompareSelf(temp);
        var report = ProgressBuilder.Build(Project(temp, """
            [[unit]]
            name = "a"
            source = "src/a.c"

            [[unit.covers]]
            symbol = "func_a"
            """), comparison, "test");

        // Five functions in the original, one covered: the rest are reported, not silently ignored,
        // so that a project with no covers cannot be mistaken for one with none written yet.
        Assert.Equal(report.Totals.FunctionsOriginal - 1, report.Uncovered.Total);
        Assert.Contains(report.Notes, note => note.Contains("not covered by any unit"));
        Assert.DoesNotContain(report.Problems, problem => problem.Length > 0);
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public void History_is_appended_one_snapshot_per_run()
    {
        using var temp = new TempDir();
        string directory = temp.Path;
        var comparison = CompareSelf(temp);
        var project = Project(temp, units: string.Empty);

        var first = ProgressBuilder.Build(project, comparison, "test", ProgressHistory.Read(directory));
        var history = ProgressHistory.Append(directory, ProgressBuilder.Snapshot(first));
        var second = ProgressBuilder.Build(project, comparison, "test", history);

        Assert.Single(history);
        Assert.Equal(1.0, history[0].Score);
        Assert.NotNull(second.Previous);
        Assert.Equal(history[0].At, second.Previous!.At);
        Assert.Equal(1.0, second.Previous.Score);

        ProgressHistory.Append(directory, ProgressBuilder.Snapshot(second));
        Assert.Equal(2, ProgressHistory.Read(directory).Count);
    }

    [Fact]
    public void A_damaged_history_line_is_skipped_not_fatal()
    {
        using var temp = new TempDir();
        File.WriteAllLines(
            Path.Combine(temp.Path, ProgressHistory.FileName),
            [
                "{\"at\":\"2026-01-01T00:00:00Z\",\"score\":0.5,\"instructions_equal\":1,\"instructions_original\":2,\"functions_exact\":1,\"functions_original\":2,\"units\":[]}",
                "not json at all",
                "",
            ]);

        var history = ProgressHistory.Read(temp.Path);
        var snapshot = Assert.Single(history);
        Assert.Equal(0.5, snapshot.Score);
    }

    // ---------------------------------------------------------------- documents

    [Fact]
    public void The_report_matches_the_published_schema()
    {
        using var temp = new TempDir();
        var comparison = CompareSelf(temp);
        var report = ProgressBuilder.Build(Project(temp, """
            [[unit]]
            name = "a"
            source = "src/a.c"

            [[unit.covers]]
            symbol = "func_a"
            """), comparison, "test", [ProgressBuilder.Snapshot(ProgressBuilder.Build(Project(temp, string.Empty), comparison, "test"))]);

        string json = Reports.Serialize(report);
        var violations = JsonSchemaValidator.Parse(BuiltInSchemas.Get("progress") ?? string.Empty)
            .Validate(json);

        Assert.Empty(violations);
        Assert.Equal("0.1", JsonNode.Parse(json)!["schema_version"]!.GetValue<string>());
    }

    [Fact]
    public void The_site_is_one_self_contained_file_that_carries_its_own_documents()
    {
        using var temp = new TempDir();
        var comparison = CompareSelf(temp);
        var report = ProgressBuilder.Build(Project(temp, units: string.Empty), comparison, "test");

        string html = ReportSite.StaticHtml(comparison, report);

        Assert.Contains(Reports.Serialize(comparison), html, StringComparison.Ordinal);
        Assert.Contains(Reports.Serialize(report), html, StringComparison.Ordinal);
        Assert.DoesNotContain("window.__RECON_API__ = {", html, StringComparison.Ordinal);

        // Nothing is fetched: no stylesheet, script or font from anywhere, which is what lets the
        // file be opened straight off a disk.
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_server_page_points_at_the_api_instead_of_embedding_anything()
    {
        string html = ReportSite.ServerHtml();

        Assert.Contains("__RECON_API__", html, StringComparison.Ordinal);
        Assert.Contains("api/comparison", html, StringComparison.Ordinal);
        Assert.Contains("api/aligned?id=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Alignment_for_one_function_can_be_computed_on_demand()
    {
        using var temp = new TempDir();
        string leftPath = temp.Write("left.exe", SyntheticPe.Build(new SyntheticPeOptions { DuplicateFuncA = true }));
        string rightPath = temp.Write("right.exe", SyntheticPe.Build(new SyntheticPeOptions()));
        var diagnostics = new Diagnostics();
        var left = ComparisonSide.FromBinary("left", leftPath, diagnostics);
        var right = ComparisonSide.FromBinary("right", rightPath, diagnostics);
        Assert.Empty(diagnostics.Errors);

        var comparison = ComparisonBuilder.Build(left, right, new ComparisonOptions());
        var pair = Assert.Single(comparison.Functions, f => f.Left?.Name == "func_a" && f.Right is not null);

        var rows = ComparisonBuilder.AlignedFor(left, right, pair.Left!.Id, pair.Right!.Id);

        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.All(rows!, row => Assert.Contains(row.Status, new[] { "equal", "changed", "added", "removed" }));
        Assert.Null(ComparisonBuilder.AlignedFor(left, right, "nope", pair.Right.Id));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A comparison of one binary with itself: everything matches, so the shares are 1.</summary>
    private static ComparisonDocument CompareSelf(TempDir temp) => Compare(temp, new SyntheticPeOptions(), new SyntheticPeOptions());

    private static ComparisonDocument Compare(TempDir temp, SyntheticPeOptions left, SyntheticPeOptions right)
    {
        string leftPath = temp.Write("left.exe", SyntheticPe.Build(left));
        string rightPath = temp.Write("right.exe", SyntheticPe.Build(right));
        var diagnostics = new Diagnostics();
        var leftSide = ComparisonSide.FromBinary("left", leftPath, diagnostics);
        var rightSide = ComparisonSide.FromBinary("right", rightPath, diagnostics);
        Assert.Empty(diagnostics.Errors);
        return ComparisonBuilder.Build(leftSide, rightSide, new ComparisonOptions());
    }

    /// <summary>
    /// A project on disk, because that is what <c>recon report</c> reads: the units say who owns which
    /// part of the original, and without them progress is only a number for the whole image.
    /// </summary>
    private static ProjectConfig Project(TempDir temp, string units)
    {
        // The shipped profiles, because a unit may name the toolchain it is rebuilt with, and the
        // report carries that name rather than repeating the target's.
        BuiltInProfiles.WriteTo(Path.Combine(temp.Path, "toolchains"));

        string file = temp.Write("project.toml", $$"""
            schema_version = 1

            [project]
            name = "fixture"

            [target]
            format = "pe32"
            arch = "x86"

            [paths]
            profiles = ["toolchains"]

            [[input]]
            id = "main"
            role = "original"
            file = "left.exe"
            sha256 = "{{Sha256Hex(File.ReadAllBytes(Path.Combine(temp.Path, "left.exe")))}}"

            {{units}}
            """);

        var diagnostics = new Diagnostics();
        var context = ProjectContext.Load(file, diagnostics);
        Assert.Empty(diagnostics.Errors);
        return context.Project;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// The viewer's server. It exists so the diff can be browsed while a reconstruction is being
    /// worked on, which means it has to answer from the documents it was given and compute alignment
    /// one function at a time rather than shipping every body to the browser.
    /// </summary>
    [Fact]
    public async Task The_server_answers_the_viewer_and_aligns_on_demand()
    {
        using var temp = new TempDir();
        string leftPath = temp.Write("left.exe", SyntheticPe.Build(new SyntheticPeOptions { DuplicateFuncA = true }));
        string rightPath = temp.Write("right.exe", SyntheticPe.Build(new SyntheticPeOptions { MutateFuncA = true }));
        var diagnostics = new Diagnostics();
        var left = ComparisonSide.FromBinary("left", leftPath, diagnostics);
        var right = ComparisonSide.FromBinary("right", rightPath, diagnostics);
        var comparison = ComparisonBuilder.Build(left, right, new ComparisonOptions());
        var report = ProgressBuilder.Build(Project(temp, units: string.Empty), comparison, "test");

        using var server = new ViewerServer(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0),
            new ViewerContent { Comparison = comparison, Progress = report, Left = left, Right = right },
            new TextLog());
        using var cancel = new CancellationTokenSource();
        var serving = Task.Run(() => server.Run(cancel.Token));

        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        string page = await client.GetStringAsync("/");
        string compared = await client.GetStringAsync("api/comparison");
        string progress = await client.GetStringAsync("api/progress");

        Assert.Contains("__RECON_API__", page, StringComparison.Ordinal);
        Assert.Contains("\"schema_version\"", compared, StringComparison.Ordinal);
        Assert.Contains("\"instruction_exact\"", progress, StringComparison.Ordinal);

        var pair = Assert.Single(comparison.Functions, f => f.Status == "changed");
        string aligned = await client.GetStringAsync($"api/aligned?id={Uri.EscapeDataString(pair.Left!.Id)}");
        var rows = System.Text.Json.Nodes.JsonNode.Parse(aligned)!["rows"]!.AsArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row!["status"]!.GetValue<string>())));

        var missing = await client.GetAsync("api/aligned?id=does-not-exist");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        var unknown = await client.GetAsync("nope");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);

        cancel.Cancel();
        await Task.WhenAny(serving, Task.Delay(2000));
    }

    /// <summary>Keeps the server's output out of the test log.</summary>
    private sealed class TextLog : Recon.Build.IBuildLog
    {
        public void Info(string text) { }

        public void Step(string text) { }

        public void Warn(string text) { }

        public void Error(string text) { }
    }
}
