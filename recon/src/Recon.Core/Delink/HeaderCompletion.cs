using System.Text;
using Recon.Pe;

namespace Recon.Delink;

/// <summary>
/// The header of a relinked image, finished by hand.
///
/// A linker that builds an import address table knows where it is; one that was handed the table's
/// bytes does not. The same goes for the TLS directory, and no option tells a linker where either
/// one is. Those entries are copied from the original, which is sound because the copy is only made
/// when the relinked image's entry is empty and the address it points at is one this plan wrote: the
/// bytes are there, only the pointer to them is missing.
///
/// The timestamp comes across too. It is what ties an image to the symbols that describe it, and a
/// relinked image that cannot be matched to its own debug info is a worse reconstruction than one
/// that can.
/// </summary>
public static class HeaderCompletion
{
    /// <summary>Directories whose "address" is a file offset, not an RVA, and cannot be copied.</summary>
    private static readonly int[] FileOffsetDirectories = [4, 11];

    /// <param name="positionalNames">
    /// Sections that were emitted under a positional name because the linker places sections by name,
    /// with the original's name for each. Restored here, and only for a section that is exactly where
    /// the plan put it: a name is a claim about what a section is, and it is only made about a section
    /// that is where it was.
    /// </param>
    /// <param name="originalBytes">
    /// The original file, for the one part of the header no option reaches: its MS-DOS stub. The
    /// linker writes a stub of its own, so the NT headers of a relinked image sit at a different file
    /// offset than the original's do and a byte comparison of the two reads a header that is out of
    /// place rather than different. The original's stub is put back and the NT structures are moved
    /// the same distance, which is a rearrangement inside the header block: no address, no raw offset
    /// and no section changes.
    /// </param>
    public static List<string> Complete(
        string relinkedPath,
        PeImage original,
        IReadOnlyList<(uint Rva, string Positional, string Original)>? positionalNames = null,
        byte[]? originalBytes = null)
    {
        var changes = new List<string>();
        byte[] bytes = File.ReadAllBytes(relinkedPath);
        if (originalBytes is not null)
        {
            changes.AddRange(RelayoutStub(bytes, originalBytes));
        }

        var layout = Layout(bytes);
        if (layout is null)
        {
            return changes;
        }

        var (coffStart, optionalStart, optionalSize, directoryStart, directoryCount) = layout.Value;

        foreach (var directory in original.DataDirectories)
        {
            if (directory.Index >= directoryCount || FileOffsetDirectories.Contains(directory.Index))
            {
                continue;
            }

            uint at = (uint)(directoryStart + (directory.Index * 8));
            uint rva = ReadUInt32(bytes, at);
            uint size = ReadUInt32(bytes, at + 4);
            if (rva != 0 || size != 0 || !directory.Present)
            {
                // The linker wrote something of its own, or the original had nothing to copy.
                continue;
            }

            if (original.SectionContainingRva(directory.Rva) is null)
            {
                continue;
            }

            WriteUInt32(bytes, at, directory.Rva);
            WriteUInt32(bytes, at + 4, directory.Size);
            changes.Add($"{directory.Name} directory -> 0x{directory.Rva:x}+0x{directory.Size:x}");
        }

        // A section's flags are the linker's conclusion from what it was handed, and it was handed a
        // transcription of one section, not the mix of code and data the original's linker saw. The
        // original's flags are copied, because the section is still the same section.
        int sectionCount = ReadUInt16(bytes, coffStart + 2);
        uint sectionTable = optionalStart + optionalSize;
        if (sectionTable + ((uint)sectionCount * 40) <= (uint)bytes.Length)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                uint entry = sectionTable + ((uint)i * 40);
                var section = original.Sections.FirstOrDefault(x => x.Rva == ReadUInt32(bytes, entry + 12));
                if (section is null)
                {
                    continue;
                }

                uint characteristics = ReadUInt32(bytes, entry + 36);
                if (characteristics != section.Characteristics)
                {
                    WriteUInt32(bytes, entry + 36, section.Characteristics);
                    changes.Add($"section {section.Name} flags 0x{section.Characteristics:x8}");
                }

                // The assembler rounds a section up to its four-byte alignment, which is three bytes
                // of padding the original did not have. Only that rounding is undone: a section that
                // came out bigger for any other reason keeps its own size, because a bigger section
                // is a section with something in it.
                uint virtualSize = ReadUInt32(bytes, entry + 8);
                if (virtualSize > section.VirtualSize
                    && virtualSize - section.VirtualSize < 4
                    && ReadUInt32(bytes, entry + 16) >= section.VirtualSize)
                {
                    WriteUInt32(bytes, entry + 8, section.VirtualSize);
                    changes.Add($"section {section.Name} size 0x{section.VirtualSize:x} (the assembler padded it to its alignment)");
                }
            }
        }

        // The names themselves, for a link where they were told to the linker by position.
        if (positionalNames is { Count: > 0 } && sectionTable + ((uint)sectionCount * 40) <= (uint)bytes.Length)
        {
            var restored = new List<string>();
            for (int i = 0; i < sectionCount; i++)
            {
                uint entry = sectionTable + ((uint)i * 40);
                uint rva = ReadUInt32(bytes, entry + 12);
                string? current = ReadSectionName(bytes, entry);
                var wanted = positionalNames.FirstOrDefault(p => p.Rva == rva && p.Positional == current);
                if (wanted.Positional is null || wanted.Original.Length > 8)
                {
                    continue;
                }

                // The original name as the section table itself spells it, which is inline for eight
                // bytes or fewer - the only case this writes, because a longer name would be an offset
                // into a string table the linker wrote for its own names.
                Span<byte> field = bytes.AsSpan((int)entry, 8);
                field.Clear();
                Encoding.ASCII.GetBytes(wanted.Original, field);
                restored.Add($"{wanted.Positional} -> {wanted.Original}");
            }

            if (restored.Count > 0)
            {
                changes.Add($"section names from the plan ({string.Join(", ", restored)})");
            }
        }

        // Whether the image can be based anywhere is a claim about its relocation table, so it is
        // copied only when the relinked image has the table the original's claim was about.
        if (directoryCount > 5 && ReadUInt32(bytes, (uint)(directoryStart + (5 * 8))) != 0)
        {
            ushort characteristics = ReadUInt16(bytes, optionalStart + 70);
            if (characteristics != original.DllCharacteristics)
            {
                WriteUInt16(bytes, optionalStart + 70, original.DllCharacteristics);
                changes.Add($"dll characteristics 0x{original.DllCharacteristics:x4}");
            }
        }

        // The linker's own account of the image: how much code it counted and how large it made each
        // part of the data. Each of those numbers is a sum over the sections, and the sections are the
        // original's sections, so the original's numbers are the true ones - lld-link leaves several of
        // them at zero. The file's own flags come across too, and one of them has to: /fixed makes the
        // linker say the relocations were stripped, and this image carries the original's relocation
        // table, so the flag is simply not true of the file it sits in.
        if (originalBytes is not null && Layout(originalBytes) is { } originalLayout)
        {
            var (originalCoff, originalOptional, _, originalDirectoryStart, originalDirectoryCount) = originalLayout;
            var copied = new List<string>();
            var fields = new (int Offset, int Length, string Name)[]
            {
                (2, 2, "linker version"),
                (4, 4, "size of code"),
                (8, 4, "size of initialized data"),
                (12, 4, "size of uninitialized data"),
                (20, 4, "base of code"),
                (40, 4, "os version"),
                (44, 4, "image version"),
                (48, 4, "subsystem version"),
            };

            foreach (var (offset, length, name) in fields)
            {
                uint at = (uint)(optionalStart + offset);
                uint from = (uint)(originalOptional + offset);
                if (optionalSize < offset + length || from + length > (uint)originalBytes.Length)
                {
                    continue;
                }

                if (!bytes.AsSpan((int)at, length).SequenceEqual(originalBytes.AsSpan((int)from, length)))
                {
                    originalBytes.AsSpan((int)from, length).CopyTo(bytes.AsSpan((int)at, length));
                    copied.Add(name);
                }
            }

            if (bytes.AsSpan((int)coffStart + 18, 2).SequenceEqual(originalBytes.AsSpan((int)originalCoff + 18, 2)) == false)
            {
                originalBytes.AsSpan((int)originalCoff + 18, 2).CopyTo(bytes.AsSpan((int)coffStart + 18, 2));
                copied.Add("file characteristics");
            }

            if (copied.Count > 0)
            {
                changes.Add($"header fields from the original ({string.Join(", ", copied)})");
            }

            // The bytes after a section's content and before the end of its raw data. They are not
            // part of the section - delink's own account of a section stops at its content - and the
            // linker fills them the way its own linker filled its own: the original's are zeros and
            // llvm's are 0xcc, so a byte comparison of the two files sees padding that says nothing
            // about the image. Where the original's run is zeros, the relinked file's is set to zeros
            // too; where the original's is not zeros, nothing is touched.
            if (sectionTable + ((uint)sectionCount * 40) <= (uint)bytes.Length)
            {
                long zeroed = 0;
                for (int i = 0; i < sectionCount; i++)
                {
                    uint entry = sectionTable + ((uint)i * 40);
                    uint rva = ReadUInt32(bytes, entry + 12);
                    uint rawSize = ReadUInt32(bytes, entry + 16);
                    uint rawOffset = ReadUInt32(bytes, entry + 20);
                    var section = original.Sections.FirstOrDefault(x => x.Rva == rva);
                    if (section is null || section.RawSize != rawSize || section.RawOffset != rawOffset
                        || section.VirtualSize == 0 || rawSize <= section.VirtualSize
                        || rawOffset + rawSize > (uint)bytes.Length || rawOffset + rawSize > (uint)originalBytes.Length)
                    {
                        continue;
                    }

                    var from = originalBytes.AsSpan((int)(rawOffset + section.VirtualSize), (int)(rawSize - section.VirtualSize));
                    if (from.IndexOfAnyExcept((byte)0) >= 0)
                    {
                        continue;
                    }

                    var to = bytes.AsSpan((int)(rawOffset + section.VirtualSize), (int)(rawSize - section.VirtualSize));
                    for (int b = 0; b < to.Length; b++)
                    {
                        if (to[b] != 0)
                        {
                            to[b] = 0;
                            zeroed++;
                        }
                    }
                }

                if (zeroed > 0)
                {
                    changes.Add($"raw padding after section content zeroed to match the original ({zeroed} byte(s))");
                }
            }

            // The certificate table, the one thing in an image that is neither a section nor a
            // directory pointing at one: its "address" is a file offset, and what sits there is the
            // signature over the bytes in front of it. Those bytes are this image, so the signature is
            // copied with the entry that points at it - and only when the file ends exactly where the
            // certificate begins, because a signature over a file with something inserted before it
            // would be a signature over a file that no longer exists.
            if (originalDirectoryCount > 4 && directoryCount > 4)
            {
                uint from = (uint)(originalDirectoryStart + (4 * 8));
                uint offset = ReadUInt32(originalBytes, from);
                uint size = ReadUInt32(originalBytes, from + 4);
                if (offset != 0 && size != 0 && offset + size <= (uint)originalBytes.Length
                    && (uint)bytes.Length == offset)
                {
                    Array.Resize(ref bytes, (int)(offset + size));
                    Array.Copy(originalBytes, (int)offset, bytes, (int)offset, (int)size);
                    uint at = (uint)(directoryStart + (4 * 8));
                    WriteUInt32(bytes, at, offset);
                    WriteUInt32(bytes, at + 4, size);
                    changes.Add($"certificate table copied from the original (0x{size:x} byte(s) at 0x{offset:x})");
                }
            }
        }

        // The checksum, computed rather than copied. A linker that is handed every section and every
        // directory has no reason to compute one, and lld-link leaves the field zero. The sum is over
        // the whole file, so when the relink is exact it comes out the original's own value - which is
        // both the right answer and a statement about every other byte in the image. When it differs
        // the field still describes the image it sits in, rather than the image it was copied from.
        void FinishChecksum()
        {
            if (optionalSize < 68)
            {
                return;
            }

            uint at = (uint)(optionalStart + 64);
            uint sum = CheckSum(bytes, at);
            if (ReadUInt32(bytes, at) != sum)
            {
                WriteUInt32(bytes, at, sum);
            }

            changes.Add(sum == original.Checksum
                ? $"checksum 0x{sum:x} (computed over the relinked file, and it is the original's)"
                : $"checksum 0x{sum:x} (computed over the relinked file; the original's is 0x{original.Checksum:x})");
        }

        uint stamp = ReadUInt32(bytes, coffStart + 4);
        if (stamp != original.TimeDateStamp)
        {
            WriteUInt32(bytes, coffStart + 4, original.TimeDateStamp);
            changes.Add($"timestamp 0x{original.TimeDateStamp:x8} (the linker stamped the link, not the build)");
        }


        // Last, because it is a sum over every byte of the file: the timestamp and the section names
        // are written above, and a checksum that described the file before those were in it would be a
        // checksum of a file that no longer exists.
        FinishChecksum();

        if (changes.Count > 0)
        {
            File.WriteAllBytes(relinkedPath, bytes);
        }

        return changes;
    }

    /// <summary>The name of a section table entry: the eight bytes inline, or null for the long form.</summary>
    private static string? ReadSectionName(byte[] bytes, uint entry)
    {
        var name = bytes.AsSpan((int)entry, 8);
        if (name[0] == (byte)'/')
        {
            return null;   // an offset into the string table, which is not a name this step can rewrite
        }

        int end = name.IndexOf((byte)0);
        if (end < 0)
        {
            end = 8;
        }

        return Encoding.ASCII.GetString(name[..end]);
    }

    /// <summary>Where the COFF header and the data directories of this image start, or null.</summary>
    private static (uint CoffStart, uint OptionalStart, uint OptionalSize, uint DirectoryStart, uint DirectoryCount)? Layout(byte[] bytes)
    {
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            return null;
        }

        uint peOffset = ReadUInt32(bytes, 0x3c);
        if (peOffset + 24 > (uint)bytes.Length || ReadUInt32(bytes, peOffset) != 0x0000_4550u)
        {
            return null;
        }

        uint coff = peOffset + 4;
        uint optionalSize = ReadUInt16(bytes, coff + 16);
        uint optional = coff + 20;
        if (optional + optionalSize > (uint)bytes.Length)
        {
            return null;
        }

        uint magic = ReadUInt16(bytes, optional);
        uint directories = magic == 0x20b ? optional + 112 : optional + 96;
        uint count = ReadUInt32(bytes, magic == 0x20b ? optional + 108 : optional + 92);
        if (directories + (count * 8) > (uint)bytes.Length)
        {
            return null;
        }

        return (coff, optional, optionalSize, directories, count);
    }

    /// <summary>
    /// Puts the original's MS-DOS stub back and moves the NT header structures behind it.
    ///
    /// The stub is the linker's own boilerplate - lld-link's is 0x78 bytes and MSVC's is 0xf0 - and it
    /// is where e_lfanew points, so a different stub means every byte of the header lands at a
    /// different file offset. Both images describe the same headers; this makes them sit at the same
    /// offset, which is what lets the comparison above be about bytes instead of about layout. The
    /// move is bound by the header block the relinked image itself claims: nothing here can write past
    /// it, and if the structures do not fit the stub is left alone and the layout is reported as it is.
    /// </summary>
    private static List<string> RelayoutStub(byte[] bytes, byte[] originalBytes)
    {
        var changes = new List<string>();
        if (bytes.Length < 0x40 || originalBytes.Length < 0x40)
        {
            return changes;
        }

        uint wanted = ReadUInt32(originalBytes, 0x3c);
        uint current = ReadUInt32(bytes, 0x3c);
        if (wanted == current || wanted < 0x40 || wanted > 0x400 || current < 0x40 || current > 0x400)
        {
            return changes;
        }

        var layout = Layout(bytes);
        if (layout is null)
        {
            return changes;
        }

        var (coffStart, optionalStart, optionalSize, _, _) = layout.Value;
        uint headerSize = ReadUInt32(bytes, optionalStart + 60);
        ushort sectionCount = ReadUInt16(bytes, coffStart + 2);
        uint ntStart = coffStart - 4;
        uint ntEnd = optionalStart + optionalSize + ((uint)sectionCount * 40);
        if (ntStart != current || ntEnd <= current || ntEnd > headerSize || headerSize > bytes.Length
            || wanted + (ntEnd - current) > headerSize)
        {
            return changes;
        }

        var header = new byte[headerSize];
        Array.Copy(originalBytes, 0, header, 0, (int)wanted);
        Array.Copy(bytes, (int)current, header, (int)wanted, (int)(ntEnd - current));
        Array.Copy(header, 0, bytes, 0, (int)headerSize);
        changes.Add(
            $"MS-DOS stub restored from the original ({wanted} byte(s); the linker wrote {current}), "
            + "so the headers start at the same file offset and read as bytes rather than as a shift");
        return changes;
    }

    /// <summary>
    /// The PE checksum: every 16-bit word of the file summed with the carries folded back in, plus the
    /// length of the file, with the four bytes the value itself occupies read as zero. Confirmed on a
    /// real image: this reproduces the checksum MSVC's own link.exe wrote.
    /// </summary>
    private static uint CheckSum(byte[] bytes, uint skip)
    {
        uint sum = 0;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            // All four bytes of the field, not the word it starts at: the value is a dword and only
            // its first half is at `skip`.
            ushort word = i >= skip && i < skip + 4
                ? (ushort)0
                : (ushort)(bytes[i] | (bytes[i + 1] << 8));
            sum += word;
            sum = (sum & 0xffff) + (sum >> 16);
        }

        if (bytes.Length % 2 == 1)
        {
            sum += bytes[^1];
        }

        sum = (sum & 0xffff) + (sum >> 16);
        return (sum + (uint)bytes.Length) & 0xffffffff;
    }

    private static ushort ReadUInt16(byte[] bytes, uint offset)
        => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadUInt32(byte[] bytes, uint offset)
        => bytes[offset]
            | ((uint)bytes[offset + 1] << 8)
            | ((uint)bytes[offset + 2] << 16)
            | ((uint)bytes[offset + 3] << 24);

    private static void WriteUInt16(byte[] bytes, uint offset, ushort value)
    {
        bytes[offset] = (byte)(value & 0xff);
        bytes[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static void WriteUInt32(byte[] bytes, uint offset, uint value)
    {
        bytes[offset] = (byte)(value & 0xff);
        bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        bytes[offset + 2] = (byte)((value >> 16) & 0xff);
        bytes[offset + 3] = (byte)((value >> 24) & 0xff);
    }

    /// <summary>What the change list looks like in one line.</summary>
    public static string Describe(List<string> changes)
    {
        if (changes.Count == 0)
        {
            return "nothing to copy: the linker wrote every header entry it needed to";
        }

        var text = new StringBuilder("copied from the original: ");
        text.Append(string.Join("; ", changes));
        return text.ToString();
    }
}
