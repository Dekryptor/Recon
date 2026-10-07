using System.Text.Json;
using Recon.Analysis;
using Recon.Config;
using Recon.Images;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M6's last slice — the Visual Basic 6 profile (plan §3.1: "Clang and VB6 native-code profiles").
/// These tests run against the synthetic PE carrying the one thing a VB6 program always carries: an
/// import of the Visual Basic virtual machine. What they pin is the attribution — that the tool says
/// "Visual Basic 6" for such a file, that it does not say it for one that merely looks like MSVC
/// output, and that the profile is the MSVC family with Visual Basic on top rather than a stub that
/// matches a string. Real VB6 binaries are the business of <see cref="RealVb6Tests"/>, which runs
/// when a copy of them is on the machine and skips when it is not.
/// </summary>
[Collection("cli")]
public class Vb6ToolchainTests
{
    private static ToolchainRegistry BuiltIns(out Diagnostics diagnostics)
    {
        using var temp = new TempDir("recon-vb6-profiles");
        string directory = Path.Combine(temp.Path, "toolchains");
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
    /// A VB6 program: linked against MSVBVM60.DLL, with the Visual Studio 98 linker's 6.0 stamp, no
    /// <c>.comment</c> and no PDB, because the VB6 compiler writes neither. The fixture's Rich header
    /// is off — it is a fake VS2008 toolset, which is evidence for a different profile.
    /// </summary>
    private static SyntheticPeOptions Vb6()
        => new() { Vb6Runtime = true, LinkerMajor = 6, LinkerMinor = 0, RichHeader = false, Comment = null };

    [Fact]
    public void The_profile_loads_and_is_the_msvc_family_with_visual_basic_on_top()
    {
        var registry = BuiltIns(out var diagnostics);
        Assert.Empty(diagnostics.Errors);

        var profile = registry.GetResolved("vb6-native");
        Assert.NotNull(profile);

        // It is not a string that matches a DLL name: it is the MSVC ABI, padding and exception
        // model, because VB6's compiler is the Visual C++ back end.
        Assert.Equal("msvc", profile!.Family);
        Assert.Equal(["msvc-base", "vb6-native"], profile.InheritanceChain);
        Assert.Equal("msvc", profile.Abi.Mangling);
        Assert.Equal([0xCC], profile.Codegen.PaddingBytes);
        Assert.Equal("msvc-seh", profile.Eh.Model);
        Assert.Contains(profile.Targets, t => t.Format == "pe32" && t.Arch == "x86");

        // A VB6 program carries no debug information of its own, which is why a pattern file
        // (recon sigs) is the practical way to name what is inside one.
        Assert.Equal("none", profile.Debug.Format);
    }

    [Fact]
    public void The_visual_basic_runtime_names_the_profile()
    {
        var registry = BuiltIns(out _);
        var report = ProducerDetector.Detect(Load(Vb6()), null, registry);

        // The linker stamp is 6.0 — the Visual Studio 98 linker VB6 links through — so the C++
        // toolset of that release is named as well. It is the runner-up, not the answer: Visual
        // Basic has both the stamp and the runtime, the toolset has only the stamp.
        var suggestion = report.Suggestions[0];
        Assert.Equal("vb6-native", suggestion.ProfileId);
        Assert.Contains("import_dll", suggestion.Evidence);
        Assert.Contains(
            report.Suggestions,
            s => s.ProfileId == "msvc-6" && s.Evidence.Count < suggestion.Evidence.Count);
        Assert.Contains(
            report.Evidence,
            e => e.Kind == "import_dll" && string.Equals(e.Detail, "MSVBVM60.DLL", System.StringComparison.Ordinal));
    }

    [Fact]
    public void The_inventory_is_analysed_with_it()
    {
        var registry = BuiltIns(out _);
        var image = Load(Vb6());

        // What the inventory actually runs: the selector, not the detector.
        var chosen = ToolchainSelector.Choose(registry, configuredId: null, image);
        Assert.NotNull(chosen);
        Assert.Equal("vb6-native", chosen!.Id);
    }

    [Fact]
    public void A_binary_without_the_visual_basic_runtime_is_not_called_visual_basic()
    {
        var registry = BuiltIns(out _);

        // The same file, importing KERNEL32 instead: ordinary Microsoft output, no Visual Basic.
        var report = ProducerDetector.Detect(Load(null), null, registry);

        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "vb6-native");
    }

    [Fact]
    public void A_stronger_signal_still_beats_the_runtime_import()
    {
        var registry = BuiltIns(out _);

        // The fixture's default linker stamp is 9.0 — a Visual C++ 2008 linker — and the file also
        // imports the VB6 runtime, which no real binary does. Asked to choose, the tool prefers the
        // linker stamp and still *reports* Visual Basic as the weaker possibility, because an import
        // is one kind of evidence and the profile it names is a claim about the whole binary.
        var options = new SyntheticPeOptions { Vb6Runtime = true, RichHeader = false, Comment = null };
        var report = ProducerDetector.Detect(Load(options), null, registry);

        Assert.Equal("msvc-2008", report.Suggestions[0].ProfileId);
        Assert.Contains(report.Suggestions, s => s.ProfileId == "vb6-native");
        Assert.Equal("low", Assert.Single(report.Suggestions, s => s.ProfileId == "vb6-native").Confidence);
    }

    // ----------------------------------------------------------------- the p-code profile

    /// <summary>
    /// <c>vb6-pcode</c> is the same toolchain with the other compilation mode: it extends
    /// <c>vb6-native</c>, so the ABI, the padding, the linker behaviour and the (absent) debug
    /// information are the ones a Visual Basic 6 program has, and what it adds is the one statement
    /// that separates the two — the kind of program it describes.
    ///
    /// It has no <c>[compile]</c> section and no <c>[link]</c> section, and that is not an omission:
    /// nothing here can rebuild a p-code program, because the compiler that produced it is an IDE on
    /// Windows and what it emits is interpreter input. A profile with no optimization levels is also
    /// what <c>recon permute --flags</c> reports when it has no flags to try.
    /// </summary>
    [Fact]
    public void The_p_code_profile_is_the_vb6_toolchain_with_the_other_code_kind()
    {
        var registry = BuiltIns(out var diagnostics);
        Assert.Empty(diagnostics.Errors);

        var profile = registry.GetResolved("vb6-pcode");
        Assert.NotNull(profile);

        Assert.Equal("pcode", profile!.Vb6CodeKind);
        Assert.Equal("Visual Basic 6.0 (p-code)", profile.DisplayName);
        Assert.Equal(["msvc-base", "vb6-native", "vb6-pcode"], profile.InheritanceChain);
        Assert.Equal("msvc", profile.Family);
        Assert.Equal("msvc", profile.Abi.Mangling);
        Assert.Equal("none", profile.Debug.Format);
        Assert.Empty(profile.OptimizationLevels);
        Assert.Null(profile.Compile);

        // The linker section it inherits stays, and it is not the same kind of statement: the program
        // really was linked, by the Visual Studio 98 linker, and those flags describe how. What is
        // absent is the compiler — the part nothing here could ever re-run.
        Assert.Equal("/OUT:{exe}", profile.Link!.OutputFlag);

        // And the profile it extends is the one that describes the other kind, so the two are a pair
        // rather than one rule list that happens to match both.
        Assert.Equal("native", registry.GetResolved("vb6-native")!.Vb6CodeKind);
    }

    /// <summary>
    /// The header decides, and it decides both ways: a p-code program matches everything
    /// <c>vb6-native</c> matches — the runtime import, the 6.0 linker stamp, the <c>vb60</c> Rich
    /// record — and is nothing like what that profile describes, so the profile is withdrawn rather
    /// than out-ranked. A profile that names a kind of Visual Basic program is suggested for that
    /// kind only.
    /// </summary>
    [Fact]
    public void A_p_code_program_is_attributed_to_the_p_code_profile_and_the_native_one_is_withdrawn()
    {
        var registry = BuiltIns(out _);
        var image = ImageLoader.LoadBytes(SyntheticVb6.Program(0), "synthetic-pcode.exe").Image!;

        var report = ProducerDetector.Detect(image, null, registry);

        Assert.Equal("vb6-pcode", report.Suggestions[0].ProfileId);
        Assert.Contains("vb_header", report.Suggestions[0].Evidence);
        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "vb6-native");

        // Withdrawn means gone, not merely ranked lower: no piece of evidence may still name it, or
        // a reader of the evidence table would see the claim the header just refuted.
        Assert.DoesNotContain(
            report.Evidence.SelectMany(e => e.Matches),
            id => id == "vb6-native");

        var header = Assert.Single(report.Evidence, e => e.Kind == "vb_header");
        Assert.Contains("p-code", header.Detail);
        Assert.Equal(["vb6-pcode"], header.Matches);

        // Which is also what the selector the inventory uses resolves to.
        Assert.Equal("vb6-pcode", ToolchainSelector.Choose(registry, null, image)!.Id);
    }

    [Fact]
    public void A_native_program_is_still_attributed_to_the_native_profile()
    {
        var registry = BuiltIns(out _);
        var image = ImageLoader.LoadBytes(SyntheticVb6.Program(0x2000), "synthetic-native.exe").Image!;

        var report = ProducerDetector.Detect(image, null, registry);

        Assert.Equal("vb6-native", report.Suggestions[0].ProfileId);
        Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "vb6-pcode");
        Assert.DoesNotContain(report.Evidence.SelectMany(e => e.Matches), id => id == "vb6-pcode");
        Assert.Contains("native code", Assert.Single(report.Evidence, e => e.Kind == "vb_header").Detail);
        Assert.Equal("vb6-native", ToolchainSelector.Choose(registry, null, image)!.Id);
    }

    /// <summary>
    /// A file that named both kinds would be chosen for a Visual Basic program whichever way it was
    /// compiled, which is the claim the rule exists to prevent. Profiles that say nothing about
    /// Visual Basic are untouched by any of this.
    /// </summary>
    [Fact]
    public void A_profile_that_names_both_kinds_of_visual_basic_program_is_rejected()
    {
        var diagnostics = new Diagnostics();
        var document = Recon.Toml.TomlParser.Parse(
            "schema_version = 1\nid = \"both\"\ndisplay_name = \"Both\"\nfamily = \"msvc\"\n\n"
            + "[[targets]]\nformat = \"pe32\"\narch = \"x86\"\n\n"
            + "[[detect.vb6_header]]\nkind = \"pcode\"\n\n"
            + "[[detect.vb6_header]]\nkind = \"native\"\n",
            "both.toml");

        var profile = ToolchainProfile.Load(document, "both.toml", diagnostics);

        Assert.Equal("pcode", profile.Vb6CodeKind);
        Assert.Contains(diagnostics.Errors, e => e.Message.Contains("one kind of Visual Basic program"));
    }

    /// <summary>
    /// The corpus: 42 p-code programs built by the Visual Basic 6 IDE on a Windows XP host
    /// (<c>RECON_PCODE_INPUTS</c>; the test returns without doing anything when it is not there).
    ///
    /// Each one is attributed to <c>vb6-pcode</c> — never to <c>vb6-native</c> — and each one states
    /// the runtime build it was compiled against twice: as the single <c>vb60</c> record in its Rich
    /// header and as its own header's <c>wRuntimeBuild</c>. The two agree in all 42, which is why this
    /// is checked rather than assumed. `msvc-6` is still reported below `vb6-pcode` for the reason the
    /// linker stamp alone gives — see the test above.
    /// </summary>
    [Fact]
    public void Every_p_code_program_in_the_corpus_is_attributed_to_the_p_code_profile()
    {
        string? corpus = Environment.GetEnvironmentVariable("RECON_PCODE_INPUTS");
        if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus))
        {
            return;
        }

        var registry = BuiltIns(out _);
        var programs = Directory.EnumerateFiles(corpus, "*.exe", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Contains("source"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(42, programs.Count);

        foreach (string program in programs)
        {
            var loaded = ImageLoader.Load(program);
            Assert.True(loaded.Ok, $"{program}: {string.Join("; ", loaded.Problems)}");
            var image = loaded.Image!;

            Assert.Equal("vb6-pcode", image.Isa);
            var report = ProducerDetector.Detect(image, null, registry);
            Assert.True(report.Suggestions[0].ProfileId == "vb6-pcode",
                $"{program}: attributed to {string.Join(", ", report.Suggestions.Select(s => s.ProfileId))}");
            Assert.DoesNotContain(report.Suggestions, s => s.ProfileId == "vb6-native");
            Assert.Equal("vb6-pcode", ToolchainSelector.Choose(registry, null, image)!.Id);

            // One Rich record, and its build number is the one the program's own header states.
            var vb = image.Pe!.Vb6!;
            var record = Assert.Single(image.Pe!.Rich!.Entries);
            Assert.Equal(0x0Du, record.ProdId);
            Assert.Equal(1u, record.Count);
            Assert.Equal(vb.RuntimeBuild, record.Build);
        }
    }

    /// <summary>
    /// What <c>recon pcode</c> says about the two runtime builds, checked through the command on one
    /// of those programs because that is where the numbers are printed: the program asks for the build
    /// its header names, the file in front of it reports its own, and the report carries both rather
    /// than one number that could be taken for the other. The runtime the corpus is decoded with here
    /// is 6.00.9848 while the programs were built against 9782 — a service-packed DLL updated on its
    /// own schedule, which is why the two are reported side by side and the line says which is which.
    /// </summary>
    [Fact]
    public void The_runtime_build_the_program_asks_for_and_the_ones_the_file_reports_are_both_printed()
    {
        string? corpus = Environment.GetEnvironmentVariable("RECON_PCODE_INPUTS");
        string? inputs = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
        string? runtime = inputs is null ? null : Path.Combine(inputs, "msvbvm60.dll");
        if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus) || runtime is null || !File.Exists(runtime))
        {
            return;
        }

        string program = Directory.EnumerateFiles(corpus, "*.exe", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Contains("source"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .First();

        var loaded = ImageLoader.Load(program);
        ushort wanted = loaded.Image!.Pe!.Vb6!.RuntimeBuild;

        var run = CliRun.Run("pcode", program, "--runtime", runtime, "--json");
        Assert.True(run.ExitCode == 0, run.All);
        using var document = JsonDocument.Parse(run.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(wanted, root.GetProperty("runtime_build_the_program_was_built_against").GetInt32());
        int? fromFile = root.GetProperty("runtime_file_build").GetInt32();
        Assert.NotNull(fromFile);

        // Both numbers, and which one is which, in the line a reader sees.
        var text = CliRun.Run("pcode", program, "--runtime", runtime);
        Assert.True(text.ExitCode == 0, text.All);
        Assert.Contains($"the program was built against runtime build {wanted}", text.StandardOutput);
        Assert.Contains($"{root.GetProperty("runtime_version").GetString()}", text.StandardOutput);
    }

    [Fact]
    public void The_runtime_import_is_what_the_file_says_and_nothing_else()
    {
        var registry = BuiltIns(out _);
        var image = Load(Vb6());

        // The import the whole case rests on, read back out of the file rather than assumed.
        Assert.Contains(image.Imports, i => i.Module.Equals("MSVBVM60.DLL", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(image.Imports, i => string.Equals(i.Name, "__vbaStrCopy", System.StringComparison.Ordinal));
    }
}
