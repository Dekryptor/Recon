using System.Diagnostics;
using Recon.Analysis;
using Recon.Config;
using Recon.Images;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The Visual C++ 5.x profile: the toolset of Visual Studio 97, whose linker stamps 5.x in the
/// optional header and signs its own object with product id 0x0013 in the Rich header.
///
/// The synthetic tests below run everywhere. The ones at the bottom read three real 1998 files —
/// <c>VBA6.DLL</c> (linker 5.12), <c>VB6.EXE</c> and <c>msvbvm60.dll</c> (linker 5.2 each) — and
/// skip when they are not on the machine, the way <see cref="RealVb6Tests"/> does; point
/// <c>RECON_VB6_INPUTS</c> at a directory holding them. What those pin is the part no fixture can:
/// that the numbers in the profile are the numbers a real file carries.
/// </summary>
[Collection("cli")]
public class Msvc5ToolchainTests
{
    private static ToolchainRegistry BuiltIns(out Diagnostics diagnostics)
    {
        using var temp = new TempDir("recon-msvc5-profiles");
        string directory = System.IO.Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        BuiltInProfiles.WriteTo(directory);
        diagnostics = new Diagnostics();
        return ToolchainRegistry.Load([directory], diagnostics);
    }

    private static IBinaryImage Load(SyntheticPeOptions? options)
    {
        var result = ImageLoader.LoadBytes(SyntheticPe.Build(options), "synthetic-pe32.exe");
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    /// <summary>
    /// A Visual C++ 5.x image: the 5.12 linker stamp and the 1997 bill of materials, both as
    /// <c>VBA6.DLL</c> carries them. The fixture's default Rich header is a Visual Studio 2008
    /// toolset, which is evidence for a different profile, so this one replaces it.
    /// </summary>
    private static SyntheticPeOptions Vc5()
        => new()
        {
            LinkerMajor = 5,
            LinkerMinor = 12,
            LinkerBuild = 8078,
            RichHeader = true,
            RichVs97Toolset = true,
        };

    // ------------------------------------------------------------------------- the profile itself

    [Fact]
    public void The_profile_loads_and_is_the_msvc_family()
    {
        var registry = BuiltIns(out var diagnostics);
        Assert.Empty(diagnostics.Errors);

        var profile = registry.GetResolved("msvc-5");
        Assert.NotNull(profile);

        Assert.Equal("msvc", profile!.Family);
        Assert.Equal(["msvc-base", "msvc-5"], profile.InheritanceChain);
        Assert.Contains(profile.Targets, t => t.Format == "pe32" && t.Arch == "x86");

        // It inherits the MSVC ABI rather than restating it: padding is int3, the exception model is
        // SEH and the debug information is a PDB, all of which were true of the 5.x toolset.
        Assert.Equal("msvc", profile.Abi.Mangling);
        Assert.Equal("msvc-seh", profile.Eh.Model);
        Assert.Equal("pdb", profile.Debug.Format);
    }

    // ----------------------------------------------------------------------------- attribution

    [Fact]
    public void A_5_12_image_with_the_1997_linker_record_is_attributed_to_it()
    {
        var registry = BuiltIns(out _);
        var report = ProducerDetector.Detect(Load(Vc5()), null, registry);

        // Two independent kinds of evidence: the stamp in the optional header and the product id in
        // the Rich header. Neither alone would be worth much; together they name a toolset.
        var msvc5 = Assert.Single(report.Suggestions, s => s.ProfileId == "msvc-5");
        Assert.Equal("medium", msvc5.Confidence);
        Assert.Contains("linker_version", msvc5.Evidence);
        Assert.Contains("rich_header", msvc5.Evidence);

        // And the generation next to it is not claimed: 6.0 is a different linker line.
        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "msvc-6");
    }

    /// <summary>
    /// In <c>VBA6.DLL</c> the last Rich record is the resource converter, not the linker. This is
    /// the case that decides whether a linker is found by its product id or by its position, so the
    /// fixture reproduces the file's shape — masm, linker, cvtres — and the middle one is the answer.
    /// </summary>
    [Fact]
    public void The_linker_record_is_matched_by_its_product_id_and_not_by_its_position()
    {
        var pe = Assert.IsType<PeBinaryImage>(Load(Vc5())).Pe;
        Assert.NotNull(pe.Rich);

        var entries = pe.Rich!.Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal("masm_6.13", entries[0].Tool);
        Assert.Equal("linker_5.12", entries[1].Tool);
        Assert.Equal("cvtres_5.0", entries[2].Tool);

        // The last entry is not the linker, and the tool says which one is.
        Assert.False(entries[2].IsLinker);
        Assert.NotNull(pe.Rich.LinkerEntry);
        Assert.Equal(0x0013u, pe.Rich.LinkerEntry!.ProdId);
        Assert.Equal(8078, pe.Rich.LinkerEntry.Build);
    }

    /// <summary>
    /// <c>VB6.EXE</c>, <c>VB6IDE.DLL</c> and <c>msvbvm60.dll</c> all carry the 5.2 stamp and no Rich
    /// header at all. A profile that demanded both would look at three real binaries it can name and
    /// report nothing, which is why the stamp alone is enough.
    /// </summary>
    [Fact]
    public void A_5_2_image_with_no_rich_header_at_all_is_still_attributed_to_it()
    {
        var registry = BuiltIns(out _);
        var report = ProducerDetector.Detect(
            Load(new SyntheticPeOptions { LinkerMajor = 5, LinkerMinor = 2, RichHeader = false }),
            null,
            registry);

        var msvc5 = Assert.Single(report.Suggestions, s => s.ProfileId == "msvc-5");
        Assert.Equal(["linker_version"], msvc5.Evidence);
    }

    [Fact]
    public void A_6_0_image_is_not_attributed_to_it()
    {
        var registry = BuiltIns(out _);
        var report = ProducerDetector.Detect(
            Load(new SyntheticPeOptions { LinkerMajor = 6, LinkerMinor = 0, RichHeader = false }),
            null,
            registry);

        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "msvc-5");
    }

    /// <summary>A Visual C++ build is not a Visual Basic program, whatever it links against.</summary>
    [Fact]
    public void A_visual_c_5_image_does_not_claim_to_be_visual_basic()
    {
        var registry = BuiltIns(out _);
        var report = ProducerDetector.Detect(Load(Vc5()), null, registry);

        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "vb6-native");
    }

    // ----------------------------------------------------------------------- the real 1998 files

    /// <summary>The directory the binaries live in, or null when they are not on this machine.</summary>
    private static string? InputsDirectory
    {
        get
        {
            string? directory = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
            return string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : directory;
        }
    }

    private static string? Path(string name)
    {
        string? directory = InputsDirectory;
        if (directory is null)
        {
            return null;
        }

        string candidate = System.IO.Path.Combine(directory, name);
        return File.Exists(candidate) ? candidate : null;
    }

    private static IBinaryImage LoadReal(string name)
    {
        var result = ImageLoader.Load(Path(name)!);
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    /// <summary>
    /// <c>VBA6.DLL</c> is the one file here that carries both: the 5.12 stamp and product id 0x0013
    /// at build 8078. These are the numbers written in the profile, and this is where they came from.
    /// </summary>
    [Fact]
    public void The_basic_runtime_carries_both_the_stamp_and_the_1997_linker_record()
    {
        if (Path("VBA6.DLL") is null)
        {
            return;
        }

        var image = LoadReal("VBA6.DLL");
        var report = ProducerDetector.Detect(image, null, BuiltIns(out _));

        Assert.Equal("msvc-5", report.Suggestions[0].ProfileId);

        var pe = Assert.IsType<PeBinaryImage>(image).Pe;
        Assert.Equal(5, pe.MajorLinkerVersion);
        Assert.Equal(12, pe.MinorLinkerVersion);

        var linkers = pe.Rich!.Entries.Where(e => e.IsLinker).ToList();
        Assert.Contains(linkers, e => e.ProdId == 0x0013 && e.Build == 8078);
    }

    /// <summary>
    /// The Visual Basic 6 IDE and its runtime: 5.2 in the optional header and no Rich header at all,
    /// which is the case a two-rule profile exists for.
    /// </summary>
    [Theory]
    [InlineData("VB6.EXE")]
    [InlineData("VB6IDE.DLL")]
    [InlineData("msvbvm60.dll")]
    public void The_visual_basic_files_are_attributed_to_the_5_x_linker_that_linked_them(string name)
    {
        if (Path(name) is null)
        {
            return;
        }

        var image = LoadReal(name);
        var report = ProducerDetector.Detect(image, null, BuiltIns(out _));

        Assert.Equal("msvc-5", report.Suggestions[0].ProfileId);

        var pe = Assert.IsType<PeBinaryImage>(image).Pe;
        Assert.Equal(5, pe.MajorLinkerVersion);
        Assert.Equal(2, pe.MinorLinkerVersion);
    }

    /// <summary>
    /// The C++ tools of the same release — LINK.EXE, C2.EXE, CVPACK.EXE — stamp 6.0 and are
    /// attributed to <c>msvc-6</c> first. They also carry 0x0013, from import libraries built before
    /// their own toolset shipped, so <c>msvc-5</c> appears beside them; what it may not do is
    /// displace them.
    /// </summary>
    [Theory]
    [InlineData("LINK.EXE")]
    [InlineData("C2.EXE")]
    [InlineData("CVPACK.EXE")]
    public void The_visual_studio_98_tools_are_still_attributed_to_the_6_0_linker(string name)
    {
        if (Path(name) is null)
        {
            return;
        }

        var registry = BuiltIns(out _);
        var report = ProducerDetector.Detect(LoadReal(name), null, registry);

        Assert.Equal("msvc-6", report.Suggestions[0].ProfileId);
    }
}
