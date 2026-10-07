using System.Buffers.Binary;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>Switches for the synthetic fixture, so one builder can cover several file shapes.</summary>
public sealed class SyntheticElfOptions
{
    /// <summary>ELF64 when true, ELF32 when false.</summary>
    public bool Is64 { get; init; } = true;

    /// <summary>Most-significant byte first: the byte order nothing on a developer's desk has.</summary>
    public bool BigEndian { get; init; }

    /// <summary>Add <c>.rela.text</c> with two relocations, one of them PC-relative.</summary>
    public bool Relocations { get; init; } = true;

    /// <summary>Add <c>.dynsym</c>: an import and an export, which is what a linked file has.</summary>
    public bool DynamicSymbols { get; init; } = true;

    /// <summary>Add <c>.comment</c>, where a GNU toolchain names itself.</summary>
    public string? Comment { get; init; } = "GCC: (GNU) 14.2.0";

    /// <summary>Add <c>.note.gnu.build-id</c>.</summary>
    public bool BuildId { get; init; } = true;

    /// <summary>Add program headers, including <c>PT_INTERP</c>.</summary>
    public bool ProgramHeaders { get; init; } = true;

    /// <summary>
    /// <c>e_machine</c>. The bytes inside are x86-64 whatever this says, which is the point: it is
    /// what lets a test hand the tool an image whose instruction set it does not speak.
    /// </summary>
    public ushort? Machine { get; init; }
}

/// <summary>
/// Builds a complete, deterministic ELF image in memory: two functions, a data object, an import,
/// relocations, a symbol table and — optionally — the other byte order and the other word size.
/// Real corpora are the better test of the parts a real linker writes, but they cannot be checked
/// in and they differ between compiler versions; this fixture is what makes a parsing rule testable
/// on a machine with no toolchain at all.
/// </summary>
public static class SyntheticElf
{
    /// <summary>Where the image is loaded: a non-PIE executable's usual base.</summary>
    public const ulong Base = 0x400000;

    /// <summary>Address of <c>func_a</c>, which the tests name by address as well as by name.</summary>
    public const ulong FuncA = Base + 0x1000;

    /// <summary>Address of <c>func_b</c>.</summary>
    public const ulong FuncB = FuncA + 0x20;

    /// <summary>Address of <c>g_counter</c>.</summary>
    public const ulong Counter = Base + 0x2000;

    private sealed class Section
    {
        public string Name = string.Empty;
        public uint Type;
        public ulong Flags;
        public ulong Address;
        public ulong Offset;
        public ulong Size;
        public uint Link;
        public uint Info;
        public ulong Align = 1;
        public ulong EntrySize;
        public List<byte> Data = [];
    }

    public static byte[] Build(SyntheticElfOptions? options = null)
    {
        var o = options ?? new SyntheticElfOptions();
        bool is64 = o.Is64;
        bool be = o.BigEndian;

        var writer = new Writer(be);
        var sections = new List<Section> { new() { Name = string.Empty, Type = 0 } };
        byte[]? interpreter = null;
        int interpreterOffset = 0;

        Section Add(string name, uint type, ulong flags, ulong? address, byte[]? data, ulong entrySize = 0, ulong align = 1)
        {
            var section = new Section
            {
                Name = name,
                Type = type,
                Flags = flags,
                Address = address ?? 0,
                EntrySize = entrySize,
                Align = align,
            };

            if (data is { Length: > 0 })
            {
                section.Data.AddRange(data);
                section.Size = (ulong)data.Length;
            }

            sections.Add(section);
            return section;
        }

        // --- code and data --------------------------------------------------------------------
        byte[] text = new byte[0x40];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = 0x90; // nop: enough for a function that is only ever looked at, never run
        }

        var code = Add(".text", 1, 0x2 | 0x4, FuncA, text, align: 16);
        var data = Add(".data", 1, 0x2 | 0x1, Counter, [1, 2, 3, 4], align: 8);
        Add(".bss", 8, 0x2 | 0x1, Counter + 0x100, null);
        Add(".rodata", 1, 0x2, Base + 0x3000, Encoding.ASCII.GetBytes("hello\0"), align: 8);

        // --- symbol tables --------------------------------------------------------------------
        var stringTable = new StringTable();
        uint nameFile = stringTable.Add("sample.c");
        uint nameFuncA = stringTable.Add("func_a");
        uint nameFuncB = stringTable.Add("func_b");
        uint nameCounter = stringTable.Add("g_counter");
        uint sectionSymbolOffset = 1; // index 0 is reserved; 1 is the file symbol

        var symbolWriter = new Writer(be);
        void Symbol(uint name, byte info, ushort shndx, ulong value, ulong size)
        {
            symbolWriter.U32(name);
            if (is64)
            {
                symbolWriter.U8(info);
                symbolWriter.U8(0);
                symbolWriter.U16(shndx);
                symbolWriter.U64(value);
                symbolWriter.U64(size);
            }
            else
            {
                symbolWriter.U32((uint)value);
                symbolWriter.U32((uint)size);
                symbolWriter.U8(info);
                symbolWriter.U8(0);
                symbolWriter.U16(shndx);
            }
        }

        ushort textIndex = (ushort)sections.IndexOf(code);
        ushort dataIndex = (ushort)sections.IndexOf(data);

        Symbol(0, 0, 0, 0, 0);                                             // 0: reserved
        Symbol(nameFile, 4, 0xFFF1, 0, 0);                                 // 1: STT_FILE
        Symbol(0, 3, textIndex, FuncA, 0);                                 // 2: section .text
        Symbol(nameFuncA, (1 << 4) | 2, textIndex, FuncA, 0x20);           // 3: func_a, global STT_FUNC
        Symbol(nameFuncB, (0 << 4) | 2, textIndex, FuncB, 0x10);           // 4: func_b, local
        Symbol(nameCounter, (1 << 4) | 1, dataIndex, Counter, 4);          // 5: g_counter, object
        byte[] symbolBytes = symbolWriter.Done();
        var symbolTable = Add(".symtab", 2, 0, 0, symbolBytes, entrySize: (ulong)(is64 ? 24 : 16), align: 8);
        var strings = Add(".strtab", 3, 0, 0, stringTable.Bytes(), align: 1);
        symbolTable.Link = (uint)sections.IndexOf(strings);
        symbolTable.Info = sectionSymbolOffset;

        // --- dynamic symbols ------------------------------------------------------------------
        if (o.DynamicSymbols)
        {
            var dynamicStrings = new StringTable();
            uint namePrintf = dynamicStrings.Add("printf");
            uint nameExported = dynamicStrings.Add("func_a");
            var dynamicWriter = new Writer(be);
            void DynamicSymbol(uint name, byte info, ushort shndx, ulong value, ulong size)
            {
                dynamicWriter.U32(name);
                if (is64)
                {
                    dynamicWriter.U8(info);
                    dynamicWriter.U8(0);
                    dynamicWriter.U16(shndx);
                    dynamicWriter.U64(value);
                    dynamicWriter.U64(size);
                }
                else
                {
                    dynamicWriter.U32((uint)value);
                    dynamicWriter.U32((uint)size);
                    dynamicWriter.U8(info);
                    dynamicWriter.U8(0);
                    dynamicWriter.U16(shndx);
                }
            }

            DynamicSymbol(0, 0, 0, 0, 0);                                   // 0: reserved
            DynamicSymbol(namePrintf, (1 << 4) | 2, 0, 0, 0);               // 1: printf, undefined
            DynamicSymbol(nameExported, (1 << 4) | 2, textIndex, FuncA, 0x20); // 2: func_a, exported
            var dynamicTable = Add(".dynsym", 11, 0x2, Base + 0x3100, dynamicWriter.Done(),
                entrySize: (ulong)(is64 ? 24 : 16), align: 8);
            var dynamicStringTable = Add(".dynstr", 3, 0x2, Base + 0x3200, dynamicStrings.Bytes(), align: 1);
            dynamicTable.Link = (uint)sections.IndexOf(dynamicStringTable);
        }

        // --- relocations -----------------------------------------------------------------------
        if (o.Relocations)
        {
            var relocationWriter = new Writer(be);
            // One call site in func_a: a PC-relative reference to func_b, and an absolute pointer
            // to g_counter — the two shapes every analyser has to tell apart.
            void Relocation(ulong offset, uint type, uint symbol, long addend)
            {
                ulong info = is64 ? ((ulong)symbol << 32) | type : ((ulong)symbol << 8) | type;
                if (is64)
                {
                    relocationWriter.U64(offset);
                    relocationWriter.U64(info);
                    relocationWriter.U64((ulong)addend);
                }
                else
                {
                    relocationWriter.U32((uint)offset);
                    relocationWriter.U32((uint)info);
                    relocationWriter.U32((uint)addend);
                }
            }

            Relocation(FuncA + 5, 2, 4, -4);   // R_*_PC32 -> func_b
            Relocation(FuncA + 12, 1, 5, 0);   // R_*_64   -> g_counter
            var relocations = Add(".rela.text", 4, 0, 0, relocationWriter.Done(),
                entrySize: (ulong)(is64 ? 24 : 12), align: 8);
            relocations.Link = (uint)sections.IndexOf(symbolTable);
            relocations.Info = textIndex;
        }

        // --- the parts that name their producer ------------------------------------------------
        if (o.Comment is { Length: > 0 })
        {
            Add(".comment", 1, 0, 0, Encoding.ASCII.GetBytes(o.Comment + "\0"), align: 1);
        }

        if (o.BuildId)
        {
            var note = new Writer(be);
            note.U32(4);                       // namesz
            note.U32(4);                       // descsz
            note.U32(3);                       // NT_GNU_BUILD_ID
            note.Ascii("GNU");                 // name, padded to 4
            note.Bytes([0xDE, 0xAD, 0xBE, 0xEF]);
            Add(".note.gnu.build-id", 7, 0x2, Base + 0x3300, note.Done(), align: 4);
        }

        // Section names are offsets into .shstrtab, which is a section itself: it is added first,
        // with no bytes, and filled in once every other section has been named.
        var shstrtab = Add(".shstrtab", 3, 0, 0, null, align: 1);
        var nameOffsets = new Dictionary<string, uint>();
        var shstrWriter = new Writer(be);
        shstrWriter.U8(0);
        foreach (var section in sections)
        {
            if (section.Name.Length == 0 || nameOffsets.ContainsKey(section.Name))
            {
                continue;
            }

            nameOffsets[section.Name] = (uint)shstrWriter.Length;
            shstrWriter.Ascii(section.Name);
        }

        shstrtab.Data.AddRange(shstrWriter.Done());
        shstrtab.Size = (ulong)shstrtab.Data.Count;

        // --- layout ----------------------------------------------------------------------------
        int headerSize = is64 ? 64 : 52;
        int programHeaderSize = is64 ? 56 : 32;
        int sectionHeaderSize = is64 ? 64 : 40;
        int programHeaderCount = o.ProgramHeaders ? 3 : 0;
        int cursor = headerSize + (programHeaderCount * programHeaderSize);

        foreach (var section in sections)
        {
            if (section.Data.Count == 0)
            {
                continue;
            }

            cursor = Align(cursor, (int)Math.Max(1, section.Align));
            section.Offset = (ulong)cursor;
            cursor += section.Data.Count;
        }

        int sectionTableOffset = Align(cursor, 8);

        // --- the file --------------------------------------------------------------------------
        writer.Bytes([0x7F, (byte)'E', (byte)'L', (byte)'F']);
        writer.U8((byte)(is64 ? 2 : 1));
        writer.U8((byte)(be ? 2 : 1));
        writer.U8(1);                                     // EI_VERSION
        writer.U8(0);                                     // EI_OSABI: SYSV
        writer.U8(0);                                     // EI_ABIVERSION
        writer.Bytes(new byte[7]);                        // padding
        writer.U16(2);                                    // e_type: ET_EXEC
        writer.U16(o.Machine ?? (ushort)(is64 ? 62 : 3)); // e_machine
        writer.U32(1);                                    // e_version
        if (is64)
        {
            writer.U64(FuncA);                            // e_entry
            writer.U64((ulong)(o.ProgramHeaders ? headerSize : 0));
            writer.U64((ulong)sectionTableOffset);
            writer.U32(0);                                // e_flags
            writer.U16((ushort)headerSize);
            writer.U16((ushort)programHeaderSize);
            writer.U16((ushort)programHeaderCount);
            writer.U16((ushort)sectionHeaderSize);
            writer.U16((ushort)sections.Count);
            writer.U16((ushort)sections.IndexOf(shstrtab));
        }
        else
        {
            writer.U32((uint)FuncA);
            writer.U32((uint)(o.ProgramHeaders ? headerSize : 0));
            writer.U32((uint)sectionTableOffset);
            writer.U32(0);
            writer.U16((ushort)headerSize);
            writer.U16((ushort)programHeaderSize);
            writer.U16((ushort)programHeaderCount);
            writer.U16((ushort)sectionHeaderSize);
            writer.U16((ushort)sections.Count);
            writer.U16((ushort)sections.IndexOf(shstrtab));
        }

        if (o.ProgramHeaders)
        {
            // PT_LOAD for the code, PT_LOAD for the data, PT_INTERP for the loader.
            void ProgramHeader(uint type, ulong offset, ulong address, ulong size, uint flags, ulong align)
            {
                if (is64)
                {
                    writer.U32(type);
                    writer.U32(flags);
                    writer.U64(offset);
                    writer.U64(address);
                    writer.U64(0);                // p_paddr
                    writer.U64(size);
                    writer.U64(size);
                    writer.U64(align);
                }
                else
                {
                    writer.U32(type);
                    writer.U32((uint)offset);
                    writer.U32((uint)address);
                    writer.U32(0);
                    writer.U32((uint)size);
                    writer.U32((uint)size);
                    writer.U32(flags);
                    writer.U32((uint)align);
                }
            }

            // The first LOAD covers the headers and the code, as a real linker lays them out: it is
            // what makes the image base the address the file is loaded at.
            byte[] interp = Encoding.ASCII.GetBytes(is64 ? "/lib64/ld-linux-x86-64.so.2\0" : "/lib/ld-linux.so.2\0");
            int interpOffset = Align(sectionTableOffset + (sections.Count * sectionHeaderSize), 1);
            ProgramHeader(1, 0, Base, (ulong)(sectionTableOffset), 5, 0x1000);
            ProgramHeader(1, data.Offset, Counter, 0x100, 6, 0x1000);
            ProgramHeader(3, (ulong)interpOffset, 0, (ulong)interp.Length, 4, 1);
            interpreter = interp;
            interpreterOffset = interpOffset;
        }

        foreach (var section in sections)
        {
            if (section.Data.Count == 0)
            {
                continue;
            }

            writer.PadTo((int)section.Offset);
            writer.Bytes(section.Data.ToArray());
        }

        writer.PadTo(sectionTableOffset);
        foreach (var section in sections)
        {
            uint nameOffset = section.Name.Length == 0 ? 0 : nameOffsets.TryGetValue(section.Name, out uint found) ? found : 0;
            writer.U32(nameOffset);
            writer.U32(section.Type);
            if (is64)
            {
                writer.U64(section.Flags);
                writer.U64(section.Address);
                writer.U64(section.Offset);
                writer.U64(section.Size);
                writer.U32(section.Link);
                writer.U32(section.Info);
                writer.U64(section.Align);
                writer.U64(section.EntrySize);
            }
            else
            {
                writer.U32((uint)section.Flags);
                writer.U32((uint)section.Address);
                writer.U32((uint)section.Offset);
                writer.U32((uint)section.Size);
                writer.U32(section.Link);
                writer.U32(section.Info);
                writer.U32((uint)section.Align);
                writer.U32((uint)section.EntrySize);
            }
        }

        // PT_INTERP points past the section table, so its bytes are written last.
        if (interpreter is not null)
        {
            writer.PadTo(interpreterOffset);
            writer.Bytes(interpreter);
        }

        return writer.Done();
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private sealed class StringTable
    {
        private readonly List<byte> _bytes = [0];

        public List<string> Strings { get; } = [];

        public uint Add(string text)
        {
            if (text.Length == 0)
            {
                return 0;
            }

            uint offset = (uint)_bytes.Count;
            _bytes.AddRange(Encoding.ASCII.GetBytes(text));
            _bytes.Add(0);
            Strings.Add(text);
            return offset;
        }

        public byte[] Bytes() => _bytes.ToArray();
    }

    /// <summary>A byte sink that writes in the byte order under test.</summary>
    private sealed class Writer(bool bigEndian)
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public void U8(byte value) => _bytes.Add(value);

        public void U16(ushort value)
        {
            Span<byte> span = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            }

            Add(span);
        }

        public void U32(uint value)
        {
            Span<byte> span = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(span, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            }

            Add(span);
        }

        public void U64(ulong value)
        {
            Span<byte> span = stackalloc byte[8];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt64BigEndian(span, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(span, value);
            }

            Add(span);
        }

        public void Bytes(byte[] value) => _bytes.AddRange(value);

        public void Ascii(string value)
        {
            _bytes.AddRange(Encoding.ASCII.GetBytes(value));
            _bytes.Add(0);
        }

        public void PadTo(int offset)
        {
            while (_bytes.Count < offset)
            {
                _bytes.Add(0);
            }
        }

        public byte[] Done() => _bytes.ToArray();

        private void Add(ReadOnlySpan<byte> span)
        {
            foreach (byte b in span)
            {
                _bytes.Add(b);
            }
        }
    }
}
