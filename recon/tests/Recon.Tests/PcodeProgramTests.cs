using System.Text.Json;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Reading a Visual Basic 6 program compiled to p-code.
///
/// The programs are the ones the DeForm6 corpus published: 42 of them, built by the Visual Basic 6 IDE
/// on a Windows XP host, with their sources beside them. `RECON_PCODE_INPUTS` names the directory.
///
/// This is the specimen the p-code work had been missing. A p-code program's method table is filled in
/// where a native build's is not, so these tests are the first that can check a decode against a whole
/// program rather than against a synthetic image: every procedure is found through the program's own
/// structures, and every stream is decoded with lengths measured out of the runtime's handlers.
/// </summary>
[Collection("cli")]
public sealed class PcodeProgramTests
{
    /// <summary>The runtime whose handlers the lengths are measured from.</summary>
    private static string? Runtime()
    {
        string? inputs = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
        if (inputs is null)
        {
            return null;
        }

        string path = Path.Combine(inputs, "msvbvm60.dll");
        return File.Exists(path) ? path : null;
    }

    private static string? Corpus()
    {
        string? root = Environment.GetEnvironmentVariable("RECON_PCODE_INPUTS");
        return root is not null && Directory.Exists(root) ? root : null;
    }

    private static string Program(string relative)
        => Path.Combine(Corpus()!, relative);

    private static JsonElement RunJson(params string[] args)
    {
        var run = CliRun.Run(args);
        Assert.True(run.ExitCode == 0, run.All);
        return JsonDocument.Parse(run.StandardOutput).RootElement;
    }

    /// <summary>Every .exe of the corpus, in a stable order.</summary>
    private static List<string> Programs()
    {
        var root = Corpus()!;
        return Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Contains("source"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void The_corpus_is_p_code_and_the_library_reads_its_structure()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        var programs = Programs();
        Assert.Equal(42, programs.Count);

        int pcode = 0;
        foreach (string program in programs)
        {
            var run = CliRun.Run("vb6", program, "--json");
            Assert.True(run.ExitCode == 0, $"{program}: {run.All}");
            var document = JsonDocument.Parse(run.StandardOutput).RootElement;
            if (document.GetProperty("project").GetProperty("isa").GetString() == "vb6-pcode")
            {
                pcode++;
            }
        }

        // Every one of them: the corpus is a p-code build of each program, which is what makes it the
        // specimen this work was missing.
        Assert.Equal(42, pcode);
    }

    [Fact]
    public void Every_procedure_of_the_corpus_decodes_to_the_descriptor_that_lengths_it()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        string runtime = Runtime()!;
        int files = 0;
        int procedures = 0;
        int exact = 0;
        int padded = 0;
        int ambiguous = 0;
        int unmeasured = 0;
        int emptySlots = 0;
        int notProcedures = 0;

        foreach (string program in Programs())
        {
            var document = RunJson("pcode", program, "--runtime", runtime, "--json");
            var summary = document.GetProperty("summary");
            files++;
            procedures += summary.GetProperty("procedures").GetInt32();
            exact += summary.GetProperty("exact").GetInt32();
            padded += summary.GetProperty("padded").GetInt32();
            emptySlots += summary.GetProperty("empty_slots").GetInt32();
            notProcedures += summary.GetProperty("entries_not_procedures").GetInt32();

            foreach (var row in document.GetProperty("procedures").EnumerateArray())
            {
                switch (row.GetProperty("status").GetString())
                {
                    case "exact":
                    case "padded":
                        break;
                    case "ambiguous":
                        ambiguous++;
                        break;
                    default:
                        unmeasured++;
                        Assert.Fail($"{program}: {row.GetProperty("object").GetString()}[{row.GetProperty("method").GetInt32()}] unread: {row.GetProperty("problems")[0]}");
                        break;
                }
            }
        }

        Assert.Equal(42, files);
        Assert.Equal(680, procedures);

        // Every procedure of every program decodes to its own descriptor, and none is left ambiguous:
        // a stream that ends in the compiler's alignment is read as ending there, and the ambiguous
        // count this used to assert — seven streams whose opcode the tables gave two lengths for — is
        // zero now that the tables measure one length for 0x09. Before those fixes 100 of the 680 were
        // undecodable and a further seven were read two ways.
        Assert.Equal(222, exact);
        Assert.Equal(458, padded);
        Assert.Equal(0, ambiguous);
        Assert.Equal(0, unmeasured);

        // A method table is longer than the object's procedures where the compiler put the form's
        // implicit members in it: 89 slots hold no address at all, and 102 entries named another
        // object, which is how a slot is told from a procedure.
        Assert.Equal(89, emptySlots);
        Assert.Equal(102, notProcedures);
    }

    [Fact]
    public void A_program_reads_as_the_procedures_its_source_declares()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        // Mandelbrot.frm declares seven Subs and one Function, and the program holds eight procedures.
        var document = RunJson(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"), "--runtime", Runtime()!, "--json");

        var summary = document.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("objects").GetInt32());
        Assert.Equal(8, summary.GetProperty("procedures").GetInt32());
        Assert.Equal(1, summary.GetProperty("empty_slots").GetInt32());
        Assert.Equal(1036, summary.GetProperty("instructions").GetInt32());
        Assert.Equal(2960, summary.GetProperty("code_bytes").GetInt32());

        var rows = document.GetProperty("procedures").EnumerateArray().ToList();
        Assert.All(rows, r => Assert.Equal("frmFractal", r.GetProperty("object").GetString()));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], rows.Select(r => r.GetProperty("method").GetInt32()));

        // The method the form calls on click: the biggest one, where the fractal is drawn.
        var redraw = rows.Single(r => r.GetProperty("method").GetInt32() == 1);
        Assert.Equal(0x30F0u, redraw.GetProperty("code_rva").GetUInt32());
        Assert.Equal(968, redraw.GetProperty("code_size").GetInt32());
        Assert.Equal(324, redraw.GetProperty("instructions").GetInt32());

        // Every procedure but one ends with an exit opcode, and the one that does not is the Function:
        // its last bytes are the code that hands the result back.
        Assert.Equal(7, rows.Count(r => r.GetProperty("ends_with_exit").GetBoolean()));
    }

    [Fact]
    public void A_selected_procedure_lists_its_instructions_and_where_it_ends()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        var document = RunJson(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"),
            "--runtime", Runtime()!, "--object", "frmFractal", "--procedure", "7", "--json");

        // The table lists the object's procedures; the instruction list is the selected one's.
        var row = document.GetProperty("procedures").EnumerateArray()
            .Single(r => r.GetProperty("method").GetInt32() == 7);
        Assert.Equal(0x273Cu, row.GetProperty("code_rva").GetUInt32());
        Assert.Equal(36, row.GetProperty("code_size").GetInt32());
        Assert.Equal(14, row.GetProperty("instructions").GetInt32());
        Assert.Equal(1, row.GetProperty("padding").GetInt32());
        Assert.Equal("padded", row.GetProperty("status").GetString());
        Assert.True(row.GetProperty("ends_with_exit").GetBoolean());

        var instructions = document.GetProperty("instructions").EnumerateArray().ToList();
        Assert.Equal(14, instructions.Count);
        Assert.Equal(0x273Cu, instructions[0].GetProperty("rva").GetUInt32());
        Assert.Equal("0x04", instructions[0].GetProperty("opcode").GetString());
        Assert.Equal(3, instructions[0].GetProperty("size").GetInt32());

        // The stream ends with ExitProcHresult, and one byte of alignment follows it.
        Assert.Equal("0x13", instructions[^1].GetProperty("opcode").GetString());
        Assert.Equal(1, instructions[^1].GetProperty("size").GetInt32());
        Assert.Equal(0x275Eu, instructions[^1].GetProperty("rva").GetUInt32());

        // The instructions account for all but the padding, which is what "padded" says.
        Assert.Equal(
            row.GetProperty("code_size").GetInt32(),
            instructions.Sum(i => i.GetProperty("size").GetInt32()) + row.GetProperty("padding").GetInt32());
    }

    /// <summary>
    /// A listing that shows where a branch goes, and the check that the place is real.
    ///
    /// The instruction listing used to be an opcode and a length. The bytes after the opcode are read
    /// from the runtime's own handlers now, so a listing carries the instruction as it lies in the
    /// stream, the operand as the handler reads it, and — for a branch — where it goes: the
    /// procedure's code address plus the word, which is the arithmetic the interpreter does. That
    /// arithmetic is what the corpus is held to: every branch of every procedure of the 42 programs
    /// lands on an instruction start, and this test asks a listing to show it.
    /// </summary>
    [Fact]
    public void A_branch_in_a_listing_says_where_it_goes_and_lands_on_an_instruction()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        var document = RunJson(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"),
            "--runtime", Runtime()!, "--object", "frmFractal", "--procedure", "1", "--json");

        var row = document.GetProperty("procedures").EnumerateArray()
            .Single(r => r.GetProperty("method").GetInt32() == 1);
        uint start = row.GetProperty("code_rva").GetUInt32();
        uint end = start + (uint)row.GetProperty("code_size").GetInt32();

        var instructions = document.GetProperty("instructions").EnumerateArray().ToList();
        Assert.Equal(324, instructions.Count);

        // The bytes are the bytes: three for a branch, five for the Next that takes two fields.
        var branch = instructions.Single(i => i.GetProperty("rva").GetUInt32() == 0x32BCu);
        Assert.Equal("1c d8 01", branch.GetProperty("bytes").GetString());
        Assert.Equal("0x1C", branch.GetProperty("opcode").GetString());
        Assert.Equal("target", branch.GetProperty("operand").GetString());
        Assert.Equal(2, branch.GetProperty("operand_bytes").GetInt32());
        Assert.Equal(0, branch.GetProperty("operand_at").GetInt32());

        // `start + 0x01D8` is where the interpreter would dispatch: an instruction of this procedure.
        Assert.Equal(start + 0x01D8u, branch.GetProperty("target").GetUInt32());
        Assert.Equal(0x32C8u, branch.GetProperty("target").GetUInt32());
        Assert.True(branch.GetProperty("target_is_an_instruction_start").GetBoolean());

        // A Next names two words and both are reported: the frame slot of the counter it counts
        // (`fe e4` is -0x11C) and, after it, the distance it branches by.
        var next = instructions.Single(i => i.GetProperty("rva").GetUInt32() == 0x32FAu);
        Assert.Equal("66 e4 fe 98 01", next.GetProperty("bytes").GetString());
        Assert.Equal("target", next.GetProperty("operand").GetString());
        Assert.Equal(2, next.GetProperty("operand_at").GetInt32());
        Assert.Equal(0, next.GetProperty("frame_slot_at").GetInt32());
        Assert.Equal(-0x11C, next.GetProperty("frame_slot").GetInt32());
        Assert.Equal(0x3288u, next.GetProperty("target").GetUInt32());
        Assert.True(next.GetProperty("target_is_an_instruction_start").GetBoolean());

        // A frame slot is read as a slot and not as an address: `74 14 ff` is -0xEC.
        var slot = instructions.Single(i => i.GetProperty("rva").GetUInt32() == 0x30F9u);
        Assert.Equal("slot", slot.GetProperty("operand").GetString());
        Assert.Equal(-0xEC, slot.GetProperty("frame_slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("target").ValueKind);

        // Every branch of the procedure, and there are enough of them for the check to mean something.
        var targets = instructions.Where(i => i.GetProperty("operand").GetString() == "target").ToList();
        Assert.Equal(12, targets.Count);
        foreach (var target in targets)
        {
            Assert.True(target.GetProperty("target_is_an_instruction_start").GetBoolean(),
                $"0x{target.GetProperty("rva").GetUInt32():X}: goes to 0x{target.GetProperty("target").GetUInt32():X}");
            uint to = target.GetProperty("target").GetUInt32();
            Assert.InRange(to, start, end - 1);
        }

        // The same in the listing a person reads: the bytes, and where the branch goes.
        var text = CliRun.Run(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"),
            "--runtime", Runtime()!, "--object", "frmFractal", "--procedure", "1");
        Assert.True(text.ExitCode == 0, text.All);
        Assert.Contains("0x32BC  1c d8 01", text.StandardOutput);
        Assert.Contains("+0x1D8 -> 0x32C8", text.StandardOutput);
        Assert.Contains("frame[-0xEC]", text.StandardOutput);
        Assert.Contains("frame[-0x11C] then +0x198 -> 0x3288", text.StandardOutput);
    }

    /// <summary>
    /// The same check over the corpus, on a sample: the three procedures with the most instructions in
    /// every tenth program, because branches are not spread evenly and the richest bodies are where
    /// they are. The whole corpus was measured the same way — 845 branch operands, every one landing
    /// on an instruction start — and this is the part of that measurement the suite holds.
    /// </summary>
    [Fact]
    public void Every_branch_of_a_corpus_sample_lands_on_an_instruction()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        string runtime = Runtime()!;
        var programs = Programs();
        int targets = 0;
        int procedures = 0;

        for (int i = 0; i < programs.Count; i += 10)
        {
            var document = RunJson("pcode", programs[i], "--runtime", runtime, "--json");
            var richest = document.GetProperty("procedures").EnumerateArray()
                .Where(p => p.GetProperty("instructions").GetInt32() > 0)
                .OrderByDescending(p => p.GetProperty("instructions").GetInt32())
                .Take(3)
                .ToList();

            foreach (var procedure in richest)
            {
                procedures++;
                var listed = RunJson(
                    "pcode", programs[i], "--runtime", runtime,
                    "--object", procedure.GetProperty("object").GetString()!,
                    "--procedure", procedure.GetProperty("method").GetInt32().ToString(),
                    "--json");

                uint start = procedure.GetProperty("code_rva").GetUInt32();
                uint end = start + (uint)procedure.GetProperty("code_size").GetInt32();
                foreach (var instruction in listed.GetProperty("instructions").EnumerateArray())
                {
                    if (instruction.GetProperty("operand").GetString() != "target")
                    {
                        continue;
                    }

                    targets++;
                    uint to = instruction.GetProperty("target").GetUInt32();
                    Assert.True(instruction.GetProperty("target_is_an_instruction_start").GetBoolean(),
                        $"{programs[i]} 0x{instruction.GetProperty("rva").GetUInt32():X} -> 0x{to:X}");
                    Assert.InRange(to, start, end - 1);
                }
            }
        }

        Assert.Equal(15, procedures);
        Assert.True(targets >= 50, $"only {targets} branch operands in the sample");
    }

    [Fact]
    public void The_runtime_the_lengths_came_from_is_reported_with_its_version()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        var document = RunJson(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"), "--runtime", Runtime()!, "--json");

        Assert.EndsWith("msvbvm60.dll", document.GetProperty("runtime").GetString(), StringComparison.OrdinalIgnoreCase);

        // A number read out of the runtime's own version resource: without it, a listing would be
        // attributable to a runtime rather than to "some MSVBVM60".
        Assert.Equal("6.00.9848", document.GetProperty("runtime_version").GetString());
        Assert.Empty(document.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public void The_document_matches_its_schema()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        var run = CliRun.Run(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"), "--runtime", Runtime()!, "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("matches schema", run.All);
    }

    [Fact]
    public void A_native_program_is_refused_rather_than_read_as_p_code()
    {
        if (Runtime() is null)
        {
            return;
        }

        string? inputs = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
        string visdata = Path.Combine(inputs!, "VISDATA.EXE");
        if (!File.Exists(visdata))
        {
            return;
        }

        var run = CliRun.Run("pcode", visdata, "--runtime", Runtime()!);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("native build", run.All, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recon disasm", run.All);
    }

    [Fact]
    public void A_runtime_is_required_and_its_absence_says_which_option_names_one()
    {
        if (Corpus() is null)
        {
            return;
        }

        // A copy in a directory of its own, with no runtime beside it: the lengths cannot be measured,
        // and the command says so instead of decoding against nothing.
        string directory = Path.Combine(Path.GetTempPath(), "recon-pcode-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            string program = Path.Combine(directory, "Mandelbrot.exe");
            File.Copy(Program("vb6-code/Mandelbrot/Mandelbrot.exe"), program);

            var run = CliRun.Run("pcode", program);

            Assert.Equal(1, run.ExitCode);
            Assert.Contains("--runtime=", run.All);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_procedure_that_is_not_there_is_a_usage_error()
    {
        if (Runtime() is null || Corpus() is null)
        {
            return;
        }

        var run = CliRun.Run(
            "pcode", Program("vb6-code/Mandelbrot/Mandelbrot.exe"),
            "--runtime", Runtime()!, "--object", "frmFractal", "--procedure", "99");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("no procedure 99", run.All);
    }
}
