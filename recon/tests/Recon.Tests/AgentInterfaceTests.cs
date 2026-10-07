using System.Text.Json;
using System.Text.Json.Nodes;
using Recon.Pe;
using Recon.Schema;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M7 — the agent-facing contract. Every command that emits JSON emits one document, and a failed
/// run has to be as legible to a program as a successful one: these tests are about the cases where
/// there is no report to print, which is exactly when a script or an agent is stuck reading prose
/// off stderr. In the "cli" collection because <see cref="CliRun"/> swaps <see cref="Console.Out"/>.
/// </summary>
[Collection("cli")]
public class AgentInterfaceTests
{
    private static string ElfProject => Path.Combine(TestPaths.RepositoryRoot, "examples", "elf-project");

    /// <summary>
    /// A run that was given nothing to do: exit 2, and one document saying so. Without --json this
    /// is a line on stderr, which is what it was before and what a human still gets.
    /// </summary>
    [Fact]
    public void A_failed_json_run_says_why_in_one_document()
    {
        var run = CliRun.Run("permute", "--project", ElfProject, "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("usage", run.StandardError);

        var document = SingleDocument(run);
        Assert.Equal("0.1", document["schema_version"]?.GetValue<string>());
        Assert.Equal("recon permute --project " + ElfProject + " --json", document["command"]?.GetValue<string>());
        Assert.Equal(2, document["exit_code"]?.GetValue<int>());

        var error = document["error"]!.AsObject();
        Assert.Equal("usage", error["kind"]?.GetValue<string>());
        Assert.Contains("usage:", error["message"]?.GetValue<string>() ?? string.Empty);
        Assert.Equal(error["message"]?.GetValue<string>(), error["errors"]?.AsArray()[0]?.GetValue<string>());
    }

    /// <summary>
    /// A configuration failure carries the file, the line and the key, so a caller can point at the
    /// thing to fix instead of parsing a sentence.
    /// </summary>
    [Fact]
    public void A_configuration_failure_names_the_file_the_line_and_the_key()
    {
        var run = CliRun.Run("permute", "--project", ElfProject, "--function", "no_such_function", "--json");

        Assert.Equal(3, run.ExitCode);

        var document = SingleDocument(run);
        var error = document["error"]!.AsObject();
        Assert.Equal("configuration", error["kind"]?.GetValue<string>());
        Assert.Equal(3, document["exit_code"]?.GetValue<int>());

        var diagnostic = error["diagnostics"]!.AsArray()[0]!.AsObject();
        Assert.EndsWith("project.toml", diagnostic["file"]?.GetValue<string>() ?? string.Empty);
        Assert.Equal("permute.unit", diagnostic["key_path"]?.GetValue<string>());
        Assert.Equal("error", diagnostic["severity"]?.GetValue<string>());
        Assert.Contains("no unit", diagnostic["message"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>
    /// The rule is one document per run. A failing <c>verify</c> has its own report to print, so it
    /// prints that and no error document: two documents on stdout would not be a contract.
    /// </summary>
    [Fact]
    public void A_failing_run_that_printed_its_own_document_does_not_print_a_second_one()
    {
        using var temp = new TempDir("recon-agent");
        string binary = TestPaths.ElfCorpus("sample-elf64-release");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "agent"

            [target]
            format = "elf64"
            arch = "x64"

            [[input]]
            id = "main"
            role = "original"
            file = "sample-elf64-release"
            sha256 = "{new string('a', 64)}"

            [paths]
            source = "src"
            build = "build"
            """);

        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.ElfCorpusDirectory.Replace('\\', '/')}"
            """);

        var run = CliRun.Run("verify", "--project", temp.Path, "--no-toolchains", "--json");

        Assert.Equal(1, run.ExitCode);

        var document = SingleDocument(run);
        Assert.False(document["ok"]?.GetValue<bool>());
        Assert.Null(document["error"]);
    }

    /// <summary>The error document is a published contract: shipped, and matching its own schema.</summary>
    [Fact]
    public void The_error_schema_ships_with_the_tool_and_the_documents_match_it()
    {
        var list = CliRun.Run("schema", "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("error", list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var show = CliRun.Run("schema", "show", "error");
        Assert.Equal(0, show.ExitCode);
        Assert.Contains("Error document", show.StandardOutput);

        string schema = BuiltInSchemas.Get("error")!;
        var validator = JsonSchemaValidator.Parse(schema);

        foreach (var run in new[]
        {
            CliRun.Run("permute", "--project", ElfProject, "--json"),
            CliRun.Run("permute", "--project", ElfProject, "--function", "no_such_function", "--json"),
        })
        {
            Assert.Empty(validator.Validate(run.StandardOutput));
        }
    }

    /// <summary>
    /// Setting a project up is the first thing an agent does, and it needs the paths afterwards.
    /// </summary>
    [Fact]
    public void Init_json_names_every_file_it_wrote()
    {
        string binary = TestPaths.ElfCorpus("sample-elf64-release");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        using var temp = new TempDir("recon-init");
        string project = Path.Combine(temp.Path, "project");

        var run = CliRun.Run("init", project, "--binary", binary, "--json", "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(Validate(run.StandardOutput, "init"));

        var document = SingleDocument(run);
        Assert.Equal(project, document["directory"]?.GetValue<string>());
        Assert.Equal("project", document["name"]?.GetValue<string>());
        Assert.True(File.Exists(document["project_file"]?.GetValue<string>()!));
        Assert.True(File.Exists(document["local_file"]?.GetValue<string>()!));
        Assert.True(Directory.Exists(document["inputs_directory"]?.GetValue<string>()!));

        var files = document["files"]!.AsArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            string path = file!["path"]!.GetValue<string>();
            Assert.True(File.Exists(path), $"the document names {path}, which was not written");
        }

        // The binary is hashed, never copied, and saying so is the whole point of `notes`.
        var binaryNode = document["binary"]!.AsObject();
        Assert.Equal(Path.GetFileName(binary), binaryNode["file"]?.GetValue<string>());
        Assert.Equal(PeImage.HashFile(binary), binaryNode["sha256"]?.GetValue<string>());
        Assert.False(binaryNode["copied"]?.GetValue<bool>());
        Assert.False(File.Exists(binaryNode["expected_at"]!.GetValue<string>()));
        Assert.NotEmpty(document["notes"]!.AsArray());
    }

    /// <summary>
    /// A schema-upgrade check: every configuration file, with the version it carries. Nothing is
    /// rewritten in this build, and the document says so rather than implying it worked.
    /// </summary>
    [Fact]
    public void Migrate_json_reports_every_configuration_file()
    {
        using var temp = new TempDir("recon-migrate");
        string project = Path.Combine(temp.Path, "project");
        CliRun.Run("init", project);

        var run = CliRun.Run("migrate", "--project", project, "--json", "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(Validate(run.StandardOutput, "migrate"));

        var document = SingleDocument(run);
        Assert.Equal(0, document["outdated"]?.GetValue<int>());
        Assert.False(document["write_requested"]?.GetValue<bool>());
        Assert.Equal(0, document["rewritten"]?.GetValue<int>());

        var kinds = document["files"]!.AsArray()
            .Select(f => f!["kind"]!.GetValue<string>())
            .ToList();

        Assert.Contains("project", kinds);
        Assert.Contains("local", kinds);
        Assert.Contains("profile", kinds);
        Assert.All(document["files"]!.AsArray(), f => Assert.True(f!["current"]!.GetValue<bool>()));
    }

    /// <summary>
    /// <c>ok</c> has to mean something. A project that names no toolchain builds — the binary says
    /// who made it — so <c>verify</c> must not call it broken, or an agent reading <c>ok</c> chases
    /// a problem that is not there.
    /// </summary>
    [Fact]
    public void Verify_agrees_with_build_when_no_unit_names_a_toolchain()
    {
        string binary = TestPaths.ElfCorpus("sample-elf64-release");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        using var temp = new TempDir("recon-verify");
        temp.Write("src/arith.c", """
            int __attribute__((noinline)) add(int a, int b)
            {
                return a + b;
            }
            """);

        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "agent"

            [target]
            format = "elf64"
            arch = "x64"

            [[input]]
            id = "main"
            role = "original"
            file = "sample-elf64-release"
            sha256 = "{PeImage.HashFile(binary)}"

            [[unit]]
            name = "arith"
            source = "src/arith.c"

            [[unit.covers]]
            symbol = "add"
            """);

        // No [toolchain] anywhere: the project says nothing about how it was built.
        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.ElfCorpusDirectory.Replace('\\', '/')}"
            """);

        var run = CliRun.Run("verify", "--project", temp.Path, "--json", "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(Validate(run.StandardOutput, "verify"));

        var document = SingleDocument(run);
        Assert.True(document["ok"]?.GetValue<bool>());

        var unit = document["units"]!.AsArray()[0]!.AsObject();
        Assert.True(unit["ok"]!.GetValue<bool>());
        Assert.Equal("gcc-14-elf64", unit["toolchain"]?.GetValue<string>());
    }

    /// <summary>
    /// Every <c>inspect</c> subcommand prints one part of an inventory, and each now has the schema
    /// that describes it — so what a caller gets for <c>inspect functions</c> is checkable, not just
    /// parseable.
    /// </summary>
    [Theory]
    [InlineData("sections", "inspect-sections")]
    [InlineData("imports", "inspect-imports")]
    [InlineData("exports", "inspect-exports")]
    [InlineData("relocs", "inspect-relocs")]
    [InlineData("data", "inspect-data")]
    [InlineData("functions", "inspect-functions")]
    [InlineData("xrefs", "inspect-xrefs")]
    [InlineData("producers", "inspect-producers")]
    [InlineData("debug", "inspect-debug")]
    [InlineData("tls", "inspect-tls")]
    [InlineData("stats", "inspect-stats")]
    public void Every_inspect_subcommand_matches_its_published_schema(string subcommand, string schema)
    {
        var run = CliRun.Run("inspect", subcommand, "--project", ElfProject, "--json", "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(Validate(run.StandardOutput, schema));
    }

    /// <summary>
    /// The commands that report on the machine and on the tool rather than on a binary: same rule,
    /// one document each, each with its own schema.
    /// </summary>
    [Theory]
    [InlineData("doctor", "doctor")]
    [InlineData("toolchain list", "toolchain-list")]
    [InlineData("toolchain show gcc-14-elf64", "toolchain-profile")]
    [InlineData("toolchain detect", "inspect-producers")]
    [InlineData("toolchain check gcc-14-elf64", "toolchain-check")]
    [InlineData("disasm 0x1000", "disasm")]
    public void The_machine_commands_match_their_published_schemas(string command, string schema)
    {
        var run = CliRun.Run([.. command.Split(' '), "--project", ElfProject, "--json", "--check-schema"]);

        Assert.Empty(Validate(run.StandardOutput, schema));

        if (command == "doctor")
        {
            // `doctor` answers about this machine, and `examples/elf-project` pins the SHA-256 of the
            // corpus it was written against, so a corpus rebuilt by another compiler is — correctly —
            // reported as a different build, and the command exits 1. What has to hold whatever the
            // machine looks like is that the exit code agrees with the document: every other row here
            // exits 0 on a machine that is set up the way the example expects, and a doctor that
            // reported a problem while exiting 0 would be the bug this catches.
            bool ok = JsonNode.Parse(run.StandardOutput)!["ok"]!.GetValue<bool>();
            Assert.Equal(ok ? 0 : 1, run.ExitCode);
            return;
        }

        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>
    /// The signature reports were the last documents still printing PascalCase while every schema
    /// says snake_case, which is the mistake a caller cannot work around.
    /// </summary>
    [Fact]
    public void Sigs_build_and_apply_are_snake_case_and_match_their_schemas()
    {
        using var temp = new TempDir("recon-sigs");
        string signatures = temp.PathOf("sigs.json");

        var build = CliRun.Run(
            "sigs", "build", "--project", ElfProject, "--library", "elf-runtime",
            "--unit", "arith,entry", "--output", signatures, "--json", "--check-schema");

        Assert.Equal(0, build.ExitCode);
        Assert.Empty(Validate(build.StandardOutput, "sigs-build"));

        var document = SingleDocument(build);
        Assert.Equal(signatures, document["path"]?.GetValue<string>());
        Assert.Equal("elf-runtime", document["library"]?.GetValue<string>());

        var apply = CliRun.Run(
            "sigs", "apply", "--project", ElfProject, "--signatures", signatures, "--json", "--check-schema");

        Assert.Equal(0, apply.ExitCode);
        Assert.Empty(Validate(apply.StandardOutput, "sigs-apply"));
        Assert.True(SingleDocument(apply).ContainsKey("named"));
    }

    /// <summary>Validates a document against one of the schemas the tool ships. Empty means it matches.</summary>
    private static IReadOnlyList<Recon.Schema.SchemaViolation> Validate(string json, string schema)
        => JsonSchemaValidator.Parse(BuiltInSchemas.Get(schema)!).Validate(json);

    /// <summary>
    /// Stdout of a <c>--json</c> run is exactly one JSON document — not a document and a warning,
    /// not two documents.
    /// </summary>
    private static JsonObject SingleDocument(CliRun run)
    {
        Assert.False(string.IsNullOrWhiteSpace(run.StandardOutput), $"nothing on stdout; stderr: {run.StandardError}");

        var node = JsonNode.Parse(run.StandardOutput)
            ?? throw new InvalidOperationException("stdout is not JSON");

        return node.AsObject();
    }
}
