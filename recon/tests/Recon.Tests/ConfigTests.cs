using Recon.Config;
using Recon.Tests.Fixtures;
using Recon.Toml;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

public class ProjectConfigTests
{
    private const string SomeHash = "619dbd547efca1f31c87bc2675c8be957330bdbf9fd5ffaea614d7615b095212";

    private const string ValidProject = """
        schema_version = 1

        [project]
        name = "sample"
        description = "Reconstruction of sample.exe"

        [target]
        format = "pe32"
        arch = "x86"
        default_toolchain = "msvc-2008"

        [[input]]
        id = "main"
        role = "original"
        file = "sample.exe"
        sha256 = "619dbd547efca1f31c87bc2675c8be957330bdbf9fd5ffaea614d7615b095212"

        [[input]]
        id = "symbols"
        role = "debug"
        file = "sample.pdb"
        sha256 = "619dbd547efca1f31c87bc2675c8be957330bdbf9fd5ffaea614d7615b095212"

        [paths]
        source = "src"
        include = ["include", "vendor/include"]

        [analysis]
        min_function_confidence = "medium"
        extra_entry_points = [5120]
        no_return = ["abort", "exit"]

        [[analysis.data_range]]
        rva = 172032
        size = 256
        kind = "rdata"

        [report]
        output = "build/report"
        history = true
        """;

    [Fact]
    public void Loads_a_complete_project_file()
    {
        using var temp = new TempDir();
        string path = temp.Write("project.toml", ValidProject);
        var diagnostics = new Diagnostics();

        var project = ProjectConfig.Load(path, diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Items));
        Assert.Equal("sample", project.Project.Name);
        Assert.Equal("pe32", project.Target.Format);
        Assert.Equal("x86", project.Target.Arch);
        Assert.Equal("msvc-2008", project.Target.DefaultToolchain);
        Assert.Equal(2, project.Inputs.Count);
        Assert.Equal("main", project.Inputs[0].Id);
        Assert.Equal("original", project.Inputs[0].Role);
        Assert.Equal("619", project.Inputs[0].Sha256[..3]);
        Assert.Equal(["include", "vendor/include"], project.Paths.Include);
        Assert.Equal("build/report", project.Report.Output);
        Assert.True(project.Report.History);
        Assert.Equal("medium", project.Analysis.MinFunctionConfidence);
        Assert.Contains(0x1400u, project.Analysis.ExtraEntryPoints);
        Assert.Contains("abort", project.Analysis.NoReturn);
        Assert.Single(project.Analysis.DataRanges);
        Assert.Equal(0x2A000u, project.Analysis.DataRanges[0].Rva);
        Assert.Equal(temp.Path, project.RootDirectory);
    }

    [Fact]
    public void Reports_missing_required_keys_with_their_key_path()
    {
        using var temp = new TempDir();
        string path = temp.Write("project.toml", """
            schema_version = 1

            [project]
            description = "no name"
            """);
        var diagnostics = new Diagnostics();

        ProjectConfig.Load(path, diagnostics);

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.Errors, d => d.KeyPath == "project.name" && d.Message.Contains("missing"));
    }

    [Fact]
    public void Rejects_unknown_keys()
    {
        using var temp = new TempDir();
        string path = temp.Write("project.toml", """
            schema_version = 1

            [project]
            name = "sample"
            nickname = "typo"

            [target]
            format = "pe32"
            arch = "x86"

            [[input]]
            id = "main"
            role = "original"
            file = "sample.exe"
            sha256 = "619dbd547efca1f31c87bc2675c8be957330bdbf9fd5ffaea614d7615b095212"
            """);
        var diagnostics = new Diagnostics();

        ProjectConfig.Load(path, diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.KeyPath == "project.nickname" && d.Message.Contains("unknown key"));
    }

    [Fact]
    public void Rejects_values_outside_the_allowed_set()
    {
        using var temp = new TempDir();
        string path = temp.Write("project.toml", """
            schema_version = 1

            [project]
            name = "sample"

            [target]
            format = "pe33"
            arch = "x86"

            [[input]]
            id = "main"
            role = "originalish"
            file = "sample.exe"
            sha256 = "619dbd547efca1f31c87bc2675c8be957330bdbf9fd5ffaea614d7615b095212"
            """);
        var diagnostics = new Diagnostics();

        ProjectConfig.Load(path, diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.KeyPath.Contains("format"));
        Assert.Contains(diagnostics.Errors, d => d.KeyPath.Contains("role"));
    }

    [Fact]
    public void Requires_an_original_input_and_a_target()
    {
        using var temp = new TempDir();
        string path = temp.Write("project.toml", """
            schema_version = 1

            [project]
            name = "sample"
            """);
        var diagnostics = new Diagnostics();

        ProjectConfig.Load(path, diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.KeyPath == "target" || d.KeyPath.StartsWith("target."));
        Assert.Contains(diagnostics.Errors, d => d.Message.Contains("original"));
    }

    [Fact]
    public void Reports_syntax_errors_as_configuration_errors()
    {
        using var temp = new TempDir();
        string path = temp.Write("project.toml", "name = \"unfinished\n");

        var error = Assert.Throws<ConfigException>(() => ProjectConfig.Load(path, new Diagnostics()));
        Assert.Contains("project.toml", error.Message);
    }

    [Fact]
    public void Local_config_defaults_when_the_file_is_absent()
    {
        using var temp = new TempDir();
        var local = LocalConfig.Load(Path.Combine(temp.Path, "local.toml"), new Diagnostics());

        Assert.Equal("inputs", local.InputsDir);
        Assert.Empty(local.Toolchains);
    }

    [Fact]
    public void Local_config_reads_the_inputs_directory_and_toolchain_roots()
    {
        using var temp = new TempDir();
        string path = temp.Write("local.toml", """
            schema_version = 1

            [inputs]
            dir = "D:/corpus"

            [toolchain.msvc-2008]
            root = "C:/tools/msvc2008"
            env = { SDK = "C:/tools/winsdk61" }
            """);
        var diagnostics = new Diagnostics();

        var local = LocalConfig.Load(path, diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Items));
        Assert.Equal("D:/corpus", local.InputsDir);
        Assert.True(local.Toolchains.ContainsKey("msvc-2008"));
        Assert.Equal("C:/tools/msvc2008", local.Toolchains["msvc-2008"].Root);
        Assert.Equal("C:/tools/winsdk61", local.Toolchains["msvc-2008"].Env["SDK"]);
    }
}

public class ToolchainProfileTests
{
    private static ToolchainRegistry LoadBuiltIns(TempDir temp, out Diagnostics diagnostics)
    {
        string directory = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        BuiltInProfiles.WriteTo(directory);
        diagnostics = new Diagnostics();
        return ToolchainRegistry.Load([directory], diagnostics);
    }

    [Fact]
    public void Ships_profiles_that_all_validate()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out var diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Errors));
        Assert.Empty(diagnostics.Errors);
        Assert.Equal(
            [
                "clang-19-elf64", "clang-19-macho64", "clang-19-msvc", "gcc-13-mingw", "gcc-14-elf32", "gcc-14-elf64",
                "gcc-14-mingw", "gcc-4_8-mingw", "gcc-base", "msvc-2008", "msvc-2010", "msvc-5",
                "msvc-6", "msvc-base", "vb6-native", "vb6-pcode",
            ],
            registry.KnownIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Inheriting_profiles_take_targets_from_their_parent()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out _);

        var msvc2010 = registry.GetResolved("msvc-2010");
        Assert.NotNull(msvc2010);
        Assert.Equal("msvc-2010", msvc2010!.Id);
        Assert.Contains(msvc2010.Targets, t => t.Format == "pe32" && t.Arch == "x86");
        Assert.Equal(["msvc-base", "msvc-2008", "msvc-2010"], msvc2010.InheritanceChain);
    }

    [Fact]
    public void A_child_overrides_what_it_lists_and_keeps_the_rest()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out _);

        var baseProfile = registry.GetResolved("msvc-base")!;
        var child = registry.GetResolved("msvc-2008")!;

        Assert.Equal(baseProfile.Abi.Mangling, child.Abi.Mangling);
        Assert.NotEqual(baseProfile.DisplayName, child.DisplayName);
        Assert.Contains(child.Detect.LinkerVersion, rule => rule.Min == "9.0");
        Assert.Contains(child.Detect.RichHeader, rule => rule.Role == "linker");
        Assert.Contains(child.Compile!.DefaultFlags, flag => flag == "/Zi");
    }

    [Fact]
    public void Abstract_profiles_are_not_usable_as_a_unit_toolchain()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out _);
        var diagnostics = new Diagnostics();

        registry.ValidateReference("msvc-base", "project.toml", 12, "unit[0].toolchain", diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.Message.Contains("abstract"));
    }

    [Fact]
    public void Unknown_profile_references_name_the_profiles_that_exist()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out _);
        var diagnostics = new Diagnostics();

        registry.ValidateReference("msvc-1998", "project.toml", 12, "unit[0].toolchain", diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.Message.Contains("unknown profile") && d.Message.Contains("msvc-2008"));
    }

    [Fact]
    public void A_profile_that_extends_itself_is_rejected()
    {
        using var temp = new TempDir();
        string path = temp.Write("toolchains/loop.toml", """
            schema_version = 1
            id = "loop"
            display_name = "Loop"
            family = "msvc"
            extends = "loop"

            [[targets]]
            format = "pe32"
            arch = "x86"
            """);
        var diagnostics = new Diagnostics();

        ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.Message.Contains("extend itself"));
    }

    [Fact]
    public void Inheritance_cycles_are_rejected()
    {
        using var temp = new TempDir();
        temp.Write("toolchains/a.toml", """
            schema_version = 1
            id = "a"
            display_name = "A"
            family = "msvc"
            extends = "b"
            """);
        string path = temp.Write("toolchains/b.toml", """
            schema_version = 1
            id = "b"
            display_name = "B"
            family = "msvc"
            extends = "a"
            """);
        var diagnostics = new Diagnostics();

        var registry = ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics);
        var resolved = registry.GetResolved("a");

        Assert.True(resolved is null || diagnostics.HasErrors);
        Assert.Contains(diagnostics.Errors, d => d.Message.Contains("cycle") || d.Message.Contains("inheritance"));
    }

    [Fact]
    public void A_profile_with_no_parent_must_declare_targets()
    {
        using var temp = new TempDir();
        string path = temp.Write("toolchains/empty.toml", """
            schema_version = 1
            id = "empty"
            display_name = "Empty"
            family = "msvc"
            """);
        var diagnostics = new Diagnostics();

        ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.KeyPath == "targets");
    }

    [Fact]
    public void Profiles_can_come_from_several_directories_and_are_checked_per_binary()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out _);

        var pe32 = registry.All("pe32", "x86").Select(p => p.Id).ToList();
        var pe64 = registry.All("pe64", "x64").Select(p => p.Id).ToList();

        Assert.Contains("msvc-2008", pe32);
        Assert.Empty(pe64);
    }

    [Fact]
    public void A_profile_can_carry_the_compare_settings_of_section_3_1()
    {
        using var temp = new TempDir();
        string path = temp.Write("toolchains/quirky.toml", """
            schema_version = 1
            id = "quirky"
            display_name = "Quirky toolchain"
            family = "gcc"

            [[targets]]
            format = "pe32"
            arch = "x86"

            [codegen]
            padding_bytes = [144]

            [compare]
            similarity_threshold = 0.9
            ignore_padding = false
            difference_limit = 7
            """);
        var diagnostics = new Diagnostics();

        var registry = ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics);
        var profile = registry.GetResolved("quirky");

        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Errors));
        Assert.NotNull(profile);
        Assert.Equal(0.9, profile!.Compare.SimilarityThreshold);
        Assert.False(profile.Compare.IgnorePadding);
        Assert.Equal(7, profile.Compare.DifferenceLimit);
    }

    [Fact]
    public void A_profile_that_says_nothing_about_comparing_leaves_the_defaults_alone()
    {
        using var temp = new TempDir();
        var registry = LoadBuiltIns(temp, out _);
        var profile = registry.GetResolved("msvc-base")!;

        // The shipped base profiles do state their settings, so the empty case needs a profile of its
        // own: what matters is that an unset value is absent rather than zero.
        Assert.Equal(0.80, profile.Compare.SimilarityThreshold);
        Assert.True(profile.Compare.IgnorePadding);
        Assert.Equal(24, profile.Compare.DifferenceLimit);

        string path = temp.Write("toolchains/bare.toml", """
            schema_version = 1
            id = "bare"
            display_name = "Bare"
            family = "gcc"

            [[targets]]
            format = "pe32"
            arch = "x86"
            """);
        var diagnostics = new Diagnostics();
        var bare = ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics).GetResolved("bare")!;

        Assert.Null(bare.Compare.SimilarityThreshold);
        Assert.Null(bare.Compare.IgnorePadding);
        Assert.Null(bare.Compare.DifferenceLimit);
    }

    /// <summary>
    /// M3 asks a profile to say how to run the real tools. The two keys that make a build correct
    /// rather than merely possible are the depfile request and the linker's output flag.
    /// </summary>
    [Fact]
    public void A_profile_says_how_to_ask_for_a_depfile_and_how_to_name_a_linked_image()
    {
        using var temp = new TempDir();
        string path = temp.Write("toolchains/buildable.toml", """
            schema_version = 1
            id = "buildable"
            display_name = "Buildable"
            family = "gcc"

            [[targets]]
            format = "pe32"
            arch = "x86"

            [compile]
            exe = "bin/gcc.exe"
            default_flags = ["-c", "-O2"]
            include_flag = "-I{path}"
            define_flag = "-D{name}"
            output_flag = "-o {obj}"
            depfile_flag = "-MMD -MF {dep}"

            [link]
            exe = "bin/gcc.exe"
            default_flags = ["-Wl,--no-insert-timestamp"]
            output_flag = "-o {exe}"
            """);

        var diagnostics = new Diagnostics();
        var profile = ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics).GetResolved("buildable")!;

        Assert.Empty(diagnostics.Errors);
        Assert.Equal("-MMD -MF {dep}", profile.Compile!.DepfileFlag);
        Assert.Equal("-o {obj}", profile.Compile.OutputFlag);
        Assert.Equal("-o {exe}", profile.Link!.OutputFlag);
        Assert.Equal(["-Wl,--no-insert-timestamp"], profile.Link.DefaultFlags);
    }

    /// <summary>
    /// Tables merge key by key (plan A.4.1), so a child that changes its compiler keeps its parent's
    /// depfile convention instead of silently losing it — which would quietly break the cache.
    /// </summary>
    [Fact]
    public void A_child_profile_keeps_the_compile_keys_it_did_not_set()
    {
        using var temp = new TempDir();
        string directory = temp.PathOf("toolchains");
        temp.Write("toolchains/parent.toml", """
            schema_version = 1
            id = "parent"
            display_name = "Parent"
            family = "gcc"

            [[targets]]
            format = "pe32"
            arch = "x86"

            [compile]
            exe = "bin/gcc.exe"
            default_flags = ["-c", "-g"]
            include_flag = "-I{path}"
            define_flag = "-D{name}"
            output_flag = "-o {obj}"
            depfile_flag = "-MMD -MF {dep}"

            [link]
            exe = "bin/gcc.exe"
            default_flags = ["-Wl,--no-insert-timestamp"]
            output_flag = "-o {exe}"
            """);
        temp.Write("toolchains/child.toml", """
            schema_version = 1
            id = "child"
            display_name = "Child"
            family = "gcc"
            extends = "parent"

            [compile]
            exe = "bin/gcc-4.8.exe"
            """);

        var diagnostics = new Diagnostics();
        var registry = ToolchainRegistry.Load([directory], diagnostics);
        var child = registry.GetResolved("child")!;

        Assert.Empty(diagnostics.Errors);
        Assert.Equal("bin/gcc-4.8.exe", child.Compile!.Exe);
        Assert.Equal("-MMD -MF {dep}", child.Compile.DepfileFlag);
        Assert.Equal("-o {obj}", child.Compile.OutputFlag);
        Assert.Equal(["-c", "-g"], child.Compile.DefaultFlags);
        Assert.Equal("-o {exe}", child.Link!.OutputFlag);
    }

    [Fact]
    public void A_profile_file_with_an_unknown_key_is_rejected()
    {
        using var temp = new TempDir();
        string path = temp.Write("toolchains/typo.toml", """
            schema_version = 1
            id = "typo"
            display_name = "Typo"
            family = "msvc"
            famly = "msvc"

            [[targets]]
            format = "pe32"
            arch = "x86"
            """);
        var diagnostics = new Diagnostics();

        ToolchainRegistry.Load([Path.GetDirectoryName(path)!], diagnostics);

        Assert.Contains(diagnostics.Errors, d => d.KeyPath.Contains("famly"));
    }
}
