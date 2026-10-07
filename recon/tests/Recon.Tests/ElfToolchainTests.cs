using System.Text;
using Recon.Analysis;
using Recon.Build;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Elf;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M6 slice 3 — the ELF toolchain profiles. The first two slices taught the tool to read and analyse
/// an ELF file; these are the profiles that say <em>which</em> compiler produced one and how that
/// compiler would be invoked on this machine. The corpus tests skip when the corpus has not been
/// built, as the other corpus tests do.
/// </summary>
[Collection("cli")]
public class ElfToolchainTests
{
    private static ToolchainRegistry BuiltInProfiles(out Diagnostics diagnostics)
    {
        using var temp = new TempDir("recon-elf-profiles");
        string directory = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        Recon.Toolchains.BuiltInProfiles.WriteTo(directory);
        diagnostics = new Diagnostics();
        return ToolchainRegistry.Load([directory], diagnostics);
    }

    /// <summary>
    /// Loads a corpus binary the way the CLI does: the DWARF sections are read once, and the symbol
    /// table is the debug source when DWARF says nothing. Detection reads the producer strings out of
    /// both, so a test that skipped the DWARF would be measuring a weaker analysis than the real one.
    /// </summary>
    private static (IBinaryImage Image, DebugInfo.DebugInfoResult? Debug, DebugInfo.DwarfInfo? Dwarf) Load(string name)
    {
        var result = ImageLoader.Load(TestPaths.ElfCorpus(name));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        var image = result.Image!;
        var dwarf = DwarfReader.Read(image, result.Bytes);
        var symbols = ElfSymbols.Read(image);
        var debug = symbols is { FunctionCount: > 0 } || dwarf is null ? symbols : DwarfSymbols.Read(image, dwarf);
        return (image, debug, dwarf);
    }

    // ----------------------------------------------------------------------- the profiles themselves

    [Fact]
    public void The_shipped_set_covers_the_three_elf_targets()
    {
        var registry = BuiltInProfiles(out var diagnostics);

        Assert.Empty(diagnostics.Errors);

        var gcc64 = registry.GetResolved("gcc-14-elf64");
        Assert.NotNull(gcc64);
        Assert.Contains(gcc64!.Targets, t => t.Format == "elf64" && t.Arch == "x64");
        Assert.Equal(8, gcc64.Abi.PointerSize);
        Assert.Equal("itanium", gcc64.Abi.Mangling);
        // x86-64 passes arguments in registers; none of the 32-bit names is true of it.
        Assert.Equal("sysv", gcc64.Abi.DefaultCc);

        var gcc32 = registry.GetResolved("gcc-14-elf32");
        Assert.NotNull(gcc32);
        Assert.Contains(gcc32!.Targets, t => t.Format == "elf32" && t.Arch == "x86");
        Assert.Equal(4, gcc32.Abi.PointerSize);
        Assert.Equal("cdecl", gcc32.Abi.DefaultCc);
        Assert.Contains("-m32", gcc32.Compile!.DefaultFlags);

        var clang = registry.GetResolved("clang-19-elf64");
        Assert.NotNull(clang);
        Assert.Contains(clang!.Targets, t => t.Format == "elf64" && t.Arch == "x64");
        Assert.Equal("gcc-base", clang.Extends);
        Assert.Equal("clang", clang.Family);
    }

    [Fact]
    public void The_elf_profiles_know_that_a_64_bit_compiler_emits_different_prologues()
    {
        var registry = BuiltInProfiles(out _);

        var hints = registry.GetResolved("gcc-14-elf64")!.Codegen.PrologueHints;

        // The 32-bit frame pointer sequence would match nothing in a 64-bit binary.
        Assert.Contains("push rbp; mov rbp, rsp", hints);
        Assert.DoesNotContain("push ebp; mov ebp, esp", hints);
        Assert.Contains(hints, h => h.Contains("endbr64", StringComparison.Ordinal));
        Assert.All(hints, h => Assert.NotEmpty(ProloguePatterns.Parse(h)));
    }

    [Fact]
    public void No_elf_profile_matches_on_evidence_only_gcc_on_windows_leaves()
    {
        var registry = BuiltInProfiles(out _);

        // A MinGW profile must not claim a Linux binary: it asks for an msvcrt import, which an ELF
        // file does not have, so the same "GCC: (" comment is not enough to match it.
        var mingw = registry.GetResolved("gcc-14-mingw")!;
        Assert.All(mingw.Targets, t => Assert.Equal("pe32", t.Format));

        var elf = registry.GetResolved("gcc-14-elf64")!;
        Assert.DoesNotContain(elf.Detect.ImportDll, rule => rule.Contains.Contains("msvcrt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(elf.Detect.ImportDll, rule => rule.Contains.Contains("libc.so", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------- the hint vocabulary

    [Fact]
    public void The_prologue_vocabulary_covers_both_widths()
    {
        Assert.Equal([0x55, 0x48, 0x89, 0xE5], ProloguePatterns.Parse("push rbp; mov rbp, rsp")[0]);
        Assert.Equal([0x53], ProloguePatterns.Parse("push rbx")[0]);
        Assert.Equal([0x41, 0x54], ProloguePatterns.Parse("push r12")[0]);
        Assert.Equal([0x48, 0x83], ProloguePatterns.Parse("sub rsp, imm")[0]);
        Assert.Equal([0xF3, 0x0F, 0x1E, 0xFA], ProloguePatterns.Parse("endbr64")[0]);

        Assert.Equal([0x55, 0x8B, 0xEC], ProloguePatterns.Parse("push ebp; mov ebp, esp")[0]);
        Assert.Equal([0x83], ProloguePatterns.Parse("sub esp, imm")[0]);
    }

    [Fact]
    public void A_hint_the_vocabulary_does_not_know_matches_nothing()
    {
        Assert.Empty(ProloguePatterns.Parse("mov rax, rax"));
        Assert.Empty(ProloguePatterns.Parse(string.Empty));

        // The default follows the width of the image: the 32-bit sequence is not what a 64-bit
        // compiler emits, and guessing it would find nothing and look like an empty binary.
        Assert.Equal("push rbp; mov rbp, rsp", ProloguePatterns.Default(is64: true));
        Assert.Equal("push ebp; mov ebp, esp", ProloguePatterns.Default(is64: false));
    }

    // ------------------------------------------------------------------ detection over the ELF corpus

    [Fact]
    public void A_gcc_elf_binary_is_attributed_to_the_gcc_elf_profile()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-release"))
        {
            return;
        }

        var registry = BuiltInProfiles(out _);
        var (image, debug, dwarf) = Load("sample-elf64-release");
        var report = ProducerDetector.Detect(image, debug, registry, dwarf);

        Assert.NotEmpty(report.Suggestions);
        Assert.Equal("gcc-14-elf64", report.Suggestions[0].ProfileId);
        Assert.Contains("comment_section", report.Suggestions[0].Evidence);

        // `tools/build-elf-corpus.sh` builds the corpus with the gcc on PATH, so the binary under this
        // test is not always a GCC 14 build while the profile is: the `.comment` rule matches the
        // family either way, but the profile's DWARF-producer rules name the version, so a 12 or a 13
        // comes back on one kind of evidence ("medium") rather than two ("high"). The version is read
        // from the binary's own comment string rather than assumed from the host, which is what makes
        // this hold on a machine whose default compiler is some other GCC.
        string comment = image.Elf?.CommentStrings.FirstOrDefault(c => c.StartsWith("GCC:", StringComparison.Ordinal)) ?? string.Empty;
        string version = comment[(comment.LastIndexOf(')') + 1)..].Trim();
        Assert.Equal(
            version.StartsWith("14.", StringComparison.Ordinal) ? "high" : "medium",
            report.Suggestions[0].Confidence);
    }

    [Fact]
    public void A_clang_elf_binary_is_attributed_to_clang()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-clang"))
        {
            return;
        }

        var registry = BuiltInProfiles(out _);
        var (image, debug, dwarf) = Load("sample-elf64-clang");
        var report = ProducerDetector.Detect(image, debug, registry, dwarf);

        Assert.NotEmpty(report.Suggestions);
        Assert.Equal("clang-19-elf64", report.Suggestions[0].ProfileId);

        // A clang binary on a GNU system carries a GCC comment too, because it links GCC's startup
        // files. It must still be attributed to clang, which is what the producer string says.
        Assert.Contains("gcc-14-elf64", report.Suggestions.Select(s => s.ProfileId));
    }

    [Fact]
    public void A_32_bit_elf_binary_is_attributed_to_the_32_bit_profile()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf32-release"))
        {
            return;
        }

        var registry = BuiltInProfiles(out _);
        var (image, debug, dwarf) = Load("sample-elf32-release");
        var report = ProducerDetector.Detect(image, debug, registry, dwarf);

        Assert.NotEmpty(report.Suggestions);
        Assert.Equal("gcc-14-elf32", report.Suggestions[0].ProfileId);
        Assert.All(report.Suggestions, s => Assert.DoesNotContain("elf64", s.ProfileId));
    }

    [Fact]
    public void A_stripped_binary_prefers_the_profile_that_matched_more_evidence()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-stripped"))
        {
            return;
        }

        var registry = BuiltInProfiles(out _);
        var (image, debug, dwarf) = Load("sample-elf64-stripped");
        var report = ProducerDetector.Detect(image, debug, registry, dwarf);

        // Stripping leaves the .comment section and the dynamic imports but takes the DWARF, so both
        // ELF profiles match at the same confidence. The one that matched more kinds of evidence —
        // the GCC comment survives — wins, rather than whichever id sorts first.
        Assert.NotEmpty(report.Suggestions);
        var top = report.Suggestions[0];
        Assert.Equal("gcc-14-elf64", top.ProfileId);
        Assert.Contains("comment_section", top.Evidence);
    }

    // ------------------------------------------------------------------------- choosing the profile

    [Fact]
    public void The_project_named_toolchain_wins_over_detection()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-release"))
        {
            return;
        }

        var registry = BuiltInProfiles(out _);
        var (image, debug, dwarf) = Load("sample-elf64-release");

        // detection says gcc-14-elf64; the project says otherwise, and that is an instruction.
        var chosen = ToolchainSelector.Choose(registry, "clang-19-elf64", image, debug, dwarf);
        Assert.NotNull(chosen);
        Assert.Equal("clang-19-elf64", chosen!.Id);
    }

    [Fact]
    public void Without_a_named_toolchain_the_detected_one_is_analysed_with()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-release"))
        {
            return;
        }

        var registry = BuiltInProfiles(out _);
        var (image, debug, dwarf) = Load("sample-elf64-release");

        var chosen = ToolchainSelector.Choose(registry, null, image, debug, dwarf);
        Assert.NotNull(chosen);
        Assert.Equal("gcc-14-elf64", chosen!.Id);
    }

    [Fact]
    public void A_binary_with_no_evidence_still_gets_a_profile_of_its_format()
    {
        using var temp = new TempDir("recon-elf-select");
        string directory = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "only.toml"), """
            schema_version = 1
            id = "only-elf64"
            display_name = "the only ELF profile"
            family = "gcc"

            [[targets]]
            format = "elf64"
            arch = "x64"

            [compile]
            exe = "cc"
            output_flag = "-o {obj}"

            [link]
            exe = "cc"
            output_flag = "-o {exe}"
            """);

        var registry = ToolchainRegistry.Load([directory], new Diagnostics());
        var image = ImageLoader.LoadBytes(SyntheticElf.Build(new SyntheticElfOptions { Comment = null }), "fixture.elf").Image!;

        // Nothing about this file matches a rule, but the format still has a profile, and using it
        // means padding bytes, alignment and pointer size come from somewhere real.
        var chosen = ToolchainSelector.Choose(registry, null, image);
        Assert.NotNull(chosen);
        Assert.Equal("only-elf64", chosen!.Id);
    }

    /// <summary>
    /// <c>recon toolchain check</c> answers "can this machine build with this profile", and an install
    /// directory is only one way to have a compiler. A toolchain installed by the system has no root,
    /// and calling it broken is what made the check useless for every Linux profile.
    /// </summary>
    [Fact]
    public void A_toolchain_with_no_install_root_checks_out_when_its_tools_are_on_path()
    {
        if (!ToolDetection.Exists("gcc") || !TestPaths.ElfCorpusExists("sample-elf64-release"))
        {
            return;
        }

        using var temp = new TempDir("recon-elf-toolchain-check");
        string profiles = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(profiles);
        Recon.Toolchains.BuiltInProfiles.WriteTo(profiles);

        string binary = TestPaths.ElfCorpus("sample-elf64-release");
        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "elf"

            [target]
            format = "elf64"
            arch = "x64"

            [[input]]
            id = "main"
            role = "original"
            file = "sample-elf64-release"
            sha256 = "{PeImage.HashFile(binary)}"

            [paths]
            profiles = ["{profiles.Replace('\\', '/')}"]
            """);

        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.ElfCorpusDirectory.Replace('\\', '/')}"

            [toolchain.gcc-14-elf64]
            """);

        var run = CliRun.Run("toolchain", "check", "gcc-14-elf64", "--project", temp.Path);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("PATH", run.All);
        Assert.Contains("gcc", run.All);
    }

    // ---------------------------------------------------------------------------- tools on this machine

    [Fact]
    public void Local_toml_may_name_a_toolchain_without_an_install_root()
    {
        using var temp = new TempDir();
        string path = temp.Write("local.toml", """
            schema_version = 1

            [inputs]
            dir = "/corpus"

            [toolchain.gcc-14-elf64]
            """);

        var diagnostics = new Diagnostics();
        var local = LocalConfig.Load(path, diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Errors));
        Assert.True(local.Toolchains.ContainsKey("gcc-14-elf64"));
        Assert.True(string.IsNullOrEmpty(local.Toolchains["gcc-14-elf64"].Root));
    }

    [Fact]
    public void A_bare_tool_name_is_found_on_path_when_there_is_no_install()
    {
        using var temp = new TempDir("recon-path-tool");
        var compiler = temp.Write("recon-fake-cc", "binary");
        var profile = Profile("""
            [compile]
            exe = "recon-fake-cc"
            output_flag = "-o {obj}"

            [link]
            exe = "recon-fake-cc"
            output_flag = "-o {exe}"
            """);

        // A Unix toolchain has no install directory: gcc is on PATH. The resolver has to look there,
        // because without a root the old rule produced a path inside the project that never exists.
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(compiler) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        try
        {
            var use = ToolResolver.Resolve(profile, null, temp.Path);

            Assert.Null(use.CcProblem);
            Assert.Equal(compiler, use.Cc);
            Assert.Equal(compiler, use.Link);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator,
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator)
                    .Where(p => p.Length > 0 && p != Path.GetDirectoryName(compiler))));
        }
    }

    [Fact]
    public void An_install_root_still_wins_over_path()
    {
        using var temp = new TempDir("recon-root-tool");
        var gcc = temp.Write("install/bin/recon-fake-cc", "binary");
        var profile = Profile("""
            [compile]
            exe = "bin/recon-fake-cc"
            output_flag = "-o {obj}"

            [link]
            exe = "bin/recon-fake-cc"
            output_flag = "-o {exe}"
            """);

        var use = ToolResolver.Resolve(profile, new LocalToolchain { Root = Path.Combine(temp.Path, "install") }, temp.Path);

        Assert.Null(use.CcProblem);
        Assert.Equal(gcc, use.Cc);
    }

    private static ToolchainProfile Profile(string toml)
    {
        var text = new StringBuilder();
        text.AppendLine("schema_version = 1");
        text.AppendLine("id = \"test\"");
        text.AppendLine("display_name = \"test\"");
        text.AppendLine("family = \"gcc\"");
        text.AppendLine();
        text.AppendLine("[[targets]]");
        text.AppendLine("format = \"elf64\"");
        text.AppendLine("arch = \"x64\"");
        text.AppendLine();
        text.Append(toml);

        var diagnostics = new Diagnostics();
        var document = Recon.Toml.TomlParser.Parse(text.ToString(), "test.toml");
        Assert.Empty(diagnostics.Errors);
        return ToolchainProfile.Load(document, "test.toml", diagnostics);
    }
}
