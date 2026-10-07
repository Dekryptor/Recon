using System.Buffers.Binary;
using System.Text;

namespace Recon.Pe;

/// <summary>
/// The version a Windows binary declares in its resource section: the numbers that say which build it
/// is. The linker's version stamp cannot answer that — it says which linker ran, not which product was
/// built — and a file's name cannot either.
/// </summary>
/// <remarks>
/// This reads the <c>VS_VERSIONINFO</c> structure in the resource of type 16: the fixed part gives the
/// binary's own numbered version, and the string table gives the strings the build system wrote, of
/// which <c>FileVersion</c> is the one that names a build.
/// </remarks>
public sealed class VersionResource
{
    /// <summary>Resource type 16 is the version resource, in every Windows binary since Windows 3.</summary>
    private const uint VersionResourceType = 16;

    /// <summary>The numbered version, as <c>a.b.c.d</c>, or empty.</summary>
    public string FileVersion { get; init; } = string.Empty;

    public string ProductVersion { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public string? CompanyName { get; init; }

    public string? OriginalFilename { get; init; }

    /// <summary>What to show when there is something to show.</summary>
    public string Display => FileVersion.Length > 0 ? FileVersion : ProductVersion;

    /// <summary>
    /// The build number in <see cref="FileVersion"/>: Microsoft's <c>6.00.9848</c> is build 9848, and
    /// that is the number a Visual Basic program's header names when it says which runtime it was
    /// compiled against. Null when the string does not carry three numeric fields, because a shorter
    /// one has no build field to read rather than a build of zero.
    /// </summary>
    public int? Build
    {
        get
        {
            var parts = FileVersion.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3
                   && int.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out int build)
                ? build
                : null;
        }
    }

    public bool Any => FileVersion.Length > 0 || ProductVersion.Length > 0 || ProductName.Length > 0;

    public static VersionResource Empty { get; } = new();

    public static VersionResource Read(PeImage image, byte[] bytes)
    {
        byte[]? resource = FindVersionResource(image, bytes);
        if (resource is null)
        {
            return Empty;
        }

        return new VersionResource
        {
            FileVersion = ReadString(resource, "FileVersion"),
            ProductVersion = ReadString(resource, "ProductVersion"),
            ProductName = ReadString(resource, "ProductName"),
            CompanyName = NullIfEmpty(ReadString(resource, "CompanyName")),
            OriginalFilename = NullIfEmpty(ReadString(resource, "OriginalFilename")),
        };
    }

    /// <summary>
    /// Walks the resource directory to the version resource's data.
    ///
    /// The directory is a tree: an entry names a type, or a name, or a language, and the entry beside
    /// it either points at another directory or at the data itself. A version resource is three
    /// levels deep in every writer anyone still meets — type 16, then the name or id, then the
    /// language — but the levels are walked rather than assumed, because the format allows a type to
    /// hang its data off the first level and a reader that counts levels is a reader that will one
    /// day count wrong.
    /// </summary>
    private static byte[]? FindVersionResource(PeImage image, byte[] bytes)
    {
        var section = image.Sections.FirstOrDefault(s => s.Name == ".rsrc");
        if (section is null || section.RawSize == 0)
        {
            return null;
        }

        int sectionOffset = (int)section.RawOffset;
        int sectionEnd = sectionOffset + (int)section.RawSize;

        var entry = Descend(sectionOffset, sectionOffset, sectionEnd, bytes, VersionResourceType);
        if (entry is null)
        {
            return null;
        }

        for (int level = 0; level < MaxResourceDepth && entry.Value.IsDirectory; level++)
        {
            entry = FirstEntry(entry.Value.Offset, sectionOffset, sectionEnd, bytes);
            if (entry is null)
            {
                return null;
            }
        }

        return entry is null || entry.Value.IsDirectory
            ? null
            : ReadData(image, bytes, entry.Value.Offset);
    }

    /// <summary>
    /// How many subdirectories to follow below a type before deciding there is nothing under it. Three
    /// is what the format has; the loop is bounded because a malformed offset that points at itself
    /// would otherwise be an endless walk.
    /// </summary>
    private const int MaxResourceDepth = 4;

    /// <summary>Where an entry in a resource directory points, and what is there.</summary>
    private readonly record struct ResourceEntry(int Offset, bool IsDirectory);

    /// <summary>
    /// The entry with this id in a directory: the entries start after the directory's sixteen-byte
    /// header, and an entry's name is either an id or, with the high bit set, a string.
    /// </summary>
    private static ResourceEntry? Descend(int directoryOffset, int sectionOffset, int sectionEnd, byte[] bytes, uint wanted)
    {
        if (directoryOffset + 16 > bytes.Length)
        {
            return null;
        }

        int named = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directoryOffset + 12, 2));
        int ids = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directoryOffset + 14, 2));

        for (int i = 0; i < named + ids; i++)
        {
            int entry = directoryOffset + 16 + (i * 8);
            if (entry + 8 > bytes.Length)
            {
                return null;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry, 4)) != wanted)
            {
                continue;
            }

            return PointedAt(entry, sectionOffset, sectionEnd, bytes);
        }

        return null;
    }

    /// <summary>The first entry of a directory, whatever its name or id.</summary>
    private static ResourceEntry? FirstEntry(int directoryOffset, int sectionOffset, int sectionEnd, byte[] bytes)
    {
        if (directoryOffset + 16 > bytes.Length)
        {
            return null;
        }

        int named = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directoryOffset + 12, 2));
        int ids = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directoryOffset + 14, 2));
        if (named + ids == 0)
        {
            return null;
        }

        return PointedAt(directoryOffset + 16, sectionOffset, sectionEnd, bytes);
    }

    /// <summary>
    /// What an entry points at. The high bit of the offset says which of the two it is — another
    /// directory, or the data entry — and both are offsets from the start of the resource section.
    /// </summary>
    private static ResourceEntry? PointedAt(int entryOffset, int sectionOffset, int sectionEnd, byte[] bytes)
    {
        if (entryOffset + 8 > bytes.Length)
        {
            return null;
        }

        uint child = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 4, 4));
        bool isDirectory = (child & 0x80000000) != 0;
        int at = sectionOffset + (int)(child & 0x7FFFFFFF);
        return at >= sectionOffset && at < sectionEnd && at < bytes.Length
            ? new ResourceEntry(at, isDirectory)
            : null;
    }

    /// <summary>
    /// The bytes a data entry has. The entry holds the resource's address as an RVA, its length, and
    /// the code page it is written in; the first two are what this needs, and the address is the
    /// image's, not the section's, so it is resolved against the sections rather than assumed to sit
    /// where the resource directory is.
    /// </summary>
    private static byte[]? ReadData(PeImage image, byte[] bytes, int dataEntry)
    {
        if (dataEntry + 16 > bytes.Length)
        {
            return null;
        }

        uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataEntry, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataEntry + 4, 4));

        int? offset = PeLoader.RvaToOffset(image, dataRva);
        if (offset is null || size == 0)
        {
            return null;
        }

        int start = offset.Value;
        if (start < 0 || start >= bytes.Length)
        {
            return null;
        }

        int length = (int)Math.Min(size, (uint)(bytes.Length - start));
        return length <= 0 ? null : bytes.AsSpan(start, length).ToArray();
    }

    /// <summary>
    /// A string out of the version resource's string table. The table is a tree of aligned UTF-16
    /// blocks and the keys are its own: finding the key and reading the value after it is what the
    /// format asks for, and it is steadier than hard-coding the offsets of a structure whose length
    /// fields vary by writer.
    /// </summary>
    private static string ReadString(byte[] resource, string key)
    {
        byte[] needle = Encoding.Unicode.GetBytes(key + "\0");
        for (int at = 0; at + needle.Length + 4 <= resource.Length; at += 2)
        {
            if (!resource.AsSpan(at, needle.Length).SequenceEqual(needle))
            {
                continue;
            }

            // The value follows the key, padded to a four-byte boundary.
            int value = at + needle.Length;
            while (value % 4 != 0)
            {
                value++;
            }

            if (value + 2 > resource.Length)
            {
                return string.Empty;
            }

            // Read a UTF-16 string to its terminator; where the writer included a length this agrees
            // with it, and where it did not, the terminator is the only thing there is.
            int end = value;
            while (end + 1 < resource.Length && !(resource[end] == 0 && resource[end + 1] == 0))
            {
                end += 2;
            }

            return Encoding.Unicode.GetString(resource, value, end - value).Trim();
        }

        return string.Empty;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
