using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Visual Basic 6 compiles to one of two things: native x86, or p-code that MSVBVM60 interprets at
/// run time. Telling them apart is one field — <c>aNativeCode</c> in the project structure — and it
/// decides whether the bytes the image calls code may be decoded at all. Handed p-code, an x86
/// decoder produces a plausible-looking program out of interpreter tokens, which is worse than
/// producing nothing.
///
/// These tests build the smallest PE that carries a VB5! header, once with <c>aNativeCode</c> set
/// and once cleared, and check that the tool tells the two apart and refuses the one it cannot
/// read. The image is synthetic because no p-code VB6 binary could be obtained; the layout it is
/// built from is the published one, and it was checked against a real VB6 native binary — see
/// <c>docs/m7-status.md</c>.
/// </summary>
[Collection("cli")]
public class Vb6PcodeTests
{
    private const uint ImageBase = 0x400000;

    private const uint EntryRva = 0x1000;
    private const uint HeaderRva = 0x1100;
    private const uint ProjectInfoRva = 0x1200;

    [Fact]
    public void A_p_code_program_is_reported_as_p_code_and_is_not_decoded()
    {
        var run = Inventory(SyntheticVb6.Program(0));

        Assert.Equal(0, run.ExitCode);
        JsonElement binary = Binary(run);
        Assert.Equal("vb6-pcode", binary.GetProperty("isa").GetString());

        JsonElement vb = binary.GetProperty("vb6");
        Assert.True(vb.GetProperty("is_pcode").GetBoolean());
        Assert.Equal(0u, vb.GetProperty("native_code_rva").GetUInt32());
        Assert.Equal("VB5!", vb.GetProperty("signature").GetString());
        Assert.Equal(0x1F4u, vb.GetProperty("template_version").GetUInt32());

        // The point of the whole detection: no invented instructions. A p-code program's functions come
        // from its own method tables or they do not exist — never from decoding interpreter tokens as
        // machine code. This image is the smallest thing that carries a VB5! header, with an empty
        // method table, so there is nothing to list and the reason is stated.
        JsonElement statistics = Root(run).GetProperty("statistics");
        Assert.Equal(0, statistics.GetProperty("instructions").GetInt32());
        Assert.Equal(0, statistics.GetProperty("functions").GetInt32());
        Assert.Equal(0, statistics.GetProperty("pcode_runtime_used").GetInt32());

        string problems = string.Join("\n", Root(run).GetProperty("problems").EnumerateArray()
            .Select(p => p.GetString() ?? string.Empty));
        Assert.Contains("method(s) and the dispatch table", problems);

        // And the listing path still refuses these bytes, naming the commands that read them instead.
        using var temp = new TempDir("recon-vb6-disasm");
        string path = WriteBinary(temp.Path, SyntheticVb6.Program(0), "program.exe");
        var listing = CliRun.Run("disasm", "--project", Project(temp.Path, path), "0x1000");
        Assert.Equal(1, listing.ExitCode);
        Assert.Contains("not vb6-pcode", listing.All);
        Assert.Contains("recon pcode", listing.All);
    }

    [Fact]
    public void A_native_program_is_reported_as_native_and_stays_x86()
    {
        var run = Inventory(SyntheticVb6.Program(ImageBase + 0x1600));

        Assert.Equal(0, run.ExitCode);
        JsonElement binary = Binary(run);
        Assert.Equal("x86", binary.GetProperty("isa").GetString());

        JsonElement vb = binary.GetProperty("vb6");
        Assert.False(vb.GetProperty("is_pcode").GetBoolean());
        Assert.Equal(0x1600u, vb.GetProperty("native_code_rva").GetUInt32());
        Assert.Equal(HeaderRva, vb.GetProperty("header_rva").GetUInt32());
        Assert.Equal(0x1400u, vb.GetProperty("code_start_rva").GetUInt32());
        Assert.Equal(0x1500u, vb.GetProperty("code_end_rva").GetUInt32());
        Assert.Equal("VB6EN.DLL", vb.GetProperty("language_dll").GetString());
    }

    [Fact]
    public void A_program_that_is_not_visual_basic_has_no_vb6_block()
    {
        using var temp = new TempDir("recon-vb6-none");
        string binary = WriteBinary(temp.Path, SyntheticPe.Build(), "plain.exe");

        var run = CliRun.Run("inventory", "--project", Project(temp.Path, binary), "--json", "--check-schema");

        Assert.Equal(0, run.ExitCode);
        JsonElement binaryElement = Binary(run);
        Assert.False(binaryElement.TryGetProperty("vb6", out _));
        Assert.Equal("x86", binaryElement.GetProperty("isa").GetString());
    }

    [Fact]
    public void A_p_code_program_is_not_attributed_to_the_native_visual_basic_profile()
    {
        var run = Producers(SyntheticVb6.Program(0));

        Assert.Equal(0, run.ExitCode);
        JsonElement root = JsonDocument.Parse(run.StandardOutput).RootElement;

        // The VB5! header is recorded as evidence for what the program is...
        var kinds = root.GetProperty("producers")
            .EnumerateArray()
            .Select(p => (Kind: p.GetProperty("kind").GetString(), Detail: p.GetProperty("detail").GetString()))
            .ToList();
        Assert.Contains(kinds, k => k.Kind == "vb_header" && (k.Detail ?? string.Empty).Contains("p-code"));

        // ...and the profile that describes native compilation is not offered for it, however
        // strongly the imports argue for Visual Basic.
        Assert.DoesNotContain("vb6-native", Suggestions(SyntheticVb6.Program(0)));
    }

    [Fact]
    public void A_native_program_keeps_its_visual_basic_attribution()
    {
        // The same image with aNativeCode set: the profile is offered, so the test above is really
        // measuring the p-code rule and not a fixture that never matched in the first place.
        Assert.Contains("vb6-native", Suggestions(SyntheticVb6.Program(ImageBase + 0x1600)));
    }

    /// <summary>The profile ids <c>toolchain detect</c> offers for an image.</summary>
    private static List<string?> Suggestions(byte[] image)
    {
        using var temp = new TempDir("recon-vb6-suggest");
        string binary = WriteBinary(temp.Path, image, "program.exe");
        string project = Project(temp.Path, binary);
        var run = CliRun.Run("toolchain", "detect", "--project", project, "--json");
        Assert.Equal(0, run.ExitCode);
        return JsonDocument.Parse(run.StandardOutput).RootElement
            .GetProperty("suggestions")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToList();
    }

    private static CliRun Producers(byte[] image)
    {
        using var temp = new TempDir("recon-vb6-producers");
        string binary = WriteBinary(temp.Path, image, "program.exe");
        return CliRun.Run("inspect", "producers", "--project", Project(temp.Path, binary), "--json");
    }

    /// <summary>Runs <c>inventory</c> over a one-file project built around <paramref name="image"/>.</summary>
    private static CliRun Inventory(byte[] image)
    {
        using var temp = new TempDir("recon-vb6");
        string binary = WriteBinary(temp.Path, image, "program.exe");
        return CliRun.Run("inventory", "--project", Project(temp.Path, binary), "--json", "--check-schema");
    }

    /// <summary>
    /// Puts a binary where the project reads it. <c>init</c> hashes a binary it is handed but does
    /// not copy it, so the file has to be in the project's <c>inputs</c> before the project is used.
    /// </summary>
    private static string WriteBinary(string directory, byte[] image, string name)
    {
        string inputs = Path.Combine(directory, "inputs");
        Directory.CreateDirectory(inputs);
        string path = Path.Combine(inputs, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    private static string Project(string directory, string binary)
    {
        var init = CliRun.Run("init", directory, "--binary", binary, "--json");
        Assert.Equal(0, init.ExitCode);
        return directory;
    }

    private static JsonElement Root(CliRun run) => JsonDocument.Parse(run.StandardOutput).RootElement;

    private static JsonElement Binary(CliRun run) => Root(run).GetProperty("binary");

}
