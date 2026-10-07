using Recon.Images;

namespace Recon.Strings;

/// <summary>How a run of characters was stored in the file.</summary>
public enum StringKind
{
    /// <summary>One byte per character, the way a C compiler stores a literal.</summary>
    Ascii,

    /// <summary>Two bytes per character, little-endian: what a Windows API is handed.</summary>
    Utf16,
}

/// <summary>One run of characters found in a binary, with where it lives.</summary>
public sealed class ExtractedString
{
    /// <summary>Offset in the file, which is what a hex editor wants.</summary>
    public long Offset { get; init; }

    /// <summary>Address in the loaded image, when a section maps the offset. This is what a disassembler wants.</summary>
    public uint? Rva { get; init; }

    /// <summary>The section the run lies in, or empty when no section contains it (an overlay, or a header).</summary>
    public string Section { get; init; } = string.Empty;

    public StringKind Kind { get; init; }

    /// <summary>Characters in the run, which is not the same as bytes for <see cref="StringKind.Utf16"/>.</summary>
    public int Length { get; init; }

    /// <summary>Bytes the run occupies in the file: <see cref="Length"/>, doubled for UTF-16.</summary>
    public int ByteLength { get; init; }

    /// <summary>The characters themselves.</summary>
    public string Text { get; init; } = string.Empty;
}

public sealed class StringScanOptions
{
    /// <summary>Runs shorter than this are not strings — the default is <c>strings(1)</c>'s.</summary>
    public int MinLength { get; init; } = 4;

    public bool Ascii { get; init; } = true;

    public bool Utf16 { get; init; } = true;

    /// <summary>Restrict the scan to one section, by name.</summary>
    public string? Section { get; init; }
}

/// <summary>
/// Finds the text in a binary. A reversing session starts here — an error message, a format string or
/// a URL names the code around it — and the tool could already name sections, functions and imports
/// but had no way to hand back the literal characters a file carries.
/// </summary>
/// <remarks>
/// What counts as printable is deliberately the same set <c>strings(1)</c> uses: <c>0x20</c> to
/// <c>0x7E</c> plus tab. Requiring a NUL terminator would be the more confident reading, and it is
/// wrong for the two cases that matter most here: a literal that fills its section exactly has no
/// terminator after it, and a UTF-16 run at the end of a file may have no second byte of padding.
/// The evidence for "this is a string" is the run of printable characters and nothing else, so the
/// report says where it is and lets the caller judge.
/// </remarks>
public static class StringScanner
{
    public static List<ExtractedString> Scan(byte[] bytes, IBinaryImage? image, StringScanOptions options)
    {
        var found = new List<ExtractedString>();
        if (bytes.Length == 0)
        {
            return found;
        }

        int min = Math.Max(1, options.MinLength);
        (long Start, long End)? window = Window(image, options.Section);
        if (options.Section is not null && window is null)
        {
            // The section was named and does not exist. Returning nothing would read as "it is empty",
            // which is a different answer; the command reports the missing section as a problem.
            return found;
        }

        long begin = window?.Start ?? 0;
        long end = window?.End ?? bytes.Length;

        if (options.Ascii)
        {
            ScanAscii(bytes, image, found, begin, end, min);
        }

        if (options.Utf16)
        {
            ScanUtf16(bytes, image, found, begin, end, min);
        }

        found.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return found;
    }

    /// <summary>
    /// The byte range one section occupies in the file. A section's <c>RawSize</c> is what the file
    /// holds, which is what can be scanned; its virtual size can be larger (that is <c>.bss</c>), and
    /// the padding past the raw data is not part of the section's contents.
    /// </summary>
    private static (long Start, long End)? Window(IBinaryImage? image, string? section)
    {
        if (image is null)
        {
            return section is null ? (0, long.MaxValue) : null;
        }

        if (section is null)
        {
            return (0, long.MaxValue);
        }

        foreach (BinarySection candidate in image.Sections)
        {
            if (string.Equals(candidate.Name, section, StringComparison.Ordinal) ||
                string.Equals(candidate.Name.TrimStart('.'), section.TrimStart('.'), StringComparison.Ordinal))
            {
                return ((long)candidate.RawOffset, (long)candidate.RawOffset + candidate.RawSize);
            }
        }

        return null;
    }

    private static void ScanAscii(byte[] bytes, IBinaryImage? image, List<ExtractedString> found, long begin, long end, int min)
    {
        long start = Math.Max(0, begin);
        long stop = Math.Min(bytes.Length, end);

        for (long i = start; i < stop;)
        {
            if (!IsPrintableAscii(bytes[i]))
            {
                i++;
                continue;
            }

            long runStart = i;
            while (i < stop && IsPrintableAscii(bytes[i]))
            {
                i++;
            }

            int length = (int)(i - runStart);
            if (length >= min)
            {
                found.Add(Describe(bytes, image, runStart, length, StringKind.Ascii, 1));
            }
        }
    }

    /// <summary>
    /// UTF-16LE: a printable byte followed by a zero byte, repeated. Every offset is tried, not every
    /// even one, because a string's alignment is a property of the structure holding it and not
    /// something the scanner is told.
    /// </summary>
    private static void ScanUtf16(byte[] bytes, IBinaryImage? image, List<ExtractedString> found, long begin, long end, int min)
    {
        long start = Math.Max(0, begin);
        long stop = Math.Min(bytes.Length, end);

        for (long i = start; i + 1 < stop;)
        {
            if (!IsPrintableAscii(bytes[i]) || bytes[i + 1] != 0)
            {
                i++;
                continue;
            }

            long runStart = i;
            while (i + 1 < stop && IsPrintableAscii(bytes[i]) && bytes[i + 1] == 0)
            {
                i += 2;
            }

            int length = (int)((i - runStart) / 2);
            if (length >= min)
            {
                found.Add(Describe(bytes, image, runStart, length, StringKind.Utf16, 2));
            }
        }
    }

    private static ExtractedString Describe(byte[] bytes, IBinaryImage? image, long offset, int length, StringKind kind, int unit)
    {
        string text = kind == StringKind.Ascii
            ? System.Text.Encoding.ASCII.GetString(bytes, (int)offset, length)
            : System.Text.Encoding.Unicode.GetString(bytes, (int)offset, length * 2);

        var (section, rva) = Locate(image, offset);

        return new ExtractedString
        {
            Offset = offset,
            Rva = rva,
            Section = section,
            Kind = kind,
            Length = length,
            ByteLength = length * unit,
            Text = text,
        };
    }

    /// <summary>Which section an offset falls in, and the address that maps it.</summary>
    private static (string Section, uint? Rva) Locate(IBinaryImage? image, long offset)
    {
        if (image is null)
        {
            return (string.Empty, null);
        }

        foreach (BinarySection section in image.Sections)
        {
            long rawStart = section.RawOffset;
            long rawEnd = rawStart + section.RawSize;
            if (rawStart < rawEnd && offset >= rawStart && offset < rawEnd)
            {
                // A section that is not part of the loaded image — debug info, the symbol tables —
                // is read from the file but has no address: reporting its zero as "rva 0" would
                // point at the header of the image, which is not where the string is.
                return (section.Name, section.IsAllocated ? (uint)(section.Rva + (offset - rawStart)) : null);
            }
        }

        return (string.Empty, null);
    }

    /// <summary>
    /// What <c>strings(1)</c> calls printable: the space through the tilde, plus tab. Tab is in the
    /// set because a format string or a block of help text carries it and splitting there would
    /// report one string where the file has one.
    /// </summary>
    private static bool IsPrintableAscii(byte value) => value == 0x09 || (value >= 0x20 && value <= 0x7E);
}
