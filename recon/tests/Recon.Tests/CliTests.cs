using System.Text.Json;
using System.Text.RegularExpressions;
using Recon.Cli;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Drives the CLI in-process: the same entry point the executable uses, with stdout and stderr
/// captured. These tests pin the behaviour the milestone-1 acceptance criteria talk about —
/// the same commands working on every platform, with honest exit codes.
/// </summary>
[Collection("cli")]
public class CliTests
{
    private static string SampleProject => Path.Combine(TestPaths.RepositoryRoot, "examples", "sample-project");

    private static bool Corpus => TestPaths.CorpusExists("sample-release.exe");

    /// <summary>
    /// One version string for the documents this tool writes, because a document is read back by a
    /// later run of the same tool and that reading is guarded by the version in it.
    ///
    /// There used to be three answers. The inventory command asked the *entry* assembly — the CLI when
    /// a person runs it, the test host when a test runs the same command in-process — so the same
    /// binary came out stamped `1.0.0` or `1.0.0.0` depending on who hosted the process, and two
    /// library defaults said `0.1.0`. The goldens in this repository carried the first two, which is
    /// what a host-dependent version looks like after a while: two of the five say `1.0.0.0` and three
    /// say `0.1.0`, about the same tool.
    /// </summary>
    [Fact]
    public void Every_document_records_the_same_version_of_this_tool()
    {
        if (!Corpus)
        {
            return;
        }

        string expected = Recon.ToolVersion.Current;
        Assert.Matches(@"^\d+\.\d+\.\d+", expected);

        // The command line: the version it prints and the version the documents carry are one string.
        var run = CliRun.Run("--version");
        Assert.Contains(expected, run.StandardOutput);

        // A comparison, whose document is built by a different class in a different assembly: the
        // two agree only because both ask the one place now.
        if (TestPaths.CorpusExists("sample-debug.exe") && TestPaths.CorpusExists("sample-release.exe"))
        {
            using var temp = new TempDir("recon-version");
            string output = temp.PathOf("comparison.json");
            var compared = CliRun.Run(
                "diff", TestPaths.Corpus("sample-debug.exe"), TestPaths.Corpus("sample-release.exe"),
                "--summary", "-o", output);
            Assert.Equal(0, compared.ExitCode);
            var parsed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal(expected, parsed["generator"]!["version"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// The parser keeps its own list of which options take a value, and the usage lines say the same
    /// thing a second time — <c>--length=N</c>, <c>-o PATH</c>. When the two disagree, the option is
    /// read as absent and the command carries on without it, which is a quiet way to be wrong:
    /// <c>--inventory</c>, <c>--length</c>, <c>--min-fixed</c> and <c>--opcode</c> were all documented
    /// and all ignored until this test existed.
    /// </summary>
    [Fact]
    public void Every_option_a_command_can_take_a_value_for_takes_one()
    {
        var declared = new List<string>();
        foreach (CommandSpec spec in CommandSpecs.All)
        {
            foreach (string option in spec.Options)
            {
                Match written = Regex.Match(option, @"^(?<name>--?[A-Za-z][A-Za-z0-9-]*)[= ](?<value>[A-Za-z]+)$");
                if (written.Success)
                {
                    declared.Add(written.Groups["name"].Value);
                }
            }
        }

        Assert.True(declared.Count > 15, $"only {declared.Count} options declare a value; the usage lines are not being read");
        foreach (string name in declared.Distinct())
        {
            Assert.Contains(name, CommandLine.ValueTaking);
        }

        // And the other way round: an option registered as taking a value that nothing gives one to
        // is either a typo or something nobody reads, and either way it eats an argument that was
        // meant as a file or a name. The project options are the exception and are global, so they
        // are documented once in --help rather than in every command's usage line: that they are
        // documented as taking a value is checked against the help text itself.
        string help = CliRun.Run("--help").StandardOutput;
        Assert.Contains("--project <dir|file>", help, StringComparison.Ordinal);

        var global = new[] { "--project", "-p" };

        // Two spellings of one option: the usage lines carry the short one.
        var spelledDifferently = new Dictionary<string, string>(StringComparer.Ordinal) { ["--output"] = "-o" };

        foreach (string name in CommandLine.ValueTaking.Where(n => n.StartsWith("--", StringComparison.Ordinal)))
        {
            string documented = spelledDifferently.TryGetValue(name, out string? canonical) ? canonical : name;
            Assert.True(declared.Contains(documented) || global.Contains(name), $"{name} takes a value and nothing documents it as one");
        }
    }

    /// <summary>
    /// <c>--opcode</c> is the option this was found on, so it is checked from the outside as well:
    /// the value has to reach the command.
    /// </summary>
    [Fact]
    public void An_option_that_takes_a_value_gets_it()
    {
        var parsed = CommandLine.Parse(["opcodes", "runtime.dll", "--opcode", "0x2A", "--json"]);

        Assert.Equal("0x2A", parsed.Value("--opcode"));
        Assert.Equal(new[] { "runtime.dll" }, parsed.Arguments);
        Assert.True(parsed.Has("--json"));

        // Both spellings, because both are documented and only one of them was ever tested.
        Assert.Equal("7", CommandLine.Parse(["permute", "--length=7"]).Value("--length"));
    }

    [Fact]
    public void Prints_the_version()
    {
        var run = CliRun.Run("--version");

        Assert.True(run.ExitCode == 0, run.All);
        Assert.StartsWith("recon ", run.StandardOutput);
    }

    [Fact]
    public void With_no_arguments_prints_help()
    {
        var run = CliRun.Run();

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("usage: recon", run.StandardOutput);
        Assert.Contains("inventory", run.StandardOutput);
    }

    [Fact]
    public void An_unknown_command_is_a_usage_error()
    {
        var run = CliRun.Run("frobnicate");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("unknown command", run.StandardError);
    }

    [Fact]
    public void A_missing_subcommand_is_a_usage_error()
    {
        Assert.Equal(2, CliRun.Run("inspect", "--project", SampleProject).ExitCode);
        Assert.Equal(2, CliRun.Run("disasm", "--project", SampleProject).ExitCode);
    }

    [Fact]
    public void A_missing_binary_argument_is_a_usage_error()
    {
        var run = CliRun.Run("hash");

        Assert.Equal(2, run.ExitCode);
    }

    [Fact]
    public void A_missing_project_is_a_configuration_error()
    {
        var run = CliRun.Run("verify", "--project", "/nonexistent/project");

        Assert.Equal(3, run.ExitCode);
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("validate")]
    [InlineData("doctor")]
    [InlineData("inspect sections")]
    [InlineData("inspect imports")]
    [InlineData("inspect relocs")]
    [InlineData("inspect data")]
    [InlineData("inspect functions")]
    [InlineData("inspect xrefs")]
    [InlineData("inspect producers")]
    [InlineData("inspect debug")]
    [InlineData("inspect tls")]
    [InlineData("inspect stats")]
    [InlineData("inventory")]
    [InlineData("sigs build")]
    [InlineData("toolchain list")]
    [InlineData("toolchain detect")]
    [InlineData("schema list")]
    public void The_documented_commands_succeed_on_the_sample_project(string command)
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir();
        string[] args = [.. command.Split(' '), "--project", SampleProject];
        if (command == "inventory")
        {
            args = [.. args, "--output", temp.PathOf("inventory.json")];
        }

        if (command == "sigs build")
        {
            args = [.. args, "--output", temp.PathOf("signatures.json")];
        }

        var run = CliRun.Run(args);
        Assert.True(run.ExitCode == 0, run.All);
    }

    [Fact]
    public void Sigs_builds_patterns_that_sigs_apply_and_inventory_both_use()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir();
        string patterns = temp.PathOf("crt.json");

        var built = CliRun.Run("sigs", "build", "--project", SampleProject, "--output", patterns, "--library", "sample-crt");
        Assert.True(built.ExitCode == 0, built.All);
        Assert.True(File.Exists(patterns));

        using var document = JsonDocument.Parse(File.ReadAllText(patterns));
        Assert.True(document.RootElement.TryGetProperty("entries", out var entries));
        Assert.True(entries.GetArrayLength() > 0);
        Assert.True(document.RootElement.GetProperty("schema_version").GetInt32() >= 1);

        // The sample binary still has its symbols, so the patterns should name nothing new in it:
        // a pattern is the weakest evidence there is, and it yields to a symbol every time.
        var applied = CliRun.Run("sigs", "apply", "--project", SampleProject, "--signatures", patterns);
        Assert.True(applied.ExitCode == 0, applied.All);
        Assert.Contains("named by them: 0", applied.StandardOutput);

        // The inventory counts it, which is how a later milestone knows how much of a binary is
        // runtime code nobody has to reconstruct.
        string inventory = temp.PathOf("inventory.json");
        var run = CliRun.Run(
            "inventory", "--project", SampleProject, "--output", inventory, "--signatures", patterns, "--check-schema");
        Assert.True(run.ExitCode == 0, run.All);

        var parsed = Recon.Inventory.InventoryJson.Deserialize(File.ReadAllText(inventory));
        Assert.NotNull(parsed);
        Assert.True(parsed!.Statistics.ContainsKey("functions_named_by_signature"));
    }

    [Fact]
    public void Sigs_apply_without_a_pattern_file_is_a_configuration_error()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("sigs", "apply", "--project", SampleProject);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("--signatures", run.All);
    }

    [Fact]
    public void An_unknown_sigs_action_is_a_usage_error()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("sigs", "rewrite", "--project", SampleProject);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("expected build or apply", run.All);
    }

    [Fact]
    public void Schema_list_names_the_pattern_file()
    {
        var run = CliRun.Run("schema", "list");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("signatures", run.StandardOutput);
    }

    [Fact]
    public void Inspect_data_separates_symbols_from_section_fallback_ranges()
    {
        if (!Corpus)
        {
            return;
        }

        var symbols = CliRun.Run("inspect", "data", "--project", SampleProject, "--json");
        Assert.Equal(0, symbols.ExitCode);
        var document = JsonDocument.Parse(symbols.StandardOutput).RootElement;
        var entries = document.GetProperty("symbols").EnumerateArray().ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.NotEqual("section", e.GetProperty("source").GetString()));
        Assert.Equal(0, document.GetProperty("section_ranges").GetArrayLength());

        // The section ranges are still in the inventory: they are the fallback the loader records, so
        // a view leaves them out by default and shows them when asked rather than losing them.
        var all = CliRun.Run("inspect", "data", "--project", SampleProject, "--all", "--json");
        Assert.Equal(0, all.ExitCode);
        var withRanges = JsonDocument.Parse(all.StandardOutput).RootElement;
        Assert.NotEqual(0, withRanges.GetProperty("section_ranges").GetArrayLength());
        Assert.All(
            withRanges.GetProperty("section_ranges").EnumerateArray(),
            e => Assert.Equal("section", e.GetProperty("source").GetString()));
    }

    [Fact]
    public void Hash_prints_the_digest_that_project_toml_needs()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("hash", TestPaths.Corpus("sample-release.exe"));

        Assert.True(run.ExitCode == 0, run.All);

        // The digest the command prints is the one project.toml records, which is what makes the
        // input hash a lock rather than a note.
        var diagnostics = new Recon.Config.Diagnostics();
        var project = Recon.Config.ProjectConfig.Load(
            Path.Combine(SampleProject, "project.toml"), diagnostics);
        Assert.Empty(diagnostics.Errors);
        Assert.Contains(project.Inputs[0].Sha256, run.StandardOutput);
    }

    [Fact]
    public void Inventory_writes_a_document_and_checks_it_against_the_schema()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir();
        string output = temp.PathOf("inventory.json");

        var run = CliRun.Run("inventory", "--project", SampleProject, "--output", output, "--check-schema");

        Assert.True(run.ExitCode == 0, run.All);
        Assert.True(File.Exists(output));

        var document = Recon.Inventory.InventoryJson.Deserialize(File.ReadAllText(output));
        Assert.NotNull(document);
        Assert.Equal("0.1", document!.SchemaVersion);
        Assert.NotEmpty(document.Functions);
        Assert.NotEmpty(document.Functions.SelectMany(f => f.FoundBy));
    }

    /// <summary>
    /// `inventory` reads the input its project declares — the paths, the signatures and the config all
    /// come from the project — so a positional argument naming a *different* file is a usage error
    /// rather than something to ignore. It used to be ignored in silence: the command inventoried the
    /// project's input and said nothing about the file it had been handed, which is how a measurement
    /// loop over 42 p-code programs reported one of them measured 42 times. Naming the project's own
    /// input is fine, by path or by id, because that is what a caller means.
    /// </summary>
    [Fact]
    public void Inventory_refuses_a_file_that_is_not_the_one_its_project_declares()
    {
        if (!Corpus)
        {
            return;
        }

        // The corpus binary the project declares, and a copy of it that the project does not: the
        // same bytes with a different name, which is exactly the case that used to pass unnoticed.
        string declared = TestPaths.Corpus("sample-release.exe");
        using var temp = new TempDir();
        string elsewhere = temp.PathOf("some-other-program.exe");
        File.Copy(declared, elsewhere);

        var refused = CliRun.Run("inventory", "--project", SampleProject, elsewhere);
        Assert.Equal(2, refused.ExitCode);
        Assert.Contains("is not that file", refused.All);
        Assert.Contains("sample-release.exe", refused.All);

        // The project's own input, named either way: accepted, and no complaint about it.
        var byId = CliRun.Run("inventory", "--project", SampleProject, "main", "--json");
        Assert.Equal(0, byId.ExitCode);

        var byPath = CliRun.Run("inventory", "--project", SampleProject, declared, "--json");
        Assert.Equal(0, byPath.ExitCode);
    }

    [Fact]
    public void Inventory_json_goes_to_stdout_when_asked()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir();
        var run = CliRun.Run(
            "inventory", "--project", SampleProject, "--output", temp.PathOf("i.json"), "--json");

        Assert.True(run.ExitCode == 0, run.All);

        // Human-readable lines are suppressed in JSON mode, so what is left has to parse.
        var parsed = JsonDocument.Parse(run.StandardOutput);
        Assert.True(parsed.RootElement.TryGetProperty("statistics", out _));
    }

    [Fact]
    public void Disasm_accepts_an_address_a_name_and_a_substring()
    {
        if (!Corpus)
        {
            return;
        }

        var byAddress = CliRun.Run("disasm", "0x1570", "--project", SampleProject);
        Assert.Equal(0, byAddress.ExitCode);
        Assert.Contains("add", byAddress.StandardOutput);
        Assert.Contains("ret", byAddress.StandardOutput);

        var byName = CliRun.Run("disasm", "add", "--project", SampleProject);
        Assert.Equal(0, byName.ExitCode);
        Assert.Contains("0x00001570", byName.StandardOutput);
        Assert.Contains("add eax", byName.StandardOutput);

        var bySubstring = CliRun.Run("disasm", "mul_", "--project", SampleProject);
        Assert.Equal(0, bySubstring.ExitCode);
        Assert.Contains("imul", bySubstring.StandardOutput);
    }

    [Fact]
    public void Disasm_reports_an_unknown_name_as_a_failed_check()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("disasm", "not_a_function_anywhere", "--project", SampleProject);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("no function matches", run.StandardError);
    }

    [Fact]
    public void Disasm_annotates_calls_and_import_thunks()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("disasm", "dispatch", "--project", SampleProject, "--count", "8");

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("jmp dword ptr", run.StandardOutput);
        Assert.Contains("data:jump_table", run.StandardOutput);
    }

    [Fact]
    public void Toolchain_show_resolves_inheritance()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("toolchain", "show", "msvc-2010", "--resolved", "--project", SampleProject);

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("msvc-base -> msvc-2008 -> msvc-2010", run.StandardOutput);
    }

    /// <summary>
    /// A profile is the file it is written in plus everything it inherits, so showing one without
    /// resolving it printed `mangling: none` and `eh / debug: none / none` about a toolchain that
    /// mangles every symbol and writes a PDB — the default value of a field read as a fact about the
    /// compiler. The default view is the resolved one; `--raw` prints the file's own contents, with
    /// what it inherits marked as inherited rather than as absent.
    /// </summary>
    [Fact]
    public void Toolchain_show_defaults_to_what_the_profile_resolves_to()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("toolchain", "show", "msvc-6", "--project", SampleProject);

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("msvc-base -> msvc-6", run.StandardOutput);
        Assert.Contains("mangling:      msvc", run.StandardOutput);
        Assert.Contains("eh / debug:    msvc-seh / pdb", run.StandardOutput);
    }

    [Fact]
    public void Toolchain_show_raw_marks_what_a_profile_inherits()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("toolchain", "show", "msvc-6", "--raw", "--project", SampleProject);

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("inherits:      msvc-base", run.StandardOutput);
        Assert.Contains("mangling:      (inherited)", run.StandardOutput);
        Assert.Contains("targets:       pe32/x86", run.StandardOutput);
    }

    [Fact]
    public void An_unknown_profile_is_a_failed_check()
    {
        if (!Corpus)
        {
            return;
        }

        var run = CliRun.Run("toolchain", "show", "msvc-1999", "--project", SampleProject);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("unknown profile", run.StandardError);
    }

    /// <summary>
    /// A project that copied the shipped profiles in keeps that copy: a profile added to the tool
    /// later never appears in it, and an edited copy never leaves. The example project in this
    /// repository was missing two profiles and carrying seven out-of-date copies, and nothing said
    /// so — the symptom was a detection that did not fire. `recon doctor` now compares them.
    /// </summary>
    [Fact]
    public void Doctor_says_when_a_projects_profile_copies_are_behind_the_shipped_ones()
    {
        using var temp = new TempDir();
        Assert.Equal(0, CliRun.Run("init", "--project", temp.Path).ExitCode);

        // A project written moments ago is current, so nothing is said about its profiles.
        var fresh = CliRun.Run("doctor", "--project", temp.Path);
        Assert.DoesNotContain("does not have", fresh.All);

        // Remove one profile and change another: what a project created before msvc-5 existed, and
        // one whose vb6-native was edited, look like.
        File.Delete(Path.Combine(temp.Path, "toolchains", "msvc-5.toml"));
        File.AppendAllText(Path.Combine(temp.Path, "toolchains", "vb6-native.toml"), "\n# a local edit\n");

        var stale = CliRun.Run("doctor", "--project", temp.Path);
        Assert.Contains("msvc-5.toml", stale.All);
        Assert.Contains("vb6-native.toml", stale.All);
        Assert.Contains("WARN", stale.All);
    }

    // ------------------------------------------------------------------------------ archives

    /// <summary>
    /// A <c>.lib</c> is not a program, so it is not the project's input: it sits beside the binary
    /// under reconstruction and holds the objects a link can pull in. <c>recon lib</c> reads it by
    /// path, and what it prints is the members and the compiler each object names.
    /// </summary>
    [Fact]
    public void Lib_lists_the_members_of_an_archive_and_what_built_them()
    {
        using var temp = new TempDir();
        string archive = temp.Write("fixture.lib", SyntheticArchive.Build(
            ("first.obj", SyntheticArchive.EmptyObject()),
            ("an_import", SyntheticArchive.ShortImport("sample.dll", "SampleFunction"))));

        var run = CliRun.Run("lib", archive);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("first.obj", run.StandardOutput);
        Assert.Contains("import SampleFunction from sample.dll", run.StandardOutput);
    }

    [Fact]
    public void Lib_emits_a_document_that_matches_its_schema()
    {
        using var temp = new TempDir();
        string archive = temp.Write("fixture.lib", SyntheticArchive.Build(
            ("first.obj", SyntheticArchive.EmptyObject())));

        var run = CliRun.Run("lib", archive, "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("lib document matches schema", run.StandardOutput);
    }

    /// <summary>
    /// A file that is not an archive gets the magic it does have, because "not a COFF archive" alone
    /// leaves the obvious next question — what is it, then? — unanswered.
    /// </summary>
    [Fact]
    public void Lib_refuses_a_file_that_is_not_an_archive_and_says_what_it_starts_with()
    {
        using var temp = new TempDir();
        string file = temp.Write("not-a-lib.bin", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);

        var run = CliRun.Run("lib", file);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("is not a COFF archive", run.All);
        Assert.Contains("!<arch>", run.All);
    }

    [Fact]
    public void Lib_without_a_file_is_a_usage_error()
    {
        var run = CliRun.Run("lib");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("usage: recon lib", run.StandardError);
    }

    [Fact]
    public void Lib_reports_a_file_that_is_not_there()
    {
        var run = CliRun.Run("lib", "/nonexistent/missing.lib");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("not found", run.StandardError);
    }

    [Fact]
    public void Verify_fails_when_an_input_hash_does_not_match()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir();
        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "mismatch"

            [target]
            format = "pe32"
            arch = "x86"

            [[input]]
            id = "main"
            role = "original"
            file = "sample-release.exe"
            sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            """);
        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.CorpusDirectory.Replace("\\", "/")}"
            """);

        var run = CliRun.Run("verify", "--project", temp.Path);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("sha256", run.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gen_docs_writes_the_command_reference()
    {
        using var temp = new TempDir();
        string output = temp.PathOf("cli.md");

        var run = CliRun.Run("gen-docs", "--output", output);

        Assert.True(run.ExitCode == 0, run.All);
        Assert.True(File.Exists(output));
        Assert.Contains("recon inventory", File.ReadAllText(output));
    }

    [Fact]
    public void Init_creates_a_project_that_validates()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir();
        var init = CliRun.Run("init", "--dir", temp.Path, "--binary", TestPaths.Corpus("sample-release.exe"));

        Assert.Equal(0, init.ExitCode);
        Assert.True(File.Exists(temp.PathOf("project.toml")));
        Assert.True(File.Exists(temp.PathOf("local.toml")));
        Assert.True(Directory.Exists(temp.PathOf("toolchains")));

        var validate = CliRun.Run("validate", "--project", temp.Path);
        Assert.Equal(0, validate.ExitCode);
    }

    /// <summary>
    /// <c>--project</c> names the directory to create the project in. It used to be ignored — every
    /// other command resolves it to a project.toml, and <c>init</c> has none yet — so
    /// <c>recon init --project somewhere</c> wrote a project into the current directory instead,
    /// where the next command in that directory then found it and behaved as if one had been asked
    /// for. The directory form and the project-file form both land in the same place.
    /// </summary>
    [Fact]
    public void Init_writes_into_the_directory_the_project_option_names()
    {
        using var temp = new TempDir();
        string target = Path.Combine(temp.Path, "project");

        var run = CliRun.Run("init", "--project", target);

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(target, "project.toml")));
        Assert.True(File.Exists(Path.Combine(target, "local.toml")));
        Assert.True(Directory.Exists(Path.Combine(target, "toolchains")));

        // Not in the directory the command was run from, which is the whole point.
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "project.toml")));
    }

    [Fact]
    public void Init_accepts_the_project_file_itself_as_well_as_its_directory()
    {
        using var temp = new TempDir();
        string target = Path.Combine(temp.Path, "project");

        var run = CliRun.Run("init", "--project", Path.Combine(target, "project.toml"));

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(target, "project.toml")));
    }

    // ---------------------------------------------------------------- report and viewer

    [Fact]
    public void Report_writes_the_progress_document_the_site_and_a_history_entry()
    {
        using var temp = new TempDir();
        string project = ReportProject(temp);
        string directory = Path.Combine(temp.Path, "build", "report");

        var first = CliRun.Run("report", "--project", project, "--check-schema");

        Assert.True(first.ExitCode == 0, first.All);
        Assert.Contains("progress report matches schema 0.1", first.StandardOutput);

        string progressPath = Path.Combine(directory, "progress.json");
        string indexPath = Path.Combine(directory, "index.html");
        Assert.True(File.Exists(progressPath));
        Assert.True(File.Exists(indexPath));

        var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(progressPath))!.AsObject();
        Assert.Equal("0.1", report["schema_version"]!.GetValue<string>());
        Assert.Equal("fixture", report["project"]!["name"]!.GetValue<string>());
        Assert.True(report["totals"]!["instructions_original"]!.GetValue<int>() > 0);
        Assert.NotEmpty(report["units"]!.AsArray());

        // The site is one file with the documents inside it: no server, no network, nothing to install.
        Assert.Contains("window.__RECON__ = { comparison:", File.ReadAllText(indexPath));

        // History is why the number can move: the second run has a previous run to be compared with.
        Assert.Single(File.ReadAllLines(Path.Combine(directory, "history.jsonl")));
        var second = CliRun.Run("report", "--project", project);
        Assert.True(second.ExitCode == 0, second.All);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(directory, "history.jsonl")).Length);
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(progressPath))!.AsObject()["previous"]);
    }

    [Fact]
    public void Report_puts_instruction_alignment_in_the_site_when_it_can()
    {
        using var temp = new TempDir();
        string project = ReportProject(temp, mutateRightHandSide: true);

        var run = CliRun.Run("report", "--project", project, "--aligned");

        Assert.True(run.ExitCode == 0, run.All);
        string html = File.ReadAllText(Path.Combine(temp.Path, "build", "report", "index.html"));

        // The aligned listing is what makes the site worth opening: two columns, instruction by
        // instruction, rather than a count of differences.
        Assert.Contains("\"aligned\": [", html);
        Assert.Contains("\"status\": \"equal\"", html);
    }

    [Fact]
    public void Report_on_a_comparison_document_says_alignment_is_unavailable()
    {
        using var temp = new TempDir();
        string project = ReportProject(temp);
        string comparison = Path.Combine(temp.Path, "comparison.json");
        Assert.True(CliRun.Run("diff", "--project", project, "-o", comparison) is { ExitCode: 0 }, "diff failed");

        var run = CliRun.Run("report", "--comparison", comparison, "--aligned", "-o", Path.Combine(temp.Path, "site"));

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("alignment is skipped", run.All);
        Assert.True(File.Exists(Path.Combine(temp.Path, "site", "index.html")));
    }

    [Fact]
    public void Report_with_no_project_measures_the_two_binaries_it_is_given()
    {
        using var temp = new TempDir();
        string left = temp.Write("left.exe", SyntheticPe.Build(new SyntheticPeOptions()));
        string right = temp.Write("right.exe", SyntheticPe.Build(new SyntheticPeOptions()));

        var run = CliRun.Run("report", left, right, "-o", Path.Combine(temp.Path, "site"));

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("100%", run.StandardOutput);
        Assert.True(File.Exists(Path.Combine(temp.Path, "site", "progress.json")));

        // Without a project there are no units and no history to keep: the report is the whole image.
        Assert.False(File.Exists(Path.Combine(temp.Path, "site", "history.jsonl")));
    }

    private static string Sha256Hex(string path) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>
    /// A project with two units and covers, over two synthetic builds: the same fixture the progress
    /// tests use, driven through the entry point a user actually types.
    /// </summary>
    private static string ReportProject(TempDir temp, bool mutateRightHandSide = false)
    {
        string left = temp.Write("inputs/left.exe", SyntheticPe.Build(new SyntheticPeOptions { DuplicateFuncA = true }));
        string right = temp.Write(
            "inputs/right.exe",
            SyntheticPe.Build(new SyntheticPeOptions { MutateFuncA = mutateRightHandSide }));
        BuiltInProfiles.WriteTo(Path.Combine(temp.Path, "toolchains"));
        string sha = Sha256Hex(left);
        string referenceSha = Sha256Hex(right);

        return temp.Write("project.toml", $$"""
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
            sha256 = "{{sha}}"

            [[input]]
            id = "rebuilt"
            role = "reference"
            file = "right.exe"
            sha256 = "{{referenceSha}}"

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

            [report]
            output = "build/report"
            history = true
            """);
    }
}
