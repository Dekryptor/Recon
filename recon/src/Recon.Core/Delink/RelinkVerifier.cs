using System.Text;
using Recon.Pe;

namespace Recon.Delink;

/// <summary>What the relinked image is, measured against the image the plan was cut from.</summary>
public sealed class RelinkComparison
{
    /// <summary>True when nothing the comparison looks at is different.</summary>
    public bool Identical { get; set; }

    /// <summary>The bytes of the file, as opposed to the bytes of its sections.</summary>
    public long FileBytesCompared { get; set; }

    public long FileBytesDiffering { get; set; }

    public int SectionsCompared { get; set; }

    public int SectionsIdentical { get; set; }

    /// <summary>The bytes of the whole file the verdict speaks for, and how many of them differ.</summary>
    public long BytesCompared { get; set; }

    public long BytesDiffering { get; set; }

    /// <summary>The same accounting inside the sections, kept apart from the file's.</summary>
    public long SectionBytesCompared { get; set; }

    public long SectionBytesDiffering { get; set; }

    public List<string> Differences { get; set; } = [];

    /// <summary>One line for the console, and the whole answer when the answer is the good one.</summary>
    public string Verdict
    {
        get
        {
            if (Differences.Count == 0)
            {
                return $"identical: {SectionsIdentical} section(s), {BytesCompared} byte(s), header included";
            }

            var text = new StringBuilder();
            text.Append($"{SectionsIdentical}/{SectionsCompared} section(s) identical, {BytesDiffering} byte(s) differ");
            return text.ToString();
        }
    }
}

/// <summary>
/// Compares a relinked image with the original, section by section and byte by byte.
///
/// This is the point of delinking: an image that can be cut into pieces and put back together is an
/// image you can replace a piece of. That claim is only worth as much as the check that follows it, so
/// `recon link` makes it instead of leaving it to be assumed — every section at the same address, of
/// the same size, with the same flags and the same bytes, plus the header entries the image is found
/// by: its base, its entry point, its subsystem, its size.
/// </summary>
public static class RelinkVerifier
{
    public static RelinkComparison Compare(PeImage original, byte[] originalBytes, string relinkedPath)
    {
        var comparison = new RelinkComparison();
        var relinked = PeLoader.Load(relinkedPath);
        if (relinked.Image is null)
        {
            comparison.Differences.Add("the relinked file is not a readable PE image");
            return comparison;
        }

        var image = relinked.Image;
        byte[] bytes = relinked.Bytes;

        if (image.ImageBase != original.ImageBase)
        {
            comparison.Differences.Add($"image base 0x{image.ImageBase:x} (the original is at 0x{original.ImageBase:x})");
        }

        if (image.EntryPointRva != original.EntryPointRva)
        {
            comparison.Differences.Add($"entry 0x{image.EntryPointRva:x} (the original's is 0x{original.EntryPointRva:x})");
        }

        if (image.Subsystem != original.Subsystem)
        {
            comparison.Differences.Add($"subsystem {image.Subsystem} (the original's is {original.Subsystem})");
        }

        if (image.SizeOfImage != original.SizeOfImage)
        {
            comparison.Differences.Add($"size of image 0x{image.SizeOfImage:x} (the original's is 0x{original.SizeOfImage:x})");
        }

        var byRva = image.Sections.ToDictionary(s => s.Rva);
        foreach (var section in original.Sections)
        {
            comparison.SectionsCompared++;
            if (!byRva.TryGetValue(section.Rva, out var copy))
            {
                comparison.Differences.Add($"section {section.Name} at 0x{section.Rva:x} is missing");
                continue;
            }

            bool same = true;
            if (!string.Equals(copy.Name, section.Name, StringComparison.Ordinal))
            {
                comparison.Differences.Add($"section at 0x{section.Rva:x} is named {copy.Name}, not {section.Name}");
                same = false;
            }

            if (copy.VirtualSize != section.VirtualSize)
            {
                comparison.Differences.Add($"section {section.Name} is {copy.VirtualSize} byte(s) in memory, the original is {section.VirtualSize}");
                same = false;
            }

            if (copy.Characteristics != section.Characteristics)
            {
                comparison.Differences.Add($"section {section.Name} has flags 0x{copy.Characteristics:x8}, the original has 0x{section.Characteristics:x8}");
                same = false;
            }

            long differing = DifferingBytes(section, originalBytes, copy, bytes, out long compared);
            comparison.SectionBytesCompared += compared;
            if (differing > 0)
            {
                comparison.SectionBytesDiffering += differing;
                comparison.Differences.Add($"section {section.Name} differs in {differing} of {compared} byte(s)");
                same = false;
            }

            if (same)
            {
                comparison.SectionsIdentical++;
            }
        }

        foreach (var extra in image.Sections.Where(s => original.Sections.All(o => o.Rva != s.Rva)))
        {
            comparison.Differences.Add($"section {extra.Name} at 0x{extra.Rva:x} is not in the original");
        }

        // Then the file itself: the header, the padding after each section's content, and whatever sits
        // past the last section - every byte the two files have. A section that matches says the image
        // was rebuilt; this says the file was, and that is the claim a relink is worth having. The two
        // are not the same question: the header is not in any section, and neither is the certificate
        // table an MSVC-signed binary carries at the end of the file.
        long fileLength = Math.Min(bytes.Length, originalBytes.Length);
        comparison.FileBytesCompared = fileLength;
        long fileDiffering = 0;
        var offsets = new List<string>();
        for (long i = 0; i < fileLength; i++)
        {
            if (bytes[i] != originalBytes[i])
            {
                fileDiffering++;
                if (offsets.Count < 8)
                {
                    offsets.Add($"0x{i:x}");
                }
            }
        }

        comparison.FileBytesDiffering = fileDiffering;
        if (bytes.Length != originalBytes.Length)
        {
            comparison.Differences.Add(
                $"the relinked file is {bytes.Length} byte(s), the original is {originalBytes.Length}");
        }

        if (fileDiffering > 0)
        {
            comparison.Differences.Add(
                $"the file differs in {fileDiffering} of {fileLength} byte(s), first at {string.Join(", ", offsets)}"
                + (fileDiffering > offsets.Count ? " ..." : string.Empty));
        }

        // The count the verdict carries is the file's, so "identical" is a statement about every byte
        // of the relinked file and not about the sections alone.
        comparison.BytesCompared = fileLength;
        comparison.BytesDiffering = fileDiffering;

        comparison.Identical = comparison.Differences.Count == 0;
        return comparison;
    }

    /// <summary>
    /// Counts the bytes that differ between two copies of a section. Only the bytes both files have
    /// are compared; the rest is the file's rounding up to its alignment, which is not part of either
    /// image.
    /// </summary>
    private static long DifferingBytes(
        PeSection original,
        byte[] originalBytes,
        PeSection copy,
        byte[] copyBytes,
        out long compared)
    {
        compared = 0;
        int length = (int)Math.Min(
            Math.Min(original.RawSize, copy.RawSize),
            Math.Max(original.VirtualSize, copy.VirtualSize));
        if (length <= 0)
        {
            return 0;
        }

        long differing = 0;
        for (int i = 0; i < length; i++)
        {
            compared++;
            uint offset = (uint)i;
            int? left = offset < original.RawSize ? (int?)(original.RawOffset + offset) : null;
            int? right = offset < copy.RawSize ? (int?)(copy.RawOffset + offset) : null;

            byte a = left is { } l && l < originalBytes.Length ? originalBytes[l] : (byte)0;
            byte b = right is { } r && r < copyBytes.Length ? copyBytes[r] : (byte)0;
            if (a != b)
            {
                differing++;
            }
        }

        return differing;
    }
}
