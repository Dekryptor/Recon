using Recon.Images;

namespace Recon.Macho;

/// <summary>
/// Reads a Mach-O file: the one the reference repository is about, and the third format this tool
/// loads. Like the others it is written to return what it could read plus a list of what it could
/// not, because a file that ends early is data rather than an exception.
/// </summary>
/// <remarks>
/// Mach-O has no section table: sections live inside the segment commands, so the load commands have
/// to be walked before anything else is known about the image. A fat file has one more layer in
/// front: several images, one per architecture, in one file.
/// </remarks>
public static class MachoLoader
{
    private const uint Magic32 = 0xFEEDFACE;
    private const uint Magic64 = 0xFEEDFACF;
    private const uint Cigam32 = 0xCEFAEDFE;
    private const uint Cigam64 = 0xCFFAEDFE;

    /// <summary>A fat header is big-endian whatever its slices are, so both magics read backwards.</summary>
    private const uint FatMagic = 0xBEBAFECA;
    private const uint FatMagic64 = 0xBFBAFECA;

    private const uint LcSegment = 0x1;
    private const uint LcSymtab = 0x2;
    private const uint LcDysymtab = 0xB;
    private const uint LcUuid = 0x1B;
    private const uint LcIdDylib = 0xD;
    private const uint LcLoadDylib = 0xC;
    private const uint LcLoadWeakDylib = 0x18;
    private const uint LcReexportDylib = 0x1F;
    private const uint LcLazyLoadDylib = 0x20;
    private const uint LcLoadUpwardDylib = 0x80000023;
    private const uint LcVersionMinMacosx = 0x24;
    private const uint LcVersionMinIphoneos = 0x25;
    private const uint LcVersionMinTvos = 0x2F;
    private const uint LcVersionMinWatchos = 0x30;
    private const uint LcFunctionStarts = 0x26;
    private const uint LcSourceVersion = 0x2A;
    private const uint LcBuildVersion = 0x32;
    private const uint LcEncryptionInfo64 = 0x2C;
    private const uint LcEncryptionInfo = 0x21;
    private const uint LcCodeSignature = 0x1D;
    private const uint LcMain = 0x80000028;
    private const uint LcSegment64 = 0x19;

    public static MachoLoadResult Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new MachoLoadResult { Problems = [$"cannot read {path}: {ex.Message}"] };
        }

        return LoadBytes(bytes, path);
    }

    public static MachoLoadResult LoadBytes(byte[] bytes, string path = "<memory>")
    {
        var result = new MachoLoadResult { Bytes = bytes };
        var problems = result.Problems;

        if (bytes.Length < 28)
        {
            problems.Add($"file is too small to be a Mach-O image ({bytes.Length} bytes)");
            return result;
        }

        var probe = new ByteReader(bytes);
        uint magic = probe.U32(0) ?? 0;

        // A fat file is a container: several images and the architectures they are for. This tool
        // reads the slice it can say the most about — the one matching the machine it is running on
        // when there is one, and the first otherwise — and records the rest rather than hiding them,
        // because "which architecture is this?" has more than one answer for these files.
        if (magic is FatMagic or FatMagic64)
        {
            return LoadFat(bytes, path, magic == FatMagic64);
        }

        var (is64, bigEndian) = ClassOf(magic);
        if (is64 is null)
        {
            problems.Add($"missing Mach-O magic: not a Mach-O file (starts with 0x{magic:X8})");
            return result;
        }

        result.Image = LoadImage(bytes, path, is64.Value, bigEndian, problems);
        return result;
    }

    private static MachoLoadResult LoadFat(byte[] bytes, string path, bool wideEntries)
    {
        var result = new MachoLoadResult { Bytes = bytes };
        var problems = result.Problems;
        var reader = new ByteReader(bytes, bigEndian: true);

        uint count = reader.U32(4) ?? 0;
        if (count is 0 or > 64)
        {
            problems.Add($"fat file claims {count} architectures, which is not a number a fat file has");
            return result;
        }

        int entrySize = wideEntries ? 32 : 20;
        var slices = new List<MachoFatSlice>();

        for (uint index = 0; index < count; index++)
        {
            int at = 8 + (int)(index * entrySize);
            ulong offset;
            ulong size;
            if (wideEntries)
            {
                offset = reader.U64(at + 8) ?? 0;
                size = reader.U64(at + 16) ?? 0;
            }
            else
            {
                offset = reader.U32(at + 8) ?? 0;
                size = reader.U32(at + 12) ?? 0;
            }

            slices.Add(new MachoFatSlice
            {
                CpuType = reader.U32(at) ?? 0,
                CpuSubType = reader.U32(at + 4) ?? 0,
                Offset = offset,
                Size = size,
                Align = reader.U32(at + (wideEntries ? 24 : 16)) ?? 0,
            });
        }

        MachoFatSlice chosen = Pick(slices);
        if (chosen.Offset > int.MaxValue || chosen.Offset + chosen.Size > (ulong)bytes.Length)
        {
            problems.Add($"fat slice for {chosen.Architecture} lies outside the file (at {chosen.Offset}, {chosen.Size} bytes)");
            return result;
        }

        int start = (int)chosen.Offset;
        int length = (int)Math.Min(chosen.Size, (ulong)(bytes.Length - start));
        byte[] slice = bytes.AsSpan(start, length).ToArray();

        var sliceProbe = new ByteReader(slice);
        var (is64, bigEndian) = ClassOf(sliceProbe.U32(0) ?? 0);
        if (is64 is null)
        {
            problems.Add($"fat slice for {chosen.Architecture} is not a Mach-O image");
            return result;
        }

        result.Image = LoadImage(slice, path, is64.Value, bigEndian, problems);
        if (result.Image is not null)
        {
            result.Image.FatSlices = slices;
            result.Image.Path = path;
        }

        return result;
    }

    /// <summary>
    /// Which slice to read: the one for this machine when the file has one, so that what is
    /// inventoried is what that machine would run, and the first otherwise.
    /// </summary>
    private static MachoFatSlice Pick(List<MachoFatSlice> slices)
    {
        uint native = Environment.Is64BitProcess ? MachoCpu.X86_64 : MachoCpu.X86;
        return slices.FirstOrDefault(s => s.CpuType == native) ?? slices[0];
    }

    private static (bool? Is64, bool BigEndian) ClassOf(uint magic) => magic switch
    {
        Magic64 => (true, false),
        Magic32 => (false, false),
        // Read the other way round: the file's own byte order is not this machine's.
        Cigam64 => (true, true),
        Cigam32 => (false, true),
        _ => (null, false),
    };

    private static MachoImage LoadImage(byte[] bytes, string path, bool is64, bool bigEndian, List<string> problems)
    {
        var reader = new ByteReader(bytes, bigEndian);
        var image = new MachoImage { Path = path, Is64 = is64, BigEndian = bigEndian, Sha256 = Pe.PeImage.HashBytes(bytes) };

        int headerSize = is64 ? 32 : 28;
        image.CpuType = reader.U32(4) ?? 0;
        image.CpuSubType = reader.U32(8) ?? 0;
        image.FileType = (MachoFileType)(reader.U32(12) ?? 0);
        image.LoadCommandCount = reader.U32(16) ?? 0;
        uint sizeOfCommands = reader.U32(20) ?? 0;
        image.Flags = (MachoFlags)(reader.U32(24) ?? 0);

        if (image.LoadCommandCount > 4096)
        {
            problems.Add($"the header claims {image.LoadCommandCount} load commands, which is not a number a Mach-O file has");
            return image;
        }

        int at = headerSize;
        int end = headerSize + (int)Math.Min(sizeOfCommands, (uint)Math.Max(0, bytes.Length - headerSize));
        var segments = new List<(MachoSegment Segment, uint SectionCount, int SectionAt)>();
        uint symtabOffset = 0, symtabCount = 0, stringOffset = 0, stringSize = 0;
        ulong entryOffset = 0;
        bool haveMain = false;

        uint read = 0;
        bool stopped = false;

        for (; read < image.LoadCommandCount && at + 8 <= end; read++)
        {
            uint command = reader.U32(at) ?? 0;
            uint commandSize = reader.U32(at + 4) ?? 0;
            if (commandSize < 8 || at + commandSize > bytes.Length)
            {
                problems.Add($"load command {read} has a size of {commandSize}, which does not fit the file");
                stopped = true;
                break;
            }

            switch (command)
            {
                case LcSegment64:
                case LcSegment:
                    bool wide = command == LcSegment64;
                    segments.Add(ReadSegment(reader, at, wide));
                    break;

                case LcSymtab:
                    symtabOffset = reader.U32(at + 8) ?? 0;
                    symtabCount = reader.U32(at + 12) ?? 0;
                    stringOffset = reader.U32(at + 16) ?? 0;
                    stringSize = reader.U32(at + 20) ?? 0;
                    break;

                case LcMain:
                    entryOffset = reader.U64(at + 8) ?? 0;
                    haveMain = true;
                    break;

                case LcUuid:
                    image.Uuid = Convert.ToHexString(bytes.AsSpan(at + 8, Math.Min(16, Math.Max(0, bytes.Length - at - 8)))).ToLowerInvariant();
                    break;

                case LcIdDylib:
                case LcLoadDylib:
                case LcLoadWeakDylib:
                case LcReexportDylib:
                case LcLazyLoadDylib:
                case LcLoadUpwardDylib:
                    if (ReadDylib(reader, at, command) is { } dylib)
                    {
                        image.Dylibs.Add(dylib);
                        if (command == LcIdDylib)
                        {
                            image.InstallName = dylib.Name;
                        }
                    }

                    break;

                case LcBuildVersion:
                    image.Platform = PlatformName(reader.U32(at + 8) ?? 0);
                    image.MinOs = VersionString(reader.U32(at + 12) ?? 0);
                    image.Sdk = VersionString(reader.U32(at + 16) ?? 0);
                    break;

                case LcVersionMinMacosx:
                    image.Platform ??= "macos";
                    image.MinOs ??= VersionString(reader.U32(at + 8) ?? 0);
                    image.Sdk ??= VersionString(reader.U32(at + 12) ?? 0);
                    break;

                case LcVersionMinIphoneos:
                    image.Platform ??= "ios";
                    image.MinOs ??= VersionString(reader.U32(at + 8) ?? 0);
                    image.Sdk ??= VersionString(reader.U32(at + 12) ?? 0);
                    break;

                case LcVersionMinTvos:
                    image.Platform ??= "tvos";
                    image.MinOs ??= VersionString(reader.U32(at + 8) ?? 0);
                    image.Sdk ??= VersionString(reader.U32(at + 12) ?? 0);
                    break;

                case LcVersionMinWatchos:
                    image.Platform ??= "watchos";
                    image.MinOs ??= VersionString(reader.U32(at + 8) ?? 0);
                    image.Sdk ??= VersionString(reader.U32(at + 12) ?? 0);
                    break;

                case LcSourceVersion:
                    ulong packed = reader.U64(at + 8) ?? 0;
                    image.SourceVersion = $"{packed >> 40}.{(packed >> 30) & 0x3FF}.{(packed >> 20) & 0x3FF}";
                    break;

                case LcCodeSignature:
                    image.HasCodeSignature = true;
                    break;

                case LcEncryptionInfo64:
                case LcEncryptionInfo:
                    // An encrypted image's text cannot be read at all, which is the one thing an
                    // inventory cannot work around.
                    uint cryptId = reader.U32(at + (command == LcEncryptionInfo64 ? 20 : 16)) ?? 0;
                    if (cryptId != 0)
                    {
                        image.IsEncrypted = true;
                        problems.Add("the image is encrypted (cryptid is not 0), so its code cannot be read");
                    }

                    break;

                case LcFunctionStarts:
                    image.FunctionStartsOffset = reader.U32(at + 8) ?? 0;
                    image.FunctionStartsSize = reader.U32(at + 12) ?? 0;
                    break;

                case LcDysymtab:
                    // The fields run ilocalsym, nlocalsym, iextsym, nextsym, iundefsym, nundefsym,
                    // itoc, ntoc, modtab, nmodtab, extrefsymoff, nextrefsyms, then the two this
                    // tool needs: the indirect symbol table's offset and count, 48 and 52 bytes into
                    // the command after its 8-byte header.
                    image.IndirectSymbolOffset = reader.U32(at + 56) ?? 0;
                    image.IndirectSymbolCount = reader.U32(at + 60) ?? 0;
                    break;
            }

            at += (int)commandSize;
        }

        // A header that claims commands the file does not hold is a truncated file, and saying so is
        // the difference between "this image has no sections" and "this image has no sections because
        // half of it is missing".
        if (!stopped && read < image.LoadCommandCount)
        {
            problems.Add(
                $"the file ends after {read} of {image.LoadCommandCount} load commands "
                + $"(the header claims {sizeOfCommands} bytes for them)");
        }

        foreach (var (segment, sectionCount, sectionAt) in segments)
        {
            ReadSections(reader, segment, sectionCount, sectionAt, problems);
            image.Segments.Add(segment);
            image.Sections.AddRange(segment.Sections);
        }

        if (symtabCount > 0)
        {
            ReadSymbols(reader, image, symtabOffset, symtabCount, stringOffset, stringSize, problems);
        }

        ReadIndirectSymbols(reader, image);

        ReadRelocations(reader, image, problems);

        // The image base is where the *mapped* part of the image starts, and __PAGEZERO — the
        // segment that catches null pointers — is not mapped: it has no protection and no bytes.
        // Taking the lowest segment address instead would put an x86-64 executable's base at 0 and
        // every address 4 GiB above it, which no RVA survives.
        var mapped = image.Segments.Where(s => s.MaxProtection != 0 || s.FileSize > 0).ToList();
        if (mapped.Count == 0)
        {
            mapped = image.Segments;
        }

        image.ImageBase = mapped.Count == 0 ? 0 : mapped.Min(s => s.Address);
        ulong endAddress = mapped.Count == 0 ? 0 : mapped.Max(s => s.EndAddress);
        image.SizeOfImage = endAddress > image.ImageBase ? endAddress - image.ImageBase : 0;

        if (haveMain)
        {
            // LC_MAIN's entryoff is a file offset inside the segment that holds it, and the address
            // is that segment's address plus how far into it the entry is.
            var owner = image.Segments.FirstOrDefault(s => entryOffset >= s.FileOffset && entryOffset < s.FileOffset + Math.Max(s.FileSize, 1));
            image.EntryPointAddress = owner is null
                ? entryOffset
                : owner.Address + (entryOffset - owner.FileOffset);
        }

        return image;
    }

    private static (MachoSegment Segment, uint SectionCount, int SectionAt) ReadSegment(ByteReader reader, int at, bool wide)
    {
        var segment = new MachoSegment
        {
            Name = reader.AsciiZ(at + 8, 16) ?? string.Empty,
            MaxProtection = (int)(reader.U32(at + (wide ? 56 : 40)) ?? 0),
            InitialProtection = (int)(reader.U32(at + (wide ? 60 : 44)) ?? 0),
            Flags = reader.U32(at + (wide ? 68 : 52)) ?? 0,
        };

        if (wide)
        {
            segment.Address = reader.U64(at + 24) ?? 0;
            segment.Size = reader.U64(at + 32) ?? 0;
            segment.FileOffset = reader.U64(at + 40) ?? 0;
            segment.FileSize = reader.U64(at + 48) ?? 0;
        }
        else
        {
            segment.Address = reader.U32(at + 24) ?? 0;
            segment.Size = reader.U32(at + 28) ?? 0;
            segment.FileOffset = reader.U32(at + 32) ?? 0;
            segment.FileSize = reader.U32(at + 36) ?? 0;
        }

        uint sectionCount = reader.U32(at + (wide ? 64 : 48)) ?? 0;
        int sectionAt = at + (wide ? 72 : 56);
        return (segment, sectionCount, sectionAt);
    }

    private static void ReadSections(ByteReader reader, MachoSegment segment, uint count, int at, List<string> problems)
    {
        int entrySize = 80;
        for (uint index = 0; index < count; index++)
        {
            int entry = at + (int)(index * entrySize);
            if (!reader.Contains(entry, entrySize))
            {
                problems.Add($"section {index} of {segment.Name} is outside the file");
                return;
            }

            segment.Sections.Add(new MachoSection
            {
                Name = reader.AsciiZ(entry, 16) ?? string.Empty,
                SegmentName = reader.AsciiZ(entry + 16, 16) ?? string.Empty,
                Address = reader.U64(entry + 32) ?? 0,
                Size = reader.U64(entry + 40) ?? 0,
                Offset = reader.U32(entry + 48) ?? 0,
                Align = reader.U32(entry + 52) ?? 0,
                RelocationOffset = reader.U32(entry + 56) ?? 0,
                RelocationCount = reader.U32(entry + 60) ?? 0,
                Flags = reader.U32(entry + 64) ?? 0,
                Reserved1 = reader.U32(entry + 68) ?? 0,
                Reserved2 = reader.U32(entry + 72) ?? 0,
            });
        }
    }

    private static void ReadSymbols(
        ByteReader reader,
        MachoImage image,
        uint tableOffset,
        uint count,
        uint stringOffset,
        uint stringSize,
        List<string> problems)
    {
        if (count > 1_000_000)
        {
            problems.Add($"the symbol table claims {count} entries, which is not a number a Mach-O file has");
            return;
        }

        int entrySize = image.Is64 ? 16 : 12;
        var byName = new Dictionary<string, MachoSymbol>(StringComparer.Ordinal);

        for (uint index = 0; index < count; index++)
        {
            int entry = (int)tableOffset + (int)(index * entrySize);
            if (!reader.Contains(entry, entrySize))
            {
                problems.Add($"symbol {index} is outside the file");
                return;
            }

            uint nameIndex = reader.U32(entry) ?? 0;
            byte type = reader.U8(entry + 4) ?? 0;
            byte sectionIndex = reader.U8(entry + 5) ?? 0;
            short desc = (short)(reader.U16(entry + 6) ?? 0);
            ulong value = image.Is64 ? reader.U64(entry + 8) ?? 0 : reader.U32(entry + 8) ?? 0;

            // Names live in their own table, and an index past its end is a file that lies about
            // itself: the symbol is still worth having, under the name it cannot have.
            string name = nameIndex == 0
                ? string.Empty
                : nameIndex < stringSize && reader.Contains((int)(stringOffset + nameIndex), 1)
                    ? reader.AsciiZ((int)(stringOffset + nameIndex), (int)(stringSize - nameIndex)) ?? string.Empty
                    : string.Empty;

            if (name.Length == 0 && nameIndex >= stringSize)
            {
                problems.Add($"symbol {index} names a string outside the string table");
            }

            var symbol = new MachoSymbol
            {
                Name = name,
                Value = value,
                Type = type,
                SectionIndex = sectionIndex,
                Desc = desc,
            };

            var section = image.SectionOf(symbol);
            symbol.SectionName = section?.FullName;
            symbol.IsFunction = section?.IsCode ?? false;
            symbol.IsData = section is not null && !section.IsCode;
            symbol.LibraryName = LibraryOf(image, symbol);

            image.Symbols.Add(symbol);
            if (name.Length > 0 && !symbol.IsStab)
            {
                byName.TryAdd(name, symbol);
            }
        }
    }

    /// <summary>
    /// The indirect symbol table: one entry per stub, and per slot in every symbol-pointer section,
    /// saying which symbol that stub or slot is for. Two of the high bits are flags rather than
    /// index — a local entry is not an import at all — so they come off first.
    /// </summary>
    private static void ReadIndirectSymbols(ByteReader reader, MachoImage image)
    {
        if (image.IndirectSymbolCount == 0 || image.IndirectSymbolOffset == 0)
        {
            return;
        }

        for (uint index = 0; index < image.IndirectSymbolCount; index++)
        {
            int at = (int)(image.IndirectSymbolOffset + (index * 4));
            if (!reader.Contains(at, 4))
            {
                break;
            }

            image.IndirectSymbols.Add(reader.U32(at) ?? 0);
        }
    }

    private static void ReadRelocations(ByteReader reader, MachoImage image, List<string> problems)
    {
        foreach (var section in image.Sections)
        {
            if (section.RelocationCount == 0 || section.RelocationOffset == 0)
            {
                continue;
            }

            for (uint index = 0; index < section.RelocationCount; index++)
            {
                int entry = (int)(section.RelocationOffset + (index * 8));
                if (!reader.Contains(entry, 8))
                {
                    problems.Add($"relocation {index} of {section.FullName} is outside the file");
                    break;
                }

                uint address = reader.U32(entry) ?? 0;
                uint word = reader.U32(entry + 4) ?? 0;

                // A scattered relocation is a different record entirely, and an object file is the
                // only place either kind appears; it is reported rather than misread.
                if ((address & 0x80000000) != 0)
                {
                    problems.Add($"relocation {index} of {section.FullName} is scattered, which this loader does not read");
                    continue;
                }

                var relocation = new MachoRelocation
                {
                    Address = section.Address + address,
                    SymbolIndex = word & 0x00FFFFFF,
                    IsPcRelative = (word & 0x01000000) != 0,
                    Length = (byte)((word >> 25) & 0x3),
                    IsExternal = (word & 0x08000000) != 0,
                    Type = (word >> 28) & 0xF,
                    SectionName = section.FullName,
                };

                relocation.TypeName = RelocationTypeName(image.CpuType, relocation.Type);

                if (relocation.IsExternal)
                {
                    var symbol = relocation.SymbolIndex < image.Symbols.Count ? image.Symbols[(int)relocation.SymbolIndex] : null;
                    relocation.SymbolName = symbol?.Name;
                    relocation.IsImport = symbol?.IsUndefined ?? false;
                    relocation.TargetAddress = symbol is { IsDefined: true } ? symbol.Value : null;
                }
                else
                {
                    // Addressed by section instead of by symbol: the section is the target, and the
                    // addend x86-64 leaves inside the instruction is not read out here.
                    var target = relocation.SymbolIndex > 0 && relocation.SymbolIndex <= image.Sections.Count
                        ? image.Sections[(int)relocation.SymbolIndex - 1]
                        : null;
                    relocation.SymbolName = target?.FullName;
                    relocation.TargetAddress = target?.Address;
                }

                image.Relocations.Add(relocation);
            }
        }
    }

    private static MachoDylib? ReadDylib(ByteReader reader, int at, uint command)
    {
        uint nameOffset = reader.U32(at + 8) ?? 0;
        string name = nameOffset < 4 ? string.Empty : reader.AsciiZ(at + (int)nameOffset, 1024) ?? string.Empty;
        if (name.Length == 0)
        {
            return null;
        }

        return new MachoDylib
        {
            Name = name,
            Kind = command switch
            {
                LcIdDylib => "id",
                LcLoadWeakDylib => "weak",
                LcReexportDylib => "reexport",
                LcLazyLoadDylib => "lazy",
                LcLoadUpwardDylib => "upward",
                _ => "load",
            },
            Timestamp = reader.U32(at + 12) ?? 0,
            CurrentVersion = reader.U32(at + 16) ?? 0,
            CompatibilityVersion = reader.U32(at + 20) ?? 0,
            IsId = command == LcIdDylib,
        };
    }

    private static string? LibraryOf(MachoImage image, MachoSymbol symbol)
    {
        if (!symbol.IsUndefined)
        {
            return null;
        }

        // Two-level namespaces name the library by its position among the LC_LOAD_DYLIB commands,
        // 1-based: 0 means this image, and the three highest values have their own meanings.
        ushort ordinal = symbol.LibraryOrdinal;
        var loads = image.Dylibs.Where(d => !d.IsId).ToList();
        return ordinal switch
        {
            0 => "(this image)",
            0xFD => "(flat lookup)",
            0xFE => "(dynamic lookup)",
            0xFF => "(the executable)",
            _ => ordinal - 1 < loads.Count ? loads[ordinal - 1].Name : $"ordinal {ordinal}",
        };
    }

    /// <summary>
    /// Relocation type names, per CPU. The ones this tool has not seen are printed in the format's
    /// own shape (<c>X86_64_RELOC_11</c>) rather than being given a name out of a guess.
    /// </summary>
    public static string RelocationTypeName(uint cpuType, uint type) => cpuType switch
    {
        MachoCpu.X86_64 => type switch
        {
            0 => "X86_64_RELOC_UNSIGNED",
            1 => "X86_64_RELOC_SIGNED",
            2 => "X86_64_RELOC_BRANCH",
            3 => "X86_64_RELOC_GOT_LOAD",
            4 => "X86_64_RELOC_GOT",
            5 => "X86_64_RELOC_SUBTRACTOR",
            6 => "X86_64_RELOC_SIGNED_1",
            7 => "X86_64_RELOC_SIGNED_2",
            8 => "X86_64_RELOC_SIGNED_4",
            9 => "X86_64_RELOC_TLV",
            _ => $"X86_64_RELOC_{type}",
        },
        MachoCpu.Arm64 => type switch
        {
            0 => "ARM64_RELOC_UNSIGNED",
            1 => "ARM64_RELOC_SUBTRACTOR",
            2 => "ARM64_RELOC_BRANCH26",
            3 => "ARM64_RELOC_PAGE21",
            4 => "ARM64_RELOC_PAGEOFF12",
            5 => "ARM64_RELOC_GOT_LOAD_PAGE21",
            6 => "ARM64_RELOC_GOT_LOAD_PAGEOFF12",
            7 => "ARM64_RELOC_POINTER_TO_GOT",
            8 => "ARM64_RELOC_TLV_LOAD_PAGE21",
            9 => "ARM64_RELOC_TLV_LOAD_PAGEOFF12",
            10 => "ARM64_RELOC_ADDEND",
            _ => $"ARM64_RELOC_{type}",
        },
        MachoCpu.X86 => type switch
        {
            0 => "GENERIC_RELOC_VANILLA",
            1 => "GENERIC_RELOC_PAIR",
            2 => "GENERIC_RELOC_SECTDIFF",
            3 => "GENERIC_RELOC_PB_LA_PTR",
            4 => "GENERIC_RELOC_LOCAL_SECTDIFF",
            5 => "GENERIC_RELOC_TLV",
            _ => $"GENERIC_RELOC_{type}",
        },
        MachoCpu.Arm => type switch
        {
            0 => "ARM_RELOC_VANILLA",
            1 => "ARM_RELOC_PAIR",
            2 => "ARM_RELOC_SECTDIFF",
            3 => "ARM_RELOC_LOCAL_SECTDIFF",
            4 => "ARM_RELOC_PB_LA_PTR",
            5 => "ARM_RELOC_BR24",
            6 => "ARM_RELOC_HALF",
            7 => "ARM_RELOC_HALF_SECTDIFF",
            _ => $"ARM_RELOC_{type}",
        },
        _ => $"relocation type {type}",
    };

    private static string PlatformName(uint platform) => platform switch
    {
        1 => "macos",
        2 => "ios",
        3 => "tvos",
        4 => "watchos",
        5 => "bridgeos",
        6 => "maccatalyst",
        7 => "ios-simulator",
        8 => "tvos-simulator",
        9 => "watchos-simulator",
        10 => "driverkit",
        _ => $"platform {platform}",
    };

    /// <summary>A packed version: 16.8.8, which is how both the OS version and the SDK are stored.</summary>
    private static string VersionString(uint packed)
        => $"{(packed >> 16) & 0xFFFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";
}
