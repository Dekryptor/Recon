using System.Diagnostics;
using Recon.Analysis;
using Recon.Config;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The real thing: Visual Basic 6 and Visual Studio 98 binaries from 1998-2004, the files the VB6
/// profile's evidence was measured against. They cannot be committed — they are Microsoft
/// binaries — so these tests run when the inputs are present and skip when they are not, the way
/// the corpus tests do. Point <c>RECON_VB6_INPUTS</c> at a directory holding them; the set this was
/// measured against is <c>https://github.com/Dekryptor/tests/raw/refs/heads/main/VB6-inputs.zip</c>.
///
/// What they pin is what read off those files by hand and cannot be re-derived from the synthetic
/// fixture: that a real VB6 program attributes to <c>vb6-native</c> on four kinds of evidence, that
/// its Rich header decodes to a Basic compiler's bill of materials, that the Visual Studio 98 tools
/// beside it are not called MinGW, and that inventorying them is not quadratic.
/// </summary>
[Collection("cli")]
public class RealVb6Tests
{
    /// <summary>The directory the binaries live in, or null when they are not on this machine.</summary>
    private static string? InputsDirectory
    {
        get
        {
            string? directory = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            return directory;
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

    private static ToolchainRegistry BuiltIns()
    {
        using var temp = new TempDir("recon-vb6-real");
        string directory = System.IO.Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        BuiltInProfiles.WriteTo(directory);
        return ToolchainRegistry.Load([directory], new Diagnostics());
    }

    private static IBinaryImage Load(string name)
    {
        var result = ImageLoader.Load(Path(name)!);
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    private static InventoryDocument Inventory(IBinaryImage image, bool xrefs = false)
    {
        return InventoryBuilder.Build(new InventoryInputs
        {
            Project = new ProjectConfig
            {
                Project = new ProjectMeta { Name = "vb6-real" },
                Target = new TargetSpec { Format = image.Format, Arch = image.ArchName },
            },
            Image = image,
            Bytes = image.Bytes,
            Options = new AnalysisOptions { BuildXrefs = xrefs },
        });
    }

    // ----------------------------------------------------------------------- VISDATA.EXE, a VB6 program

    /// <summary>
    /// VISDATA.EXE is the data-manager sample that shipped with Visual Basic 6: a program compiled
    /// by VB6 itself, and the file every number in the <c>vb6-native</c> profile was read from.
    /// </summary>
    [Fact]
    public void A_real_visual_basic_program_is_attributed_to_visual_basic()
    {
        if (Path("VISDATA.EXE") is null)
        {
            return;
        }

        var registry = BuiltIns();
        var image = Load("VISDATA.EXE");
        var report = ProducerDetector.Detect(image, null, registry);

        var best = report.Suggestions[0];
        Assert.Equal("vb6-native", best.ProfileId);

        // Four kinds of evidence, all of them read out of this one file: the runtime it links
        // against, the linker stamp, the Basic compiler's Rich records, and the program's own header
        // saying it holds native code. The last one is what keeps the pair apart — a p-code program
        // matches the other three exactly, and is attributed to `vb6-pcode` for this one field.
        Assert.Contains("import_dll", best.Evidence);
        Assert.Contains("linker_version", best.Evidence);
        Assert.Contains("rich_header", best.Evidence);
        Assert.Contains("vb_header", best.Evidence);

        Assert.Contains(
            report.Evidence,
            e => e.Kind == "import_dll" && string.Equals(e.Detail, "MSVBVM60.DLL", System.StringComparison.Ordinal));
        Assert.Contains(
            report.Evidence,
            e => e.Kind == "linker_version" && string.Equals(e.Version, "6.0", System.StringComparison.Ordinal));

        // And the selector the inventory actually uses agrees.
        Assert.Equal("vb6-native", ToolchainSelector.Choose(registry, null, image)!.Id);
    }

    /// <summary>
    /// The Rich header of that same file, decoded. These are the bytes as the linker wrote them:
    /// one assembler object, thirty-six Basic objects — a program's worth of forms and modules —
    /// and one Visual Basic 6 record for the link step. None of them is a linker record, which is
    /// the case the "the last entry is the linker" assumption got wrong.
    /// </summary>
    [Fact]
    public void The_rich_header_decodes_to_a_visual_basic_bill_of_materials()
    {
        if (Path("VISDATA.EXE") is null)
        {
            return;
        }

        var image = Load("VISDATA.EXE");
        var pe = Assert.IsType<PeBinaryImage>(image).Pe;
        Assert.NotNull(pe.Rich);

        var rich = pe.Rich!;
        Assert.Equal(0x8917A385u, rich.XorKey);
        Assert.Equal(
            [(0x0Eu, (ushort)7299, 1u), (0x09u, (ushort)8041, 36u), (0x0Du, (ushort)8167, 1u)],
            rich.Entries.Select(e => (e.ProdId, e.Build, e.Count)).ToArray());
        Assert.True(rich.Entries[^1].IsLast);

        // The entries are XORed with the key, the counts included: read without it, this file's
        // counts come out in the billions and not one product id is known.
        Assert.Equal("masm_6.13", rich.Entries[0].Tool);
        Assert.Equal("vb6_basic", rich.Entries[1].Tool);
        Assert.Equal("vb60", rich.Entries[2].Tool);

        // No linker took part, so there is no linker record to report.
        Assert.DoesNotContain(rich.Entries, e => e.IsLinker);
        Assert.Null(rich.LinkerEntry);
    }

    // --------------------------------------------------------- the Visual Studio 98 tools beside it

    /// <summary>
    /// LINK.EXE, C2.EXE and CVPACK.EXE are the Visual C++ 6.0 linker, compiler and symbol packer.
    /// Each imports MSVCRT.dll, which used to be enough to call them MinGW. They are attributed to
    /// the toolset that built them, and no GCC profile is mentioned at all.
    /// </summary>
    [Theory]
    [InlineData("LINK.EXE")]
    [InlineData("C2.EXE")]
    [InlineData("CVPACK.EXE")]
    public void The_visual_studio_tools_are_not_called_mingw(string name)
    {
        if (Path(name) is null)
        {
            return;
        }

        var registry = BuiltIns();
        var report = ProducerDetector.Detect(Load(name), null, registry);

        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId.StartsWith("gcc", System.StringComparison.Ordinal));
        Assert.Equal("msvc-6", report.Suggestions[0].ProfileId);
    }

    /// <summary>Product id 0x0004 is the Visual Studio 98 linker; these three files carry it.</summary>
    [Fact]
    public void The_visual_studio_linker_record_is_identified_by_its_product_id()
    {
        if (Path("LINK.EXE") is null)
        {
            return;
        }

        var pe = Assert.IsType<PeBinaryImage>(Load("LINK.EXE")).Pe;
        Assert.NotNull(pe.Rich);

        var linkers = pe.Rich!.Entries.Where(e => e.IsLinker).ToList();
        Assert.Contains(linkers, e => e.ProdId == 0x0004 && e.Build == 8447);

        // The last record is the resource converter, which is why "last means linker" was wrong.
        Assert.Equal("cvtres_5.0", pe.Rich.Entries[^1].Tool);
        Assert.False(pe.Rich.Entries[^1].IsLinker);
    }

    // -------------------------------------------------------------------------------------- scaling

    /// <summary>
    /// Inventorying these files used to take minutes: the prologue scan asked every known function
    /// about every byte of code, and the thunk scan asked every instruction whether any other
    /// instruction called it. Both are now lookups, and what is left grows with the size of the
    /// image. The budget is generous — the largest input here is 3.6 MB and finishes in under twenty
    /// seconds on the machine this was measured on, while the quadratic version of the same code was
    /// killed at ten minutes — because what this test guards is the shape of the curve, not the speed
    /// of one machine.
    /// </summary>
    [Fact]
    public void Inventorying_a_real_binary_scales_with_the_size_of_the_image()
    {
        if (InputsDirectory is null)
        {
            return;
        }

        string[] inputs = ["MSO97RT.DLL", "VB6.EXE", "VBA6.DLL", "msvbvm60.dll", "VISDATA.EXE"];
        // The largest input under 2 MB, not simply the largest: decoding a multi-megabyte image holds
        // a few hundred megabytes of instructions, and a test host that shares 2 GB with every other
        // test in the run does not reliably survive the 3.6 MB one. What matters is the size being
        // real — this is the binary that took over ten minutes before the fix and takes seconds now.
        string? largest = inputs
            .Select(Path)
            .Where(p => p is not null && new FileInfo(p!).Length <= 2 * 1024 * 1024)
            .OrderByDescending(p => new FileInfo(p!).Length)
            .FirstOrDefault();
        if (largest is null)
        {
            return;
        }
        long megabytes = Math.Max(1, new FileInfo(largest).Length / (1024 * 1024));
        var stopwatch = Stopwatch.StartNew();
        var document = Inventory(Load(System.IO.Path.GetFileName(largest)));
        stopwatch.Stop();

        Assert.True(document.Functions.Count > 0);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30 * megabytes),
            $"{System.IO.Path.GetFileName(largest)} ({megabytes} MB) took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }
}
