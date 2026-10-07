using System.Buffers.Binary;
using System.Text;

namespace Recon.Archive;

/// <summary>One section of a COFF object: its bytes, and the relocations that patch them.</summary>
public sealed class CoffObjectSection
{
    /// <summary>1-based, the way a symbol's section number refers to it.</summary>
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The bytes as the file holds them — for a section with no raw data, empty.</summary>
    public byte[] Bytes { get; init; } = [];

    public uint Characteristics { get; init; }

    public List<CoffObjectRelocation> Relocations { get; } = [];

    /// <summary>True when the bytes are not in the file at all and the section is all zeroes (.bss).</summary>
    public bool Uninitialized => Bytes.Length == 0 && (Characteristics & 0x80) != 0;
}

public sealed class CoffObjectRelocation
{
    /// <summary>Offset within the section the relocation patches.</summary>
    public uint At { get; init; }

    /// <summary>Index into <see cref="CoffObject.Symbols"/>.</summary>
    public uint SymbolIndex { get; init; }

    /// <summary>The raw type, which is machine-specific.</summary>
    public ushort Type { get; init; }

    /// <summary>What that type means on this object's machine, or its number when it means nothing here.</summary>
    public string Kind { get; init; } = "unknown";
}

public sealed class CoffObjectSymbol
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The name with the C decoration removed: <c>_add</c>, <c>_mul@8</c> and <c>@f@8</c> are one name each.</summary>
    public string PlainName { get; init; } = string.Empty;

    /// <summary>Offset within the section it is defined in, or 0 when it is not defined.</summary>
    public uint Value { get; init; }

    /// <summary>1-based section index; 0 for undefined, -1 absolute, -2 debug.</summary>
    public int Section { get; init; }

    /// <summary>The type field: 0x20 is a function on every COFF target that has one.</summary>
    public ushort Type { get; init; }

    /// <summary>IMAGE_SYM_CLASS_*.</summary>
    public byte StorageClass { get; init; }

    public int AuxCount { get; init; }

    /// <summary>
    /// Which record of the symbol table this is. A relocation names a symbol by *record* index, and a
    /// symbol with auxiliary records is followed by them, so the records and the symbols read out of
    /// them are two different numberings: looking one up with the other's index quietly names some
    /// other symbol, which is how a reference to the code is read as a reference to a variable.
    /// </summary>
    public uint RecordIndex { get; init; }

    public bool IsFunction => Type == 0x20;

    /// <summary>IMAGE_SYM_CLASS_EXTERNAL — a name the object defines or needs, not a section or a file.</summary>
    public bool IsExternal => StorageClass == 2;

    /// <summary>IMAGE_SYM_CLASS_STATIC — a section symbol or a name local to the object.</summary>
    public bool IsStatic => StorageClass == 3;

    public bool IsDefined => Section > 0;

    /// <summary>True for the symbol that names a section rather than something in it.</summary>
    public bool IsSection => IsStatic && Name.StartsWith('.');
}

/// <summary>
/// One COFF object file, read deep enough to take a function out of it: the bytes of every section,
/// every symbol, and the relocations that patch the bytes.
///
/// This is a different question from the one <see cref="CoffArchive"/> answers. That reader walks a
/// library and summarizes each member — what compiler stamped it, which sections and symbols it has,
/// which import it satisfies — because a library is a catalogue to be browsed. A unit's object is a
/// source of code to be placed at an address, which needs the section bytes and the relocations, so
/// it is read properly here. A <c>.o</c> is also not an archive member, so a reader asked for one
/// cannot borrow the other's walk.
/// </summary>
public sealed class CoffObject
{
    private const int HeaderSize = 20;

    private const int SectionHeaderSize = 40;

    private const int SymbolSize = 18;

    private const int RelocationSize = 10;

    public string Path { get; init; } = "<memory>";

    public ushort Machine { get; init; }

    public List<CoffObjectSection> Sections { get; } = [];

    public List<CoffObjectSymbol> Symbols { get; } = [];

    public List<string> Problems { get; } = [];

    /// <summary>
    /// Whether these bytes are a COFF object rather than an image, an archive or something else. A PE
    /// image has an MZ header; an archive starts with its own magic; an object starts with a machine
    /// this reader knows and a section count that fits the file.
    /// </summary>
    public static bool Sniff(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || bytes[0] == 'M' || bytes[1] == 'Z')
        {
            return false;
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
        return MachineOf(machine) is not null && sections is > 0 and < 4096 && HeaderSize + (sections * SectionHeaderSize) <= bytes.Length;
    }

    /// <summary>The machine's name, or null when this is not a machine a COFF object starts with.</summary>
    public static string? MachineOf(ushort machine) => machine switch
    {
        0x014c => "i386",
        0x8664 => "x86-64",
        0x01c0 => "arm",
        0xaa64 => "arm64",
        0x0166 => "mips",
        _ => null,
    };

    /// <summary>
    /// Reads an object. A malformed one produces what could be read plus a problem saying so, never an
    /// exception: the caller is deciding whether to place a rebuild, and "the object is not readable"
    /// is an answer it has to be able to give.
    /// </summary>
    public static CoffObject Load(byte[] bytes, string path = "<memory>")
    {
        if (bytes.Length < HeaderSize)
        {
            var short1 = new CoffObject { Path = path };
            short1.Problems.Add($"\"{path}\" is {bytes.Length} bytes, too short to hold a COFF header");
            return short1;
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
        uint symbolTable = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        int symbolCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16, 2));

        var object_ = new CoffObject
        {
            Path = path,
            Machine = machine,
        };

        if (MachineOf(machine) is null && machine != 0)
        {
            object_.Problems.Add($"\"{path}\" names machine 0x{machine:x4}, which is not one this reader knows");
        }

        // The string table sits after the symbol table, and both section names and symbol names point
        // into it. Its first four bytes are its own length, which is why an offset of 4 is the first
        // byte a name can start at.
        long stringTable = symbolTable + ((long)symbolCount * SymbolSize);
        ReadOnlySpan<byte> strings = stringTable >= 0 && stringTable < bytes.Length
            ? bytes.AsSpan((int)stringTable)
            : [];

        int sectionTable = HeaderSize + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int at = sectionTable + (i * SectionHeaderSize);
            if (at + SectionHeaderSize > bytes.Length)
            {
                object_.Problems.Add($"\"{path}\": section {i + 1} header runs past the end of the file");
                break;
            }

            var header = bytes.AsSpan(at, SectionHeaderSize);
            string name = SectionName(header, bytes, strings);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
            uint relocationPointer = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
            int relocationCount = BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]);
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]);

            byte[] data = [];
            if (rawSize > 0)
            {
                if (rawPointer + rawSize <= bytes.Length)
                {
                    data = bytes[(int)rawPointer..(int)(rawPointer + rawSize)];
                }
                else
                {
                    object_.Problems.Add($"\"{path}\": section {name} claims {rawSize} bytes at 0x{rawPointer:x}, past the end of the file");
                }
            }

            var section = new CoffObjectSection
            {
                Index = i + 1,
                Name = name,
                Bytes = data,
                Characteristics = characteristics,
            };

            for (int r = 0; r < relocationCount; r++)
            {
                long entry = relocationPointer + ((long)r * RelocationSize);
                if (entry + RelocationSize > bytes.Length)
                {
                    break;
                }

                var record = bytes.AsSpan((int)entry, RelocationSize);
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(record[8..10]);
                section.Relocations.Add(new CoffObjectRelocation
                {
                    At = BinaryPrimitives.ReadUInt32LittleEndian(record[0..4]),
                    SymbolIndex = BinaryPrimitives.ReadUInt32LittleEndian(record[4..8]),
                    Type = type,
                    Kind = RelocationKind(machine, type),
                });
            }

            object_.Sections.Add(section);
        }

        if (symbolTable > 0 && symbolCount > 0 && symbolTable + ((long)symbolCount * SymbolSize) <= bytes.Length)
        {
            for (int i = 0; i < symbolCount; i++)
            {
                var record = bytes.AsSpan((int)symbolTable + (i * SymbolSize), SymbolSize);
                string name = SymbolName(record, strings);
                object_.Symbols.Add(new CoffObjectSymbol
                {
                    RecordIndex = (uint)i,
                    Name = name,
                    PlainName = PlainSymbolName(name),
                    Value = BinaryPrimitives.ReadUInt32LittleEndian(record[8..12]),
                    Section = BinaryPrimitives.ReadInt16LittleEndian(record[12..14]),
                    Type = BinaryPrimitives.ReadUInt16LittleEndian(record[14..16]),
                    StorageClass = record[16],
                    AuxCount = record[17],
                });

                i += record[17];
            }
        }

        return object_;
    }

    /// <summary>
    /// The symbol a relocation names, by record index — see <see cref="CoffObjectSymbol.RecordIndex"/>.
    /// </summary>
    public CoffObjectSymbol? SymbolAt(uint recordIndex)
        => Symbols.FirstOrDefault(s => s.RecordIndex == recordIndex);

    /// <summary>The section with this 1-based index, for resolving a symbol's section number.</summary>
    public CoffObjectSection? Section(int index) => index >= 1 && index <= Sections.Count ? Sections[index - 1] : null;

    /// <summary>
    /// The function a name refers to, tolerant of the decoration the C compiler adds: a piece named
    /// <c>add</c> is the object's <c>_add</c>, and <c>mul_std</c> is <c>_mul_std@8</c>. An exact
    /// match wins, and among the rest a defined function wins over an undefined one.
    /// </summary>
    public CoffObjectSymbol? Function(string name)
    {
        CoffObjectSymbol? best = null;
        foreach (var symbol in Symbols)
        {
            if (!symbol.IsFunction || symbol.IsSection)
            {
                continue;
            }

            bool exact = string.Equals(symbol.Name, name, StringComparison.Ordinal);
            bool plain = string.Equals(symbol.PlainName, name, StringComparison.Ordinal)
                || string.Equals(symbol.PlainName, PlainSymbolName(name), StringComparison.Ordinal);
            if (!exact && !plain)
            {
                continue;
            }

            if (exact && symbol.IsDefined)
            {
                return symbol;
            }

            if (best is null || (symbol.IsDefined && !best.IsDefined))
            {
                best = symbol;
            }
        }

        return best;
    }

    /// <summary>
    /// The name with the C decoration removed, using the tool's own demangler so that one spelling of
    /// a name is answerable in one place: <c>_add</c> → <c>add</c>, <c>_mul_std@8</c> → <c>mul_std</c>,
    /// <c>@sub_fast@8</c> → <c>sub_fast</c>, <c>?f@@YAXXZ</c> → <c>f</c>.
    /// </summary>
    public static string PlainSymbolName(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        // Section and file symbols are not names to be decorated: `.text` is `.text`, and so is a
        // datum whose C name really begins with an underscore (`__imp_...`, `__tls_used`).
        if (name[0] == '.')
        {
            return name;
        }

        var demangled = Pe.Demangler.Demangle(name);
        string? text = demangled.Text;
        return string.IsNullOrEmpty(text) || string.Equals(text, name, StringComparison.Ordinal) ? Underscore(name) : text;
    }

    /// <summary>
    /// The fallback for a name the demangler has nothing to say about: one leading underscore is the
    /// cdecl decoration and is not part of the name.
    /// </summary>
    private static string Underscore(string name)
        => name.Length > 1 && name[0] == '_' && name[1] != '_' ? name[1..] : name;

    /// <summary>
    /// What a relocation's type means on this object's machine. The names are the ones the formats
    /// use — <c>dir32</c> is a full address written into four bytes, <c>rel32</c> a displacement from
    /// the field's own address — and a type this reader does not know is reported by its number rather
    /// than guessed at, because a wrong relocation is a wrong image.
    /// </summary>
    private static string RelocationKind(ushort machine, ushort type) => machine switch
    {
        0x014c => type switch
        {
            0x0001 => "abs",
            0x0002 => "secidx",
            0x0006 => "dir32",
            0x0007 => "dir32nb",
            0x000a => "secrel",
            0x000b => "secrel",       // SECTION is the same encoding as SECREL on i386
            0x0014 => "rel32",
            _ => $"type_0x{type:x4}",
        },
        0x8664 => type switch
        {
            0x0001 => "addr64",
            0x0002 => "dir32",
            0x0003 => "dir32nb",
            0x0004 => "rel32",
            0x0005 or 0x0006 or 0x0007 or 0x0008 or 0x0009 => "rel32",
            0x000a or 0x000b => "secrel",
            _ => $"type_0x{type:x4}",
        },
        _ => $"type_0x{type:x4}",
    };

    private static string SymbolName(ReadOnlySpan<byte> record, ReadOnlySpan<byte> strings)
    {
        if (record[0] != 0)
        {
            return Text(record[..8]).TrimEnd('\0');
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(record[4..8]);
        return ReadAt(strings, offset);
    }

    private static string SectionName(ReadOnlySpan<byte> header, byte[] file, ReadOnlySpan<byte> strings)
    {
        var raw = header[..8];
        if (raw[0] != '/')
        {
            return Text(raw).TrimEnd('\0');
        }

        // The offset is decimal and either starts after the '/' or is written by a tool that left the
        // leading byte empty; a name that is not decimal leaves the section named by its bytes.
        string digits = Text(raw[1..]).TrimEnd('\0');
        if (digits.Length == 0 && raw[1] == 0)
        {
            digits = Text(raw[2..]).TrimEnd('\0');
        }

        return int.TryParse(digits, out int offset) ? ReadAt(strings, (uint)offset) : Text(raw).TrimEnd('\0');
    }

    private static string ReadAt(ReadOnlySpan<byte> strings, uint offset)
    {
        if (offset >= strings.Length)
        {
            return string.Empty;
        }

        int end = strings[(int)offset..].IndexOf((byte)0);
        return Text(end < 0 ? strings[(int)offset..] : strings.Slice((int)offset, end));
    }

    private static string Text(ReadOnlySpan<byte> span) => Encoding.ASCII.GetString(span);
}
