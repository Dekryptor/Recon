using System.Text;
using Recon.Images;
using Recon.Pe;

namespace Recon.DebugInfo;

/// <summary>A function described by a DWARF subprogram DIE.</summary>
public sealed class DwarfFunction
{
    public required string Name { get; init; }

    /// <summary>RVA of the first instruction, or null when the DIE carries no address.</summary>
    public uint? Rva { get; init; }

    /// <summary>Exact size when DWARF states one, otherwise null.</summary>
    public uint? Size { get; init; }

    /// <summary>Compilation unit (source file) this function belongs to.</summary>
    public string? Unit { get; init; }

    /// <summary>True for <c>DW_AT_declaration</c> DIEs: the function is declared here, not defined.</summary>
    public bool IsDeclaration { get; init; }

    /// <summary>Set when the body spans several ranges and must be reconstructed from DIEs.</summary>
    public bool HasRanges { get; init; }
}

/// <summary>What we read out of the DWARF sections of an image.</summary>
public sealed class DwarfInfo
{
    public List<string> Producers { get; set; } = [];

    public List<(string Name, string Producer)> Units { get; set; } = [];

    public List<DwarfFunction> Functions { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    public bool Parsed { get; set; }
}

/// <summary>
/// A DWARF reader whose scope is deliberate: it walks the DIE tree of every compilation unit and
/// keeps what a reconstruction pipeline needs from it — the unit's producer string (toolchain
/// detection) and each subprogram's name, address, size and owning unit (function inventory).
/// Types, line tables and locations are ignored, but every form is stepped over correctly, which
/// matters because a single mis-sized form desynchronises the rest of the unit.
/// </summary>
public static class DwarfReader
{
    // Tags.
    private const int DwTagCompileUnit = 0x11;
    private const int DwTagSubprogram = 0x2E;
    private const int DwTagInlinedSubroutine = 0x1D;
    private const int DwTagPartialUnit = 0x3C;
    private const int DwTagSkeletonUnit = 0x4A;

    // Attributes.
    private const int DwAtName = 0x03;
    private const int DwAtStmtList = 0x10;
    private const int DwAtLowPc = 0x11;
    private const int DwAtHighPc = 0x12;
    private const int DwAtCompDir = 0x1B;
    private const int DwAtProducer = 0x25;
    private const int DwAtAbstractOrigin = 0x31;
    private const int DwAtDeclaration = 0x3C;
    private const int DwAtRanges = 0x55;
    private const int DwAtLinkageName = 0x6E;
    private const int DwAtStrOffsetsBase = 0x72;
    private const int DwAtAddrBase = 0x73;

    // Forms.
    private const int DwFormAddr = 0x01;
    private const int DwFormBlock2 = 0x03;
    private const int DwFormBlock4 = 0x04;
    private const int DwFormData2 = 0x05;
    private const int DwFormData4 = 0x06;
    private const int DwFormData8 = 0x07;
    private const int DwFormString = 0x08;
    private const int DwFormBlock = 0x09;
    private const int DwFormBlock1 = 0x0A;
    private const int DwFormData1 = 0x0B;
    private const int DwFormFlag = 0x0C;
    private const int DwFormSdata = 0x0D;
    private const int DwFormStrp = 0x0E;
    private const int DwFormUdata = 0x0F;
    private const int DwFormRefAddr = 0x10;
    private const int DwFormRef1 = 0x11;
    private const int DwFormRef2 = 0x12;
    private const int DwFormRef4 = 0x13;
    private const int DwFormRef8 = 0x14;
    private const int DwFormRefUdata = 0x15;
    private const int DwFormIndirect = 0x16;
    private const int DwFormSecOffset = 0x17;
    private const int DwFormExprLoc = 0x18;
    private const int DwFormFlagPresent = 0x19;
    private const int DwFormStrx = 0x1A;
    private const int DwFormAddrx = 0x1B;
    private const int DwFormRefSup4 = 0x1C;
    private const int DwFormStrpSup = 0x1D;
    private const int DwFormData16 = 0x1E;
    private const int DwFormLineStrp = 0x1F;
    private const int DwFormRefSig8 = 0x20;
    private const int DwFormImplicitConst = 0x21;
    private const int DwFormLocListx = 0x22;
    private const int DwFormRngListx = 0x23;
    private const int DwFormRefSup8 = 0x24;
    private const int DwFormStrx1 = 0x25;
    private const int DwFormStrx2 = 0x26;
    private const int DwFormStrx3 = 0x27;
    private const int DwFormStrx4 = 0x28;
    private const int DwFormAddrx1 = 0x29;
    private const int DwFormAddrx2 = 0x2A;
    private const int DwFormAddrx3 = 0x2B;
    private const int DwFormAddrx4 = 0x2C;

    /// <summary>Reads the DWARF sections of an image; returns an empty result when there are none.</summary>
    public static DwarfInfo Read(IBinaryImage image, byte[] bytes)
    {
        var result = new DwarfInfo();
        var infoSection = image.SectionNamed(".debug_info");
        var abbrevSection = image.SectionNamed(".debug_abbrev");
        if (infoSection is null || abbrevSection is null || infoSection.RawSize == 0 || abbrevSection.RawSize == 0)
        {
            return result;
        }

        var context = new Context(image, bytes, abbrevSection, infoSection);
        if (context.Info.Length == 0)
        {
            return result;
        }

        int cursor = 0;
        int units = 0;
        while (cursor + 12 <= context.Info.Length && units < 65536)
        {
            long length = ReadInt(context.Info, ref cursor, 4);
            if (length == 0)
            {
                continue; // padding between units
            }

            if (length < 0 || length > int.MaxValue)
            {
                result.Problems.Add($"compilation unit at 0x{cursor:x} has an unsupported length {length}");
                break;
            }

            int unitEnd = cursor + (int)length;
            if (unitEnd > context.Info.Length)
            {
                result.Problems.Add($"compilation unit at offset {cursor - 4} runs past .debug_info");
                break;
            }

            int version = (int)ReadInt(context.Info, ref cursor, 2);
            int addressSize = 4;
            uint abbrevOffset;
            if (version >= 5)
            {
                int unitType = (int)ReadInt(context.Info, ref cursor, 1);
                addressSize = (int)ReadInt(context.Info, ref cursor, 1);
                abbrevOffset = (uint)ReadInt(context.Info, ref cursor, 4);
                if (unitType is not (1 or DwTagPartialUnit or DwTagSkeletonUnit))
                {
                    // Type units and split units carry no subprograms worth inventorying here.
                    if (unitType is not (0x02 or 0x03 or 0x04))
                    {
                        cursor = unitEnd;
                        units++;
                        continue;
                    }
                }
            }
            else
            {
                abbrevOffset = (uint)ReadInt(context.Info, ref cursor, 4);
                addressSize = (int)ReadInt(context.Info, ref cursor, 1);
            }

            context.AddressSize = addressSize is > 0 and <= 8 ? addressSize : 4;
            context.AbbrevTable = AbbrevTable.Parse(context.Abbrev, (int)abbrevOffset);
            context.StrOffsetsBase = 8;
            context.AddrBase = 8;

            if (context.AbbrevTable is null)
            {
                result.Problems.Add($"compilation unit at offset {cursor} references a missing abbreviation table at 0x{abbrevOffset:x}");
                cursor = unitEnd;
                units++;
                continue;
            }

            WalkUnit(context, cursor, unitEnd, version, result);
            cursor = unitEnd;
            units++;
        }

        result.Parsed = units > 0;
        return result;
    }

    private sealed class Context
    {
        public Context(IBinaryImage image, byte[] bytes, BinarySection abbrevSection, BinarySection infoSection)
        {
            Image = image;
            Bytes = bytes;
            _abbrevSection = abbrevSection;
            _infoSection = infoSection;
            _strSection = image.SectionNamed(".debug_str");
            _lineStrSection = image.SectionNamed(".debug_line_str");
            _strOffsetsSection = image.SectionNamed(".debug_str_offsets");
            _addrSection = image.SectionNamed(".debug_addr");
        }

        public IBinaryImage Image { get; }

        public byte[] Bytes { get; }

        // Sections are sliced on demand: a span cannot be stored in a class field.
        public ReadOnlySpan<byte> Abbrev => Slice(Bytes, _abbrevSection);

        public ReadOnlySpan<byte> Info => Slice(Bytes, _infoSection);

        public ReadOnlySpan<byte> Str => Slice(Bytes, _strSection);

        public ReadOnlySpan<byte> LineStr => Slice(Bytes, _lineStrSection);

        public ReadOnlySpan<byte> StrOffsets => Slice(Bytes, _strOffsetsSection);

        public ReadOnlySpan<byte> Addr => Slice(Bytes, _addrSection);

        private readonly BinarySection _abbrevSection;

        private readonly BinarySection _infoSection;

        private readonly BinarySection? _strSection;

        private readonly BinarySection? _lineStrSection;

        private readonly BinarySection? _strOffsetsSection;

        private readonly BinarySection? _addrSection;

        public int AddressSize { get; set; } = 4;

        public AbbrevTable? AbbrevTable { get; set; }

        public long StrOffsetsBase { get; set; } = 8;

        public long AddrBase { get; set; } = 8;
    }

    private static ReadOnlySpan<byte> Slice(byte[] bytes, BinarySection? section)
    {
        if (section is null || section.RawSize == 0 || section.RawOffset + section.RawSize > bytes.Length)
        {
            return default;
        }

        return new ReadOnlySpan<byte>(bytes, (int)section.RawOffset, (int)section.RawSize);
    }

    /// <summary>Walks every DIE of one compilation unit and records the subprograms it finds.</summary>
    private static void WalkUnit(Context context, int start, int unitEnd, int version, DwarfInfo result)
    {
        int cursor = start;
        string? unitName = null;
        string? unitProducer = null;
        uint? unitNameRecorded = null;

        while (cursor < unitEnd && cursor < context.Info.Length)
        {
            uint code = ReadUleb(context.Info, ref cursor);
            if (code == 0)
            {
                continue; // end of a child list
            }

            var entry = context.AbbrevTable!.Get(code);
            if (entry is null)
            {
                result.Problems.Add($"abbreviation code {code} is undefined in this unit");
                return;
            }

            bool isDeclaration = false;
            uint? lowPc = null;
            uint? highPc = null;
            bool highPcIsAddress = false;
            string? name = null;
            string? linkageName = null;
            string? producer = null;
            bool hasAbstractOrigin = false;
            bool hasRanges = false;
            bool highPcOverflowed = false;

            foreach (var (attribute, form, implicitConst) in entry.Attributes)
            {
                var value = ReadForm(context, ref cursor, form, implicitConst, version);
                if (!value.Known)
                {
                    if (attribute == DwAtHighPc)
                    {
                        highPcOverflowed = true;
                    }

                    // An unknown form means the remainder of this unit cannot be trusted.
                    if (!value.Consumed)
                    {
                        result.Problems.Add($"unsupported DWARF form 0x{value.Form:x} in abbreviation {code}");
                        return;
                    }

                    continue;
                }

                switch (attribute)
                {
                    case DwAtName:
                        name = value.Text;
                        break;
                    case DwAtLinkageName:
                        linkageName = value.Text;
                        break;
                    case DwAtProducer:
                        producer = value.Text;
                        break;
                    case DwAtLowPc:
                        lowPc = ToRva(context.Image, value.Integer);
                        break;
                    case DwAtHighPc:
                        highPcIsAddress = value.Form == DwFormAddr;
                        highPc = value.Integer > uint.MaxValue ? null : (uint)value.Integer;
                        break;
                    case DwAtDeclaration:
                        isDeclaration = value.Integer != 0;
                        break;
                    case DwAtAbstractOrigin:
                        hasAbstractOrigin = true;
                        break;
                    case DwAtRanges:
                        hasRanges = true;
                        break;
                    case DwAtStrOffsetsBase:
                        context.StrOffsetsBase = value.Integer;
                        break;
                    case DwAtAddrBase:
                        context.AddrBase = value.Integer;
                        break;
                }
            }

            switch (entry.Tag)
            {
                case DwTagCompileUnit:
                case DwTagPartialUnit:
                case DwTagSkeletonUnit:
                    unitName = name ?? unitName;
                    unitProducer = producer ?? unitProducer;
                    if (unitProducer is not null && unitNameRecorded != (uint?)null)
                    {
                        break;
                    }

                    if (unitProducer is not null)
                    {
                        result.Producers.Add(unitProducer);
                        result.Units.Add((unitName ?? string.Empty, unitProducer));
                        unitNameRecorded = 1;
                    }

                    break;

                case DwTagSubprogram:
                    if (hasAbstractOrigin && name is null && linkageName is null)
                    {
                        break; // an instance of an inlined function, not a definition
                    }

                    if (name is not null || linkageName is not null)
                    {
                        uint? size = null;
                        if (lowPc is not null && highPc is not null && !highPcOverflowed)
                        {
                            size = highPcIsAddress ? highPc - lowPc : highPc;
                        }

                        result.Functions.Add(new DwarfFunction
                        {
                            Name = name ?? linkageName!,
                            Rva = isDeclaration ? null : lowPc,
                            Size = isDeclaration ? null : size,
                            Unit = unitName,
                            IsDeclaration = isDeclaration,
                            HasRanges = hasRanges,
                        });
                    }

                    break;
            }
        }

        // A unit whose root DIE was never seen (truncated section) still deserves a record.
        if (unitProducer is null && unitName is not null)
        {
            result.Units.Add((unitName, string.Empty));
        }
    }

    private static uint? ToRva(IBinaryImage image, long address)
    {
        if (address <= 0)
        {
            return null;
        }

        ulong baseAddress = image.ImageBase;

        if (address < (long)baseAddress)
        {
            // Some producers already write RVAs; a value below the image base can only be one.
            return address <= uint.MaxValue ? (uint)address : null;
        }

        return (uint)(address - (long)baseAddress);
    }

    /// <summary>A value read from a DIE, with the form that produced it, so callers can interpret it.</summary>
    private readonly struct FormValue
    {
        public required int Form { get; init; }

        public long Integer { get; init; }

        public string? Text { get; init; }

        /// <summary>False when the value could not be interpreted (unknown form, missing index).</summary>
        public bool Known { get; init; }

        /// <summary>False when the form was unknown and its bytes were not stepped over.</summary>
        public bool Consumed { get; init; }
    }

    private static FormValue ReadForm(Context context, ref int cursor, int form, long implicitConst, int version)
    {
        ReadOnlySpan<byte> data = context.Info;
        if (cursor < 0 || cursor > data.Length)
        {
            return new FormValue { Form = form, Consumed = false };
        }

        try
        {
            switch (form)
            {
                case DwFormAddr:
                    {
                        long value = ReadInt(data, ref cursor, context.AddressSize);
                        return new FormValue { Form = form, Integer = value, Known = true, Consumed = true };
                    }

                case DwFormData1:
                case DwFormFlag:
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 1), Known = true, Consumed = true };

                case DwFormData2:
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 2), Known = true, Consumed = true };

                case DwFormData4:
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 4), Known = true, Consumed = true };

                case DwFormData8:
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 8), Known = true, Consumed = true };

                case DwFormData16:
                    _ = ReadInt(data, ref cursor, 8);
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 8), Known = true, Consumed = true };

                case DwFormUdata:
                case DwFormLocListx:
                case DwFormRngListx:
                    return new FormValue { Form = form, Integer = ReadUleb(data, ref cursor), Known = true, Consumed = true };

                case DwFormSdata:
                    return new FormValue { Form = form, Integer = ReadSleb(data, ref cursor), Known = true, Consumed = true };

                case DwFormString:
                    return new FormValue { Form = form, Text = ReadCString(data, ref cursor), Known = true, Consumed = true };

                case DwFormStrp:
                    {
                        uint offset = (uint)ReadInt(data, ref cursor, 4);
                        string? text = GetString(context.Str, offset);
                        return new FormValue { Form = form, Text = text, Known = text is not null, Consumed = true };
                    }

                case DwFormLineStrp:
                case DwFormStrpSup:
                    {
                        uint offset = (uint)ReadInt(data, ref cursor, 4);
                        string? text = GetString(context.LineStr, offset);
                        return new FormValue { Form = form, Text = text, Known = text is not null, Consumed = true };
                    }

                case DwFormSecOffset:
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 4), Known = true, Consumed = true };

                case DwFormRefAddr:
                case DwFormRefSup4:
                    return new FormValue { Form = form, Integer = ReadInt(data, ref cursor, 4), Known = true, Consumed = true };

                case DwFormRefSup8:
                case DwFormRefSig8:
                    _ = ReadInt(data, ref cursor, 8);
                    return new FormValue { Form = form, Known = true, Consumed = true };

                case DwFormRef1:
                    _ = ReadInt(data, ref cursor, 1);
                    return new FormValue { Form = form, Known = true, Consumed = true };

                case DwFormRef2:
                    _ = ReadInt(data, ref cursor, 2);
                    return new FormValue { Form = form, Known = true, Consumed = true };

                case DwFormRef4:
                    _ = ReadInt(data, ref cursor, 4);
                    return new FormValue { Form = form, Known = true, Consumed = true };

                case DwFormRef8:
                    _ = ReadInt(data, ref cursor, 8);
                    return new FormValue { Form = form, Known = true, Consumed = true };

                case DwFormRefUdata:
                    _ = ReadUleb(data, ref cursor);
                    return new FormValue { Form = form, Known = true, Consumed = true };

                case DwFormFlagPresent:
                    return new FormValue { Form = form, Integer = 1, Known = true, Consumed = true };

                case DwFormImplicitConst:
                    return new FormValue { Form = form, Integer = implicitConst, Known = true, Consumed = true };

                case DwFormBlock1:
                    {
                        int length = (int)ReadInt(data, ref cursor, 1);
                        return SkipBlock(data, ref cursor, length, form);
                    }

                case DwFormBlock2:
                    {
                        int length = (int)ReadInt(data, ref cursor, 2);
                        return SkipBlock(data, ref cursor, length, form);
                    }

                case DwFormBlock4:
                    {
                        int length = (int)ReadInt(data, ref cursor, 4);
                        return SkipBlock(data, ref cursor, length, form);
                    }

                case DwFormBlock:
                case DwFormExprLoc:
                    {
                        int length = (int)ReadUleb(data, ref cursor);
                        return SkipBlock(data, ref cursor, length, form);
                    }

                case DwFormStrx:
                case DwFormStrx1:
                case DwFormStrx2:
                case DwFormStrx3:
                case DwFormStrx4:
                    {
                        int index = ReadIndexed(data, ref cursor, form);
                        long byteOffset = context.StrOffsetsBase + (index * 4L);
                        if (context.StrOffsets.Length < byteOffset + 4)
                        {
                            return new FormValue { Form = form, Consumed = true };
                        }

                        uint offset = (uint)ReadIntAt(context.StrOffsets, (int)byteOffset, 4);
                        string? text = GetString(context.Str, offset);
                        return new FormValue { Form = form, Text = text, Known = text is not null, Consumed = true };
                    }

                case DwFormAddrx:
                case DwFormAddrx1:
                case DwFormAddrx2:
                case DwFormAddrx3:
                case DwFormAddrx4:
                    {
                        int index = ReadIndexed(data, ref cursor, form);
                        long byteOffset = context.AddrBase + (index * (long)context.AddressSize);
                        if (context.Addr.Length < byteOffset + context.AddressSize)
                        {
                            return new FormValue { Form = form, Consumed = true };
                        }

                        long value = ReadIntAt(context.Addr, (int)byteOffset, context.AddressSize);
                        return new FormValue { Form = form, Integer = value, Known = true, Consumed = true };
                    }

                case DwFormIndirect:
                    {
                        uint inner = ReadUleb(data, ref cursor);
                        return ReadForm(context, ref cursor, (int)inner, implicitConst, version);
                    }

                default:
                    return new FormValue { Form = form, Consumed = false };
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return new FormValue { Form = form, Consumed = false };
        }
    }

    private static int ReadIndexed(ReadOnlySpan<byte> data, ref int cursor, int form)
        => form switch
        {
            DwFormStrx or DwFormAddrx => (int)ReadUleb(data, ref cursor),
            DwFormStrx1 or DwFormAddrx1 => (int)ReadInt(data, ref cursor, 1),
            DwFormStrx2 or DwFormAddrx2 => (int)ReadInt(data, ref cursor, 2),
            DwFormStrx3 or DwFormAddrx3 => (int)ReadInt(data, ref cursor, 3),
            _ => (int)ReadInt(data, ref cursor, 4),
        };

    private static FormValue SkipBlock(ReadOnlySpan<byte> data, ref int cursor, int length, int form)
    {
        if (length < 0 || cursor + length > data.Length)
        {
            return new FormValue { Form = form, Consumed = false };
        }

        cursor += length;
        return new FormValue { Form = form, Known = true, Consumed = true };
    }

    private sealed class AbbrevTable
    {
        private readonly Dictionary<uint, Entry> _entries = [];

        public sealed record Entry(int Tag, bool Children, List<(int Attribute, int Form, long ImplicitConst)> Attributes);

        public static AbbrevTable? Parse(ReadOnlySpan<byte> abbrev, int offset)
        {
            if (abbrev.Length == 0 || offset < 0 || offset >= abbrev.Length)
            {
                return null;
            }

            var table = new AbbrevTable();
            int cursor = offset;
            while (cursor < abbrev.Length)
            {
                uint code = ReadUleb(abbrev, ref cursor);
                if (code == 0)
                {
                    break;
                }

                int tag = (int)ReadUleb(abbrev, ref cursor);
                bool children = ReadByte(abbrev, ref cursor) != 0;
                var attributes = new List<(int, int, long)>();
                while (cursor < abbrev.Length)
                {
                    int attribute = (int)ReadUleb(abbrev, ref cursor);
                    int form = (int)ReadUleb(abbrev, ref cursor);
                    if (attribute == 0 && form == 0)
                    {
                        break;
                    }

                    long implicitConst = form == DwFormImplicitConst ? ReadSleb(abbrev, ref cursor) : 0;
                    attributes.Add((attribute, form, implicitConst));
                }

                table._entries[code] = new Entry(tag, children, attributes);
            }

            return table;
        }

        public Entry? Get(uint code) => _entries.TryGetValue(code, out var entry) ? entry : null;
    }

    private static long ReadInt(ReadOnlySpan<byte> data, ref int cursor, int size)
    {
        if (cursor < 0 || cursor + size > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor), "DWARF read past the end of a section");
        }

        long value = 0;
        for (int i = 0; i < size; i++)
        {
            value |= (long)data[cursor + i] << (8 * i);
        }

        cursor += size;
        return value;
    }

    private static long ReadIntAt(ReadOnlySpan<byte> data, int offset, int size)
    {
        if (offset < 0 || offset + size > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "DWARF read past the end of a section");
        }

        long value = 0;
        for (int i = 0; i < size; i++)
        {
            value |= (long)data[offset + i] << (8 * i);
        }

        return value;
    }

    private static string? GetString(ReadOnlySpan<byte> str, uint offset)
    {
        if (str.Length == 0 || offset >= str.Length)
        {
            return null;
        }

        int end = (int)offset;
        while (end < str.Length && str[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(str[(int)offset..end]);
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int cursor)
    {
        int start = cursor;
        while (cursor < data.Length && data[cursor] != 0)
        {
            cursor++;
        }

        string value = Encoding.UTF8.GetString(data[start..Math.Min(cursor, data.Length)]);
        cursor++;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int cursor) => cursor < data.Length ? data[cursor++] : (byte)0;

    private static uint ReadUleb(ReadOnlySpan<byte> data, ref int cursor)
    {
        uint result = 0;
        int shift = 0;
        while (cursor < data.Length)
        {
            byte b = data[cursor++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    private static long ReadSleb(ReadOnlySpan<byte> data, ref int cursor)
    {
        long result = 0;
        int shift = 0;
        byte b = 0;
        do
        {
            if (cursor >= data.Length)
            {
                break;
            }

            b = data[cursor++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
        {
            result |= -1L << shift;
        }

        return result;
    }
}
