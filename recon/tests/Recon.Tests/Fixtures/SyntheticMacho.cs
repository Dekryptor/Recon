using System.Buffers.Binary;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>Switches for the synthetic fixture, so one builder can cover several file shapes.</summary>
public sealed class SyntheticMachoOptions
{
    /// <summary>Mach-O 64 when true, Mach-O 32 when false. No linker here writes the 32-bit one.</summary>
    public bool Is64 { get; init; } = true;

    /// <summary>
    /// Most-significant byte first: the byte order of a PowerPC Mac, which no machine on a
    /// developer's desk has and no corpus binary here carries.
    /// </summary>
    public bool BigEndian { get; init; }

    /// <summary>Which CPU the header names. The bytes inside are x86 whatever this says.</summary>
    public uint? CpuType { get; init; }

    /// <summary>Wrap the image in a fat (universal) file, with one other slice in front of it.</summary>
    public bool Fat { get; init; }

    /// <summary>Write the fat header's 32-byte entries rather than its 20-byte ones.</summary>
    public bool Fat64 { get; init; }

    /// <summary>Cut the file short after the header, the way a truncated download ends.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Builds a complete, deterministic Mach-O image in memory: one code section, one stub section, a
/// data section, two symbols, an import stub named by the indirect symbol table, and the entry point
/// from <c>LC_MAIN</c>.
/// </summary>
/// <remarks>
/// The corpus is the better test of what a real linker writes, and the Mach-O corpus is built here —
/// but it is built by one toolchain for two architectures, and neither of them is the 32-bit or the
/// byte-reversed case. Those are exactly the two shapes a header parser gets wrong, and a test that
/// cannot build them is a test of one file rather than of the format.
/// </remarks>
public static class SyntheticMacho
{
    /// <summary>Where a 64-bit image is loaded: the base every macOS executable uses.</summary>
    public const ulong Base = 0x1_0000_0000;

    /// <summary>Where a 32-bit image is loaded, since the 64-bit base does not fit in 32 bits.</summary>
    public const ulong Base32 = 0x10_0000;

    /// <summary>Offset of <c>_add</c> above the image base, which is how the tests name it.</summary>
    public const ulong AddOffset = 0x1000;

    /// <summary>Offset of <c>__stubs</c>, and of the one stub in it.</summary>
    public const ulong StubOffset = 0x2000;

    /// <summary>Offset of <c>__data</c>.</summary>
    public const ulong DataOffset = 0x3000;

    /// <summary>Offset of <c>__la_symbol_ptr</c>, and of <c>_printf</c>'s pointer in it.</summary>
    public const ulong PointerOffset = 0x4000;

    /// <summary>Offset of the entry point inside <c>__text</c>.</summary>
    public const ulong EntryOffset = 0x10;

    private const uint LcSegment = 0x1;
    private const uint LcSymtab = 0x2;
    private const uint LcDysymtab = 0xB;
    private const uint LcSegment64 = 0x19;
    private const uint LcMain = 0x80000028;

    // The magic as the file's own bytes read: 0xFEEDFACF. A reader that reads them the way this
    // machine reads them sees 0xFEEDFACF back from a little-endian file and 0xCFFAEDFE from a
    // byte-reversed one, which is how the loader tells the two apart — so the fixture writes this one
    // constant in whichever byte order it is building, and not a constant per byte order.
    private const uint Magic64 = 0xFEEDFACF;
    private const uint Magic32 = 0xFEEDFACE;

    // A fat header is big-endian whatever its slices are: 0xCAFEBABE in the file's own order, which
    // the loader sees as 0xBEBAFECA when it reads the first four bytes the way this machine reads them.
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;

    private const int SectionEntrySize = 80;

    // Where each name sits in the string table: index 0 is the NUL every real table starts with.
    private const uint AddName = 1;
    private const uint MainName = 6;
    private const uint PrintfName = 12;

    public static byte[] Build(SyntheticMachoOptions? options = null)
    {
        var o = options ?? new SyntheticMachoOptions();
        uint cpuType = o.CpuType ?? (o.Is64 ? 0x01000007u : 7u); // x86_64, or x86 for the 32-bit file

        byte[] image = BuildThin(o, o.BigEndian, cpuType);
        return o.Fat ? WrapFat(image, o, cpuType, o.BigEndian) : image;
    }

    private static byte[] BuildThin(SyntheticMachoOptions o, bool bigEndian, uint cpuType)
    {
        bool wide = o.Is64;
        ulong imageBase = wide ? Base : Base32;

        // --- layout -----------------------------------------------------------
        // Header, then load commands, then the bytes of the sections, then the symbol tables.
        // Nothing overlaps, because a fixture that overlaps is a fixture that hides bugs.
        int textSize = 0x20;
        int stubSize = 6;
        int dataSize = 4;
        int pointerSize = wide ? 8 : 4;

        int headerSize = wide ? 32 : 28;
        int segmentHeaderSize = wide ? 72 : 56;
        int segmentCommandSize = segmentHeaderSize + (2 * SectionEntrySize);
        int symtabCommandSize = 24;
        int dysymtabCommandSize = 80;
        int mainCommandSize = 24;

        int commandsSize = (2 * segmentCommandSize) + symtabCommandSize + dysymtabCommandSize + mainCommandSize;
        int commandsAt = headerSize;

        // The header and the load commands occupy the first page of the file, and that page is part
        // of __TEXT — which is why a segment's address is not the address of its first section, and
        // why LC_MAIN's entry point is a file offset rather than an address.
        const int pageSize = 0x1000;
        int textOffset = pageSize;
        int stubOffset = (int)StubOffset;
        int dataOffset = (int)DataOffset;
        int pointerOffset = (int)PointerOffset;

        int entrySize = wide ? 16 : 12;
        int symbolTableAt = pointerOffset + pointerSize;
        int stringTableAt = symbolTableAt + (entrySize * 3); // _add, _main, _printf
        int indirectAt = stringTableAt + 48;

        if (commandsAt + commandsSize > textOffset)
        {
            throw new InvalidOperationException($"the load commands ({commandsSize} bytes) do not fit in the first page");
        }

        var file = new byte[indirectAt + 8]; // two entries in the indirect symbol table
        var writer = new Writer(file, bigEndian);

        // --- header -----------------------------------------------------------
        writer.U32(wide ? Magic64 : Magic32);
        writer.U32(cpuType);
        writer.U32(0); // subtype
        writer.U32(2); // file type: MH_EXECUTE
        writer.U32(5); // five load commands
        writer.U32((uint)commandsSize);
        writer.U32(0); // flags
        if (wide)
        {
            writer.U32(0); // the reserved word only the 64-bit header has
        }

        // --- __TEXT: __text and __stubs ---------------------------------------
        // The segment starts at the image base and at file offset 0, so an address and a file offset
        // inside it differ by the image base and by nothing else — which is what makes LC_MAIN's
        // entryoff convertible at all.
        int at = commandsAt;
        writer.At = at;
        WriteSegmentHeader(writer, wide, "__TEXT", segmentCommandSize, imageBase, StubOffset + (ulong)stubSize, 0);
        WriteSection(writer, "__text", "__TEXT", imageBase + AddOffset, (ulong)textSize, (uint)textOffset, 0x80000400);
        WriteSection(writer, "__stubs", "__TEXT", imageBase + StubOffset, (ulong)stubSize, (uint)stubOffset, 0x80000408, reserved2: 6);
        at += segmentCommandSize;

        // --- __DATA: __data and __la_symbol_ptr -------------------------------
        writer.At = at;
        WriteSegmentHeader(writer, wide, "__DATA", segmentCommandSize, imageBase + DataOffset, (ulong)(dataSize + pointerSize), (ulong)dataOffset);
        WriteSection(writer, "__data", "__DATA", imageBase + DataOffset, (ulong)dataSize, (uint)dataOffset, 0);
        // Reserved1 is the first entry of the indirect symbol table this section uses, and the
        // pointer's own entry is the second (see the table written below).
        WriteSection(writer, "__la_symbol_ptr", "__DATA", imageBase + PointerOffset, (ulong)pointerSize, (uint)pointerOffset, 0x7, reserved1: 1);
        at += segmentCommandSize;

        // --- LC_SYMTAB: _add, _main and the undefined _printf -----------------
        writer.At = at;
        writer.U32(LcSymtab);
        writer.U32((uint)symtabCommandSize);
        writer.U32((uint)symbolTableAt);
        writer.U32(3);
        writer.U32((uint)stringTableAt);
        writer.U32(48);
        at += symtabCommandSize;

        // --- LC_DYSYMTAB: two indirect entries, and nothing else filled in -----
        writer.At = at;
        writer.U32(LcDysymtab);
        writer.U32((uint)dysymtabCommandSize);
        writer.Zero(48); // the twelve counts and offsets before the indirect table
        writer.U32((uint)indirectAt);
        writer.U32(2);
        writer.Zero(16); // the relocation tables
        at += dysymtabCommandSize;

        // --- LC_MAIN: the entry point, as an offset into __TEXT ----------------
        writer.At = at;
        writer.U32(LcMain);
        writer.U32((uint)mainCommandSize);
        writer.U64(AddOffset + EntryOffset); // a file offset, which in this layout is the address minus the base
        writer.U64(0); // stack size

        // --- the symbols -------------------------------------------------------
        // The string table is packed, one NUL between names and no padding: an nlist names its symbol
        // by the index of a byte in that table, not by a fixed-width field, and it begins with a NUL
        // so that index 0 is the empty name as it is in every real one.
        writer.At = symbolTableAt;
        WriteSymbol(writer, AddName, wide, imageBase + AddOffset, section: 1, external: true);
        WriteSymbol(writer, MainName, wide, imageBase + AddOffset + EntryOffset, section: 1, external: true);
        WriteSymbol(writer, PrintfName, wide, 0, undefined: true, external: true);

        writer.At = stringTableAt;
        writer.U8(0);
        WritePacked(writer, "_add");
        WritePacked(writer, "_main");
        WritePacked(writer, "_printf");

        // --- the indirect symbol table: the stub, then the pointer -------------
        writer.At = indirectAt;
        writer.U32(2); // the stub at index 0 is _printf
        writer.U32(2); // and so is the pointer it jumps through

        // --- the bytes themselves ---------------------------------------------
        writer.At = textOffset;
        for (int i = 0; i < textSize; i++)
        {
            writer.U8((ulong)i == EntryOffset ? (byte)0xC3 : (byte)0x90); // a ret where the entry point is
        }

        writer.At = stubOffset;
        writer.U16(0x25FF); // jmp [rip + disp32], which is what an x86-64 stub is
        writer.U32(0);

        writer.At = pointerOffset;
        if (wide)
        {
            writer.U64(imageBase + PointerOffset);
        }
        else
        {
            writer.U32((uint)(imageBase + PointerOffset));
        }

        return o.Truncated ? file[..headerSize] : file;
    }

    private static void WriteSegmentHeader(
        Writer writer,
        bool wide,
        string name,
        int commandSize,
        ulong address,
        ulong size,
        ulong fileOffset)
    {
        writer.U32(wide ? LcSegment64 : LcSegment);
        writer.U32((uint)commandSize);
        writer.AsciiZ(name, 16);
        if (wide)
        {
            writer.U64(address);
            writer.U64(size);
            writer.U64(fileOffset);
            writer.U64(size);
        }
        else
        {
            writer.U32((uint)address);
            writer.U32((uint)size);
            writer.U32((uint)fileOffset);
            writer.U32((uint)size);
        }

        writer.U32(name == "__TEXT" ? 5u : 3u); // max protection: read + execute, or read + write
        writer.U32(name == "__TEXT" ? 5u : 3u);
        writer.U32(2); // two sections
        writer.U32(0); // flags
    }

    private static void WriteSection(
        Writer writer,
        string name,
        string segment,
        ulong address,
        ulong size,
        uint offset,
        uint flags,
        uint reserved1 = 0,
        uint reserved2 = 0)
    {
        writer.AsciiZ(name, 16);
        writer.AsciiZ(segment, 16);
        writer.U64(address);
        writer.U64(size);
        writer.U32(offset);
        writer.U32(0); // alignment
        writer.U32(0); // relocations
        writer.U32(0);
        writer.U32(flags);
        writer.U32(reserved1);
        writer.U32(reserved2);
        writer.U32(0); // reserved3, which only the 64-bit record has
    }

    private static void WritePacked(Writer writer, string text)
    {
        foreach (char c in text)
        {
            writer.U8((byte)c);
        }

        writer.U8(0);
    }

    private static void WriteSymbol(
        Writer writer,
        uint nameIndex,
        bool wide,
        ulong value,
        uint section = 0,
        bool external = false,
        bool undefined = false)
    {
        writer.U32(nameIndex);
        // N_SECT (0x0E) for a defined symbol, N_UNDF (0x00) for an import; N_EXT (0x01) marks one
        // visible outside the file, which is what makes it an export.
        writer.U8(undefined ? (byte)0x01 : (byte)(0x0E | (external ? 0x01 : 0x00)));
        writer.U8(unchecked((byte)section));
        writer.U16(0);
        if (wide)
        {
            writer.U64(value);
        }
        else
        {
            writer.U32((uint)value);
        }
    }

    /// <summary>
    /// Wraps an image in a fat (universal) file, with a byte-reversed PowerPC slice in front of it —
    /// the shape a universal binary from the transition years really had, where the file's own byte
    /// order is the fat header's and not the slice's.
    /// </summary>
    private static byte[] WrapFat(byte[] image, SyntheticMachoOptions o, uint cpuType, bool bigEndian)
    {
        byte[] other = BuildThin(new SyntheticMachoOptions { Is64 = false, BigEndian = true, CpuType = 18 }, true, 18);

        int entrySize = o.Fat64 ? 32 : 20;
        int headerSize = 8 + entrySize + entrySize;
        const int pageSize = 0x1000;
        int firstAt = (headerSize + pageSize - 1) / pageSize * pageSize;
        int secondAt = (firstAt + other.Length + pageSize - 1) / pageSize * pageSize;

        var file = new byte[secondAt + image.Length];
        var writer = new Writer(file, bigEndian: true);
        writer.U32(o.Fat64 ? FatMagic64 : FatMagic);
        writer.U32(2);

        // The first slice is the PowerPC one, so that reading the file's own slice is a choice the
        // loader has to make rather than the accident of it being first.
        WriteSlice(writer, 18, 0, (ulong)firstAt, (ulong)other.Length, o.Fat64);
        WriteSlice(writer, cpuType, 0, (ulong)secondAt, (ulong)image.Length, o.Fat64);

        other.CopyTo(file, firstAt);
        image.CopyTo(file, secondAt);
        return file;
    }

    private static void WriteSlice(Writer writer, uint cpuType, uint subType, ulong offset, ulong size, bool wide)
    {
        writer.U32(cpuType);
        writer.U32(subType);
        if (wide)
        {
            writer.U64(offset);
            writer.U64(size);
            writer.U32(12); // alignment as a power of two
            writer.U32(0); // reserved
        }
        else
        {
            writer.U32((uint)offset);
            writer.U32((uint)size);
            writer.U32(12);
        }
    }

    /// <summary>A cursor that writes in either byte order: the mirror of the loader's reader.</summary>
    private sealed class Writer
    {
        private readonly byte[] _bytes;
        private readonly bool _bigEndian;

        public Writer(byte[] bytes, bool bigEndian)
        {
            _bytes = bytes;
            _bigEndian = bigEndian;
        }

        public int At { get; set; }

        public void U8(byte value) => _bytes[At++] = value;

        public void U16(ushort value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(_bytes.AsSpan(At), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(At), value);
            }

            At += 2;
        }

        public void U32(uint value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(_bytes.AsSpan(At), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(At), value);
            }

            At += 4;
        }

        public void U64(ulong value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt64BigEndian(_bytes.AsSpan(At), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_bytes.AsSpan(At), value);
            }

            At += 8;
        }

        public void Zero(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _bytes[At++] = 0;
            }
        }

        public void AsciiZ(string text, int width)
        {
            byte[] name = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i < width; i++)
            {
                _bytes[At + i] = i < name.Length ? name[i] : (byte)0;
            }

            At += width;
        }
    }
}
