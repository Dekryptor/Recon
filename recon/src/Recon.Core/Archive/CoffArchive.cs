using System.Buffers.Binary;
using System.Text;
using Recon.Pe;

namespace Recon.Archive;

public enum ArchiveMemberKind
{
    /// <summary>One of the two symbol indexes the linker writes at the front of the archive.</summary>
    Linker,

    /// <summary>The <c>//</c> member: the string table that holds names too long for a header.</summary>
    Longnames,

    /// <summary>A COFF object file: compiled code, or data, that a link can pull in.</summary>
    Object,

    /// <summary>
    /// A short import member: not an object at all, but a record saying "this symbol comes from that
    /// DLL", which is what an import library is made of.
    /// </summary>
    Import,

    /// <summary>A member whose contents this tool does not read.</summary>
    Unknown,
}

/// <summary>One member of an <c>!&lt;arch&gt;</c> archive.</summary>
public sealed class ArchiveMember
{
    /// <summary>The name as the archive knows it, with the long-name table resolved.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The name exactly as the member header spells it, before any resolving.</summary>
    public string RawName { get; init; } = string.Empty;

    public ArchiveMemberKind Kind { get; init; }

    /// <summary>File offset of the member's contents.</summary>
    public long DataOffset { get; init; }

    public int Size { get; init; }

    /// <summary>The archive header's date field, which is a Unix timestamp in decimal.</summary>
    public long Date { get; init; }

    // ------------------------------------------------------------------ COFF object members

    /// <summary>The machine the COFF header names: <c>x86</c>, <c>x64</c>, or <c>0xNNNN</c>.</summary>
    public string Machine { get; init; } = string.Empty;

    public uint TimeDateStamp { get; init; }

    public IReadOnlyList<string> Sections { get; init; } = [];

    /// <summary>The object's own symbols: what it offers, and what it needs.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>
    /// The <c>@comp.id</c> value, which is the compiler and its build packed into one word:
    /// <c>(product id &lt;&lt; 16) | build</c>, the same encoding the PE Rich header uses.
    /// </summary>
    public uint? CompId { get; init; }

    /// <summary>The product id from <see cref="CompId"/>, named from the Rich header's table.</summary>
    public string? CompTool { get; init; }

    /// <summary>The build number from <see cref="CompId"/>.</summary>
    public int? CompBuild { get; init; }

    // --------------------------------------------------------------- short import members

    /// <summary>For an import member: the DLL the symbol is imported from.</summary>
    public string? ImportDll { get; init; }

    /// <summary>For an import member: the symbol the DLL exports.</summary>
    public string? ImportSymbol { get; init; }

    /// <summary>For an import member: the ordinal or name-table hint, when the member carries one.</summary>
    public int? ImportOrdinal { get; init; }

    /// <summary>
    /// What this member says about itself, in the words the rest of the tool uses: the compiler that
    /// built an object, or the DLL an import member points at.
    /// </summary>
    public string Describe() => Kind switch
    {
        ArchiveMemberKind.Object when CompTool is not null => $"{CompTool} build {CompBuild}",
        ArchiveMemberKind.Object => $"{Machine}, {Sections.Count} section(s)",
        ArchiveMemberKind.Import => $"import {ImportSymbol} from {ImportDll}",
        ArchiveMemberKind.Linker => "symbol index",
        ArchiveMemberKind.Longnames => "long name table",
        _ when Machine == "elf" => "an ELF object, not COFF: this is a Unix .a",
        _ => "not read",
    };
}

/// <summary>
/// A COFF archive: the <c>!&lt;arch&gt;</c> member format Microsoft's librarian writes, which is what
/// every <c>.lib</c> on Windows is — both static libraries of objects and import libraries, which are
/// made of short members naming a DLL and the symbols it exports.
///
/// It is not a program, and this tool does not pretend otherwise: an archive has no entry point, no
/// sections to decode and no addresses to speak of. What it has is a list of the objects a link can
/// pull in, and — in every object that carries one — an <c>@comp.id</c> record naming the compiler
/// that produced it, in the same <c>(product id &lt;&lt; 16) | build</c> words the PE Rich header
/// uses. That is the bill of materials of a runtime, and it is why this is here.
/// </summary>
public sealed class CoffArchive
{
    private const int HeaderSize = 60;
    private static readonly byte[] Magic = "!<arch>\n"u8.ToArray();

    /// <summary>The 20-byte COFF header, and the 18-byte symbol record, are the only fixed shapes.</summary>
    private const int CoffHeaderSize = 20;

    private const int SymbolSize = 18;
    private const int SectionHeaderSize = 40;

    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<ArchiveMember> Members { get; init; } = [];

    /// <summary>What could not be read, said out loud rather than dropped.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>True when the bytes begin with the eight-byte <c>!&lt;arch&gt;\n</c> signature.</summary>
    public static bool Sniff(byte[] bytes)
        => bytes.Length >= Magic.Length && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>
    /// The objects in the archive: the members a link can pull code from.
    /// </summary>
    public IEnumerable<ArchiveMember> Objects => Members.Where(m => m.Kind is ArchiveMemberKind.Object);

    /// <summary>
    /// The imports the archive describes, which for an import library is the whole point of it.
    /// </summary>
    public IEnumerable<ArchiveMember> Imports => Members.Where(m => m.Kind is ArchiveMemberKind.Import);

    /// <summary>
    /// Every compiler the archive's objects name, most-built first. A runtime library usually names
    /// more than one: the shipped objects were built by whichever release shipped them.
    /// </summary>
    public IReadOnlyList<string> CompilersUsed => Objects
        .Where(m => m.CompTool is not null)
        .GroupBy(m => $"{m.CompTool} build {m.CompBuild}")
        .OrderByDescending(g => g.Count())
        .Select(g => $"{g.Key} ({g.Count()} object{(g.Count() == 1 ? "" : "s")})")
        .ToList();

    public static CoffArchive Load(byte[] bytes, string path = "<memory>")
    {
        var problems = new List<string>();
        var members = new List<ArchiveMember>();

        if (!Sniff(bytes))
        {
            return new CoffArchive
            {
                Path = path,
                Problems = [$"{path} does not begin with the eight-byte !<arch> signature"],
            };
        }

        long offset = Magic.Length;
        string? longNames = null;
        int linkerMembers = 0;
        int elfObjects = 0;

        while (offset + HeaderSize <= bytes.Length)
        {
            var span = bytes.AsSpan((int)offset, HeaderSize);

            // The end marker is what makes this a sequential format: a header that does not end in
            // "`\n" is not a header, and the walk stops rather than reading past it.
            if (span[58] != 0x60 || span[59] != 0x0A)
            {
                problems.Add($"member at offset 0x{offset:X} has no header terminator; the archive ends here");
                break;
            }

            string rawName = Text(span[..16]).TrimEnd(' ');
            if (!TryDecimal(span[48..58], out int size) || size < 0)
            {
                problems.Add($"member \"{rawName}\" has an unreadable size; the archive ends here");
                break;
            }

            TryDecimal(span[16..28], out int rawDate);

            long dataOffset = offset + HeaderSize;
            if (dataOffset + size > bytes.Length)
            {
                problems.Add($"member \"{rawName}\" claims {size} bytes at 0x{dataOffset:X}, past the end of the file");
                break;
            }

            var data = bytes.AsSpan((int)dataOffset, size);
            string name = rawName;
            var kind = ArchiveMemberKind.Object;

            if (rawName is "/")
            {
                // Two members share this name: the first and second symbol indexes. Only their
                // byte order differs, and neither holds objects, so both are recorded as one kind.
                kind = ArchiveMemberKind.Linker;
                name = linkerMembers++ == 0 ? "first linker member" : "second linker member";
            }
            else if (rawName is "//")
            {
                kind = ArchiveMemberKind.Longnames;
                name = "long name table";
                longNames = Encoding.ASCII.GetString(data);
            }
            else if (rawName.StartsWith('/') && int.TryParse(rawName[1..], out int nameOffset) && longNames is not null)
            {
                name = LongName(longNames, nameOffset, rawName);
            }
            else if (rawName.EndsWith('/'))
            {
                name = rawName[..^1];
            }

            bool sawElf = false;
            members.Add(Describe(bytes, data, dataOffset, name, rawName, kind, rawDate, problems, ref sawElf));
            if (sawElf)
            {
                elfObjects++;
            }

            // Members are two-byte aligned: an odd size is followed by one padding byte.
            offset = dataOffset + size + ((size & 1) == 1 ? 1 : 0);
        }

        if (elfObjects > 0)
        {
            problems.Insert(0, $"{elfObjects} member(s) hold ELF objects: this is a Unix .a, and this reader reads COFF archives");
        }

        return new CoffArchive { Path = path, Members = members, Problems = problems };
    }

    private static ArchiveMember Describe(
        byte[] file,
        ReadOnlySpan<byte> data,
        long dataOffset,
        string name,
        string rawName,
        ArchiveMemberKind kind,
        long date,
        List<string> problems,
        ref bool elfSeen)
    {
        if (kind is not ArchiveMemberKind.Object)
        {
            return new ArchiveMember
            {
                Name = name,
                RawName = rawName,
                Kind = kind,
                DataOffset = dataOffset,
                Size = data.Length,
                Date = date,
            };
        }

        // A short import member is not a COFF object: it begins with the two words 0x0000 0xFFFF,
        // which no real COFF header can carry, and holds a name and a DLL instead of sections.
        if (data.Length >= 20 && BinaryPrimitives.ReadUInt16LittleEndian(data[..2]) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]) == 0xFFFF)
        {
            return ReadImportMember(data, dataOffset, name, rawName, date, problems);
        }

        // A Unix .a is the same container with different members: every one of them is an ELF
        // object. Reading a COFF header out of those bytes produces a machine called 0x457F and a
        // section count from the ELF class and endianness fields — a confident answer about a format
        // this reader does not have, which is the one thing it must not give.
        if (data.Length >= 4 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F')
        {
            elfSeen = true;
            return new ArchiveMember
            {
                Name = name,
                RawName = rawName,
                Kind = ArchiveMemberKind.Unknown,
                DataOffset = dataOffset,
                Size = data.Length,
                Date = date,
                Machine = "elf",
            };
        }

        return ReadObject(file, data, dataOffset, name, rawName, date, problems);
    }

    private static ArchiveMember ReadImportMember(
        ReadOnlySpan<byte> data,
        long dataOffset,
        string name,
        string rawName,
        long date,
        List<string> problems)
    {
        // The short form: version, machine, timestamp, size, then the ordinal or hint and a flags
        // word, then two NUL-terminated strings — the imported symbol and the DLL that exports it.
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]);
        int ordinal = BinaryPrimitives.ReadUInt16LittleEndian(data[16..18]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[18..20]);
        int nameType = (flags >> 2) & 0x07;

        int start = 20;
        string symbol = Ascii(data[start..]);
        string dll = Ascii(data[(start + symbol.Length + 1)..]);

        if (dll.Length == 0)
        {
            problems.Add($"import member \"{name}\" names no DLL, which an import member must");
        }

        return new ArchiveMember
        {
            Name = name,
            RawName = rawName,
            Kind = ArchiveMemberKind.Import,
            DataOffset = dataOffset,
            Size = data.Length,
            Date = date,
            Machine = PeImage.DescribeMachine(machine),
            TimeDateStamp = timestamp,
            ImportDll = dll,
            ImportSymbol = symbol,
            // An ordinal is only an ordinal when the flags say the member imports by number;
            // otherwise the two bytes are a hint into the DLL's name table.
            ImportOrdinal = nameType == 0 ? ordinal : null,
        };
    }

    private static ArchiveMember ReadObject(
        byte[] file,
        ReadOnlySpan<byte> data,
        long dataOffset,
        string name,
        string rawName,
        long date,
        List<string> problems)
    {
        if (data.Length < CoffHeaderSize)
        {
            problems.Add($"member \"{name}\" is {data.Length} bytes, too short to hold a COFF header");
            return new ArchiveMember
            {
                Name = name,
                RawName = rawName,
                Kind = ArchiveMemberKind.Unknown,
                DataOffset = dataOffset,
                Size = data.Length,
                Date = date,
            };
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]);
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        uint symbolTable = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]);
        int symbolCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(data[16..18]);

        var sections = new List<string>();
        int sectionTable = CoffHeaderSize + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int at = sectionTable + (i * SectionHeaderSize);
            if (at + SectionHeaderSize > data.Length)
            {
                break;
            }

            sections.Add(SectionName(data, at, file, dataOffset, symbolTable, symbolCount));
        }

        var symbols = new List<string>();
        uint? compId = null;
        if (symbolTable > 0 && symbolCount > 0 && symbolTable + ((long)symbolCount * SymbolSize) <= data.Length)
        {
            long stringTable = symbolTable + ((long)symbolCount * SymbolSize);
            for (int i = 0; i < symbolCount; i++)
            {
                int at = (int)symbolTable + (i * SymbolSize);
                var record = data.Slice(at, SymbolSize);

                string symbol = SymbolName(record, data, stringTable);
                int aux = record[17];

                if (symbol.Length > 0 && !symbol.StartsWith('@'))
                {
                    symbols.Add(symbol);
                }

                // A zero value is worth the same as no record: nothing was stamped.
                if (symbol is "@comp.id")
                {
                    // Section number 0xFFFF marks an absolute symbol: its value is not an address
                    // but the number the compiler stamped — the product id in its high bits and
                    // the build in its low ones.
                    compId = BinaryPrimitives.ReadUInt32LittleEndian(record[8..12]);
                }

                i += aux;
            }
        }

        string? tool = null;
        int? build = null;
        if (compId is > 0)
        {
            uint product = compId.Value >> 16;
            build = (int)(compId.Value & 0xFFFF);
            tool = RichHeader.LegacyProdIds.TryGetValue(product, out string? named) ? named : $"tool_0x{product:X4}";
        }

        return new ArchiveMember
        {
            Name = name,
            RawName = rawName,
            Kind = ArchiveMemberKind.Object,
            DataOffset = dataOffset,
            Size = data.Length,
            Date = date,
            Machine = PeImage.DescribeMachine(machine),
            TimeDateStamp = timestamp,
            Sections = sections,
            Symbols = symbols,
            CompId = compId,
            CompTool = tool,
            CompBuild = build,
        };
    }

    /// <summary>
    /// A section name is eight bytes, and a longer one is written as <c>/offset</c> pointing into the
    /// object's string table, which sits immediately after its symbol table.
    /// </summary>
    private static string SectionName(ReadOnlySpan<byte> data, int at, byte[] file, long dataOffset, uint symbolTable, int symbolCount)
    {
        var raw = data.Slice(at, 8);
        if (raw[0] != '/')
        {
            return Text(raw).TrimEnd('\0');
        }

        if (!int.TryParse(Text(raw[1..]).TrimEnd('\0', ' '), out int offset))
        {
            return Text(raw).TrimEnd('\0');
        }

        long stringTable = dataOffset + symbolTable + ((long)symbolCount * SymbolSize);
        if (stringTable + 4 > file.Length)
        {
            return Text(raw).TrimEnd('\0');
        }

        long at2 = stringTable + offset;
        if (at2 < 0 || at2 >= file.Length)
        {
            return Text(raw).TrimEnd('\0');
        }

        return Ascii(file.AsSpan((int)at2));
    }

    /// <summary>
    /// A symbol name is eight bytes, and a longer one is written as four zero bytes followed by an
    /// offset into the string table.
    /// </summary>
    private static string SymbolName(ReadOnlySpan<byte> record, ReadOnlySpan<byte> data, long stringTable)
    {
        if (record[0] != 0)
        {
            return Text(record[..8]).TrimEnd('\0');
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(record[4..8]);
        long at = stringTable + offset;
        return at >= 0 && at < data.Length ? Ascii(data[(int)at..]) : string.Empty;
    }

    /// <summary>A NUL-terminated string, which is how the short import member and the string tables write them.</summary>
    private static string Ascii(ReadOnlySpan<byte> span)
    {
        int end = span.IndexOf((byte)0);
        return Text(end < 0 ? span : span[..end]);
    }

    private static string LongName(string longNames, int offset, string rawName)
    {
        if (offset < 0 || offset >= longNames.Length)
        {
            return rawName;
        }

        // Names in the table end with a newline or a NUL; both are used by different librarians.
        // Microsoft's librarian also ends them with a slash, which is a terminator and not part of
        // the name — the same slash a short header name carries for the same reason.
        int end = longNames.IndexOfAny(['\n', '\0'], offset);
        string name = end < 0 ? longNames[offset..].TrimEnd() : longNames[offset..end];
        return name.EndsWith('/') ? name[..^1] : name;
    }

    private static string Text(ReadOnlySpan<byte> span) => Encoding.ASCII.GetString(span);

    private static bool TryDecimal(ReadOnlySpan<byte> span, out int value)
    {
        value = 0;
        int digits = 0;
        foreach (byte b in span)
        {
            if (b == ' ' || b == 0)
            {
                continue;
            }

            if (b < '0' || b > '9')
            {
                return false;
            }

            digits++;
            value = (value * 10) + (b - '0');
        }

        return digits > 0;
    }
}
