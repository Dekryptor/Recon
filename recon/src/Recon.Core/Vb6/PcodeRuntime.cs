using System.Buffers.Binary;

namespace Recon.Vb6;

/// <summary>One section of the runtime image, with both addresses it has: mapped and stored.</summary>
public sealed record PcodeSection(string Name, uint Rva, uint RawOffset, uint Size, bool IsCode = false);

/// <summary>
/// One dispatch table in the p-code interpreter: the byte-indexed jump table an opcode is looked up
/// in, the site that jumps through it, and how many sites do.
/// </summary>
public sealed class PcodeDispatchTable
{
    /// <summary>The table's address, relative to the image base.</summary>
    public uint TableRva { get; init; }

    /// <summary>How many dispatch sites jump through this table.</summary>
    public int UsedBySites { get; init; }

    /// <summary>
    /// Entries read: 256, because the site indexes with a byte. Where the table is shorter than that
    /// — the interpreter bounds-checks one of them — the trailing slots point at something that is
    /// not a handler and are reported through <see cref="ValidEntries"/> rather than dropped.
    /// </summary>
    public int Entries { get; init; }

    /// <summary>How many of the entries are addresses of code. The rest are not opcodes.</summary>
    public int ValidEntries { get; init; }

    /// <summary>
    /// How many slots the interpreter can index with this table. A table shorter than 256 is guarded
    /// at its dispatch site — <c>cmp eax, 46h</c> then <c>ja</c> to the unhandled case — and the bound
    /// is read from that guard rather than assumed: what lies past the guard in the file is the next
    /// thing the assembler wrote, which can look like a table of handlers and is not one.
    /// <see cref="Handlers"/> has 256 entries either way, with the slots past the bound zero.
    /// </summary>
    public int IndexBound { get; init; } = 256;

    /// <summary>The handler each opcode goes to, indexed by opcode.</summary>
    public IReadOnlyList<uint> Handlers { get; init; } = [];

    /// <summary>
    /// The handler the most slots in this table share, and how many that is. Reported as measured
    /// rather than interpreted: in the main table it is 2 of 256, which says an opcode and a handler
    /// are the same thing there; in the others it is large, which says they dispatch on something
    /// with cases that go unhandled. <see cref="PcodeRuntime.UnhandledCaseHandlerRva"/> is where that
    /// reading is made, once, for the tables that agree on it.
    /// </summary>
    public uint? MostSharedHandlerRva { get; init; }

    public int MostSharedCount { get; init; }

    /// <summary>
    /// True for the table the interpreter's handlers use to continue the loop. There are hundreds of
    /// sites and one table between them, which is what a threaded interpreter looks like from the
    /// outside: every handler ends by dispatching the next opcode.
    /// </summary>
    public bool IsPrimary => UsedBySites > 1;
}

/// <summary>
/// The p-code interpreter inside a Visual Basic runtime, read as itself.
///
/// A VB6 program compiled to p-code is not machine code: it is a byte stream for the interpreter in
/// <c>MSVBVM60.DLL</c>, and what an opcode means is whatever that interpreter's dispatch table says.
/// The format was never published, which is why the opcode tables in circulation are somebody's
/// reconstruction of it. They do not have to be reconstructed: the tables are in the runtime, as
/// data, and this reads them from there.
///
/// A dispatch site is twelve bytes, and the same twelve bytes everywhere in the interpreter:
///
/// <code>
///   33 c0            xor  eax, eax
///   8a 06            mov  al, [esi]            ; the opcode byte
///   46               inc  esi                  ; past it
///   ff 24 85 &lt;imm32&gt;  jmp  dword ptr [eax*4+table]
/// </code>
///
/// There are 293 of them in <c>MSVBVM60.DLL</c>, and they name five distinct tables. 289 sites share
/// the first, because every handler dispatches the next opcode when it finishes; the other four are
/// used once each, from four consecutive sites at the interpreter's entry.
///
/// **The main table is the opcode table, and the count is what says so.** Its 256 entries hold 252
/// distinct handlers: an opcode and a handler are the same thing there, which is what an instruction
/// set looks like from the inside. The four others hold 165, 188, 204 and 168, and each sends a large
/// block of its slots to one shared handler — so they dispatch on something with cases that go
/// unhandled, not on the opcode. The Visual Basic 6 p-code instruction set is therefore 256 opcodes,
/// and each opcode's handler is an address in the runtime's engine section, read out of the shipped
/// file rather than taken from a table somebody reconstructed.
///
/// What the four secondary tables dispatch on — the type of an operand, or the shape of the value on
/// the stack — is not decided here, and saying it would be the kind of guess this reader exists to
/// replace.
/// </summary>
public sealed class PcodeRuntime
{

    /// <summary>An opcode is a byte, so a slot is looked up by a value from 0 to 255.</summary>
    public const int OpcodesPerTable = 256;

    public string Path { get; init; } = string.Empty;

    public ulong ImageBase { get; init; }

    /// <summary>The sections, so a table address can be told from a handler address.</summary>
    public IReadOnlyList<PcodeSection> Sections { get; init; } = [];

    /// <summary>
    /// The dispatch tables, in the order the sites name them: the one 289 sites share first, then the
    /// four the interpreter's entry uses once each.
    /// </summary>
    public IReadOnlyList<PcodeDispatchTable> Tables { get; init; } = [];

    /// <summary>How many dispatch sites were found: 293 in the Visual Basic 6 runtime.</summary>
    public int DispatchSites { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>
    /// True when at least one dispatch table was found: the runtime knows what p-code is, and the
    /// opcodes it accepts are the ones in <see cref="Tables"/>.
    /// </summary>
    public bool IsPcodeRuntime => Tables.Count > 0;

    /// <summary>The interpreter's main table: the one every handler dispatches the next opcode through.</summary>
    public PcodeDispatchTable? PrimaryTable => Tables.FirstOrDefault(t => t.IsPrimary);

    /// <summary>
    /// Where the interpreter sends a case it does not handle.
    /// </summary>
    /// <remarks>
    /// Derived from the shape of the tables rather than assumed: it is the handler that most slots
    /// share in two or more of them, which is only true of a table with cases to reject. The main
    /// table does not contribute — it gives nearly every opcode its own handler, so nothing dominates
    /// there — and that is exactly why its absence is informative.
    /// </remarks>
    public uint? UnhandledCaseHandlerRva => Tables
        .Where(t => t.MostSharedCount >= 8)
        .GroupBy(t => t.MostSharedHandlerRva!.Value)
        .Where(g => g.Count() >= 2)
        .OrderByDescending(g => g.Sum(t => t.MostSharedCount))
        .Select(g => (uint?)g.Key)
        .FirstOrDefault();

    /// <summary>Every distinct handler the tables name, which is how many entry points the interpreter has.</summary>
    public int DistinctHandlers => Tables
        .SelectMany(t => t.Handlers)
        .Where(h => h != 0)
        .Distinct()
        .Count();

    /// <summary>Every opcode slot in every table.</summary>
    public int OpcodeSlots => Tables.Sum(t => t.ValidEntries);

    /// <summary>
    /// Reads the dispatch tables out of a loaded runtime image. <paramref name="bytes"/> is the whole
    /// file and <paramref name="sectionRanges"/> its sections as they are mapped and stored.
    /// </summary>
    public static PcodeRuntime Read(
        byte[] bytes,
        string path,
        ulong imageBase,
        IReadOnlyList<PcodeSection> sectionRanges)
    {
        var problems = new List<string>();
        var siteTables = new List<(uint Site, uint Table)>();

        foreach (int at in FindDispatchSites(bytes))
        {
            uint table = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 3, 4));
            if (table >= imageBase)
            {
                siteTables.Add(((uint)at, table - (uint)imageBase));
            }
        }

        // Some sites guard the index before jumping through the table — `cmp eax, 46h` `ja` the
        // unhandled case — and that guard is the table's length as the interpreter itself states it. A
        // site without one indexes with a whole byte. Where several sites name the same table the
        // narrowest bound wins: a site that would index past it can only be one the interpreter never
        // reaches with such an index.
        var bounds = new Dictionary<uint, int>();
        foreach (var (site, table) in siteTables)
        {
            int bound = GuardedBound(bytes, (int)site);
            if (bound < (bounds.TryGetValue(table, out int known) ? known : OpcodesPerTable))
            {
                bounds[table] = bound;
            }
        }

        var tables = new List<PcodeDispatchTable>();
        foreach (var group in siteTables.GroupBy(s => s.Table).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            long offset = RvaToOffset(sectionRanges, group.Key);
            if (offset < 0 || offset + (OpcodesPerTable * 4) > bytes.Length)
            {
                problems.Add($"the dispatch sites name table 0x{group.Key:X}, which is not in any section");
                continue;
            }

            int bound = bounds.TryGetValue(group.Key, out int guarded) ? guarded : OpcodesPerTable;
            var handlers = new List<uint>(OpcodesPerTable);
            int valid = 0;
            for (int i = 0; i < OpcodesPerTable; i++)
            {
                uint value = i < bound
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset + (i * 4), 4))
                    : 0;
                uint handler = value >= imageBase ? value - (uint)imageBase : 0;

                // A slot holds a handler only when it points at mapped code. An unfilled slot, a slot
                // past the bound the interpreter guards the index with, and a slot that points at data
                // all hold something else.
                if (handler != 0 && IsCode(sectionRanges, handler))
                {
                    valid++;
                    handlers.Add(handler);
                }
                else
                {
                    handlers.Add(0);
                }
            }

            var common = handlers.Where(h => h != 0).GroupBy(h => h).OrderByDescending(g => g.Count()).FirstOrDefault();

            tables.Add(new PcodeDispatchTable
            {
                TableRva = group.Key,
                UsedBySites = group.Count(),
                Entries = OpcodesPerTable,
                IndexBound = bound,
                ValidEntries = valid,
                Handlers = handlers,
                MostSharedHandlerRva = common?.Key,
                MostSharedCount = common?.Count() ?? 0,
            });
        }

        if (tables.Count == 0)
        {
            problems.Add($"{path} has no p-code dispatch: it is not a Visual Basic runtime, or not one this reads");
        }

        return new PcodeRuntime
        {
            Path = path,
            ImageBase = imageBase,
            Sections = sectionRanges,
            Tables = tables,
            DispatchSites = siteTables.Count,
            Problems = problems,
        };
    }

    /// <summary>
    /// The offset of the jump of every byte-opcode dispatch in the file — the <c>jmp [eax*4+table]</c>
    /// the last four bytes of which name the table.
    ///
    /// The plain form of a dispatch is four instructions that only make sense together, and most sites
    /// are that. But a handler may bounds-check the byte it read before it dispatches: the handler for
    /// 0xFF, which is a lead byte like the four before it, compares the byte against the number of
    /// cases its table has and sends anything above it to the unhandled-case handler. Requiring the
    /// bare sequence misses that site, and missing it loses a whole table — 71 opcodes that a program
    /// built with 0xFF in it is decoded against. So a site is what the jump says it is, with the read
    /// of the opcode through <c>esi</c> in the instructions before it, which is what tells a dispatch
    /// from any other jump through a table of addresses.
    /// </summary>
    private static IEnumerable<int> FindDispatchSites(byte[] bytes)
    {
        // ff 24 85 <table>: jmp dword ptr [eax*4+table]. The opcode is dispatched on eax, so the read
        // of it is the `mov al,[esi]` and the `inc esi` that the jump is reached through; both are
        // looked for in the instructions immediately before, and the jump's own bytes are the site.
        byte[] jump = [0xFF, 0x24, 0x85];
        for (int at = 0; at + 7 <= bytes.Length; at++)
        {
            if (bytes[at] != jump[0] || bytes[at + 1] != jump[1] || bytes[at + 2] != jump[2])
            {
                continue;
            }

            // The table address has to look like one: four-byte aligned, and too high in the file to
            // be a small immediate. Without this, the same eight bytes inside a string or a table
            // would be read as an instruction.
            uint table = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 3, 4));
            if ((table & 3) != 0 || table <= 0x10000 || !ReadsOpcodeIntoEax(bytes, at))
            {
                continue;
            }

            yield return at;
        }
    }

    /// <summary>
    /// Whether the instructions before <paramref name="at"/> read the byte opcode through <c>esi</c>
    /// into <c>eax</c> and move <c>esi</c> past it: <c>mov al,[esi]</c> then <c>inc esi</c>, in either
    /// order and with the bounds check a lead handler does in between. The read is what makes the jump
    /// a dispatch: both halves of the interpreter's loop are in the handler, next to each other.
    /// </summary>
    private static bool ReadsOpcodeIntoEax(byte[] bytes, int at)
    {
        const int Window = 16;

        bool read = false;
        bool advanced = false;

        for (int i = at - 1; i >= Math.Max(0, at - Window); i--)
        {
            if (bytes[i] == 0x8A && bytes[i + 1] == 0x06)                 // mov al,[esi]
            {
                read = true;
            }
            else if (bytes[i] == 0x46)                                    // inc esi
            {
                advanced = true;
            }

            if (read && advanced)
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// Whether an address is in code. A handler is code, and the sections say which parts of the image
    /// are: the sixth table of the Visual Basic 6 runtime has slots that point into <c>.data</c>, and a
    /// walk that reads those as handlers measures strings and relocation tables.
    ///
    /// A caller that marked no section as code is taken at its word for all of them, because a runtime
    /// whose sections came from somewhere that does not say is not a reason to find no handlers.
    /// </summary>
    private static bool IsCode(IReadOnlyList<PcodeSection> sections, uint rva)
    {
        bool anyMarked = sections.Any(s => s.IsCode);
        return sections.Any(s => rva >= s.Rva && rva < s.Rva + s.Size && (!anyMarked || s.IsCode));
    }

    /// <summary>
    /// The bound an interpreter states for a table index, read from the guard in front of a dispatch
    /// site: <c>cmp eax, 46h</c> / <c>ja</c> then <c>jmp [eax*4+table]</c>, which is the same code with
    /// one case per value up to 0x46 and everything above it sent to the unhandled case. No guard reads
    /// as a whole byte, which is what a table of 256 slots looks like: 256.
    /// </summary>
    private static int GuardedBound(byte[] bytes, int site)
    {
        // The conditional jump immediately in front of the site, and the comparison in front of that.
        int at = site;
        if (at >= 6 && bytes[at - 6] == 0x0F && (bytes[at - 5] & 0xF0) == 0x80)
        {
            at -= 6;
        }
        else if (at >= 2 && (bytes[at - 2] & 0xF0) == 0x70)
        {
            at -= 2;
        }
        else
        {
            return OpcodesPerTable;
        }

        if (at >= 3 && bytes[at - 3] == 0x83 && bytes[at - 2] == 0xF8)
        {
            return bytes[at - 1] + 1;
        }

        if (at >= 2 && bytes[at - 2] == 0x3C)
        {
            return bytes[at - 1] + 1;
        }

        return OpcodesPerTable;
    }

    /// <summary>
    /// Where an RVA is in the file. A section's raw offset is not always its RVA — the resource and
    /// relocation sections of this very runtime are mapped 4 KB later than they are stored — so the
    /// offset comes from the header rather than from an assumption about it.
    /// </summary>
    private static long RvaToOffset(IReadOnlyList<PcodeSection> sections, uint rva)
    {
        foreach (var section in sections)
        {
            if (rva >= section.Rva && rva < section.Rva + section.Size)
            {
                return section.RawOffset + (rva - section.Rva);
            }
        }

        return -1;
    }

    /// <summary>Which section an address is in, for saying where a handler lives.</summary>
    public string SectionOf(uint rva)
        => Sections.FirstOrDefault(s => rva >= s.Rva && rva < s.Rva + s.Size)?.Name ?? "not mapped";
}
