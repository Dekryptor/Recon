using System.Text;
using Recon.Build;
using Recon.Config;
using Recon.Pe;
using Recon.Project;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Milestone 3: build orchestration. Shares the "cli" collection with the other command tests,
/// because running the CLI in-process captures Console and two classes doing that at once would
/// capture each other's output.
/// </summary>
/// capture each other's output. The planner is tested without a compiler (it only reads the project
/// and the profiles), the runner with one when MinGW is installed, and the depfile reader on the
/// text GCC actually writes.
/// </summary>
[Collection("cli")]
public class BuildTests
{
    // ------------------------------------------------------------------ dependency files

    [Fact]
    public void A_depfile_names_every_header_the_compiler_read()
    {
        const string text = """
            build/obj/game.o: src/game.c src/game.h \
              include/config.h \
              include/with\ space.h
            """;

        Assert.Equal(
            ["src/game.c", "src/game.h", "include/config.h", "include/with space.h"],
            DepFile.Parse(text));
    }

    [Fact]
    public void A_depfile_with_several_rules_is_read_without_duplicates()
    {
        const string text = """
            a.o: a.c shared.h
            b.o: b.c shared.h
            # a comment, and an empty line follows

            """;

        Assert.Equal(["a.c", "shared.h", "b.c"], DepFile.Parse(text));
    }

    [Fact]
    public void A_depfile_whose_paths_are_absolute_is_left_alone()
    {
        const string text = "/tmp/build/x.o: /tmp/src/x.c\n";
        Assert.Equal(["/tmp/src/x.c"], DepFile.Parse(text));
    }

    // ------------------------------------------------------------------ tool resolution

    [Fact]
    public void A_profile_says_how_to_run_its_tools_and_local_toml_says_where_they_are()
    {
        using var temp = new TempDir("recon-resolve");
        var root = temp.PathOf("install");
        var compiler = temp.Write("install/bin/gcc.exe", "binary");
        var linker = temp.Write("install/bin/ld.exe", "binary");

        var profile = Profile("""
            [compile]
            exe = "bin/gcc.exe"
            default_flags = ["-c"]
            include_flag = "-I{path}"
            define_flag = "-D{name}"
            output_flag = "-o {obj}"
            depfile_flag = "-MMD -MF {dep}"
            env = { CPATH = "{root}/include" }

            [link]
            exe = "bin/ld.exe"
            default_flags = ["--nologo"]
            output_flag = "-o {exe}"
            """);

        var use = ToolResolver.Resolve(profile, new LocalToolchain { Root = root }, root);

        Assert.Equal(compiler, use.Cc);
        Assert.Equal(linker, use.Link);
        Assert.Null(use.CcProblem);
        Assert.Null(use.LinkProblem);
        Assert.Equal(Path.Combine(root, "include"), use.Env["CPATH"]);
        Assert.Equal(".o", use.ObjectExtension);
    }

    /// <summary>
    /// The machine gets the last word: a cross compiler in <c>/usr/bin</c> is used without inventing
    /// a second profile for the same GCC version, which is how this repository tests MinGW on Linux.
    /// </summary>
    [Fact]
    public void Local_toml_can_name_the_compiler_itself()
    {
        using var temp = new TempDir("recon-resolve-override");
        var compiler = temp.Write("cross/gcc", "binary");

        var profile = Profile("""
            [compile]
            exe = "bin/gcc.exe"
            output_flag = "-o {obj}"

            [link]
            exe = "bin/ld.exe"
            output_flag = "-o {exe}"
            """);

        var use = ToolResolver.Resolve(profile, new LocalToolchain { Root = "/nonexistent", Cc = compiler }, temp.Path);

        Assert.Equal(compiler, use.Cc);
        Assert.Null(use.CcProblem);
        Assert.NotNull(use.LinkProblem);
    }

    [Fact]
    public void A_missing_compiler_is_a_problem_the_plan_can_report()
    {
        var profile = Profile("""
            [compile]
            exe = "bin/cl.exe"
            output_flag = "/Fo{obj}"
            """);

        var use = ToolResolver.Resolve(profile, new LocalToolchain { Root = "/nonexistent" }, "/");

        Assert.NotNull(use.CcProblem);
        Assert.Contains("bin/cl.exe", use.CcProblem);
        Assert.Contains("local.toml", use.CcProblem);
    }

    [Fact]
    public void The_toolchain_fingerprint_covers_what_changes_the_output()
    {
        var one = Profile("""
            [compile]
            exe = "bin/gcc.exe"
            default_flags = ["-c", "-O2"]
            """);
        var same = Profile("""
            [compile]
            exe = "bin/gcc.exe"
            default_flags = ["-c", "-O2"]
            """);
        var other = Profile("""
            [compile]
            exe = "bin/gcc.exe"
            default_flags = ["-c", "-O0"]
            """);

        Assert.Equal(ToolResolver.Fingerprint(one), ToolResolver.Fingerprint(same));
        Assert.NotEqual(ToolResolver.Fingerprint(one), ToolResolver.Fingerprint(other));
    }

    // ------------------------------------------------------------------ planning

    [Fact]
    public void Flags_merge_in_the_documented_order()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        var unit = plan.Find("main");

        Assert.NotNull(unit);
        // Profile defaults, then [defaults], then the unit's own flags: later wins, so the unit's
        // -O0 comes last and is what the compiler sees.
        Assert.Equal(["-c", "-g", "-O2", "-Wall", "-O0"], unit.Flags);
        Assert.Equal(["NDEBUG", "GAME_BUILD"], unit.Defines);
    }

    [Fact]
    public void The_compile_command_carries_the_flags_the_object_and_a_depfile()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        var unit = plan.Find("main")!;

        var command = BuildPlanner.CompileCommand(unit, plan);

        Assert.Contains("-o " + BuildPlanner.Slash(unit.ObjectPath), command.Display);
        Assert.EndsWith("-MMD -MF " + BuildPlanner.Slash(unit.DepFilePath), command.Display);
        Assert.Contains("-I" + BuildPlanner.Slash(unit.Includes[0]), command.Display);
        Assert.Contains("-D" + "NDEBUG", command.Display);
        Assert.Contains(BuildPlanner.Slash(unit.SourcePath), command.Display);
    }

    [Fact]
    public void An_object_file_name_comes_from_the_unit_name()
    {
        using var project = new BuildProject("game/update");
        var plan = project.Plan();

        Assert.NotNull(plan.Find("game/update"));
        Assert.Equal("game_update.o", plan.Find("game/update")!.ObjectFileName);
        Assert.Equal("obj", Path.GetFileName(plan.ObjectsDirectory));
    }

    [Fact]
    public void An_unknown_toolchain_stops_the_build_with_the_known_ids_listed()
    {
        using var project = new BuildProject(toolchain: "no-such-toolchain");
        var run = CliRun.Run("build", "--project", project.DirectoryPath);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("unknown profile", run.All);
        Assert.Contains("gcc-13-mingw", run.All);
    }

    [Fact]
    public void An_abstract_profile_cannot_build_a_unit()
    {
        using var project = new BuildProject(toolchain: "gcc-base");
        var run = CliRun.Run("build", "--project", project.DirectoryPath);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("abstract", run.All);
    }

    [Fact]
    public void A_unit_whose_source_is_missing_is_named_in_the_problems()
    {
        using var project = new BuildProject();
        File.Delete(project.PathOf("src/main.c"));
        var plan = project.Plan();

        Assert.Contains(plan.Problems, p => p.Contains("src/main.c"));
    }

    [Fact]
    public void A_unit_that_keeps_the_original_bytes_is_not_compiled()
    {
        using var project = new BuildProject(provider: "rebuilt");
        project.AddUnit("crt", "src/main.c", provider: "original");

        var plan = project.Plan();
        var unit = plan.Find("crt")!;

        Assert.True(unit.IsOriginal);
        Assert.Contains(plan.Warnings, w => w.Contains("provider = original"));

        // Its bytes come from the original image at relink time (M5), so it contributes no object
        // to the link, and the plan says so rather than silently leaving it out.
        Assert.DoesNotContain(BuildPlanner.LinkCommand(plan).Arguments, a => a.EndsWith(unit.ObjectPath, StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_with_no_units_has_nothing_to_build()
    {
        using var project = new BuildProject(units: false);
        var plan = project.Plan();

        Assert.Contains(plan.Problems, p => p.Contains("no [[unit]] entries"));
        Assert.False(plan.Links);
    }

    [Fact]
    public void The_link_step_uses_the_profile_linker_and_its_output_flag()
    {
        using var project = new BuildProject();
        var plan = project.Plan();

        Assert.True(plan.Links);
        var command = BuildPlanner.LinkCommand(plan);
        Assert.Contains(plan.LinkFlags[0], command.Display);
        Assert.EndsWith("-o " + BuildPlanner.Slash(plan.OutputPath), command.Display);
        Assert.Contains(BuildPlanner.Slash(plan.Find("main")!.ObjectPath), command.Display);
        Assert.Equal(Path.Combine(plan.BuildDirectory, plan.ProjectName + ".exe"), plan.OutputPath);
    }

    [Fact]
    public void A_toolchain_without_a_link_section_says_so_instead_of_guessing()
    {
        using var project = new BuildProject(link: false);
        var plan = project.Plan();

        Assert.Contains(plan.Problems, p => p.Contains("no [link] section"));
    }

    // ------------------------------------------------------------------ the cache

    [Fact]
    public void A_cache_key_changes_with_the_source_and_not_with_the_clock()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        var unit = plan.Find("main")!;
        string key = unit.CacheKey;

        Assert.Equal(key, project.Plan().Find("main")!.CacheKey);

        project.Write("src/main.c", project.Source + "\nint extra(void) { return 1; }\n");
        Assert.NotEqual(key, project.Plan().Find("main")!.CacheKey);
    }

    [Fact]
    public void Editing_the_project_flags_invalidates_the_object()
    {
        using var project = new BuildProject();
        string before = project.Plan().Find("main")!.CacheKey;

        project.Write("project.toml", project.Read("project.toml").Replace("-Wall", "-Wall -Wextra"));
        Assert.NotEqual(before, project.Plan().Find("main")!.CacheKey);
    }

    [Fact]
    public void A_second_build_of_an_unchanged_unit_is_a_cache_hit()
    {
        using var project = new BuildProject();
        var first = project.Plan();
        var unit = first.Find("main")!;
        Directory.CreateDirectory(first.ObjectsDirectory);
        File.WriteAllText(unit.ObjectPath, "object bytes");

        var manifest = project.ManifestAfterBuild(first);
        var second = project.Plan(previous: manifest);
        var again = second.Find("main")!;

        Assert.True(again.Cached);
        Assert.Equal("up to date", again.CacheReason);
        Assert.Equal(unit.CacheKey, again.CacheKey);
    }

    [Fact]
    public void Force_rebuilds_a_unit_that_would_otherwise_be_cached()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        var unit = plan.Find("main")!;
        Directory.CreateDirectory(plan.ObjectsDirectory);
        File.WriteAllText(unit.ObjectPath, "object bytes");

        var manifest = project.ManifestAfterBuild(plan);
        var forced = project.Plan(previous: manifest, force: true);

        Assert.False(forced.Find("main")!.Cached);
        Assert.Equal("--force", forced.Find("main")!.CacheReason);
    }

    [Fact]
    public void A_changed_header_invalidates_the_unit_that_included_it()
    {
        using var project = new BuildProject();
        project.Write("include/config.h", "#define VERSION 1\n");
        var plan = project.Plan();
        var unit = plan.Find("main")!;
        Directory.CreateDirectory(plan.ObjectsDirectory);
        File.WriteAllText(unit.ObjectPath, "object bytes");
        var manifest = project.ManifestAfterBuild(plan, dependencies: ["include/config.h"]);

        Assert.True(project.Plan(previous: manifest).Find("main")!.Cached);

        project.Write("include/config.h", "#define VERSION 2\n");
        var second = project.Plan(previous: manifest).Find("main")!;

        Assert.False(second.Cached);
        Assert.Contains("changed dependency: include/config.h", second.CacheReason);
    }

    [Fact]
    public void A_missing_object_file_is_recompiled_even_when_nothing_changed()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        var manifest = project.ManifestAfterBuild(plan);
        Directory.CreateDirectory(plan.ObjectsDirectory);

        var second = project.Plan(previous: manifest).Find("main")!;

        Assert.False(second.Cached);
        Assert.Equal("object file is missing", second.CacheReason);
    }

    // ------------------------------------------------------------------ ninja

    [Fact]
    public void The_plan_is_exported_as_a_ninja_file_a_ninja_can_run()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        string ninja = NinjaWriter.Write(plan, new BuildOptions());

        Assert.Contains("rule cc", ninja);
        Assert.Contains("deps = gcc", ninja);
        Assert.Contains("build obj/main.o: cc ../src/main.c", ninja);
        Assert.Contains("out_flag = -o obj/main.o", ninja);
        Assert.Contains("dep_flag = -MMD -MF obj/main.d", ninja);
        Assert.Contains($"build {plan.ProjectName}.exe: link obj/main.o", ninja);
        Assert.Contains($"default {plan.ProjectName}.exe", ninja);

        // A path with a space is escaped, otherwise ninja would read it as two files.
        Assert.DoesNotContain("/home/", ninja.Split("flags = ")[1].Split('\n')[0]);
    }

    [Fact]
    public void The_ninja_file_names_the_same_tool_the_plan_would_run()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        string ninja = NinjaWriter.Write(plan, new BuildOptions());

        Assert.Contains(BuildPlanner.CompileCommand(plan.Find("main")!, plan).Executable, ninja);
        Assert.Contains(plan.LinkExecutable, ninja);
    }

    /// <summary>
    /// The exported file has to be the same build, not a lookalike: each edge is expanded back into
    /// a command line and compared with the one <c>recon build</c> would have run. Ninja itself is
    /// not installed on every machine, so this is how the export stays honest.
    /// </summary>
    [Fact]
    public void Every_ninja_edge_is_the_command_recon_would_run()
    {
        using var project = new BuildProject();
        var plan = project.Plan();
        string ninja = NinjaWriter.Write(plan, new BuildOptions());

        var compile = ParseEdge(ninja, "obj/main.o");
        var expected = BuildPlanner.CompileCommand(plan.Find("main")!, plan);

        Assert.Equal(expected.Executable, compile["cc"]);
        Assert.Equal(
            [.. expected.Arguments.Select(argument => RelativeToBuildDirectory(plan, argument))],
            EdgeArguments(compile, plan));

        var link = ParseEdge(ninja, "sample.exe");
        var linkCommand = BuildPlanner.LinkCommand(plan);
        Assert.Equal(linkCommand.Executable, link["link"]);
        Assert.Equal(
            [.. linkCommand.Arguments.Select(argument => RelativeToBuildDirectory(plan, argument))],
            [.. link["flags"].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                RelativeToBuildDirectory(plan, plan.Find("main")!.ObjectPath),
                "-o", "sample.exe"]);
    }

    /// <summary>
    /// The same argument as <c>recon build</c> would pass, seen from the Ninja file's directory:
    /// that is the only difference between the two, and it is what makes the file relocatable.
    /// </summary>
    private static string RelativeToBuildDirectory(BuildPlan plan, string argument)
    {
        string root = plan.Root.Replace(Path.DirectorySeparatorChar, '/');
        string build = plan.BuildDirectory.Replace(Path.DirectorySeparatorChar, '/');
        string text = argument.Replace(Path.DirectorySeparatorChar, '/');

        // A flag such as -I/path keeps its prefix; only the path inside it is rewritten.
        int index = text.IndexOf(root + "/", StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        return text[..index]
            + Path.GetRelativePath(build + "/", text[index..]).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// The command an edge would run, in the order recon itself builds it: flags, the source, the
    /// output flag and the depfile flag.
    /// </summary>
    private static List<string> EdgeArguments(Dictionary<string, string> edge, BuildPlan plan)
    {
        var arguments = Words(edge["flags"]);
        arguments.Add(RelativeToBuildDirectory(plan, plan.Find("main")!.SourcePath));
        arguments.AddRange(Words(edge["out_flag"]));
        if (edge.TryGetValue("dep_flag", out string? depFlag))
        {
            arguments.AddRange(Words(depFlag));
        }

        return arguments;
    }

    private static List<string> Words(string text)
        => [.. Unescape(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Reads the variables of one <c>build</c> statement out of a generated ninja file.</summary>
    private static Dictionary<string, string> ParseEdge(string ninja, string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inside = false;
        foreach (string line in ninja.Split('\n'))
        {
            if (line.StartsWith("build ", StringComparison.Ordinal))
            {
                inside = line.Split(':')[0]["build ".Length..].Split(' ').Contains(target);
                continue;
            }

            if (!inside)
            {
                continue;
            }

            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("build ", StringComparison.Ordinal) && !line.StartsWith("  ", StringComparison.Ordinal))
            {
                break;
            }

            int equals = trimmed.IndexOf('=');
            if (equals > 0)
            {
                result[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim();
            }
        }

        return result;
    }

    private static string Unescape(string text)
        => text.Replace("$$", "$", StringComparison.Ordinal)
               .Replace("$ ", " ", StringComparison.Ordinal)
               .Replace("$$:", ":", StringComparison.Ordinal);

    // ------------------------------------------------------------------ the command, end to end

    [Fact]
    public void A_dry_run_says_what_it_would_do_and_writes_nothing()
    {
        using var project = new BuildProject();
        var run = CliRun.Run("build", "--project", project.DirectoryPath, "--dry-run");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("dry run: nothing was compiled", run.StandardOutput);
        Assert.Contains("-o " + BuildPlanner.Slash(Path.Combine(project.DirectoryPath, "build", "obj", "main.o")), run.StandardOutput);
        Assert.False(File.Exists(project.PathOf("build/build.json")));
        Assert.False(File.Exists(project.PathOf("build/build.ninja")));
        Assert.False(File.Exists(project.PathOf("build/sample.exe")));
    }

    [Fact]
    public void A_build_with_no_units_fails_and_says_why()
    {
        using var project = new BuildProject(units: false);
        var run = CliRun.Run("build", "--project", project.DirectoryPath);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("no [[unit]] entries", run.All);
    }

    [Fact]
    public void A_unit_filter_builds_only_the_unit_it_names()
    {
        using var project = new BuildProject();
        project.AddUnit("other", "src/other.c");
        var run = CliRun.Run("build", "--project", project.DirectoryPath, "--dry-run", "--unit", "other");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("1 unit(s)", run.StandardOutput);
        Assert.Contains("src/other.c", run.StandardOutput);
        Assert.DoesNotContain("src/main.c", run.StandardOutput);
    }

    [Fact]
    public void An_unknown_unit_name_is_a_build_failure()
    {
        using var project = new BuildProject();
        var run = CliRun.Run("build", "--project", project.DirectoryPath, "--dry-run", "--unit", "nope");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("no unit named", run.All);
    }

    [Fact]
    public void The_build_command_rejects_a_job_count_that_is_not_a_number()
    {
        using var project = new BuildProject();
        var run = CliRun.Run("build", "--project", project.DirectoryPath, "--jobs", "many");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--jobs", run.All);
    }

    // ------------------------------------------------------------------ with a real compiler

    /// <summary>
    /// The whole point of M3: the reconstruction's own build runs, and <c>recon diff</c> then says
    /// whether it reproduced the original. Skipped when no cross compiler is installed.
    /// </summary>
    [Fact]
    public void A_rebuilt_image_compares_against_the_original_it_came_from()
    {
        if (!ToolDetection.Exists(MingwCc)
            || !TestPaths.CorpusExists("sample-release.exe")
            || !TestPaths.CorpusSourceExists("sample.c"))
        {
            return;
        }

        // The corpus's release binary was built by `tools/build-corpus.sh` at `-O2 -g -Wall`, so the
        // rebuild asks for the same three flags. It used to leave this harness's default `-O0` in place,
        // which made every assertion below a statement about an image nobody had built: the functions
        // that differ between the two levels are the ones the comparison is measuring. Checked by
        // rebuilding the corpus with the script and comparing hashes — the box's compiler reproduces
        // `sample-release.exe` byte for byte — which is what says the recipe, not the compiler, was the
        // difference.
        using var project = new BuildProject(
            source: File.ReadAllText(TestPaths.CorpusSource("sample.c")),
            mingw: true,
            unitFlags: new[] { "-O2", "-g", "-Wall" });
        // Under the name the project declares it by, which is `sample.exe`: the corpus keeps the name a
        // build by the script writes, and a project names its own inputs.
        project.CopyInput(TestPaths.Corpus("sample-release.exe"), asName: "sample.exe");

        var first = CliRun.Run("build", "--project", project.DirectoryPath, "--check-schema");
        Assert.Equal(0, first.ExitCode);
        Assert.True(File.Exists(project.PathOf("build/sample.exe")));
        Assert.Contains("1 compiled", first.StandardOutput);
        Assert.Contains("build manifest matches schema 0.1", first.StandardOutput);

        string built = PeImage.HashFile(project.PathOf("build/sample.exe"));

        // Nothing changed: the second build compiles nothing, links nothing, and hands back the same
        // image. A linker stamps its output, so re-linking would have produced a different file.
        var second = CliRun.Run("build", "--project", project.DirectoryPath);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("0 compiled, 1 cached", second.StandardOutput);
        Assert.Contains("link cached", second.StandardOutput);
        Assert.Equal(built, PeImage.HashFile(project.PathOf("build/sample.exe")));

        // The rebuilt image is the original's code: every function matches, which is the check the
        // plan's §7.3 asks `build` to make automatic. This is asked of the image the build just made
        // from the source as it stands — the same program as the original — and it comes *before* the
        // edit below. It used to come after, which made the test assert two opposite things about one
        // file: that the edit changed the image, and that the image was the original's code.
        var diff = CliRun.Run(
            "diff",
            "--project", project.DirectoryPath,
            project.PathOf("build/sample.exe"),
            "-o", project.PathOf("build/comparison.json"),
            "--summary", "--min-score", "1.0");
        // The report is the message: a failing comparison has to say what differed, not just that
        // something did.
        Assert.True(diff.ExitCode == 0, diff.StandardOutput + diff.StandardError);
        Assert.Contains("142 matched", diff.StandardOutput);
        Assert.Contains("score        1", diff.StandardOutput);

        // Change the source and it rebuilds, because the cache keys are content hashes. Nothing is
        // claimed about this image's code: it is a different program on purpose.
        project.Write("src/main.c", File.ReadAllText(TestPaths.CorpusSource("sample.c")) + "\nint recon_probe(void){return 7;}\n");
        var third = CliRun.Run("build", "--project", project.DirectoryPath);
        Assert.Equal(0, third.ExitCode);
        Assert.Contains("1 compiled", third.StandardOutput);
        Assert.NotEqual(built, PeImage.HashFile(project.PathOf("build/sample.exe")));
    }

    [Fact]
    public void A_broken_source_is_reported_with_the_compilers_own_words()
    {
        if (!ToolDetection.Exists(MingwCc))
        {
            return;
        }

        using var project = new BuildProject(source: "int main(void) { this is not C }\n", mingw: true);
        var run = CliRun.Run("build", "--project", project.DirectoryPath);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("FAILED", run.All);
        Assert.Contains("error", run.All.ToLowerInvariant());
    }


    /// <summary>
    /// The other toolchain family: an MSVC-ABI profile uses <c>.obj</c>, names its output with
    /// <c>/OUT:</c> and links with lld-link. The shipped <c>clang-19-msvc</c> profile names
    /// <c>clang-cl.exe</c>, which a Linux box does not have, so this test writes the profile the
    /// same way a user with a different install would: same family, this machine's binaries.
    /// </summary>
    [Fact]
    public void An_msvc_family_toolchain_builds_with_its_own_object_extension_and_linker()
    {
        if (!ToolDetection.Exists("clang-19") || !ToolDetection.Exists("lld-link-19"))
        {
            return;
        }

        // /nodefaultlib and /entry:mainCRTStartup: a freestanding image, with no runtime to call main.
        using var project = new BuildProject(
            toolchain: "msvc-test",
            msvcFamily: true,
            source: "int mainCRTStartup(void) { return 0; }\n");
        var run = CliRun.Run("build", "--project", project.DirectoryPath);

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(project.PathOf("build/obj/main.obj")));
        Assert.True(File.Exists(project.PathOf("build/sample.exe")));

        // The image is a real PE32, so the rest of the tool can read what the build produced.
        var image = Recon.Pe.PeLoader.Load(project.PathOf("build/sample.exe"));
        Assert.NotNull(image.Image);
        Assert.Equal("x86", image.Image!.DescribeMachine());
        Assert.Empty(image.Problems);
    }

    /// <summary>
    /// M3 asks <c>verify</c> to check the inputs; the build's inputs are the units' sources and
    /// toolchains, so a project that cannot be built must not verify clean.
    /// </summary>
    [Fact]
    public void Verify_checks_that_every_unit_can_be_built()
    {
        using var project = new BuildProject();

        var ok = CliRun.Run("verify", "--project", project.DirectoryPath);
        Assert.Contains("buildable", ok.StandardOutput);

        File.Delete(project.PathOf("src/main.c"));
        var broken = CliRun.Run("verify", "--project", project.DirectoryPath);
        Assert.Equal(1, broken.ExitCode);
        Assert.Contains("source not found: src/main.c", broken.StandardOutput);
    }

    [Fact]
    public void Doctor_says_whether_the_tools_a_unit_needs_are_installed()
    {
        if (!ToolDetection.Exists(MingwCc))
        {
            return;
        }

        using var project = new BuildProject(mingw: true);
        var run = CliRun.Run("doctor", "--project", project.DirectoryPath);

        // The exit code is not asserted: this project's original binary is a placeholder, which is
        // itself a doctor finding. What matters is that the tools the build needs are checked.
        Assert.Contains("build gcc-13-mingw", run.StandardOutput);
        Assert.Contains($"cc /usr/bin/{MingwCc}", run.StandardOutput);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The cross compiler this class's end-to-end tests run for real, when it is installed.</summary>
    internal const string MingwCcName = "i686-w64-mingw32-gcc";

    private const string MingwCc = MingwCcName;

    private static ToolchainProfile Profile(string toml)
    {
        var diagnostics = new Diagnostics();
        var document = Recon.Toml.TomlParser.Parse(
            "schema_version = 1\nid = \"test-gcc\"\ndisplay_name = \"Test GCC\"\nfamily = \"gcc\"\n\n" + toml,
            "test.toml");
        Assert.Empty(diagnostics.Errors);
        return ToolchainProfile.Load(document, "test.toml", diagnostics);
    }

    /// <summary>
    /// A complete, tiny build project on disk: project.toml, local.toml, a source and a header. Shared
    /// with <see cref="MovedCodeTests"/>, which needs two of them to compare one source against itself
    /// built another way.
    /// </summary>
    internal sealed class BuildProject : IDisposable
    {
        private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

        private readonly TempDir _temp = new("recon-build");

        public BuildProject(
            string unitName = "main",
            string toolchain = "gcc-13-mingw",
            string provider = "rebuilt",
            bool units = true,
            bool link = true,
            bool mingw = false,
            bool msvcFamily = false,
            string source = "int main(void) { return 0; }\n",
            string[]? unitFlags = null,
            string name = "sample")
        {
            Write("src/main.c", source);
            Write("src/other.c", "int other(void) { return 1; }\n");
            Write("include/config.h", "#define VERSION 1\n");

            string profiles = Path.Combine(TestPaths.RepositoryRoot, "src", "Recon.Core", "Toolchains", "profiles")
                .Replace("\\", "/");

            var project = new StringBuilder();
            project.Append("schema_version = 1\n\n");
            project.Append($"[project]\nname = \"{name}\"\n\n");
            project.Append("[target]\nformat = \"pe32\"\narch = \"x86\"\n").Append($"default_toolchain = \"{toolchain}\"\n\n");
            project.Append("[[input]]\nid = \"main\"\nrole = \"original\"\nfile = \"sample.exe\"\n").Append($"sha256 = \"{ZeroHash}\"\n\n");
            project.Append("[paths]\nsource = \"src\"\ninclude = [\"include\"]\nbuild = \"build\"\n").Append($"profiles = [\"{profiles}\"]\n\n");
            project.Append("[defaults]\nflags = [\"-Wall\"]\ndefines = [\"NDEBUG\"]\n\n");
            if (units)
            {
                project.Append($"[[unit]]\nname = \"{unitName}\"\n");
                project.Append("source = \"src/main.c\"\n");
                // The flags are a parameter because they have to be able to be the recipe that produced
                // the binary being compared against: a rebuild at another optimisation level is a
                // different program, and a test that compares bytes has to say which program it built.
                var flags = unitFlags ?? new[] { "-O0" };
                project.Append($"flags = [{string.Join(", ", Array.ConvertAll(flags, f => $"\"{f}\""))}]\ndefines = [\"GAME_BUILD\"]\n");
                if (provider != "rebuilt")
                {
                    project.Append($"provider = \"{provider}\"\n");
                }

                project.Append('\n');
            }

            Write("project.toml", project.ToString());

            // local.toml is where a machine says where the toolchain lives; with `mingw` it points
            // at this box's cross compiler, which is how the end-to-end test runs at all.
            var local = new StringBuilder("schema_version = 1\n\n[inputs]\ndir = \"inputs\"\n\n");
            local.Append($"[toolchain.{toolchain}]\nroot = \"/usr\"\n");
            if (mingw)
            {
                local.Append($"cc = \"/usr/bin/{MingwCc}\"\nlink = \"/usr/bin/{MingwCc}\"\n");
            }

            Write("local.toml", local.ToString());

            if (msvcFamily)
            {
                // Same family as the shipped clang-19-msvc profile, this machine's binaries.
                Write("toolchains/msvc-test.toml", """
                    schema_version = 1
                    id = "msvc-test"
                    display_name = "clang/lld-link, MSVC ABI (this machine)"
                    family = "msvc"
                    extends = "msvc-base"

                    [[targets]]
                    format = "pe32"
                    arch = "x86"

                    [compile]
                    exe = "/usr/bin/clang-19"
                    default_flags = ["--target=i686-pc-windows-msvc", "-c", "-g", "-gcodeview"]
                    include_flag = "-I{path}"
                    define_flag = "-D{name}"
                    output_flag = "-o {obj}"

                    [link]
                    exe = "/usr/bin/lld-link-19"
                    default_flags = ["/nologo", "/entry:mainCRTStartup", "/subsystem:console", "/nodefaultlib", "/machine:x86", "/brepro", "/timestamp:0"]
                    output_flag = "/out:{exe}"
                    """);
                Write("project.toml", Read("project.toml").Replace($"profiles = [\"{profiles}\"]", $"profiles = [\"{profiles}\", \"toolchains\"]"));
            }

            if (!link)
            {
                // A profile with no [link]: the plan has to say so rather than link with nothing.
                Write("toolchains/only-compile.toml", """
                    schema_version = 1
                    id = "gcc-13-mingw"
                    display_name = "GCC without a linker"
                    family = "gcc"

                    [[targets]]
                    format = "pe32"
                    arch = "x86"

                    [compile]
                    exe = "bin/gcc.exe"
                    default_flags = ["-c", "-g", "-O2"]
                    include_flag = "-I{path}"
                    define_flag = "-D{name}"
                    output_flag = "-o {obj}"
                    depfile_flag = "-MMD -MF {dep}"
                    """);
                Write("project.toml", Read("project.toml").Replace($"profiles = [\"{profiles}\"]", "profiles = [\"toolchains\"]"));
            }
        }

        public string DirectoryPath => _temp.Path;

        public string PathOf(string name) => _temp.PathOf(name);

        public string Source => Read("src/main.c");

        public string Write(string name, string content) => _temp.Write(name, content);

        public string Read(string name) => File.ReadAllText(_temp.PathOf(name));

        /// <summary>Puts a real binary in the project's inputs and records its hash.</summary>
        /// <summary>
        /// Puts a file where the project's <c>[[input]]</c> says it is, and records its hash. The name
        /// is the corpus file's unless <paramref name="asName"/> says otherwise, because a project
        /// declares the input by the name it has in the project — copying a corpus file under its own
        /// name into a project that asks for another one leaves the input missing, which is what this
        /// used to do to the one test that calls it with a corpus binary.
        /// </summary>
        public void CopyInput(string file, string? asName = null)
        {
            Directory.CreateDirectory(PathOf("inputs"));
            string name = asName ?? Path.GetFileName(file);
            File.Copy(file, PathOf(Path.Combine("inputs", name)), overwrite: true);
            Write("project.toml", Read("project.toml").Replace(ZeroHash, PeImage.HashFile(PathOf(Path.Combine("inputs", name)))));
        }

        public void AddUnit(string name, string source, string provider = "rebuilt")
        {
            Write(source, Read("src/main.c"));
            Write("project.toml", Read("project.toml") + $"""

                [[unit]]
                name = "{name}"
                source = "{source}"
                provider = "{provider}"
                """);
        }

        public BuildPlan Plan(BuildManifest? previous = null, bool force = false)
        {
            var diagnostics = new Diagnostics();
            var context = ProjectContext.Load(PathOf("project.toml"), diagnostics);
            Assert.Empty(diagnostics.Errors);
            return BuildPlanner.Plan(context, new BuildOptions { Force = force }, previous);
        }

        /// <summary>
        /// The manifest <c>recon build</c> would leave behind for this plan: the cache of the next
        /// run. Used to test cache hits without compiling anything.
        /// </summary>
        public BuildManifest ManifestAfterBuild(BuildPlan plan, IEnumerable<string>? dependencies = null)
        {
            var manifest = new BuildManifest();
            foreach (var unit in plan.Units)
            {
                if (dependencies is not null)
                {
                    unit.Dependencies = [.. dependencies.Select(path => new BuildDependency
                    {
                        Path = path,
                        Sha256 = File.Exists(PathOf(path)) ? PeImage.HashFile(PathOf(path)) : null,
                    })];
                    unit.CacheKey = BuildPlanner.RecomputeKey(unit);
                }

                manifest.Units.Add(new BuildUnitInfo
                {
                    Name = unit.Name,
                    Source = unit.Source,
                    CacheKey = unit.CacheKey,
                    Cached = unit.Cached,
                    CacheReason = unit.CacheReason,
                    Dependencies = [.. unit.Dependencies],
                });
            }

            return manifest;
        }

        public void Dispose() => _temp.Dispose();
    }
}