using System.Text.Json;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// A p-code program in the inventory. The tool used to refuse these bytes as machine code — correctly,
/// because they are interpreter tokens — but refusing them left the whole program invisible: one
/// function named `entry` covering the section, no procedures, no instruction counts, and nothing for
/// `inspect`, `report` or a comparison to read. A p-code program states its own procedure table, so
/// what is published now is that table: one function per procedure, its extent taken from the
/// descriptor the compiler wrote, and its instructions counted against the runtime that will run them.
///
/// Measured over the DeForm6 corpus (`RECON_PCODE_INPUTS`) with the runtime from `RECON_VB6_INPUTS`.
/// </summary>
[Collection("cli")]
public class PcodeInventoryTests
{
    private static string? Corpus()
    {
        string? root = Environment.GetEnvironmentVariable("RECON_PCODE_INPUTS");
        return root is not null && Directory.Exists(root) ? root : null;
    }

    private static string? Runtime()
    {
        string? inputs = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
        if (inputs is null)
        {
            return null;
        }

        string path = Path.Combine(inputs, "msvbvm60.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The corpus's HexScroll, the program the p-code reader's own tests measure.</summary>
    private static string? HexScroll()
    {
        string? corpus = Corpus();
        if (corpus is null)
        {
            return null;
        }

        string path = Path.Combine(corpus, "public-domain", "HexScroll", "Hex Scroll.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>A project over one p-code program, with the runtime placed beside it or not.</summary>
    private static TempDir Project(string program, string? runtime)
    {
        var temp = new TempDir("recon-pcode-inventory");
        string name = Path.GetFileName(program);
        File.Copy(program, temp.PathOf(name), overwrite: true);
        if (runtime is not null)
        {
            File.Copy(runtime, temp.PathOf("msvbvm60.dll"), overwrite: true);
        }

        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "pcode"

            [target]
            format = "pe32"
            arch = "x86"
            isa = "vb6-pcode"

            [[input]]
            id = "main"
            role = "original"
            file = "{name}"
            sha256 = "{PeImage.HashFile(temp.PathOf(name))}"
            """);
        temp.Write("local.toml", """
            schema_version = 1

            [inputs]
            dir = "."
            """);
        return temp;
    }

    [Fact]
    public void A_p_code_programs_inventory_is_its_procedure_table()
    {
        if (HexScroll() is not { } program || Runtime() is not { } runtime)
        {
            return;
        }

        using var project = Project(program, runtime);
        var run = CliRun.Run("inventory", "--project", project.Path, "--json", "--check-schema");
        Assert.True(run.ExitCode == 0, run.All);

        using var document = JsonDocument.Parse(run.StandardOutput);
        var root = document.RootElement;

        // The instruction set is its own axis, and the decoder is still not asked to read these bytes.
        Assert.Equal("vb6-pcode", root.GetProperty("binary").GetProperty("isa").GetString());

        var statistics = root.GetProperty("statistics");
        int procedures = statistics.GetProperty("functions").GetInt32();
        Assert.Equal(23, procedures);
        Assert.Equal(895, statistics.GetProperty("instructions").GetInt32());
        Assert.Equal(1, statistics.GetProperty("pcode_runtime_used").GetInt32());
        Assert.Equal(2, statistics.GetProperty("pcode_objects").GetInt32());
        Assert.Equal(procedures, statistics.GetProperty("functions_high").GetInt32());
        Assert.Equal(0, statistics.GetProperty("functions_with_unknown_size").GetInt32());

        // Nothing about this program is a problem: it is read, not refused.
        Assert.Empty(root.GetProperty("problems").EnumerateArray());

        var functions = root.GetProperty("functions").EnumerateArray().ToList();
        Assert.Equal(procedures, functions.Count);

        // Every one names the object and method the program's own table gives it, and says so: VB6 does
        // not store procedure names in the binary, and the report must not look like it found any.
        foreach (var function in functions)
        {
            string name = function.GetProperty("name").GetString()!;
            Assert.Matches(@"^[A-Za-z_][A-Za-z0-9_]*\[[0-9]+\]$", name);
            Assert.Equal("vb6-pcode", function.GetProperty("isa").GetString());
            Assert.Equal("high", function.GetProperty("confidence").GetString());
            Assert.Contains("pcode_procedure", function.GetProperty("found_by").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains(
                "name_is_object_and_method_index",
                function.GetProperty("unknowns").EnumerateArray().Select(e => e.GetString()));

            // The extent is what the descriptor states, and it is inside the image.
            var range = function.GetProperty("ranges").EnumerateArray().Single();
            Assert.True(range.GetProperty("size").GetInt32() > 0);
            Assert.Equal(".text", function.GetProperty("section").GetString());
        }

        // The procedures are the ones `recon pcode` lists, by address and by extent: the two commands
        // read the same table, and a disagreement between them would be a bug in one of them.
        var pcode = CliRun.Run("pcode", program, "--runtime", runtime, "--json");
        Assert.True(pcode.ExitCode == 0, pcode.All);
        using var listed = JsonDocument.Parse(pcode.StandardOutput);
        var fromPcode = listed.RootElement.GetProperty("procedures").EnumerateArray()
            .Select(p => (Rva: p.GetProperty("code_rva").GetUInt32(), Size: p.GetProperty("code_size").GetInt32(),
                          Name: $"{p.GetProperty("object").GetString()}[{p.GetProperty("method").GetInt32()}]"))
            .OrderBy(p => p.Rva)
            .ToList();
        var fromInventory = functions
            .Select(f => (Rva: f.GetProperty("ranges").EnumerateArray().Single().GetProperty("rva").GetUInt32(),
                          Size: f.GetProperty("ranges").EnumerateArray().Single().GetProperty("size").GetInt32(),
                          Name: f.GetProperty("name").GetString()!))
            .OrderBy(f => f.Rva)
            .ToList();
        Assert.Equal(fromPcode, fromInventory);
    }

    [Fact]
    public void Without_a_runtime_the_procedures_are_still_published_and_the_gap_is_stated()
    {
        if (HexScroll() is not { } program)
        {
            return;
        }

        // No runtime beside the program and none named: the extents come from the program's own method
        // tables, which need no interpreter, and the instructions inside them are not guessed at.
        using var project = Project(program, runtime: null);
        var run = CliRun.Run("inventory", "--project", project.Path, "--json", "--check-schema");
        Assert.True(run.ExitCode == 0, run.All);

        using var document = JsonDocument.Parse(run.StandardOutput);
        var root = document.RootElement;
        var statistics = root.GetProperty("statistics");
        Assert.Equal(23, statistics.GetProperty("functions").GetInt32());
        Assert.Equal(0, statistics.GetProperty("instructions").GetInt32());
        Assert.Equal(0, statistics.GetProperty("pcode_runtime_used").GetInt32());

        // One problem, and it says what to do about it rather than only what is missing.
        string problem = Assert.Single(root.GetProperty("problems").EnumerateArray()).GetString()!;
        Assert.Contains("runtime", problem);
        Assert.Contains("--runtime", problem);

        // Not measured is not the same as measured and wrong: no stream was read, so nothing claims a
        // procedure has no exit instruction.
        foreach (var function in root.GetProperty("functions").EnumerateArray())
        {
            Assert.Empty(function.GetProperty("flags").EnumerateArray());
        }
    }
}
