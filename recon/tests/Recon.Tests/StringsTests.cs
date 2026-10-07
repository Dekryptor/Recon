using System.Text.Json;
using Recon.Cli;
using Recon.Schema;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The text a binary carries: the runs of printable characters, and where each one lies. The corpus
/// binaries are built by the same scripts as the other real-binary tests, so the strings checked here
/// are the ones the compiler actually wrote — a format string the source has, the producer comment
/// gcc leaves in <c>.comment</c>, and the path of the build the debug info records.
///
/// Each test skips when its corpus is missing, the way the rest of the suite does.
/// </summary>
[Collection("cli")]
public class StringsTests
{
    /// <summary>The VB6 sample from <c>RECON_VB6_INPUTS</c>, or null when this machine has none.</summary>
    private static string? Input(string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string candidate = Path.Combine(directory, name);
        return File.Exists(candidate) ? candidate : null;
    }

    private static JsonElement RunJson(params string[] args)
    {
        var run = CliRun.Run(args);
        Assert.True(run.ExitCode == 0, $"recon {string.Join(' ', args)} exited {run.ExitCode}: {run.StandardError}");
        return JsonDocument.Parse(run.StandardOutput).RootElement.Clone();
    }

    private static IReadOnlyList<(string Rva, string Section, string Text)> Rows(JsonElement document)
    {
        Assert.Empty(JsonSchemaValidator.Parse(BuiltInSchemas.Get("strings")!).Validate(document.GetRawText()));
        var rows = new List<(string, string, string)>();
        foreach (JsonElement row in document.GetProperty("strings").EnumerateArray())
        {
            rows.Add((
                row.TryGetProperty("rva", out JsonElement rva) && rva.ValueKind == JsonValueKind.Number ? $"0x{rva.GetUInt32():X8}" : string.Empty,
                row.GetProperty("section").GetString()!,
                row.GetProperty("text").GetString()!));
        }

        return rows;
    }

    /// <summary>
    /// A string the source wrote is found, and it is where the compiler put it: the format string of
    /// the ELF sample is in <c>.rodata</c>, which is what makes the offset in the file and the
    /// address in the image both meaningful.
    /// </summary>
    [Fact]
    public void A_literal_the_source_has_is_found_at_the_address_the_section_maps_it_to()
    {
        string binary = TestPaths.ElfCorpus("sample-elf64-debug");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        JsonElement document = RunJson("strings", binary, "--json", "--check-schema");
        var rows = Rows(document);

        JsonElement row = document.GetProperty("strings").EnumerateArray().First(r => r.GetProperty("text").GetString() == "impossible");
        Assert.Equal(".rodata", row.GetProperty("section").GetString());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("rva").ValueKind);

        // The address is not this command's claim: it is the one the section table makes, so it is
        // checked against the same map the rest of the tool reads, from the offset that was reported.
        var image = Recon.Images.ImageLoader.Load(binary).Image!;
        var sectionRow = image.Sections.First(s => s.Name == ".rodata");
        long offset = row.GetProperty("offset").GetInt64();
        Assert.InRange(offset, sectionRow.RawOffset, sectionRow.RawOffset + sectionRow.RawSize - 1);
        Assert.Equal(sectionRow.Rva + (uint)(offset - sectionRow.RawOffset), row.GetProperty("rva").GetUInt32());
        Assert.Equal("impossible", rows.First(r => r.Text == "impossible").Text);
    }

    /// <summary>
    /// A section that is not part of the loaded image — the debug info, the symbol tables — has no
    /// address, and reporting the zero in the section table as one would point at the header. The
    /// string is still found, because its bytes are in the file and that is what was asked.
    /// </summary>
    [Fact]
    public void A_string_in_a_section_that_is_not_loaded_has_no_address()
    {
        string binary = TestPaths.ElfCorpus("sample-elf64-debug");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        JsonElement document = RunJson("strings", binary, "--json", "--check-schema");
        var rows = Rows(document);

        var inDebug = rows.Where(r => r.Section.StartsWith(".debug")).ToList();
        Assert.NotEmpty(inDebug);
        Assert.All(inDebug, r => Assert.Equal(string.Empty, r.Rva));

        // And the same section is still scanned by name, with the file offsets it holds.
        JsonElement scoped = RunJson("strings", binary, "--section", ".comment", "--json", "--check-schema");
        Assert.Single(scoped.GetProperty("summary").GetProperty("sections_scanned").EnumerateArray());
        Assert.Contains(Rows(scoped), r => r.Text.StartsWith("GCC: (", StringComparison.Ordinal));
    }

    /// <summary>
    /// UTF-16 is the encoding a Windows program keeps its text in, and a scanner that only reads
    /// one byte per character finds almost nothing in a Visual Basic program. The corpus is that
    /// program, so the two encodings can be told apart by what each one finds.
    /// </summary>
    [Fact]
    public void Utf16_is_read_separately_from_ascii_and_can_be_asked_for_alone()
    {
        string? runtime = Input("VISDATA.EXE");
        if (runtime is null)
        {
            return;
        }

        JsonElement both = RunJson("strings", runtime, "--min", "8", "--json", "--check-schema");
        Assert.True(both.GetProperty("summary").GetProperty("utf16").GetInt32() > 0, "the sample has no UTF-16 strings");

        JsonElement only = RunJson("strings", runtime, "--min", "8", "--encoding", "utf16", "--json", "--check-schema");
        Assert.Equal(0, only.GetProperty("summary").GetProperty("ascii").GetInt32());

        // The project path is in the file's own debug text, and it is UTF-16: a Visual Basic 6
        // program records where it was built.
        Assert.Contains(Rows(only), r => r.Text.Contains(".vbp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// --filter and --unique are the two questions asked of a long list, and the summary has to say
    /// what they did: <c>found</c> is what the scan saw, <c>reported</c> what survived.
    /// </summary>
    [Fact]
    public void The_filter_and_the_unique_switch_narrow_the_list_and_say_so()
    {
        string binary = TestPaths.ElfCorpus("sample-elf64-debug");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        JsonElement all = RunJson("strings", binary, "--json", "--check-schema");

        // The filter is case-insensitive on purpose — "gcc" is also the name of a directory in a
        // path — so the needle here is the whole prefix of the comment and matches one run.
        JsonElement filtered = RunJson("strings", binary, "--filter", "GCC: (", "--json", "--check-schema");

        int found = all.GetProperty("summary").GetProperty("found").GetInt32();
        int reported = filtered.GetProperty("summary").GetProperty("reported").GetInt32();
        Assert.True(reported > 0 && reported < found, $"{reported} of {found} survived a filter that matches one comment");
        Assert.Equal(found, filtered.GetProperty("summary").GetProperty("found").GetInt32());
        Assert.All(filtered.GetProperty("strings").EnumerateArray(), row => Assert.Contains("gcc: (", row.GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase));

        // --unique keeps the first of each text; the distinct count is what is left.
        JsonElement unique = RunJson("strings", binary, "--unique", "--json", "--check-schema");
        int distinct = all.GetProperty("summary").GetProperty("distinct").GetInt32();
        Assert.Equal(distinct, unique.GetProperty("summary").GetProperty("reported").GetInt32());
        Assert.True(distinct <= found);
    }

    /// <summary>
    /// A file this build cannot identify is not a refusal: every byte is scanned and the document
    /// says there is no address to report, rather than answering nothing or inventing one.
    /// </summary>
    [Fact]
    public void A_file_with_no_recognised_format_is_scanned_without_addresses()
    {
        using var temp = new TempDir("recon-strings");
        string blob = Path.Combine(temp.Path, "blob.bin");
        var bytes = new byte[512];
        Random.Shared.NextBytes(bytes);
        byte[] marker = "A_MARKER_IN_A_BLOB"u8.ToArray();
        marker.CopyTo(bytes, 200);
        File.WriteAllBytes(blob, bytes);

        JsonElement document = RunJson("strings", blob, "--json", "--check-schema");
        Assert.Equal(JsonValueKind.Null, document.GetProperty("format").ValueKind);
        Assert.Contains(Rows(document), r => r.Text.Contains("A_MARKER_IN_A_BLOB", StringComparison.Ordinal));
        Assert.All(Rows(document), r => Assert.Equal(string.Empty, r.Rva));

        string text = CliRun.Run("strings", blob).StandardOutput;
        Assert.Contains("A_MARKER_IN_A_BLOB", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run shorter than <c>--min</c> is not a string, and a section that does not exist is a
    /// mistake worth naming: the sections are listed because the likely cause is the leading dot.
    /// </summary>
    [Fact]
    public void The_minimum_length_and_a_missing_section_are_refused_rather_than_guessed_at()
    {
        string binary = TestPaths.ElfCorpus("sample-elf64-release");
        Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary}");

        var missing = CliRun.Run("strings", binary, "--section", ".no_such_section");
        Assert.Equal(ExitCodes.Usage, missing.ExitCode);
        Assert.Contains("has no section named", missing.StandardError, StringComparison.Ordinal);
        Assert.Contains(".text", missing.StandardError, StringComparison.Ordinal);

        var badMin = CliRun.Run("strings", binary, "--min", "0");
        Assert.Equal(ExitCodes.Usage, badMin.ExitCode);

        var badEncoding = CliRun.Run("strings", binary, "--encoding", "utf7");
        Assert.Equal(ExitCodes.Usage, badEncoding.ExitCode);

        // The dot is optional on both sides, because half the formats do not have one.
        Assert.Equal(0, CliRun.Run("strings", binary, "--section", "text").ExitCode);
    }

    /// <summary>
    /// The contract: the option list in the usage line is the one the parser honours, and a document
    /// that does not match its schema fails the run. The second half cannot be reached by running a
    /// command — every shipped document does match, which is the point — so it is reached the way
    /// the code reaches it: with something to check and a schema to check it against.
    /// </summary>
    [Fact]
    public void The_usage_line_names_the_options_and_a_violation_fails_the_run()
    {
        var spec = CommandSpecs.All.Single(s => s.Name == "strings");
        Assert.All(spec.Options.Where(o => o.Contains('=')), option =>
        {
            string name = option[..option.IndexOf('=')];
            Assert.Contains(name, CommandLine.ValueTaking);
        });

        var output = new Output(json: false, verbose: false, quiet: false);
        var commands = new Commands(output, CommandLine.Parse(["strings", "--check-schema"]));
        Assert.Equal(0, commands.SchemaViolations);
        commands.EmitWithSchemaCheck(new Recon.Reporting.StringRow { Text = "hello" }, "no-such-schema");
        Assert.Equal(1, commands.SchemaViolations);
    }
}
