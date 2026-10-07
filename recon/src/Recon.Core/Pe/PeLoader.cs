using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Recon.Pe;

/// <summary>
/// Parses PE images. PE32 is the milestone-1 target; PE32+ headers are parsed too so a 64-bit input
/// produces an honest result instead of a parse failure, but the analyser refuses it for now.
/// Everything that cannot be read is reported in <see cref="PeLoadResult.Problems"/> rather than
/// thrown: a damaged binary is data, not a crash.
/// </summary>
public static class PeLoader
{
    public static PeLoadResult Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new PeLoadResult { Problems = [$"cannot read {path}: {ex.Message}"] };
        }

        return LoadBytes(bytes, path);
    }

    public static PeLoadResult LoadBytes(byte[] bytes, string path = "<memory>")
    {
        var result = new PeLoadResult { Bytes = bytes };
        var problems = result.Problems;
        var reader = new ByteReader(bytes);

        if (bytes.Length < 0x40)
        {
            problems.Add($"file is too small to be a PE image ({bytes.Length} bytes)");
            return result;
        }

        if (reader.U16(0) != 0x5A4D)
        {
            problems.Add("missing MZ signature: not a PE image");
            return result;
        }

        int peOffset = (int)(reader.U32(0x3C) ?? 0);
        if (!reader.Contains(peOffset, 4) || reader.U32(peOffset) != 0x00004550)
        {
            problems.Add($"missing PE signature at 0x{peOffset:X}");
            return result;
        }

        var image = new PeImage
        {
            Path = path,
            Sha256 = PeImage.HashBytes(bytes),
        };

        int coff = peOffset + 4;
        image.Machine = reader.U16(coff) ?? 0;
        int sectionCount = reader.U16(coff + 2) ?? 0;
        image.TimeDateStamp = reader.U32(coff + 4) ?? 0;
        image.SymbolTableOffset = reader.U32(coff + 8) ?? 0;
        image.SymbolCount = reader.U32(coff + 12) ?? 0;
        int optionalSize = reader.U16(coff + 16) ?? 0;
        image.Characteristics = reader.U16(coff + 18) ?? 0;

        int optional = coff + 20;
        if (!reader.Contains(optional, 2))
        {
            problems.Add("optional header is outside the file");
            return result;
        }

        ushort magic = reader.U16(optional) ?? 0;
        if (magic == 0x107)
        {
            problems.Add("ROM images are not supported");
            return result;
        }

        if (magic is not (0x10B or 0x20B))
        {
            problems.Add($"unknown optional header magic 0x{magic:X4}");
            return result;
        }

        image.Kind = magic == 0x10B ? PeKind.Pe32 : PeKind.Pe32Plus;
        bool plus = image.Kind is PeKind.Pe32Plus;

        image.MajorLinkerVersion = reader.U8(optional + 2) ?? 0;
        image.MinorLinkerVersion = reader.U8(optional + 3) ?? 0;
        image.EntryPointRva = reader.U32(optional + 16) ?? 0;
        image.ImageBase = plus ? reader.U64(optional + 24) ?? 0 : reader.U32(optional + 28) ?? 0;
        image.MajorOsVersion = reader.U8(optional + 40) ?? 0;
        image.MinorOsVersion = reader.U8(optional + 42) ?? 0;
        image.MajorSubsystemVersion = reader.U8(optional + 48) ?? 0;
        image.MinorSubsystemVersion = reader.U8(optional + 50) ?? 0;
        image.SizeOfImage = reader.U32(optional + 56) ?? 0;
        image.SizeOfHeaders = reader.U32(optional + 60) ?? 0;
        image.Checksum = reader.U32(optional + 64) ?? 0;
        image.Subsystem = reader.U16(optional + 68) ?? 0;
        image.DllCharacteristics = reader.U16(optional + 70) ?? 0;

        // The data directories are the one part of the optional header whose offset depends on the
        // magic: the 64-bit form has eight-byte stack and heap fields before them. Reading the 32-bit
        // offset on a 64-bit image lands in the middle of SizeOfHeapReserve — whose high half is zero
        // in every image that fits in memory — so every directory read as absent: no imports, no
        // exports, no exception directory, no debug directory, on every 64-bit image this tool had
        // ever loaded.
        uint directoryCount = reader.U32(optional + (plus ? 108 : 92)) ?? 0;
        int directoryOffset = optional + (plus ? 112 : 96);
        for (int i = 0; i < PeDataDirectories.Names.Length; i++)
        {
            var (rva, size) = i < directoryCount
                ? (reader.U32(directoryOffset + (i * 8)) ?? 0, reader.U32(directoryOffset + (i * 8) + 4) ?? 0)
                : (0u, 0u);
            image.DataDirectories.Add(new PeDataDirectory
            {
                Index = i,
                Name = PeDataDirectories.Names[i],
                Rva = rva,
                Size = size,
            });
        }

        // Sections. GNU ld stores names longer than 8 bytes as "/<offset>" into the string table
        // that follows the COFF symbol table, so the name has to be resolved there.
        int sectionTable = optional + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int offset = sectionTable + (i * 40);
            if (!reader.Contains(offset, 40))
            {
                problems.Add($"section header {i} is outside the file");
                break;
            }

            uint characteristics = reader.U32(offset + 36) ?? 0;
            string rawName = reader.AsciiZ(offset, 8) ?? string.Empty;
            string name = ResolveSectionName(image, reader, rawName) ?? rawName;
            var section = new PeSection
            {
                Name = name,
                VirtualSize = reader.U32(offset + 8) ?? 0,
                Rva = reader.U32(offset + 12) ?? 0,
                RawSize = reader.U32(offset + 16) ?? 0,
                RawOffset = reader.U32(offset + 20) ?? 0,
                Characteristics = characteristics,
                Flags = SectionFlagNames.FromCharacteristics(characteristics),
            };

            if (section.RawOffset != 0 && section.RawSize != 0
                && (ulong)section.RawOffset + section.RawSize > (ulong)bytes.Length)
            {
                problems.Add($"section {section.Name} claims data past the end of the file; it reads as zero");
            }

            image.Sections.Add(section);
        }

        if (image.Sections.Count == 0)
        {
            problems.Add("the image has no sections: RVAs cannot be mapped to file offsets");
        }

        image.Rich = RichHeader.TryParse(reader);
        image.Vb6 = Vb6Header.TryRead(image, bytes);
        ReadCommentStrings(image, reader);

        ReadImports(image, reader);
        ReadExports(image, reader);
        ReadExceptionDirectory(image, reader);
        ReadRelocations(image, reader, problems);
        ReadTlsCallbacks(image, reader);
        ReadDebugDirectory(image, reader);
        ReadResourceTypes(image, reader);

        result.Image = image;
        return result;
    }

    // ---------------------------------------------------------------- address mapping

    /// <summary>Maps an RVA to a file offset, or null when the RVA is not backed by file data.</summary>
    public static int? RvaToOffset(PeImage image, uint rva)
    {
        if (rva < image.SizeOfHeaders)
        {
            // The headers are stored at the start of the file, unmapped.
            return (int)rva;
        }

        return image.SectionContainingRva(rva)?.RvaToOffset(rva);
    }

    /// <summary>Converts a virtual address in the image to an RVA, when it lies inside the image.</summary>
    public static uint? AddressToRva(PeImage image, ulong address)
    {
        if (address < image.ImageBase || address - image.ImageBase > uint.MaxValue)
        {
            return null;
        }

        uint rva = (uint)(address - image.ImageBase);
        return image.ContainsRva(rva) ? rva : null;
    }

    public static uint? ReadU16AtRva(PeImage image, byte[] bytes, uint rva)
    {
        int? offset = RvaToOffset(image, rva);
        if (offset is null || offset + 2 > bytes.Length)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset.Value, 2));
    }

    public static uint? ReadU32AtRva(PeImage image, byte[] bytes, uint rva)
    {
        int? offset = RvaToOffset(image, rva);
        if (offset is null || offset + 4 > bytes.Length)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset.Value, 4));
    }

    public static ulong? ReadU64AtRva(PeImage image, byte[] bytes, uint rva)
    {
        int? offset = RvaToOffset(image, rva);
        if (offset is null || offset + 8 > bytes.Length)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset.Value, 8));
    }

    public static string? ReadAsciiZAtRva(PeImage image, byte[] bytes, uint rva, int maxLength = 512)
    {
        int? offset = RvaToOffset(image, rva);
        if (offset is null)
        {
            return null;
        }

        int end = offset.Value;
        int limit = Math.Min(bytes.Length, offset.Value + maxLength);
        while (end < limit && bytes[end] != 0)
        {
            end++;
        }

        return end == offset.Value ? string.Empty : Encoding.Latin1.GetString(bytes, offset.Value, end - offset.Value);
    }

    /// <summary>True when the RVA lies inside the image's mapped range, even if not backed by a file.</summary>
    public static bool IsMapped(PeImage image, uint rva) => rva < image.SizeOfImage && image.SectionContainingRva(rva) is not null;

    private static string? ResolveSectionName(PeImage image, ByteReader reader, string rawName)
    {
        if (!rawName.StartsWith('/') || image.SymbolTableOffset == 0)
        {
            return rawName;
        }

        string digits = rawName[1..].TrimEnd('\0');
        if (!int.TryParse(digits, out int stringOffset))
        {
            return rawName;
        }

        long stringTable = image.SymbolTableOffset + (image.SymbolCount * 18L);
        long position = stringTable + stringOffset;
        if (position < 0 || position >= reader.Length)
        {
            return rawName;
        }

        return reader.AsciiZ((int)position) ?? rawName;
    }

    // ---------------------------------------------------------------- directories

    private static void ReadCommentStrings(PeImage image, ByteReader reader)
    {
        foreach (var section in image.Sections)
        {
            // The linker records tool versions in .comment; a few toolchains use .drectve instead.
            if (!section.Name.Equals(".comment", StringComparison.OrdinalIgnoreCase)
                && !section.Name.StartsWith(".drectve", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = reader.Span((int)section.RawOffset, (int)section.RawSize);
            var current = new StringBuilder();
            foreach (byte b in data)
            {
                if (b is >= 0x20 and < 0x7F)
                {
                    current.Append((char)b);
                }
                else
                {
                    if (current.Length >= 4)
                    {
                        image.CommentStrings.Add(current.ToString());
                    }

                    current.Clear();
                }
            }

            if (current.Length >= 4)
            {
                image.CommentStrings.Add(current.ToString());
            }
        }
    }

    private static void ReadImports(PeImage image, ByteReader reader)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "import");
        if (directory is null || !directory.Present)
        {
            return;
        }

        var bytes = reader.Data;
        for (int i = 0; i < 4096; i++)
        {
            uint entryRva = directory.Rva + (uint)(i * 20);
            var (ilt, nameRva, iat) = (ReadU32AtRva(image, bytes, entryRva), ReadU32AtRva(image, bytes, entryRva + 12), ReadU32AtRva(image, bytes, entryRva + 16));
            if (ilt is null || nameRva is null || iat is null)
            {
                break;
            }

            if (ilt == 0 && nameRva == 0 && iat == 0)
            {
                break; // terminator
            }

            string? dll = ReadAsciiZAtRva(image, bytes, nameRva.Value);
            if (dll is null || dll.Length == 0 || !IsPlausibleModuleName(dll))
            {
                // Reading past a missing terminator would invent modules out of whatever bytes
                // follow; stopping here keeps the import list honest.
                break;
            }

            uint thunkTable = ilt.Value != 0 ? ilt.Value : iat.Value;

            for (uint j = 0; j < 65535; j++)
            {
                uint thunkRva = thunkTable + (j * 4);
                uint? thunk = ReadU32AtRva(image, bytes, thunkRva);
                if (thunk is null || thunk == 0)
                {
                    break;
                }

                uint iatRva = iat.Value + (j * 4);
                if ((thunk.Value & 0x8000_0000) != 0)
                {
                    image.Imports.Add(new PeImport
                    {
                        Dll = dll,
                        Name = null,
                        Ordinal = (ushort)(thunk.Value & 0xFFFF),
                        IatRva = iatRva,
                        LookupRva = thunkRva,
                    });
                    continue;
                }

                image.Imports.Add(new PeImport
                {
                    Dll = dll,
                    Name = ReadAsciiZAtRva(image, bytes, thunk.Value + 2),
                    Ordinal = null,
                    IatRva = iatRva,
                    LookupRva = thunkRva,
                });
            }
        }
    }

    /// <summary>Module names are printable and carry an extension: <c>KERNEL32.dll</c>.</summary>
    private static bool IsPlausibleModuleName(string name)
    {
        if (name.Length is < 4 or > 260 || !name.Contains('.'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c < 0x20 || c > 0x7E || c is '"' or '<' or '>' or '|' or '?' or '*')
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadExports(PeImage image, ByteReader reader)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "export");
        if (directory is null || !directory.Present)
        {
            return;
        }

        var bytes = reader.Data;
        uint? ordinalBase = ReadU32AtRva(image, bytes, directory.Rva + 16);
        uint? functionCount = ReadU32AtRva(image, bytes, directory.Rva + 20);
        uint? nameCount = ReadU32AtRva(image, bytes, directory.Rva + 24);
        uint? addressOfFunctions = ReadU32AtRva(image, bytes, directory.Rva + 28);
        uint? addressOfNames = ReadU32AtRva(image, bytes, directory.Rva + 32);
        uint? addressOfOrdinals = ReadU32AtRva(image, bytes, directory.Rva + 36);
        if (ordinalBase is null || functionCount is null || addressOfFunctions is null)
        {
            return;
        }

        var names = new Dictionary<uint, string>();
        if (nameCount is > 0 && addressOfNames is not null && addressOfOrdinals is not null)
        {
            for (uint i = 0; i < nameCount; i++)
            {
                uint? nameRva = ReadU32AtRva(image, bytes, addressOfNames.Value + (i * 4));
                uint? index = ReadU32AtRva(image, bytes, addressOfOrdinals.Value + (i * 2));
                if (nameRva is null || index is null)
                {
                    continue;
                }

                // The ordinal table holds 16-bit indices; reading 4 bytes is harmless because only
                // the low half is used.
                string? name = ReadAsciiZAtRva(image, bytes, nameRva.Value);
                if (name is { Length: > 0 })
                {
                    names[index.Value & 0xFFFF] = name;
                }
            }
        }

        for (uint i = 0; i < Math.Min(functionCount.Value, 65536); i++)
        {
            uint? rva = ReadU32AtRva(image, bytes, addressOfFunctions.Value + (i * 4));
            if (rva is null || rva == 0)
            {
                continue; // a hole in the export address table
            }

            uint ordinal = ordinalBase.Value + i;
            names.TryGetValue(i, out string? name);

            // A forwarder's "RVA" points inside the export directory and holds "dll.function".
            string? forwarder = rva.Value >= directory.Rva && rva.Value < directory.Rva + directory.Size + 1
                ? ReadAsciiZAtRva(image, bytes, rva.Value)
                : null;

            image.Exports.Add(new PeExport
            {
                Name = name,
                Ordinal = ordinal,
                Rva = forwarder is null ? rva.Value : 0,
                Forwarder = forwarder,
            });
        }
    }

    /// <summary>
    /// The exception directory: 8 bytes per entry on PE32, 12 on PE32+, each naming a function's
    /// range. Both widths are real and both are read here: the 32-bit form states the range and
    /// nothing else, the 64-bit form adds the address of the function's unwind data in `.xdata`.
    /// </summary>
    private static void ReadExceptionDirectory(PeImage image, ByteReader reader)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "exception");
        if (directory is null || !directory.Present)
        {
            return;
        }

        bool plus = image.Kind is PeKind.Pe32Plus;
        int width = plus ? 12 : 8;
        uint count = directory.Size / (uint)width;
        for (uint i = 0; i < count; i++)
        {
            uint entryRva = directory.Rva + (i * (uint)width);
            uint? begin = ReadU32AtRva(image, reader.Data, entryRva);
            uint? end = ReadU32AtRva(image, reader.Data, entryRva + 4);
            if (begin is null || end is null)
            {
                break;   // the table claims more entries than the file holds
            }

            if (begin == 0)
            {
                break;   // a zero begin is not a function: the table ends here
            }

            image.RuntimeFunctions.Add(new PeRuntimeFunction
            {
                BeginRva = begin.Value,
                EndRva = end.Value > begin.Value ? end.Value : null,
                UnwindInfoRva = plus ? ReadU32AtRva(image, reader.Data, entryRva + 8) : null,
            });
        }
    }

    private static void ReadRelocations(PeImage image, ByteReader reader, List<string> problems)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "basereloc");
        if (directory is null || !directory.Present)
        {
            return;
        }

        var bytes = reader.Data;
        uint end = directory.Rva + directory.Size;
        for (uint block = directory.Rva; block + 8 <= end;)
        {
            uint? pageRva = ReadU32AtRva(image, bytes, block);
            uint? blockSize = ReadU32AtRva(image, bytes, block + 4);
            if (pageRva is null || blockSize is null)
            {
                problems.Add($"relocation block at 0x{block:X8} is outside the file");
                return;
            }

            if (pageRva.Value == 0 && blockSize.Value == 0)
            {
                return; // terminator
            }

            if (blockSize.Value < 8 || block + blockSize.Value > end)
            {
                problems.Add($"relocation block at 0x{block:X8} has an invalid size {blockSize.Value}");
                return;
            }

            uint entries = (blockSize.Value - 8) / 2;
            for (uint i = 0; i < entries; i++)
            {
                uint? raw = ReadU16AtRva(image, bytes, block + 8 + (i * 2));
                if (raw is null)
                {
                    problems.Add($"relocation entry {i} of block 0x{block:X8} is outside the file");
                    return;
                }

                ushort entry = (ushort)raw.Value;
                uint type = (uint)(entry >> 12);
                uint offset = (uint)(entry & 0x0FFF);
                if (type == 0)
                {
                    continue; // padding
                }

                uint rva = pageRva.Value + offset;
                uint? rawValue = ReadU32AtRva(image, bytes, rva);
                uint? target = type switch
                {
                    3 => rawValue is not null && rawValue.Value >= image.ImageBase ? rawValue.Value - (uint)image.ImageBase : null,
                    1 => (rawValue ?? 0) << 16,
                    2 => (rawValue ?? 0) & 0xFFFF,
                    _ => null, // HIGHADJ and DIR64 need the neighbouring entry / a 64-bit read
                };

                image.Relocations.Add(new PeRelocation
                {
                    Rva = rva,
                    Type = (ushort)type,
                    Kind = BaseRelocationTypes.Name(type),
                    TargetRva = target,
                    RawValue = rawValue ?? 0,
                    Width = (byte)BaseRelocationTypes.Width(type),
                });
            }

            block += blockSize.Value;
        }
    }

    private static void ReadTlsCallbacks(PeImage image, ByteReader reader)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "tls");
        if (directory is null || !directory.Present)
        {
            return;
        }

        bool plus = image.Kind is PeKind.Pe32Plus;
        uint callbacksVa = plus
            ? (uint)Math.Min(ReadU64AtRva(image, reader.Data, directory.Rva + 24) ?? 0, uint.MaxValue)
            : ReadU32AtRva(image, reader.Data, directory.Rva + 12) ?? 0;
        if (callbacksVa == 0)
        {
            return;
        }

        uint? callbacksRva = AddressToRva(image, callbacksVa);
        if (callbacksRva is null)
        {
            return;
        }

        for (int i = 0; i < 64; i++)
        {
            uint? callback = ReadU32AtRva(image, reader.Data, callbacksRva.Value + (uint)(i * 4));
            if (callback is null || callback == 0)
            {
                break;
            }

            uint? rva = callback >= image.ImageBase ? AddressToRva(image, callback.Value) : callback;
            if (rva is null)
            {
                break; // a callback outside the image cannot be analysed here
            }

            image.TlsCallbacks.Add(new PeTlsCallback { Rva = rva.Value, Index = i });
        }
    }

    private static void ReadDebugDirectory(PeImage image, ByteReader reader)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "debug");
        if (directory is null || !directory.Present || directory.Size < 28)
        {
            return;
        }

        var data = reader.Data;
        int filesize = data.Length;
        uint count = Math.Min(directory.Size / 28, 64);
        for (uint i = 0; i < count; i++)
        {
            uint entryRva = directory.Rva + (i * 28);
            uint? type = ReadU32AtRva(image, data, entryRva + 12);
            uint? sizeOfData = ReadU32AtRva(image, data, entryRva + 16);
            uint? addressOfRawData = ReadU32AtRva(image, data, entryRva + 20);
            uint? pointerToRawData = ReadU32AtRva(image, data, entryRva + 24);
            if (type is null)
            {
                break;
            }

            var entry = new PeDebugEntry
            {
                Type = type.Value,
                TypeName = DebugTypes.Name(type.Value),
                TimeDateStamp = ReadU32AtRva(image, data, entryRva + 4) ?? 0,
                SizeOfData = sizeOfData ?? 0,
                SymbolTableOffset = addressOfRawData ?? 0,
                SymbolCount = 0,
                RawOffset = pointerToRawData ?? 0,
            };

            if (type.Value == 2 && sizeOfData is > 0 && pointerToRawData is > 0 && pointerToRawData + sizeOfData <= filesize)
            {
                ReadCodeView(entry, data, (int)pointerToRawData.Value, sizeOfData.Value);
            }

            image.DebugEntries.Add(entry);
        }
    }

    private static void ReadCodeView(PeDebugEntry entry, byte[] data, int offset, uint size)
    {
        string signature = Encoding.ASCII.GetString(data, offset, 4);

        if (signature == "RSDS" && size >= 24)
        {
            entry.PdbGuid = new Guid(data.AsSpan(offset + 4, 16));
            entry.PdbAge = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 20, 4));
            entry.PdbPath = ReadPath(data, offset + 24, (int)size - 24);
            return;
        }

        if (signature == "NB10" && size >= 16)
        {
            // NB10: signature, offset, timestamp, age, then the path. There is no GUID.
            entry.PdbAge = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12, 4));
            entry.PdbPath = ReadPath(data, offset + 16, (int)size - 16);
        }
    }

    private static string ReadPath(byte[] data, int offset, int length)
    {
        int end = offset;
        int limit = Math.Min(data.Length, offset + Math.Max(0, length));
        while (end < limit && data[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    private static void ReadResourceTypes(PeImage image, ByteReader reader)
    {
        var directory = image.DataDirectories.FirstOrDefault(d => d.Name == "resource");
        if (directory is null || !directory.Present)
        {
            return;
        }

        var data = reader.Data;
        uint? named = ReadU32AtRva(image, data, directory.Rva + 12);
        uint? ids = ReadU32AtRva(image, data, directory.Rva + 14);
        if (named is null || ids is null)
        {
            return;
        }

        // The high 16 bits of the 4-byte reads above belong to the next field, hence the masking.
        int total = (int)((named.Value & 0xFFFF) + (ids.Value & 0xFFFF));
        for (int i = 0; i < total && i < 32; i++)
        {
            uint entryRva = directory.Rva + 16 + (uint)(i * 8);
            uint? nameOrId = ReadU32AtRva(image, data, entryRva);
            uint? offset = ReadU32AtRva(image, data, entryRva + 4);
            if (nameOrId is null || offset is null)
            {
                break;
            }

            string name;
            if ((nameOrId.Value & 0x8000_0000) != 0)
            {
                uint nameRva = directory.Rva + (nameOrId.Value & 0x7FFF_FFFF);
                int? nameOffset = RvaToOffset(image, nameRva + 2);
                name = nameOffset is null ? "named" : ReadUtf16Z(data, nameOffset.Value);
                if (name.Length == 0)
                {
                    name = "named";
                }
            }
            else
            {
                name = ResourceTypeNames.Name(nameOrId.Value & 0xFFFF);
            }

            int entries = 0;
            if ((offset.Value & 0x8000_0000) != 0)
            {
                uint subdirectoryRva = directory.Rva + (offset.Value & 0x7FFF_FFFF);
                entries = (int)((ReadU32AtRva(image, data, subdirectoryRva + 12) ?? 0) & 0xFFFF)
                          + (int)((ReadU32AtRva(image, data, subdirectoryRva + 14) ?? 0) & 0xFFFF);
            }

            image.ResourceTypes.Add(new PeResourceType
            {
                Name = name,
                Id = (ushort)(nameOrId.Value & 0xFFFF),
                Entries = entries,
            });
        }
    }

    private static string ReadUtf16Z(byte[] data, int offset)
    {
        int end = offset;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(data, offset, Math.Max(0, end - offset));
    }
}

public static class PeDataDirectories
{
    public static readonly string[] Names =
    [
        "export",
        "import",
        "resource",
        "exception",
        "certificate",
        "basereloc",
        "debug",
        "architecture",
        "globalptr",
        "tls",
        "load_config",
        "bound_import",
        "iat",
        "delay_import",
        "com_descriptor",
        "reserved",
    ];
}

public static class SectionFlagNames
{
    public static SectionFlags FromCharacteristics(uint characteristics)
    {
        SectionFlags flags = SectionFlags.None;
        if ((characteristics & 0x00000020) != 0)
        {
            flags |= SectionFlags.Code;
        }

        if ((characteristics & 0x00000040) != 0)
        {
            flags |= SectionFlags.InitializedData;
        }

        if ((characteristics & 0x00000080) != 0)
        {
            flags |= SectionFlags.UninitializedData;
        }

        if ((characteristics & 0x02000000) != 0)
        {
            flags |= SectionFlags.Discardable;
        }

        if ((characteristics & 0x10000000) != 0)
        {
            flags |= SectionFlags.Shared;
        }

        if ((characteristics & 0x20000000) != 0)
        {
            flags |= SectionFlags.Executable;
        }

        if ((characteristics & 0x40000000) != 0)
        {
            flags |= SectionFlags.Readable;
        }

        if ((characteristics & 0x80000000) != 0)
        {
            flags |= SectionFlags.Writable;
        }

        return flags;
    }
}

public static class BaseRelocationTypes
{
    public static string Name(uint type) => type switch
    {
        0 => "ABSOLUTE",
        1 => "HIGH",
        2 => "LOW",
        3 => "HIGHLOW",
        4 => "HIGHADJ",
        5 => "MIPS_JMPADDR",
        10 => "DIR64",
        _ => $"type_{type}",
    };

    public static int Width(uint type) => type switch
    {
        3 or 4 => 4,
        10 => 8,
        1 or 2 => 2,
        _ => 4,
    };
}

public static class DebugTypes
{
    public static string Name(uint type) => type switch
    {
        0 => "unknown",
        1 => "coff",
        2 => "codeview",
        3 => "fpo",
        4 => "misc",
        5 => "exception",
        6 => "fixup",
        9 => "borland",
        10 => "reserved10",
        11 => "clsid",
        12 => "vc_feature",
        13 => "pogo",
        14 => "iltcg",
        16 => "repro",
        17 => "embedded_portable_pdb",
        19 => "pdb_checksum",
        _ => $"type_{type}",
    };
}

public static class ResourceTypeNames
{
    private static readonly Dictionary<uint, string> Known = new()
    {
        [1] = "cursor",
        [2] = "bitmap",
        [3] = "icon",
        [4] = "menu",
        [5] = "dialog",
        [6] = "string",
        [7] = "fontdir",
        [8] = "font",
        [9] = "accelerator",
        [10] = "rcdata",
        [11] = "message_table",
        [12] = "group_cursor",
        [14] = "group_icon",
        [16] = "version",
        [24] = "manifest",
    };

    public static string Name(uint id) => Known.TryGetValue(id, out string? name) ? name : $"type_{id}";
}
