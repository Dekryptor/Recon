using System.Text.Json;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The p-code opcode reader, run against a real Visual Basic 6 runtime when one is present.
///
/// The runtime is MSVBVM60.DLL 6.00.9848, the build Visual Basic 6.0 SP6 shipped. What is checked
/// here is what the reader says about that file: how many tables it dispatches through and how each
/// one is paired with the lead byte that selects it, how many opcodes point at the interpreter's one
/// piece of code for a case it does not implement, the handlers of opcodes that were disassembled by
/// hand while the reader was written, the lengths the handlers themselves state, and — through the
/// CLI — that the report validates against its schema and that the filters narrow it.
///
/// The tests skip when the runtime is not on this machine, the way the other real-binary tests do.
/// Point <c>RECON_VB6_INPUTS</c> at a directory holding MSVBVM60.DLL and VISDATA.EXE.
/// </summary>
[Collection("cli")]
public class OpcodesTests
{
    /// <summary>The runtime's opcode table, as the reader finds it in 6.00.9848.</summary>
    private const uint ExpectedTableRva = 0x10AA24;

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
        Assert.True(run.ExitCode == 0, run.All);
        return JsonDocument.Parse(run.StandardOutput).RootElement;
    }

    private static List<JsonElement> Rows(JsonElement document)
        => document.GetProperty("opcodes").EnumerateArray().ToList();

    /// <summary>
    /// The rows of the primary table: the ones whose <c>lead</c> is null, which is where an opcode is
    /// the first byte of an instruction. Every other row belongs to a table selected by a lead byte,
    /// and there the opcode is the byte *after* the lead — the same byte number means a different
    /// instruction in each table, which is why a test about one opcode has to say which table.
    /// </summary>
    private static List<JsonElement> Primary(JsonElement document)
        => Rows(document).Where(r => r.GetProperty("lead").ValueKind == JsonValueKind.Null).ToList();

    private static List<JsonElement> WithLead(JsonElement document, int lead)
        => Rows(document)
            .Where(r => r.GetProperty("lead").ValueKind != JsonValueKind.Null
                && r.GetProperty("lead").GetInt32() == lead)
            .ToList();

    /// <summary>The measured length of one opcode from a `recon opcodes --json` run.</summary>
    private static int Length(List<JsonElement> rows, int opcode)
    {
        var row = rows.Single(r => r.GetProperty("opcode").GetInt32() == opcode);
        var size = row.GetProperty("instruction_size");
        return size.ValueKind == JsonValueKind.Null ? -1 : size.GetInt32();
    }

    [Fact]
    public void The_runtime_is_read_as_six_tables_and_every_lead_byte_is_paired_with_its_own()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var summary = document.GetProperty("summary");

        // Six tables of 256 rows: the primary one and one per lead byte, which is 1,536 rows. The
        // report used to publish the primary table alone, and a reader of it would decode a lead
        // instruction — `ff 2e` — as though the byte after the lead were an opcode of the primary
        // table, which it is not.
        Assert.Equal(ExpectedTableRva, document.GetProperty("table_rva").GetUInt32());
        Assert.Equal(6, summary.GetProperty("tables").GetInt32());
        Assert.Equal(1536, summary.GetProperty("rows").GetInt32());
        Assert.Equal(256, summary.GetProperty("opcodes").GetInt32());
        Assert.Equal(1536, document.GetProperty("opcodes").GetArrayLength());

        var tables = document.GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal(6, tables.Count);

        // The primary table, then the five lead bytes in order — and each pairing is *evidence*, also
        // in the report: the primary opcodes whose handlers dispatch through that table. 0xFB's
        // handler jumps through 0x10AE24, so 0xFB is that table's lead byte, and so on for the rest.
        Assert.Equal(JsonValueKind.Null, tables[0].GetProperty("lead").ValueKind);
        Assert.Equal(ExpectedTableRva, tables[0].GetProperty("table_rva").GetUInt32());
        Assert.Empty(tables[0].GetProperty("claimed_by").EnumerateArray());

        var expected = new (int Lead, uint TableRva)[]
        {
            (0xFB, 0x10AE24), (0xFC, 0x10B224), (0xFD, 0x10B624), (0xFE, 0x10BA24), (0xFF, 0x10BE24),
        };
        for (int i = 0; i < expected.Length; i++)
        {
            var table = tables[i + 1];
            Assert.Equal(expected[i].Lead, table.GetProperty("lead").GetInt32());
            Assert.Equal(expected[i].TableRva, table.GetProperty("table_rva").GetUInt32());
            Assert.Equal([expected[i].Lead], table.GetProperty("claimed_by").EnumerateArray().Select(c => c.GetInt32()));
            Assert.Equal(1, table.GetProperty("dispatch_sites").GetInt32());
            Assert.Equal(256, table.GetProperty("opcodes").GetInt32());
        }

        // The version the file declares, out of its own version resource: the build every number in
        // this project's p-code findings is stated for.
        Assert.Equal("6.00.9848", document.GetProperty("runtime_version").GetString());
    }

    [Fact]
    public void Two_opcodes_have_no_handler_and_go_to_the_one_unhandled_case_slot()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var unhandled = Primary(document).Where(r => r.GetProperty("unhandled").GetBoolean()).ToList();

        // Two slots of the primary table go where the interpreter sends a case it does not implement.
        Assert.Equal([0x01, 0x03], unhandled.Select(r => r.GetProperty("opcode").GetInt32()).ToArray());

        // Both go to the same address: the interpreter has one piece of code for a case it does not
        // implement, and that is what makes those two slots unimplemented rather than unknown.
        Assert.Single(unhandled.Select(r => r.GetProperty("handler_rva").GetUInt32()).Distinct());

        // The sixth table is the guarded one, and its length is the interpreter's own statement about
        // it: everything above 0x46 is rejected as a case before the table is indexed, so the rows
        // past 0x46 are not opcodes — 202 of the 256 rows of that table are unhandled, and 71 are
        // implemented. Reading them as opcodes would be reading the next thing the assembler wrote.
        var guarded = WithLead(document, 0xFF);
        Assert.Equal(256, guarded.Count);
        Assert.Equal(202, guarded.Count(r => r.GetProperty("unhandled").GetBoolean()));
        Assert.Equal(71, guarded.Count(r => r.GetProperty("instruction_size").ValueKind != JsonValueKind.Null));
        Assert.All(guarded.Where(r => r.GetProperty("opcode").GetInt32() > 0x46), r =>
        {
            Assert.True(r.GetProperty("unhandled").GetBoolean());
            Assert.Equal(0u, r.GetProperty("handler_rva").GetUInt32());
        });
    }

    [Fact]
    public void Every_opcode_but_the_unhandled_ones_has_a_handler_inside_the_interpreter()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var rows = Primary(document).Where(r => !r.GetProperty("unhandled").GetBoolean()).ToList();

        Assert.Equal(254, rows.Count);

        // The handlers live in the section the interpreter's own code is in, so every one of them is
        // an address in this image and none of them is zero.
        foreach (var row in rows)
        {
            uint handler = row.GetProperty("handler_rva").GetUInt32();
            Assert.InRange(handler, 0x1000u, 0x1060000u);
        }

        // A few opcodes are aliases of another and share its handler, which is a fact about the table
        // rather than a fault in reading it.
        int shared = rows.GroupBy(r => r.GetProperty("handler_rva").GetUInt32()).Count(g => g.Count() > 1);
        Assert.InRange(shared, 0, 8);
    }

    [Fact]
    public void A_name_is_only_derived_where_the_handler_calls_exactly_one_thing()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var rows = Rows(document);

        foreach (var row in rows)
        {
            string name = row.GetProperty("name").GetString()!;
            var calls = row.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!).ToList();
            var reached = row.GetProperty("reached_calls").EnumerateArray().Select(c => c.GetString()!).ToList();
            string basis = row.GetProperty("basis").GetString()!;
            bool unhandled = row.GetProperty("unhandled").GetBoolean();

            if (unhandled)
            {
                // A slot the runtime does not handle is not an instruction, and gets no name: naming
                // it after the error path would describe the interpreter, not the opcode. 188 slots of
                // this runtime are that, and they were all named `RtlUnwind` before this rule.
                Assert.Equal(string.Empty, name);
                Assert.Contains("no instruction to name", basis, StringComparison.Ordinal);
                continue;
            }

            if (name.Length > 0)
            {
                // A derived name is the undecorated form of the call it came from — the runtime
                // function's own name, without the C decoration or the DLL it was imported from — and
                // the basis says which function, and whether the handler called it or reached it.
                var evidence = calls.Concat(reached).Select(Undecorated).ToList();
                Assert.Contains(name, evidence);
                Assert.True(
                    basis.Contains("calls", StringComparison.Ordinal) || basis.Contains("reaches", StringComparison.Ordinal),
                    basis);
                Assert.DoesNotContain("__", name);
                Assert.DoesNotContain("(", name);
            }
            else
            {
                Assert.DoesNotContain("the handler calls exactly", basis);
            }
        }

        int named = rows.Count(r => r.GetProperty("name").GetString()!.Length > 0);
        Assert.Equal(named, document.GetProperty("summary").GetProperty("named").GetInt32());
        Assert.True(named >= 10, $"only {named} opcodes were named");

        // A name is what a handler's own evidence says. The count is stated here so that a change in
        // it is a decision rather than a drift: 159 of 1,536 rows are named, 122 of them differently.
        Assert.Equal(159, named);
        Assert.Equal(122, document.GetProperty("summary").GetProperty("distinct_names").GetInt32());
    }

    /// <summary>The undecorated form of a call, as the report names it.</summary>
    private static string Undecorated(string call)
    {
        int decorated = call.IndexOf(" (", StringComparison.Ordinal);
        return (decorated > 0 ? call[..decorated] : call).TrimStart('_');
    }

    [Fact]
    public void An_opcode_the_runtime_does_not_handle_is_not_named_after_the_error_it_raises()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var rows = Rows(document).Where(r => r.GetProperty("unhandled").GetBoolean()).ToList();

        // 373 rows of the 1,536: 185 slots with no address at all, and 188 the runtime sends to the
        // one handler that raises. That handler's code straight-line calls into the unwind, so every
        // one of those rows has that call as evidence — which is how they came to be named after it.
        Assert.Equal(373, rows.Count);
        Assert.Equal(185, rows.Count(r => r.GetProperty("handler_rva").GetUInt32() == 0));
        Assert.Equal(188, rows.Count(r => r.GetProperty("handler_rva").GetUInt32() != 0));
        Assert.All(rows, r => Assert.Equal(string.Empty, r.GetProperty("name").GetString()));

        // The 188 share one handler, and it is the one the runtime's own guarded dispatcher rejects
        // with: `cmp eax, 46h; ja <here>`.
        var raise = rows.Where(r => r.GetProperty("handler_rva").GetUInt32() != 0)
            .Select(r => r.GetProperty("handler_rva").GetUInt32())
            .Distinct()
            .ToList();
        Assert.Single(raise);
        Assert.Equal(0x1105C2u, raise[0]);
    }

    [Fact]
    public void A_handler_that_calls_a_helper_is_named_for_what_the_helper_reaches()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");

        // NextEachVar's handler reads a word operand and calls a routine it loaded into a register —
        // `mov ebx, 66111940h` then `call ebx` — and that routine is a preface of pushes around
        // __vbaNextEachVar. The row is named for the export the helper reaches, and says so: the
        // evidence is second-hand and the report calls it second-hand.
        var nextEachVar = WithLead(document, 0xFE).Single(r => r.GetProperty("opcode").GetInt32() == 0x8C);
        Assert.Equal("vbaNextEachVar", nextEachVar.GetProperty("name").GetString());
        Assert.Equal([], nextEachVar.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!));
        Assert.Contains("__vbaNextEachVar", nextEachVar.GetProperty("reached_calls").EnumerateArray().Select(c => c.GetString()!));
        Assert.Contains("reaches", nextEachVar.GetProperty("basis").GetString()!, StringComparison.Ordinal);

        // A call through a register the path loaded with an address is a call to that address, and the
        // opcode is named for the export there: 0xFE 0x72 calls __vbaForEachCollVar directly, through
        // ebx, and is named for it.
        var forEachColl = WithLead(document, 0xFE).Single(r => r.GetProperty("opcode").GetInt32() == 0x72);
        Assert.Equal("vbaForEachCollVar", forEachColl.GetProperty("name").GetString());
        Assert.Contains("__vbaForEachCollVar", forEachColl.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!));

        // And a name reached through a helper is only used where the handler calls no export itself:
        // an observation is never replaced by a second-hand one.
        Assert.All(
            Rows(document).Where(r => r.GetProperty("name").GetString()!.Length > 0 && r.GetProperty("reached_calls").GetArrayLength() > 0),
            r => Assert.Equal(0, r.GetProperty("calls").GetArrayLength()));
    }

    [Fact]
    public void The_handlers_that_call_out_are_named_for_the_runtime_function_they_call()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var rows = Primary(document).ToDictionary(r => r.GetProperty("opcode").GetInt32());

        // Handlers disassembled by hand while the reader was written, and what they were seen to call.
        // The name is the function's own name with the C decoration taken off, which is how these are
        // written in circulation — and the call beside it keeps the decoration, because that is what
        // the file says.
        Assert.Equal("vbaStrCat", rows[0x2A].GetProperty("name").GetString());
        Assert.Equal("__vbaStrCat", rows[0x2A].GetProperty("calls").EnumerateArray().Single().GetString());
        Assert.Equal("vbaLenBstr", rows[0x4A].GetProperty("name").GetString());
        Assert.Equal("vbaErase", rows[0x5A].GetProperty("name").GetString());

        // An import the file names only by number is reported as the number it is and no name is
        // derived from it: a name that is not in the file is not this reader's to give.
        var ordinal = rows[0x43];
        Assert.Equal(string.Empty, ordinal.GetProperty("name").GetString());
        Assert.Contains("OLEAUT32.dll!#150", ordinal.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!));
        Assert.Contains("ordinal", ordinal.GetProperty("basis").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    // An opcode with no operand: the handler reads nothing and moves the pointer by the opcode byte.
    [InlineData(0x2A, 1)]
    // A one-byte operand: the fetch that reads the next opcode is already past it.
    [InlineData(0x00, 2)]
    // Two word operands read before the dispatch.
    [InlineData(0x0A, 5)]
    // A handler that reads its operand into the instruction pointer and goes there: the operand is
    // two bytes wide and the pointer never moves past it, so the width is what says the length.
    [InlineData(0x1E, 3)]
    // One that leaves by the interpreter's exit rather than dispatching.
    [InlineData(0x13, 1)]
    public void The_length_of_the_instruction_is_what_the_handler_moves_the_pointer_by(int opcode, int length)
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var row = Primary(document).Single(r => r.GetProperty("opcode").GetInt32() == opcode);

        Assert.Equal(length, row.GetProperty("instruction_size").GetInt32());
        Assert.Equal([length], row.GetProperty("sizes").EnumerateArray().Select(s => s.GetInt32()).ToArray());
    }

    /// <summary>
    /// What the bytes after an opcode are, as the runtime's own handlers read them.
    ///
    /// The reader's four kinds are not a table of names but a reading of the handler: <c>target</c> is
    /// the shape that computes the instruction stream's base plus the word and goes there,
    /// <c>slot</c> the one that addresses the frame by a sign-extended word, <c>none</c> an instruction
    /// whose handler reads nothing after the opcode, and <c>data</c> everything else. This checks the
    /// reading against rows that were disassembled by hand, the arithmetic that has to hold between
    /// the operands and the measured lengths, and that every basis names the instruction it was read
    /// from — so a kind is a fact with a place in the file attached to it.
    /// </summary>
    [Fact]
    public void What_the_bytes_after_an_opcode_are_is_read_from_the_handler()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var handled = document.GetProperty("opcodes").EnumerateArray()
            .Where(r => !r.GetProperty("unhandled").GetBoolean())
            .ToList();

        Assert.Equal(1163, handled.Count);
        var byKind = handled.GroupBy(r => r.GetProperty("operand").GetString()!)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new[] { "data", "none", "slot", "target" },
            byKind.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // `none` is the instruction that is the opcode byte alone, and the other three partition the
        // rest: every handled row is one of the four and says why.
        foreach (var row in handled)
        {
            string basis = row.GetProperty("operand_basis").GetString()!;
            Assert.NotEmpty(basis);

            int? size = row.GetProperty("instruction_size").ValueKind == JsonValueKind.Null
                ? null
                : row.GetProperty("instruction_size").GetInt32();
            int operandBytes = row.GetProperty("operand_bytes").GetInt32();
            int at = row.GetProperty("operand_at").GetInt32();
            bool counted = row.GetProperty("counted").GetBoolean();

            // A counted instruction's operand is a list whose length is in the stream: no number
            // describes it. Everything else is one byte less than the measured length — one byte less
            // in both tables, because a measured length counts from the opcode byte and a lead byte is
            // not part of any row's own measurement.
            if (counted)
            {
                Assert.Equal(-1, operandBytes);
                Assert.Equal(-1, row.GetProperty("operand_slot_at").GetInt32());
                continue;
            }

            if (size is int length)
            {
                Assert.Equal(length - 1, operandBytes);
            }

            int slotAt = row.GetProperty("operand_slot_at").GetInt32();
            Assert.True(slotAt == -1 || (slotAt >= 0 && slotAt + 2 <= operandBytes),
                $"opcode {row.GetProperty("opcode").GetInt32():X2}: slot at {slotAt} of {operandBytes}");

            // Every basis names the instruction it was read from, which is the difference between a
            // kind and an opinion: the reader used to state `data` without saying what it read, and
            // that was the one kind a reader of the report could not check.
            // `operand_bytes` is -1 where the handler's own paths do not agree on a length (a counted
            // list, or a row measured two ways), and there the basis is about that rather than about an
            // instruction.
            if (row.GetProperty("operand").GetString() is not "none" && operandBytes >= 0)
            {
                Assert.Contains(" at 0x", basis);
            }

            if (row.GetProperty("operand").GetString() is "target" or "slot")
            {
                // The word the kind is about is inside the operand.
                Assert.True(at >= 0 && at + 2 <= operandBytes, $"opcode {row.GetProperty("opcode").GetInt32():X2}: offset {at}");

                // A frame slot is never the same word as the branch: the two words of a For/Next are
                // different fields, and a reading that pointed both at one word would be saying the
                // counter's slot is where the instruction goes.
                if (row.GetProperty("operand").GetString() == "target" && slotAt >= 0)
                {
                    Assert.NotEqual(at, slotAt);
                }
            }
        }

        Assert.Equal(45, byKind["target"]);
        Assert.Equal(303, byKind["slot"]);
        Assert.Equal(330, byKind["data"]);
        Assert.Equal(485, byKind["none"]);

        // The three unconditional and conditional branches, read from the one handler that loads the
        // word into the instruction pointer and adds the stream's base to it, and the frame slot of
        // 0x1A, read from the instruction that sign-extends the word and addresses [ebx+ebp] by it.
        var primary = Primary(document).ToDictionary(r => r.GetProperty("opcode").GetInt32());
        foreach (int opcode in (int[])[0x1C, 0x1D, 0x1E])
        {
            Assert.Equal("target", primary[opcode].GetProperty("operand").GetString());
            Assert.Equal(0, primary[opcode].GetProperty("operand_at").GetInt32());
        }

        // A For/Next names two words: the frame slot of its counter at the operand's start and the
        // distance it branches by after it. Both are reported, at their own offsets.
        foreach (int opcode in (int[])[0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A])
        {
            Assert.Equal(0, primary[opcode].GetProperty("operand_slot_at").GetInt32());
        }

        // A plain branch names no frame slot, and a frame load's slot is the word its kind is about.
        Assert.Equal(-1, primary[0x1C].GetProperty("operand_slot_at").GetInt32());
        Assert.Equal("slot", primary[0x08].GetProperty("operand").GetString());
        Assert.Equal(0, primary[0x08].GetProperty("operand_slot_at").GetInt32());

        Assert.Equal("slot", primary[0x1A].GetProperty("operand").GetString());
        Assert.Equal("slot", primary[0x08].GetProperty("operand").GetString());
        Assert.Equal("data", primary[0x0D].GetProperty("operand").GetString());
        Assert.Equal("none", primary[0x13].GetProperty("operand").GetString());

        // A Next takes two fields — its counter's frame slot and then the distance it branches by — so
        // the word its target is read from is the second one. A reader that resolved the first would
        // be resolving a frame slot as an address, which is the mistake this offset exists to prevent.
        foreach (int opcode in (int[])[0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A])
        {
            Assert.Equal("target", primary[opcode].GetProperty("operand").GetString());
            Assert.Equal(2, primary[opcode].GetProperty("operand_at").GetInt32());
            Assert.Equal(4, primary[opcode].GetProperty("operand_bytes").GetInt32());
        }
    }

    [Fact]
    public void A_length_is_left_null_rather_than_chosen_where_the_handler_paths_disagree()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var rows = Primary(document);

        // Every row of the primary table has exactly one length. Two of them used to have more: 0x09,
        // the API call, was measured at 5 and 13 — the 13 being this reader reading the *next*
        // handler's operands, reached by falling through a call that does not return — and 0x0D, the
        // late-bound call, at 5 and 6, the 6 being an error path followed into shared code. Both are
        // one length now, and both are the length the reference states.
        Assert.DoesNotContain(rows, r => r.GetProperty("instruction_size").ValueKind == JsonValueKind.Null);
        foreach (var row in rows)
        {
            var sizes = row.GetProperty("sizes").EnumerateArray().Select(s => s.GetInt32()).ToList();
            Assert.Equal(
                row.GetProperty("instruction_size").ValueKind != JsonValueKind.Null,
                sizes.Count == 1);
        }

        // Three rows of the lead tables do have two lengths, and they are the same shape: a
        // conditional jump that may or may not have moved the pointer over a byte, with a dispatch on
        // each side of it. The report states both rather than choosing between them.
        var twoLengths = Rows(document)
            .Where(r => r.GetProperty("sizes").GetArrayLength() > 1)
            .Select(r => (Lead: (int?)r.GetProperty("lead").GetInt32(), Opcode: r.GetProperty("opcode").GetInt32()))
            .ToList();
        Assert.Equal([(0xFB, 0x87), (0xFB, 0x88), (0xFC, 0x85)], twoLengths);
        foreach (var (lead, opcode) in twoLengths)
        {
            var row = WithLead(document, lead!.Value).Single(r => r.GetProperty("opcode").GetInt32() == opcode);
            Assert.Equal([1, 2], row.GetProperty("sizes").EnumerateArray().Select(s => s.GetInt32()));
            Assert.Equal(JsonValueKind.Null, row.GetProperty("instruction_size").ValueKind);
        }

        Assert.Equal(5, Length(rows, 0x09));    // VCallAd: an API call, measured at one length
        Assert.Equal(1, Length(rows, 0x13));    // ExitProcHresult: leaves by the interpreter's exit
        Assert.Equal(1, Length(rows, 0x3B));    // Ary1StStrCopy: hands off without reading anything
        Assert.Equal(1, Length(rows, 0x45));    // Error
        Assert.Equal(1, Length(rows, 0xA9));    // AddI2
        Assert.Equal(3, Length(rows, 0x22));    // ImpAdLdPr: reads a 2-byte operand, then hands off
        Assert.Equal(3, Length(rows, 0x56));    // NewIfNullAd: reads its operand where it hands off

        // The five lead opcodes are one byte each, and their handlers sit twelve bytes apart: that is
        // where the runtime's five secondary dispatch tables are reached. All five are published, and
        // the report's own table list says which byte selects which table rather than this test
        // restating it.
        var leadOpcodes = new[] { 0xFB, 0xFC, 0xFD, 0xFE, 0xFF };
        var leads = leadOpcodes.Select(op => rows.Single(r => r.GetProperty("opcode").GetInt32() == op)).ToList();
        Assert.All(leads, l => Assert.Equal(1, l.GetProperty("instruction_size").GetInt32()));
        var handlers = leads.Select(l => l.GetProperty("handler_rva").GetUInt32()).ToList();
        for (int i = 1; i < handlers.Count; i++)
        {
            Assert.Equal(12u, handlers[i] - handlers[i - 1]);
        }
    }

    [Fact]
    public void The_report_validates_against_its_schema_and_the_filters_narrow_it()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        Assert.NotNull(BuiltInSchemas.Get("opcodes"));

        var checkedRun = CliRun.Run("opcodes", runtime, "--json", "--check-schema");
        Assert.True(checkedRun.ExitCode == 0, checkedRun.All);
        Assert.DoesNotContain("violation", checkedRun.All);

        var text = CliRun.Run("opcodes", runtime, "--opcode", "0x2A");
        Assert.True(text.ExitCode == 0, text.All);
        Assert.Contains("__vbaStrCat", text.StandardOutput);
        Assert.DoesNotContain("0x2B ", text.StandardOutput);

        // A byte is not an instruction on its own: 0x2A is one row in each of the six tables, and the
        // filter reports all six because the byte alone does not say which instruction is meant.
        var document = RunJson("opcodes", runtime, "--opcode", "42", "--json", "--check-schema");
        Assert.Equal(6, Rows(document).Count);
        Assert.All(Rows(document), r => Assert.Equal(0x2A, r.GetProperty("opcode").GetInt32()));
        Assert.Equal(6, Rows(document).Select(r => r.GetProperty("table_rva").GetUInt32()).Distinct().Count());

        // Naming the table as well as the byte picks one row out of the 1,536.
        var one = RunJson("opcodes", runtime, "--lead", "0xFF", "--opcode", "42", "--json", "--check-schema");
        Assert.Single(Rows(one));
        Assert.Equal(0xFF, Rows(one)[0].GetProperty("lead").GetInt32());
        Assert.Equal(0x10BE24u, Rows(one)[0].GetProperty("table_rva").GetUInt32());

        // The filters are a view, not a claim that the runtime has only one table or one opcode.
        Assert.Equal(256, one.GetProperty("summary").GetProperty("opcodes").GetInt32());
        Assert.Equal(1536, one.GetProperty("summary").GetProperty("rows").GetInt32());
    }

    /// <summary>
    /// The fixes that turned 100 undecodable procedures of the corpus into none, each pinned to the
    /// handler it was found in. Every one of these numbers is a fact about MSVBVM60.DLL 6.00.9848 that
    /// was wrong in an earlier revision of this reader, and the way it was wrong is worth keeping:
    /// each produced a length that looked plausible and made streams run past the descriptor they
    /// live in.
    /// </summary>
    [Fact]
    public void The_handlers_whose_lengths_were_wrong_are_measured_from_what_they_do()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");

        // 0x09, VCallAd: the API call used to be measured at 13 as well as 5. The 13 was the walk
        // stepping past a call that does not return and reading the operands of the handler the
        // assembler put next to it. One length now, and it is the one the reference gives.
        Assert.Equal(5, Length(Primary(document), 0x09));

        // 0xFD 0x63, FStVarNoPop: its own code is `push [esp]` `nop` then a jump into 0xFD 0x00's
        // handler, which reads a two-byte operand — so it is three bytes, and the jump has to be
        // followed for the reader to see the second and third.
        var fstVarNoPop = WithLead(document, 0xFD).Single(r => r.GetProperty("opcode").GetInt32() == 0x63);
        Assert.Equal(3, fstVarNoPop.GetProperty("instruction_size").GetInt32());

        // 0xFF 0x2F and 0x30, ExitProcCbHresult and ExitProcFrameCbHresult: their handlers read a
        // word operand and then restore esi from a register, which the walk cannot follow, so the
        // length is the operand the handler read — 5 and 7 — and not a pointer adjustment measured
        // after the restore.
        var guarded = WithLead(document, 0xFF);
        Assert.Equal(5, guarded.Single(r => r.GetProperty("opcode").GetInt32() == 0x2F).GetProperty("instruction_size").GetInt32());
        Assert.Equal(7, guarded.Single(r => r.GetProperty("opcode").GetInt32() == 0x30).GetProperty("instruction_size").GetInt32());

        // The 25 variable operations of the first lead table — ImpVar, EqvVar, ModVar and the
        // comparisons — read their operand at [esi] in the shared code they hand off to, so a path
        // that hands off is measured from what it read, not from where the shared code dispatches.
        // 254 of that table's 256 rows are measured at a single length; the two that are not are the
        // rows above, which are measured at two.
        var firstLead = WithLead(document, 0xFB);
        Assert.Equal(254, firstLead.Count(r => r.GetProperty("instruction_size").ValueKind != JsonValueKind.Null));
        Assert.Equal(3, firstLead.Single(r => r.GetProperty("opcode").GetInt32() == 0x07).GetProperty("instruction_size").GetInt32());
        Assert.Equal(3, firstLead.Single(r => r.GetProperty("opcode").GetInt32() == 0xA4).GetProperty("instruction_size").GetInt32());

        // The rows where this reader and the independent table disagree, pinned to the bytes that
        // settle them. 0xC3 and 0xC5 are the currency and date literals: the sub-handler loads 6 or 7
        // into bx and jumps to shared code that reads a word and two dwords and does `add esi,10h`,
        // so the instruction is eleven bytes — and the 6 and 7 are VT_CY and VT_DATE, which is the
        // naming the independent table itself uses for these two rows.
        var varLiterals = WithLead(document, 0xFE);
        Assert.Equal(11, varLiterals.Single(r => r.GetProperty("opcode").GetInt32() == 0xC3).GetProperty("instruction_size").GetInt32());
        Assert.Equal(11, varLiterals.Single(r => r.GetProperty("opcode").GetInt32() == 0xC5).GetProperty("instruction_size").GetInt32());

        // 0x8C, NextEachVar: the handler reads a word and calls a helper, and the code that call
        // leads to reads a second word — four operand bytes, five with the lead byte. The independent
        // table states 9 for this row.
        Assert.Equal(5, varLiterals.Single(r => r.GetProperty("opcode").GetInt32() == 0x8C).GetProperty("instruction_size").GetInt32());

        // 0x95 and 0x96, OnGosub and OnGoto: `movsx eax,[esi]` `add esi,2` `add esi,eax` skips a
        // table whose size is in the stream, so the instruction's length is not a constant at all.
        // The report gives the part the handler always consumes — three bytes — and the row is a
        // fact about those two opcodes rather than a mis-measurement to be closed.
        Assert.Equal(3, varLiterals.Single(r => r.GetProperty("opcode").GetInt32() == 0x95).GetProperty("instruction_size").GetInt32());
        Assert.Equal(3, varLiterals.Single(r => r.GetProperty("opcode").GetInt32() == 0x96).GetProperty("instruction_size").GetInt32());

        // 0xFF 0x06 and 0x07: the handler stores a continuation address into its frame and jumps to
        // shared code, which reads the operand and jumps through the stored address. The walk follows
        // the store, so the operand is read as this instruction's; it also keeps the incomplete path
        // out of the report, and the row is 5 rather than 1-and-5.
        Assert.Equal(5, guarded.Single(r => r.GetProperty("opcode").GetInt32() == 0x06).GetProperty("instruction_size").GetInt32());
        Assert.Equal(5, guarded.Single(r => r.GetProperty("opcode").GetInt32() == 0x07).GetProperty("instruction_size").GetInt32());

        // 0x0D, VCallHresult: reads a word and a word, one of them the call's own argument, and
        // dispatches five bytes on — which is what the reference states for it too.
        Assert.Equal(5, Length(Primary(document), 0x0D));
    }

    /// <summary>
    /// The opcodes that raise, and one that must not.
    ///
    /// The runtime raises by loading an error code and leaving: the handler writes the code into a
    /// register and jumps to the block whose first two instructions are `push` and `call` the runtime's
    /// raiser (66385 2Ch in 6.00.9848, which compares the code against 9C68h itself). 0x0D's handler does
    /// exactly that — `mov eax, 9C68h` at 0010A479 then `jmp 66108D50h` — so its row says it can raise.
    ///
    /// 0x1E is the counter-example the earlier rules got wrong in both directions: `Branch` has nothing
    /// to raise and must not be called raising because an address it jumps to is jumped to by many other
    /// handlers.
    /// </summary>
    [Fact]
    public void An_opcode_that_loads_an_error_code_and_leaves_can_raise_and_a_branch_does_not()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var document = RunJson("opcodes", runtime, "--json");
        var primary = Primary(document);

        // The report says which function it found, so the flag can be checked rather than trusted.
        Assert.Equal(0x3852C, document.GetProperty("summary").GetProperty("raiser_rva").GetInt32());

        JsonElement raise = primary.Single(r => r.GetProperty("opcode").GetInt32() == 0x0D);
        Assert.True(raise.GetProperty("raises").GetBoolean());

        JsonElement branch = primary.Single(r => r.GetProperty("opcode").GetInt32() == 0x1E);
        Assert.False(branch.GetProperty("raises").GetBoolean());
    }

    /// <summary>
    /// The stack effect of an opcode whose handler calls a runtime function that cleans its own
    /// arguments: <c>call __vbaStrCat</c> removes two slots through that function's <c>ret 8</c>, and the
    /// <c>push eax</c> after it puts one back, so 0x2A's effect is minus one.
    ///
    /// The same number is in the published opcode tables for VB6 p-code (pops 2, pushes 1), which is what
    /// makes this a reading of the runtime rather than a convention of this tool. The amount a call
    /// removes is read from the callee's own `ret N`, and that has to be read up to the first `ret` — the
    /// runtime is a run of small thunks with a `ret` at the end of each, and reading through into the next
    /// one is what used to make every handler's paths look like they disagreed with themselves.
    /// </summary>
    [Fact]
    public void An_opcode_that_calls_a_function_cleaning_its_own_arguments_has_the_net_effect_of_the_call()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var primary = Primary(RunJson("opcodes", runtime, "--json"));

        JsonElement concat = primary.Single(r => r.GetProperty("opcode").GetInt32() == 0x2A);
        Assert.Equal(-1, concat.GetProperty("stack_effect").GetInt32());
        Assert.Equal("vbaStrCat", concat.GetProperty("name").GetString());

        // 0x1E `Branch` moves nothing, and the opcode tables say the same.
        JsonElement branch = primary.Single(r => r.GetProperty("opcode").GetInt32() == 0x1E);
        Assert.Equal(0, branch.GetProperty("stack_effect").GetInt32());
    }

    /// <summary>
    /// Where a handler hands the next opcode back on every path it takes, the row says what stopped the
    /// amount being known — a call through a pointer, a stack pointer written by something the walk cannot
    /// follow, a called routine whose own returns disagree — rather than saying only that there is none.
    /// </summary>
    [Fact]
    public void A_row_without_a_stack_effect_says_which_path_stopped_it_being_known()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        var rows = Rows(RunJson("opcodes", runtime, "--json")).Where(r => !r.GetProperty("unhandled").GetBoolean()).ToList();
        var measured = rows.Where(r => r.GetProperty("stack_effect").ValueKind != JsonValueKind.Null).ToList();
        var unmeasured = rows.Where(r => r.GetProperty("stack_effect").ValueKind == JsonValueKind.Null).ToList();

        // Most of the runtime's handlers are measured, and every one that is not says why.
        Assert.True(measured.Count > rows.Count / 2, $"{measured.Count} of {rows.Count} measured");
        // Two rows are a third case: their paths do leave an opcode to be gone on to, but with different
        // amounts, so both amounts are stated instead of one.
        Assert.All(unmeasured, r =>
        {
            string basis = r.GetProperty("stack_basis").GetString()!;
            Assert.True(
                basis.Contains("have no single amount:", StringComparison.Ordinal)
                    || basis.Contains("leaves the interpreter", StringComparison.Ordinal)
                    || basis.Contains("paths disagree", StringComparison.Ordinal),
                basis);
        });
    }

    [Fact]
    public void An_opcode_that_is_not_a_byte_is_a_usage_error()
    {
        if (Input("msvbvm60.dll") is not string runtime)
        {
            return;
        }

        foreach (string bad in new[] { "zz", "0x300", "256" })
        {
            var run = CliRun.Run("opcodes", runtime, "--opcode", bad);
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("--opcode wants a number", run.All);

            var lead = CliRun.Run("opcodes", runtime, "--lead", bad);
            Assert.Equal(2, lead.ExitCode);
            Assert.Contains("--lead wants a number", lead.All);
        }

        // A lead byte no table is selected by is not a usage error but a question with no answer, and
        // it is refused rather than silently answered with the primary table's rows.
        var none = CliRun.Run("opcodes", runtime, "--lead", "0x01");
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("no table of this runtime is selected by 0x01", none.All);
    }

    [Fact]
    public void A_file_that_is_not_a_runtime_is_refused_rather_than_read()
    {
        if (Input("msvbvm60.dll") is null || Input("VISDATA.EXE") is not string program)
        {
            return;
        }

        var run = CliRun.Run("opcodes", program);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("not a Visual Basic runtime", run.All);
    }
}
