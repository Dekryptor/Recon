using Recon.Images;
using Recon.Tests.Fixtures;
using Recon.Pe;
using Recon.Vb6;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The p-code interpreter's dispatch tables, read out of a Visual Basic runtime.
///
/// The opcode tables in circulation are reconstructions of an unpublished format, and this does not
/// add another one: it reads the tables that are *in the runtime*, as data. The test that matters is
/// against the real <c>MSVBVM60.DLL</c>, which is why it is here rather than in a fixture — a
/// synthetic dispatch proves the reader works on input written by the reader. It skips without
/// <c>RECON_VB6_INPUTS</c>.
/// </summary>
public class PcodeRuntimeTests
{
    private static string? RuntimePath
    {
        get
        {
            string? directory = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            string candidate = Path.Combine(directory, "msvbvm60.dll");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    private static PcodeRuntime? ReadRuntime()
    {
        string? path = RuntimePath;
        if (path is null)
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(path);
        var pe = PeLoader.LoadBytes(bytes, path);
        if (pe.Image is null)
        {
            return null;
        }

        var sections = pe.Image.Sections
            .Select(s => new PcodeSection(s.Name, s.Rva, s.RawOffset, s.VirtualSize))
            .ToList();

        return PcodeRuntime.Read(bytes, path, pe.Image.ImageBase, sections);
    }

    /// <summary>
    /// The Visual Basic 6 runtime has the interpreter, and the interpreter has six dispatch tables,
    /// because an opcode is a byte: the primary table, indexed by the first byte of an instruction,
    /// and five more, each indexed by the byte *after* one of the five lead bytes 0xFB to 0xFF.
    ///
    /// Six, and the six is read rather than assumed. This asserted five while the lead bytes' tables
    /// were found only by hand, from the sites at the interpreter's entry; the sixth — 0xFF's, reached
    /// from a *guarded* dispatcher (`cmp eax, 46h` before the jump, so the site does not look like the
    /// others) — is what a scan that only matched the plain eleven-byte form could not see.
    /// </summary>
    [Fact]
    public void The_visual_basic_runtime_dispatches_p_code_through_six_byte_indexed_tables()
    {
        var runtime = ReadRuntime();
        if (runtime is null)
        {
            return;
        }

        Assert.True(runtime.IsPcodeRuntime, string.Join("; ", runtime.Problems));

        // The tables are named by the sites that use them, and there are six distinct ones.
        Assert.Equal(6, runtime.Tables.Count);
        Assert.All(runtime.Tables, t => Assert.Equal(256, t.Entries));
        Assert.All(runtime.Tables, t => Assert.Equal(256, t.Handlers.Count));

        // The handlers live in the engine section, not in the ordinary code section: the interpreter
        // is part of the runtime, and the runtime says so in its own section table.
        Assert.Equal("ENGINE", runtime.SectionOf(runtime.Tables[0].Handlers.First(h => h != 0)));

        // One table is reached from hundreds of sites — every handler dispatches the next opcode
        // through it when it finishes, which is what makes this a threaded interpreter and not five
        // unrelated switches. The other four are reached once each, from consecutive sites.
        var primary = runtime.PrimaryTable;
        Assert.NotNull(primary);
        Assert.True(primary!.UsedBySites > 200, $"the main table is reached from only {primary.UsedBySites} sites");
        Assert.Equal(5, runtime.Tables.Count(t => !t.IsPrimary));
        Assert.All(runtime.Tables.Where(t => !t.IsPrimary), t => Assert.Equal(1, t.UsedBySites));

        // The sites sharing one table, plus the five the interpreter's entry uses: the count is a
        // fact about the file, so it is asserted as one.
        Assert.Equal(294, runtime.DispatchSites);

        // One of the five is guarded, and the file says how many slots it has: `cmp eax, 46h` before
        // the jump means the interpreter rejects everything above 0x46 as an opcode, so the table is
        // 71 entries long and the rest of what lies after it in the file is not a table at all. Read
        // from the guard rather than assumed, because reading 256 slots there measures whatever the
        // assembler put next — bytes that look like handlers and point into `.data`.
        var guarded = runtime.Tables.Where(t => !t.IsPrimary).Single(t => t.IndexBound != 256);
        Assert.Equal(0x47, guarded.IndexBound);
        Assert.Equal(71, guarded.ValidEntries);
        Assert.All(runtime.Tables.Where(t => t.IsPrimary || t != guarded), t => Assert.Equal(256, t.IndexBound));
    }

    /// <summary>
    /// The test that settles which table is the opcode table, and it settles it by counting rather
    /// than by argument: the main table gives 256 opcodes **252 distinct handlers**, so an opcode and
    /// a handler are the same thing there. The four the interpreter's entry uses give 165, 188, 204
    /// and 168, and each of them sends a large block of its slots to one shared handler — they are
    /// dispatches on something with many invalid cases, not on the opcode.
    ///
    /// That is also the shape of a real instruction set: one entry per operation, and separately a
    /// small number of things to do with a value whose type is not something the runtime handles.
    /// </summary>
    [Fact]
    public void The_main_table_gives_almost_every_opcode_its_own_handler_and_the_others_do_not()
    {
        var runtime = ReadRuntime();
        if (runtime is null)
        {
            return;
        }

        var primary = runtime.PrimaryTable;
        Assert.NotNull(primary);

        int primaryDistinct = primary!.Handlers.Where(h => h != 0).Distinct().Count();
        Assert.True(primaryDistinct >= 250, $"the main table has only {primaryDistinct} distinct handlers for 256 opcodes");

        // Nothing is dominant there: no handler covers more than a handful of opcodes, so there is no
        // "invalid opcode" case in this table at all.
        Assert.True(primary.MostSharedCount <= 4, $"one handler covers {primary.MostSharedCount} opcodes in the main table");

        // The five others are the opposite shape: a large block of slots goes to one handler.
        foreach (var table in runtime.Tables.Where(t => !t.IsPrimary))
        {
            Assert.True(table.MostSharedCount >= 16, $"table 0x{table.TableRva:X} shares its top handler with only {table.MostSharedCount} slots");

            int distinct = table.Handlers.Where(h => h != 0).Distinct().Count();
            Assert.True(distinct < primaryDistinct, $"table 0x{table.TableRva:X} looks more like an opcode table than the main one does");
        }

        // And they agree on where an unhandled case goes, which is how "the interpreter does not
        // implement this" is told from "the interpreter does not have this at all". The main table
        // takes no part in that agreement — it has nothing to reject.
        Assert.NotNull(runtime.UnhandledCaseHandlerRva);
        Assert.Equal("ENGINE", runtime.SectionOf(runtime.UnhandledCaseHandlerRva!.Value));
    }

    /// <summary>
    /// The tables are read out of a file, so the number that comes back is a fact about the file and
    /// not about the reader: 1,280 slots in five tables of 256 plus the guarded one's 71, and the count
    /// of distinct handlers has to be in a range a dispatch can actually have — more than one
    /// interpreter entry point but far fewer than one per opcode, since many opcodes differ only in
    /// their operand.
    /// </summary>
    [Fact]
    public void The_tables_hold_more_than_one_handler_but_far_fewer_than_one_per_opcode()
    {
        var runtime = ReadRuntime();
        if (runtime is null)
        {
            return;
        }

        // Six tables of a byte-indexed size, one of them guarded short by the interpreter itself.
        // Unfilled slots are reported rather than counted as opcodes, so the measured total is at most
        // the sum of the bounds and never more.
        Assert.Equal(runtime.Tables.Count * 256, runtime.Tables.Sum(t => t.Entries));
        Assert.Equal(1351, runtime.Tables.Sum(t => t.IndexBound));
        Assert.True(runtime.OpcodeSlots > 1000, $"only {runtime.OpcodeSlots} opcode slots hold a handler");
        Assert.True(runtime.OpcodeSlots <= 1351);

        Assert.True(runtime.DistinctHandlers > 100, $"only {runtime.DistinctHandlers} distinct handlers");
        Assert.True(
            runtime.DistinctHandlers < runtime.OpcodeSlots,
            "every slot holding its own handler would mean nothing was dispatched on");
    }

    /// <summary>
    /// A handler is an address in code, and the file's own section table says which parts of the image
    /// those are. The sixth table's slots past the bound the interpreter guards it with point at
    /// strings and relocation tables in `.data`, and reading those as handlers measures data. The
    /// reader is handed the sections; this is the check that it uses them.
    /// </summary>
    [Fact]
    public void A_handler_that_does_not_point_at_code_is_not_a_handler()
    {
        var runtime = ReadRuntime();
        if (runtime is null)
        {
            return;
        }

        var guarded = runtime.Tables.Single(t => t.IndexBound != 256);
        Assert.All(guarded.Handlers.Where(h => h != 0), h =>
        {
            string section = runtime.SectionOf(h);
            Assert.True(section is ".text" or "ENGINE", $"handler {h:X} is in section {section}");
        });
        // All 71 are in the engine: the sixth table is dispatched from the interpreter's own code,
        // and the handlers it names — including the strcmp-like inner loops — live there too.
        Assert.Equal(71, guarded.Handlers.Count(h => h != 0));
        Assert.All(guarded.Handlers.Where(h => h != 0), h => Assert.Equal("ENGINE", runtime.SectionOf(h)));

        // And the count is the guard's: 71 slots of the 256 the table would have if an opcode byte
        // could index all of it.
        Assert.Equal(71, guarded.ValidEntries);
    }

    /// <summary>
    /// A runtime that is not Visual Basic has no such dispatch, and the reader must say so rather than
    /// find five tables in a file that has none. An ordinary Windows DLL is the control: the same code
    /// path, a real file, nothing there to find.
    /// </summary>
    [Fact]
    public void A_runtime_that_is_not_visual_basic_reports_that_it_has_no_dispatch()
    {
        // An ordinary Windows DLL as the control: the same code path over real bytes, with nothing
        // there to find. MSVCRT rather than the runtime, because the point is a file that is real
        // and also not the thing.
        string? directory = Environment.GetEnvironmentVariable("RECON_VB6_INPUTS");
        string? path = directory is null ? null : Path.Combine(directory, "MSVCRT.DLL");
        if (path is null || !File.Exists(path))
        {
            // No control binary on this machine: use the synthetic image, which has one section of
            // zeroes and therefore equally has no dispatch in it.
            byte[] synthetic = SyntheticPe.Build();
            var loaded = ImageLoader.LoadBytes(synthetic, "control.exe");
            Assert.NotNull(loaded.Image);
            Assert.NotNull(loaded.Image!.Pe);

            var none = PcodeRuntime.Read(
                synthetic,
                "control.exe",
                loaded.Image.Pe!.ImageBase,
                loaded.Image.Sections.Select(s => new PcodeSection(s.Name, s.Rva, s.RawOffset, s.VirtualSize)).ToList());

            Assert.False(none.IsPcodeRuntime);
            Assert.NotEmpty(none.Problems);
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        var pe = PeLoader.LoadBytes(bytes, path);
        Assert.NotNull(pe.Image);

        var runtime = PcodeRuntime.Read(
            bytes,
            path,
            pe.Image!.ImageBase,
            pe.Image.Sections.Select(s => new PcodeSection(s.Name, s.Rva, s.RawOffset, s.VirtualSize)).ToList());

        Assert.False(runtime.IsPcodeRuntime);
        Assert.NotEmpty(runtime.Problems);
    }
}
