using System.Text;

namespace Recon.Elf;

/// <summary>
/// Parses ELF files: executables, shared objects and relocatable objects, 32 and 64 bit, either
/// byte order. Everything that cannot be read is reported in <see cref="ElfLoadResult.Problems"/>
/// rather than thrown, because a damaged or unusual file is data, not a crash.
/// </summary>
public static class ElfLoader
{
    private const int Header32Size = 52;
    private const int Header64Size = 64;

    public static ElfLoadResult Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new ElfLoadResult { Problems = [$"cannot read {path}: {ex.Message}"] };
        }

        return LoadBytes(bytes, path);
    }

    public static ElfLoadResult LoadBytes(byte[] bytes, string path = "<memory>")
    {
        var result = new ElfLoadResult { Bytes = bytes };
        var problems = result.Problems;

        if (bytes.Length < 16)
        {
            problems.Add($"file is too small to be an ELF image ({bytes.Length} bytes)");
            return result;
        }

        if (bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
        {
            problems.Add("missing ELF magic: not an ELF file");
            return result;
        }

        byte classByte = bytes[4];
        byte dataByte = bytes[5];
        byte version = bytes[6];

        if (classByte is not (1 or 2))
        {
            problems.Add($"unknown ELF class {classByte}");
            return result;
        }

        if (dataByte is not (1 or 2))
        {
            problems.Add($"unknown ELF data encoding {dataByte}");
            return result;
        }

        var reader = new ByteReader(bytes, bigEndian: dataByte == 2);
        bool is64 = classByte == 2;

        if (bytes.Length < (is64 ? Header64Size : Header32Size))
        {
            problems.Add($"file is too small to hold an ELF{(is64 ? "64" : "32")} header ({bytes.Length} bytes)");
            return result;
        }

        if (version != 1)
        {
            problems.Add($"unsupported ELF ident version {version}");
        }

        var image = new ElfImage
        {
            Path = path,
            Sha256 = ElfImage.HashBytes(bytes),
            Class = is64 ? ElfClass.Elf64 : ElfClass.Elf32,
            Data = dataByte == 2 ? ElfData.BigEndian : ElfData.LittleEndian,
            OsAbi = bytes[7],
            AbiVersion = bytes[8],
        };

        image.Type = (ElfType)(reader.U16(16) ?? 0);
        image.Machine = reader.U16(18) ?? 0;

        uint versionWord = reader.U32(20) ?? 0;
        if (versionWord != 1)
        {
            problems.Add($"unsupported ELF header version {versionWord}");
        }

        ulong entry;
        ulong programHeaderOffset;
        ulong sectionHeaderOffset;
        int sectionHeaderEntrySize;
        int programHeaderEntrySize;
        int programHeaderCount;
        int sectionHeaderCount;
        int sectionNameIndex;

        if (is64)
        {
            entry = reader.U64(24) ?? 0;
            programHeaderOffset = reader.U64(32) ?? 0;
            sectionHeaderOffset = reader.U64(40) ?? 0;
            image.Flags = reader.U32(48) ?? 0;
            programHeaderEntrySize = reader.U16(54) ?? 0;
            programHeaderCount = reader.U16(56) ?? 0;
            sectionHeaderEntrySize = reader.U16(58) ?? 0;
            sectionHeaderCount = reader.U16(60) ?? 0;
            sectionNameIndex = reader.U16(62) ?? 0;
        }
        else
        {
            entry = reader.U32(24) ?? 0;
            programHeaderOffset = reader.U32(28) ?? 0;
            sectionHeaderOffset = reader.U32(32) ?? 0;
            image.Flags = reader.U32(36) ?? 0;
            programHeaderEntrySize = reader.U16(42) ?? 0;
            programHeaderCount = reader.U16(44) ?? 0;
            sectionHeaderEntrySize = reader.U16(46) ?? 0;
            sectionHeaderCount = reader.U16(48) ?? 0;
            sectionNameIndex = reader.U16(50) ?? 0;
        }

        image.EntryPoint = entry;
        result.Image = image;

        ReadProgramHeaders(reader, image, problems, programHeaderOffset, programHeaderEntrySize,
            programHeaderCount, is64);
        ReadSectionHeaders(reader, image, problems, sectionHeaderOffset, sectionHeaderEntrySize,
            sectionHeaderCount, sectionNameIndex, is64);

        if (image.Segments.Count > 0)
        {
            image.ImageBase = image.Segments.Where(s => s.Type == 1).Min(s => s.VirtualAddress);
        }
        else if (image.AllocatedSections.Count > 0)
        {
            image.ImageBase = image.AllocatedSections.Min(s => s.Address);
        }

        ReadSymbols(reader, image, problems, is64);
        ReadRelocations(reader, image, problems, is64);
        ReadDynamic(reader, image, problems, is64);
        ReadNotes(reader, image, problems, is64);
        ReadCommentStrings(reader, image);
        ReadDebugLink(reader, image);

        return result;
    }

    // ---------------------------------------------------------------- program headers

    private static void ReadProgramHeaders(
        ByteReader reader, ElfImage image, List<string> problems,
        ulong offset, int entrySize, int count, bool is64)
    {
        if (count == 0 || offset == 0)
        {
            return;
        }

        int expected = is64 ? 56 : 32;
        if (entrySize < expected)
        {
            problems.Add($"program header entry size {entrySize} is smaller than {expected}");
            return;
        }

        if (!TryOffset(offset, out int start, problems, "program headers"))
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            int at = start + (i * entrySize);
            if (!reader.Contains(at, expected))
            {
                problems.Add($"program header {i} is outside the file");
                break;
            }

            var segment = new ElfSegment { Type = reader.U32(at) ?? 0 };
            if (is64)
            {
                segment.Flags = reader.U32(at + 4) ?? 0;
                segment.Offset = reader.U64(at + 8) ?? 0;
                segment.VirtualAddress = reader.U64(at + 16) ?? 0;
                segment.FileSize = reader.U64(at + 32) ?? 0;
                segment.MemorySize = reader.U64(at + 40) ?? 0;
                segment.Alignment = reader.U64(at + 48) ?? 0;
            }
            else
            {
                segment.Offset = reader.U32(at + 4) ?? 0;
                segment.VirtualAddress = reader.U32(at + 8) ?? 0;
                segment.FileSize = reader.U32(at + 16) ?? 0;
                segment.MemorySize = reader.U32(at + 20) ?? 0;
                segment.Flags = reader.U32(at + 24) ?? 0;
                segment.Alignment = reader.U32(at + 28) ?? 0;
            }

            segment.TypeName = SegmentTypeName(segment.Type);
            image.Segments.Add(segment);

            if (segment.Type == 3 && image.Interpreter is null && reader.Contains((int)segment.Offset, 1))
            {
                // PT_INTERP: the path of the dynamic loader, read from the file at its offset.
                if (TryOffset(segment.Offset, out int interp, problems, "PT_INTERP"))
                {
                    image.Interpreter = reader.AsciiZ(interp, Math.Min(256, (int)segment.FileSize));
                }
            }
        }
    }

    private static string SegmentTypeName(uint type) => type switch
    {
        0 => "NULL",
        1 => "LOAD",
        2 => "DYNAMIC",
        3 => "INTERP",
        4 => "NOTE",
        5 => "SHLIB",
        6 => "PHDR",
        7 => "TLS",
        0x6474E550 => "GNU_EH_FRAME",
        0x6474E551 => "GNU_STACK",
        0x6474E552 => "GNU_RELRO",
        0x6474E553 => "GNU_PROPERTY",
        _ => $"0x{type:X8}",
    };

    // ---------------------------------------------------------------- sections

    private static void ReadSectionHeaders(
        ByteReader reader, ElfImage image, List<string> problems,
        ulong offset, int entrySize, int count, int nameIndex, bool is64)
    {
        if (offset == 0)
        {
            // A file with no section headers still describes itself through its program headers;
            // only a stripped-of-sections file needs them, so this is not a problem.
            return;
        }

        int expected = is64 ? 64 : 40;
        if (entrySize < expected)
        {
            problems.Add($"section header entry size {entrySize} is smaller than {expected}");
            return;
        }

        if (!TryOffset(offset, out int start, problems, "section headers"))
        {
            return;
        }

        // A file with more than 64k sections, or section names beyond the first 64k sections,
        // keeps the real numbers in section header 0 instead of in the ELF header.
        if (reader.Contains(start, expected))
        {
            ulong firstSize = is64 ? reader.U64(start + 32) ?? 0 : reader.U32(start + 20) ?? 0;
            uint firstLink = is64 ? reader.U32(start + 40) ?? 0 : reader.U32(start + 24) ?? 0;
            if (count == 0)
            {
                count = firstSize > int.MaxValue ? 0 : (int)firstSize;
            }

            if (nameIndex == 0xFFFF)
            {
                nameIndex = (int)firstLink;
            }
        }

        for (int i = 0; i < count; i++)
        {
            int at = start + (i * entrySize);
            if (!reader.Contains(at, expected))
            {
                problems.Add($"section header {i} is outside the file");
                break;
            }

            var section = new ElfSection { Index = i };
            section.RawType = reader.U32(at + 4) ?? 0;
            section.Type = (ElfSectionType)section.RawType;
            if (is64)
            {
                section.Flags = (ElfSectionFlags)(reader.U64(at + 8) ?? 0);
                section.Address = reader.U64(at + 16) ?? 0;
                section.Offset = reader.U64(at + 24) ?? 0;
                section.Size = reader.U64(at + 32) ?? 0;
                section.Link = reader.U32(at + 40) ?? 0;
                section.Info = reader.U32(at + 44) ?? 0;
                section.Alignment = reader.U64(at + 48) ?? 0;
                section.EntrySize = reader.U64(at + 56) ?? 0;
            }
            else
            {
                section.Flags = (ElfSectionFlags)(reader.U32(at + 8) ?? 0);
                section.Address = reader.U32(at + 12) ?? 0;
                section.Offset = reader.U32(at + 16) ?? 0;
                section.Size = reader.U32(at + 20) ?? 0;
                section.Link = reader.U32(at + 24) ?? 0;
                section.Info = reader.U32(at + 28) ?? 0;
                section.Alignment = reader.U32(at + 32) ?? 0;
                section.EntrySize = reader.U32(at + 36) ?? 0;
            }

            section.Name = string.Empty;
            image.Sections.Add(section);
        }

        // Names come last: they live in a section of their own, so every header has to be read
        // before any of them can be named.
        if (nameIndex >= 0 && nameIndex < image.Sections.Count)
        {
            var names = image.Sections[nameIndex];
            foreach (var section in image.Sections)
            {
                int nameOffset = start + (section.Index * entrySize);
                uint index = reader.U32(nameOffset) ?? 0;
                section.Name = StringAt(reader, names, index);
            }
        }
    }

    private static string StringAt(ByteReader reader, ElfSection strings, uint index)
    {
        if (strings.Type is ElfSectionType.NoBits || !TryOffset(strings.Offset + index, out int at, null, null))
        {
            return string.Empty;
        }

        return reader.AsciiZ(at, Math.Max(0, (int)(strings.Size - index))) ?? string.Empty;
    }

    // ---------------------------------------------------------------- symbols

    private static void ReadSymbols(ByteReader reader, ElfImage image, List<string> problems, bool is64)
    {
        var versions = ReadSymbolVersions(reader, image, is64);
        int entrySize = is64 ? 24 : 16;

        foreach (var section in image.Sections)
        {
            if (section.Type is not (ElfSectionType.SymbolTable or ElfSectionType.DynamicSymbols))
            {
                continue;
            }

            string source = section.Type is ElfSectionType.SymbolTable ? "symtab" : "dynsym";
            var strings = section.Link < image.Sections.Count ? image.Sections[(int)section.Link] : null;
            if (strings is null)
            {
                problems.Add($"symbol table {section.Name} has no string table");
                continue;
            }

            if (!TryOffset(section.Offset, out int start, problems, section.Name))
            {
                continue;
            }

            ulong size = section.EntrySize > 0 ? section.EntrySize : (ulong)entrySize;
            if (size < (ulong)entrySize)
            {
                problems.Add($"symbol table {section.Name} has an entry size of {size}");
                continue;
            }

            long count = (long)(section.Size / size);
            for (long i = 0; i < count; i++)
            {
                int at = start + (int)(i * (long)size);
                if (!reader.Contains(at, entrySize))
                {
                    problems.Add($"symbol {i} of {section.Name} is outside the file");
                    break;
                }

                var symbol = new ElfSymbol { Source = source };
                uint nameIndex = reader.U32(at) ?? 0;
                if (is64)
                {
                    byte info = reader.U8(at + 4) ?? 0;
                    symbol.Binding = (ElfSymbolBinding)(info >> 4);
                    symbol.Kind = (ElfSymbolKind)(info & 0xF);
                    symbol.Visibility = (ElfSymbolVisibility)((reader.U8(at + 5) ?? 0) & 0x3);
                    symbol.SectionIndex = reader.U16(at + 6) ?? 0;
                    symbol.Address = reader.U64(at + 8) ?? 0;
                    symbol.Size = reader.U64(at + 16) ?? 0;
                }
                else
                {
                    symbol.Address = reader.U32(at + 4) ?? 0;
                    symbol.Size = reader.U32(at + 8) ?? 0;
                    byte info = reader.U8(at + 12) ?? 0;
                    symbol.Binding = (ElfSymbolBinding)(info >> 4);
                    symbol.Kind = (ElfSymbolKind)(info & 0xF);
                    symbol.Visibility = (ElfSymbolVisibility)((reader.U8(at + 13) ?? 0) & 0x3);
                    symbol.SectionIndex = reader.U16(at + 14) ?? 0;
                }

                symbol.Name = StringAt(reader, strings, nameIndex);
                symbol.SectionName = symbol.SectionIndex < image.Sections.Count
                    ? image.Sections[symbol.SectionIndex].Name
                    : (symbol.SectionIndex == 0 ? null : $"shndx {symbol.SectionIndex}");

                // Entry 0 is the reserved "no symbol" entry. It is kept, because a relocation
                // names a symbol by its index in this table, and dropping it would shift them all.
                if (versions.TryGetValue((section.Index, (int)i), out string? version))
                {
                    symbol.VersionedName = $"{symbol.Name}@{version}";
                }
                else
                {
                    symbol.VersionedName = symbol.Name;
                }

                image.Symbols.Add(symbol);
            }
        }
    }

    /// <summary>
    /// Symbol versioning: <c>.gnu.version</c> gives each symbol an index, and the index names a
    /// version in <c>.gnu.version_d</c> (defined here) or <c>.gnu.version_r</c> (needed from a
    /// library). Without it an import is just <c>printf</c>; with it, <c>printf@GLIBC_2.2.5</c>.
    /// </summary>
    private static Dictionary<(int Section, int Symbol), string> ReadSymbolVersions(
        ByteReader reader, ElfImage image, bool is64)
    {
        var versions = new Dictionary<(int, int), string>();
        var versionSymbolSection = image.Sections.FirstOrDefault(s => s.Type is ElfSectionType.GnuVersionSymbol);
        if (versionSymbolSection is null)
        {
            return versions;
        }

        // Index -> name, from both tables. A defined symbol's version is one this file provides;
        // an undefined one's is a version it asks a library for.
        var names = new Dictionary<uint, string>();
        foreach (var section in image.Sections.Where(s => s.Type is ElfSectionType.GnuVersionDefinition))
        {
            var strings = section.Link < image.Sections.Count ? image.Sections[(int)section.Link] : null;
            if (strings is null || !TryOffset(section.Offset, out int start, null, null))
            {
                continue;
            }

            long at = start;
            long end = start + (long)section.Size;
            while (at + 20 <= end)
            {
                // vd_version(0) vd_flags(2) vd_ndx(4) vd_cnt(6) vd_hash(8) vd_aux(12) vd_next(16)
                ushort index = reader.U16((int)(at + 4)) ?? 0;
                ushort count = reader.U16((int)(at + 6)) ?? 0;
                uint auxiliary = reader.U32((int)(at + 12)) ?? 0;
                uint next = reader.U32((int)(at + 16)) ?? 0;
                long entry = at + auxiliary;
                for (int i = 0; i < count; i++)
                {
                    uint nameIndex = reader.U32((int)entry) ?? 0;
                    uint nextEntry = reader.U32((int)(entry + 4)) ?? 0;
                    string name = StringAt(reader, strings, nameIndex);
                    // Index 1 is the file's own base version, which readelf prints with two @s and
                    // which says nothing an analyst needs, so it is left off the name.
                    if (index > 1 && name.Length > 0 && !names.ContainsKey(index))
                    {
                        names[index] = name;
                    }

                    if (nextEntry == 0)
                    {
                        break;
                    }

                    entry += nextEntry;
                }

                if (next == 0)
                {
                    break;
                }

                at += next;
            }
        }

        foreach (var section in image.Sections.Where(s => s.Type is ElfSectionType.GnuVersionNeed))
        {
            var strings = section.Link < image.Sections.Count ? image.Sections[(int)section.Link] : null;
            if (strings is null || !TryOffset(section.Offset, out int start, null, null))
            {
                continue;
            }

            long at = start;
            long end = start + (long)section.Size;
            while (at + 16 <= end)
            {
                // vn_version(0) vn_cnt(2) vn_file(4) vn_aux(8) vn_next(12)
                ushort count = reader.U16((int)(at + 2)) ?? 0;
                uint auxiliary = reader.U32((int)(at + 8)) ?? 0;
                uint next = reader.U32((int)(at + 12)) ?? 0;
                long entry = at + auxiliary;
                for (int i = 0; i < count; i++)
                {
                    // vna_hash(0) vna_flags(4) vna_other(6) vna_name(8) vna_next(12)
                    ushort other = reader.U16((int)(entry + 6)) ?? 0;
                    uint nameIndex = reader.U32((int)(entry + 8)) ?? 0;
                    uint nextEntry = reader.U32((int)(entry + 12)) ?? 0;
                    string name = StringAt(reader, strings, nameIndex);
                    if (other > 1 && name.Length > 0 && !names.ContainsKey(other))
                    {
                        names[other] = name;
                    }

                    if (nextEntry == 0)
                    {
                        break;
                    }

                    entry += nextEntry;
                }

                if (next == 0)
                {
                    break;
                }

                at += next;
            }
        }

        if (names.Count == 0 || !TryOffset(versionSymbolSection.Offset, out int table, null, null))
        {
            return versions;
        }

        long entries = (long)(versionSymbolSection.Size / 2);
        for (long i = 0; i < entries; i++)
        {
            ushort raw = reader.U16(table + (int)(i * 2)) ?? 0;
            uint index = (uint)(raw & 0x7FFF);
            if (index > 1 && names.TryGetValue(index, out string? name))
            {
                versions[(versionSymbolSection.Link is 0 ? 0 : (int)versionSymbolSection.Link, (int)i)] = name;
            }
        }

        _ = is64;
        return versions;
    }

    // ---------------------------------------------------------------- relocations

    private static void ReadRelocations(ByteReader reader, ElfImage image, List<string> problems, bool is64)
    {
        foreach (var section in image.Sections)
        {
            if (section.Type is not (ElfSectionType.Rel or ElfSectionType.Rela))
            {
                continue;
            }

            bool hasAddend = section.Type is ElfSectionType.Rela;
            int unit = is64 ? (hasAddend ? 24 : 16) : (hasAddend ? 12 : 8);
            if (section.EntrySize > (ulong)unit)
            {
                unit = (int)section.EntrySize;
            }

            var symbols = SymbolsOf(image, section);
            if (!TryOffset(section.Offset, out int start, problems, section.Name))
            {
                continue;
            }

            long count = (long)(section.Size / (ulong)unit);
            for (long i = 0; i < count; i++)
            {
                int at = start + (int)(i * unit);
                if (!reader.Contains(at, unit))
                {
                    problems.Add($"relocation {i} of {section.Name} is outside the file");
                    break;
                }

                var relocation = new ElfRelocation { SectionName = section.Name };
                ulong info;
                if (is64)
                {
                    relocation.Address = reader.U64(at) ?? 0;
                    info = reader.U64(at + 8) ?? 0;
                    if (hasAddend)
                    {
                        relocation.Addend = (long)(reader.U64(at + 16) ?? 0);
                    }
                }
                else
                {
                    relocation.Address = reader.U32(at) ?? 0;
                    info = reader.U32(at + 4) ?? 0;
                    if (hasAddend)
                    {
                        relocation.Addend = (int)(reader.U32(at + 8) ?? 0);
                    }
                }

                // The split of r_info depends on the word size, not on the machine: an ELF64 file
                // puts the symbol in the high 32 bits and gives the type the low 32, and an ELF32
                // one puts the symbol in the high 24 and the type in the low 8. Going by the machine
                // instead gave an AArch64 file's 275 the value 19, since 275 & 0xFF is 19.
                uint symbolIndex = is64 ? (uint)(info >> 32) : (uint)(info >> 8);
                relocation.Type = (uint)(is64 ? info & 0xFFFF_FFFF : info & 0xFF);
                relocation.TypeName = RelocationTypeName(image.Machine, relocation.Type);
                relocation.Width = RelocationWidth(image.Machine, relocation.Type);

                if (symbolIndex < symbols.Count)
                {
                    var symbol = symbols[(int)symbolIndex];
                    relocation.SymbolName = symbol.Display;
                    relocation.IsDefined = symbol.IsDefined;
                    if (symbol.IsDefined)
                    {
                        relocation.SymbolAddress = symbol.Address;
                        relocation.TargetAddress = (ulong)((long)symbol.Address + relocation.Addend);
                    }
                }
                else if (symbolIndex != 0)
                {
                    relocation.SymbolName = $"symbol {symbolIndex}";
                }

                if (relocation.TargetAddress is null && IsRelative(image.Machine, relocation.Type))
                {
                    // R_*_RELATIVE names no symbol: the addend is the address itself, which for a
                    // non-PIE file is already the target and for a PIE is a link-time address.
                    relocation.TargetAddress = (ulong)relocation.Addend;
                }

                image.Relocations.Add(relocation);
            }
        }
    }

    /// <summary>The symbol table a relocation section refers to, as a list indexed by symbol index.</summary>
    private static List<ElfSymbol> SymbolsOf(ElfImage image, ElfSection relocationSection)
    {
        if (relocationSection.Link >= image.Sections.Count)
        {
            return [];
        }

        var table = image.Sections[(int)relocationSection.Link];
        return [.. image.Symbols.Where(s => s.Source == (table.Type is ElfSectionType.DynamicSymbols ? "dynsym" : "symtab"))];
    }

    /// <summary>Relocations that name no symbol: the addend already is the address.</summary>
    private static bool IsRelative(ushort machine, uint type) => machine switch
    {
        62 => type is 8 or 37 or 38,
        3 => type is 8 or 42,
        _ => type == 8,
    };

    private static string RelocationTypeName(ushort machine, uint type) => machine switch
    {
        62 => X86_64TypeName(type),
        3 => I386TypeName(type),
        183 => AArch64TypeName(type),
        40 => ArmTypeName(type),
        _ => $"type {type} of machine {MachineName(machine)}",
    };

    /// <summary>
    /// Whose number a relocation type is, for the machines this tool has no table for: `type 257`
    /// means nothing on its own, and `type 257 of machine AArch64` names the place to look it up.
    /// </summary>
    private static string MachineName(ushort machine) => machine switch
    {
        3 => "i386",
        40 => "ARM",
        62 => "x86-64",
        183 => "AArch64",
        _ => $"0x{machine:X}",
    };

    /// <summary>
    /// AArch64 relocation types. Every name here was read out of an object assembled by
    /// <c>llvm-mc-19 -triple=aarch64-linux-gnu</c> and printed by <c>llvm-readelf-19 -r</c>, which is
    /// the only source this tool trusts for a number like this; the ones it did not produce fall back
    /// to <c>R_AARCH64_&lt;n&gt;</c>, which is at least the right namespace rather than a guess.
    /// </summary>
    private static string AArch64TypeName(uint type) => type switch
    {
        257 => "R_AARCH64_ABS64",
        258 => "R_AARCH64_ABS32",
        259 => "R_AARCH64_ABS16",
        260 => "R_AARCH64_PREL64",
        261 => "R_AARCH64_PREL32",
        275 => "R_AARCH64_ADR_PREL_PG_HI21",
        277 => "R_AARCH64_ADD_ABS_LO12_NC",
        278 => "R_AARCH64_LDST8_ABS_LO12_NC",
        282 => "R_AARCH64_JUMP26",
        283 => "R_AARCH64_CALL26",
        284 => "R_AARCH64_LDST16_ABS_LO12_NC",
        285 => "R_AARCH64_LDST32_ABS_LO12_NC",
        286 => "R_AARCH64_LDST64_ABS_LO12_NC",
        309 => "R_AARCH64_GOT_LD_PREL19",
        311 => "R_AARCH64_ADR_GOT_PAGE",
        312 => "R_AARCH64_LD64_GOT_LO12_NC",
        _ => $"R_AARCH64_{type}",
    };

    /// <summary>ARM relocation types, verified the same way (<c>-triple=armv7a-linux-gnueabihf</c>).</summary>
    private static string ArmTypeName(uint type) => type switch
    {
        2 => "R_ARM_ABS32",
        4 => "R_ARM_LDR_PC_G0",
        5 => "R_ARM_ABS16",
        28 => "R_ARM_CALL",
        29 => "R_ARM_JUMP24",
        _ => $"R_ARM_{type}",
    };

    private static byte RelocationWidth(ushort machine, uint type) => machine switch
    {
        62 => type switch
        {
            1 or 24 or 25 or 27 or 28 or 29 or 30 or 31 or 33 or 38 => 8,
            12 or 13 => 2,
            14 or 15 => 1,
            _ => 4,
        },
        3 => type switch
        {
            20 or 21 => 2,
            22 or 23 => 1,
            _ => 4,
        },
        183 => type switch
        {
            // The width the name states: the 64-bit absolutes, and the 16-bit one.
            257 or 260 => 8,
            259 => 2,
            _ => 4,
        },
        40 => type switch
        {
            5 or 6 => 2,
            _ => 4,
        },
        _ => 8,
    };

    private static string X86_64TypeName(uint type) => type switch
    {
        0 => "R_X86_64_NONE",
        1 => "R_X86_64_64",
        2 => "R_X86_64_PC32",
        3 => "R_X86_64_GOT32",
        4 => "R_X86_64_PLT32",
        5 => "R_X86_64_COPY",
        6 => "R_X86_64_GLOB_DAT",
        7 => "R_X86_64_JUMP_SLOT",
        8 => "R_X86_64_RELATIVE",
        9 => "R_X86_64_GOTPCREL",
        10 => "R_X86_64_32",
        11 => "R_X86_64_32S",
        12 => "R_X86_64_16",
        13 => "R_X86_64_PC16",
        14 => "R_X86_64_8",
        15 => "R_X86_64_PC8",
        16 => "R_X86_64_DTPMOD64",
        17 => "R_X86_64_DTPOFF64",
        18 => "R_X86_64_TPOFF64",
        19 => "R_X86_64_TLSGD",
        20 => "R_X86_64_TLSLD",
        21 => "R_X86_64_DTPOFF32",
        22 => "R_X86_64_GOTTPOFF",
        23 => "R_X86_64_TPOFF32",
        24 => "R_X86_64_PC64",
        25 => "R_X86_64_GOTOFF64",
        26 => "R_X86_64_GOTPC32",
        27 => "R_X86_64_GOT64",
        28 => "R_X86_64_GOTPCREL64",
        29 => "R_X86_64_GOTPC64",
        30 => "R_X86_64_GOTPLT64",
        31 => "R_X86_64_PLTOFF64",
        32 => "R_X86_64_SIZE32",
        33 => "R_X86_64_SIZE64",
        34 => "R_X86_64_GOTPC32_TLSDESC",
        35 => "R_X86_64_TLSDESC_CALL",
        36 => "R_X86_64_TLSDESC",
        37 => "R_X86_64_IRELATIVE",
        38 => "R_X86_64_RELATIVE64",
        41 => "R_X86_64_GOTPCRELX",
        42 => "R_X86_64_REX_GOTPCRELX",
        43 => "R_X86_64_CODE_4_GOTPCRELX",
        44 => "R_X86_64_CODE_4_GOTTPOFF",
        45 => "R_X86_64_CODE_4_GOTPC32_TLSDESC",
        46 => "R_X86_64_CODE_5_GOTPCRELX",
        47 => "R_X86_64_CODE_5_GOTTPOFF",
        48 => "R_X86_64_CODE_5_GOTPC32_TLSDESC",
        49 => "R_X86_64_CODE_6_GOTPCRELX",
        _ => $"R_X86_64_{type}",
    };

    private static string I386TypeName(uint type) => type switch
    {
        0 => "R_386_NONE",
        1 => "R_386_32",
        2 => "R_386_PC32",
        3 => "R_386_GOT32",
        4 => "R_386_PLT32",
        5 => "R_386_COPY",
        6 => "R_386_GLOB_DAT",
        7 => "R_386_JUMP_SLOT",
        8 => "R_386_RELATIVE",
        9 => "R_386_GOTOFF",
        10 => "R_386_GOTPC",
        11 => "R_386_32PLT",
        14 => "R_386_TLS_TPOFF",
        15 => "R_386_TLS_IE",
        16 => "R_386_TLS_GOTIE",
        17 => "R_386_TLS_LE",
        18 => "R_386_TLS_GD",
        19 => "R_386_TLS_LDM",
        20 => "R_386_16",
        21 => "R_386_PC16",
        22 => "R_386_8",
        23 => "R_386_PC8",
        24 => "R_386_TLS_GD_32",
        25 => "R_386_TLS_GD_PUSH",
        26 => "R_386_TLS_GD_CALL",
        27 => "R_386_TLS_GD_POP",
        28 => "R_386_TLS_LDM_32",
        29 => "R_386_TLS_LDM_PUSH",
        30 => "R_386_TLS_LDM_CALL",
        31 => "R_386_TLS_LDM_POP",
        32 => "R_386_TLS_LDO_32",
        33 => "R_386_TLS_IE_32",
        34 => "R_386_TLS_LE_32",
        35 => "R_386_TLS_DTPMOD32",
        36 => "R_386_TLS_DTPOFF32",
        37 => "R_386_TLS_TPOFF32",
        38 => "R_386_SIZE32",
        39 => "R_386_TLS_GOTDESC",
        40 => "R_386_TLS_DESC_CALL",
        41 => "R_386_TLS_DESC",
        42 => "R_386_IRELATIVE",
        43 => "R_386_GOT32X",
        _ => $"R_386_{type}",
    };

    // ---------------------------------------------------------------- dynamic section

    private static void ReadDynamic(ByteReader reader, ElfImage image, List<string> problems, bool is64)
    {
        var section = image.Sections.FirstOrDefault(s => s.Type is ElfSectionType.Dynamic);
        if (section is null)
        {
            return;
        }

        if (!TryOffset(section.Offset, out int start, problems, section.Name))
        {
            return;
        }

        // Strings named by DT_NEEDED and friends live in DT_STRTAB, whose value is an address.
        ulong stringTableAddress = 0;
        ulong stringTableSize = 0;
        int unit = is64 ? 16 : 8;
        long count = (long)(section.Size / (ulong)unit);
        var entries = new List<(ulong Tag, ulong Value)>();

        for (long i = 0; i < count; i++)
        {
            int at = start + (int)(i * unit);
            if (!reader.Contains(at, unit))
            {
                problems.Add($"dynamic entry {i} is outside the file");
                break;
            }

            ulong tag = is64 ? reader.U64(at) ?? 0 : reader.U32(at) ?? 0;
            ulong value = is64 ? reader.U64(at + 8) ?? 0 : reader.U32(at + 4) ?? 0;
            entries.Add((tag, value));
            if (tag == 0)
            {
                break;
            }

            if (tag == 5)
            {
                stringTableAddress = value;
            }

            if (tag == 10)
            {
                stringTableSize = value;
            }

            // Nothing calls these two; the loader does, and the dynamic table is the only place
            // that says where they are.
            if (tag == 12)
            {
                image.InitAddress = value;
            }

            if (tag == 13)
            {
                image.FiniAddress = value;
            }
        }

        foreach (var (tag, value) in entries)
        {
            var entry = new ElfDynamicEntry { Tag = tag, Value = value, TagName = DynamicTagName(tag) };
            if (tag is 1 or 14 or 15 or 29 && stringTableAddress != 0)
            {
                if (image.AddressToOffset(stringTableAddress + value) is ulong stringOffset
                    && TryOffset(stringOffset, out int at, problems, "dynamic string"))
                {
                    entry.Text = reader.AsciiZ(at, (int)Math.Max(0, stringTableSize - value));
                }

                if (tag == 1 && entry.Text is not null)
                {
                    image.Needed.Add(entry.Text);
                }

                if (tag == 14)
                {
                    image.SoName = entry.Text;
                }
            }

            image.Dynamic.Add(entry);
        }
    }

    private static string DynamicTagName(ulong tag) => tag switch
    {
        0 => "DT_NULL",
        1 => "DT_NEEDED",
        2 => "DT_PLTRELSZ",
        3 => "DT_PLTGOT",
        4 => "DT_HASH",
        5 => "DT_STRTAB",
        6 => "DT_SYMTAB",
        7 => "DT_RELA",
        8 => "DT_RELASZ",
        9 => "DT_RELAENT",
        10 => "DT_STRSZ",
        11 => "DT_SYMENT",
        12 => "DT_INIT",
        13 => "DT_FINI",
        14 => "DT_SONAME",
        15 => "DT_RPATH",
        16 => "DT_SYMBOLIC",
        17 => "DT_REL",
        18 => "DT_RELSZ",
        19 => "DT_RELENT",
        20 => "DT_PLTREL",
        21 => "DT_DEBUG",
        22 => "DT_TEXTREL",
        23 => "DT_JMPREL",
        24 => "DT_BIND_NOW",
        25 => "DT_INIT_ARRAY",
        26 => "DT_FINI_ARRAY",
        27 => "DT_INIT_ARRAYSZ",
        28 => "DT_FINI_ARRAYSZ",
        29 => "DT_RUNPATH",
        30 => "DT_FLAGS",
        32 => "DT_PREINIT_ARRAY",
        33 => "DT_PREINIT_ARRAYSZ",
        34 => "DT_SYMTAB_SHNDX",
        35 => "DT_RELRSZ",
        36 => "DT_RELR",
        0x6FFFFEF5 => "DT_GNU_HASH",
        0x6FFFFFF0 => "DT_VERSYM",
        0x6FFFFFF9 => "DT_RELACOUNT",
        0x6FFFFFFA => "DT_RELCOUNT",
        0x6FFFFFFC => "DT_VERDEF",
        0x6FFFFFFD => "DT_VERDEFNUM",
        0x6FFFFFFE => "DT_VERNEED",
        0x6FFFFFFF => "DT_VERNEEDNUM",
        _ => $"0x{tag:X}",
    };

    // ---------------------------------------------------------------- notes, comments, debug link

    private static void ReadNotes(ByteReader reader, ElfImage image, List<string> problems, bool is64)
    {
        foreach (var section in image.Sections.Where(s => s.Type is ElfSectionType.Note))
        {
            if (!TryOffset(section.Offset, out int start, problems, section.Name))
            {
                continue;
            }

            long at = start;
            long end = start + (long)section.Size;
            while (at + 12 <= end)
            {
                uint nameSize = reader.U32((int)at) ?? 0;
                uint descriptionSize = reader.U32((int)at + 4) ?? 0;
                uint type = reader.U32((int)at + 8) ?? 0;
                long nameAt = at + 12;
                int nameLength = (int)Math.Min(nameSize, end - nameAt);
                string name = nameLength > 0 ? reader.AsciiZ((int)nameAt, nameLength) ?? string.Empty : string.Empty;
                long descriptionAt = nameAt + Align4(nameSize);
                int descriptionLength = (int)Math.Min(descriptionSize, end - descriptionAt);
                var note = new ElfNote
                {
                    Name = name.TrimEnd('\0'),
                    Type = type,
                    SectionName = section.Name,
                    Description = descriptionLength > 0 && reader.Contains((int)descriptionAt, descriptionLength)
                        ? reader.Span((int)descriptionAt, descriptionLength).ToArray()
                        : [],
                };

                image.Notes.Add(note);
                if (note.Name == "GNU" && note.Type == 3 && image.BuildId is null)
                {
                    image.BuildId = Convert.ToHexStringLower(note.Description);
                }

                long step = 12 + Align4(nameSize) + Align4(descriptionSize);
                if (step <= 0)
                {
                    break;
                }

                at += step;
            }
        }

        _ = is64;
    }

    private static long Align4(uint size) => (size + 3) & ~3u;

    private static void ReadCommentStrings(ByteReader reader, ElfImage image)
    {
        var section = image.SectionNamed(".comment");
        if (section is null || section.Type is ElfSectionType.NoBits
            || !TryOffset(section.Offset, out int start, null, null))
        {
            return;
        }

        foreach (string part in Encoding.Latin1.GetString(reader.Span(start, (int)section.Size)).Split('\0'))
        {
            string text = part.Trim();
            if (text.Length > 0)
            {
                image.CommentStrings.Add(text);
            }
        }
    }

    private static void ReadDebugLink(ByteReader reader, ElfImage image)
    {
        var section = image.SectionNamed(".gnu_debuglink");
        if (section is null || !TryOffset(section.Offset, out int start, null, null))
        {
            return;
        }

        string? name = reader.AsciiZ(start, Math.Max(0, (int)section.Size - 4));
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        image.DebugLink = name;
        int crcAt = start + name.Length + 1;
        crcAt = (crcAt + 3) & ~3;
        if (reader.Contains(crcAt, 4))
        {
            image.DebugLinkCrc = reader.U32(crcAt);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// ELF offsets and sizes are 64-bit; the reader works in signed integers, so anything that does
    /// not fit is reported as a problem instead of being truncated into a wrong answer.
    /// </summary>
    private static bool TryOffset(ulong offset, out int result, List<string>? problems, string? what)
    {
        if (offset > int.MaxValue)
        {
            problems?.Add($"{what ?? "offset"} is at {offset}, which is beyond what can be read");
            result = 0;
            return false;
        }

        result = (int)offset;
        return true;
    }
}
