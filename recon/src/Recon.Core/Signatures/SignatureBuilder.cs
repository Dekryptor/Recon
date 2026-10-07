using Recon.DebugInfo;
using Recon.Images;

namespace Recon.Signatures;

/// <summary>
/// Builds patterns from a binary the tool already understands: every function debug information names
/// becomes the first bytes of its body, with the bytes a link may change marked as wildcards.
/// </summary>
/// <remarks>
/// Two things decide whether a pattern is worth keeping. It must have enough bytes a link cannot
/// touch, or it matches more than it should. And it must name one thing: a pattern that two functions
/// share is dropped, because naming a function from it would be a guess dressed as a fact.
/// </remarks>
public static class SignatureBuilder
{
    public sealed class Options
    {
        public int PatternLength { get; init; } = SignatureDatabase.DefaultPatternLength;

        public int MinFixedBytes { get; init; } = SignatureDatabase.DefaultMinFixedBytes;

        /// <summary>What to call the code these functions belong to: a runtime, a library, a version.</summary>
        public string? Library { get; init; }

        /// <summary>
        /// Only take patterns from functions whose unit (source file or object) contains one of these.
        /// Empty means every named function, which is what you want when the reference binary is the
        /// library itself; a list is how you keep a program's own code out of the patterns you take
        /// from an executable that mixes the two.
        /// </summary>
        public string[] UnitFilters { get; init; } = [];
    }

    public static SignatureDatabase Build(IBinaryImage image, byte[] bytes, DebugInfoResult debug, Options? options = null)
    {
        var o = options ?? new Options();
        var database = new SignatureDatabase
        {
            PatternLength = o.PatternLength,
            MinFixedBytes = o.MinFixedBytes,
        };

        // Pattern to the addresses that produced it. One pattern at two addresses is the case that
        // has to be thrown away — it cannot say which of them it names — and this is where it shows
        // up. Counting addresses rather than names is what keeps a folded alias out of that set: two
        // names at one address are one function the linker gave two names, not two functions.
        var byPattern = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        var namesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sample = new Dictionary<string, FunctionSignature>(StringComparer.Ordinal);

        int tooWild = 0;
        int notInFile = 0;

        foreach (var symbol in debug.Symbols)
        {
            if (symbol.IsData || symbol.Name.Length == 0)
            {
                continue;
            }

            if (o.UnitFilters.Length > 0
                && !o.UnitFilters.Any(wanted => symbol.Unit?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            int? offset = image.RvaToOffset(symbol.Rva);
            if (offset is null || offset.Value + o.PatternLength > bytes.Length)
            {
                notInFile++;
                continue;
            }

            byte[] head = bytes.AsSpan(offset.Value, o.PatternLength).ToArray();
            byte[] mask = Mask(image, bytes, offset.Value, head, symbol.Rva);

            int fixedBytes = mask.Count(b => b != 0);
            if (fixedBytes < o.MinFixedBytes)
            {
                tooWild++;
                continue;
            }

            string key = Hex(head) + ":" + Hex(mask);
            if (!byPattern.TryGetValue(key, out var addresses))
            {
                addresses = [];
                byPattern[key] = addresses;
                namesOf[key] = [];
                sample[key] = new FunctionSignature
                {
                    Name = symbol.Name,
                    Size = symbol.Size ?? 0,
                    Library = o.Library,
                    Unit = symbol.Unit,
                    Bytes = Hex(head),
                    Mask = Hex(mask),
                };
            }

            if (addresses.Add(symbol.Rva))
            {
                namesOf[key].Add(symbol.Name);
            }
        }

        int ambiguous = 0;
        foreach (var (key, addresses) in byPattern)
        {
            if (addresses.Count > 1)
            {
                ambiguous++;
                database.Problems.Add(
                    $"pattern {sample[key].Bytes[..16]}… is shared by "
                    + $"{string.Join(", ", namesOf[key].OrderBy(n => n, StringComparer.Ordinal))}, so it names none of them");
                continue;
            }

            database.Entries.Add(sample[key]);
        }

        if (notInFile > 0)
        {
            database.Problems.Add($"{notInFile} function(s) have fewer than {o.PatternLength} bytes of body to take a pattern from");
        }

        if (tooWild > 0)
        {
            database.Problems.Add(
                $"{tooWild} function(s) have fewer than {o.MinFixedBytes} bytes a link cannot change, so no pattern was taken from them");
        }

        if (ambiguous > 0)
        {
            database.Problems.Add($"{ambiguous} pattern(s) were shared by more than one function and were dropped");
        }

        database.Entries = [.. database.Entries.OrderBy(e => e.Name, StringComparer.Ordinal)];
        return database;
    }

    /// <summary>
    /// Which bytes count. A relocation says so outright. Failing that — and a release build often has
    /// no relocation table to say it — a word in the code that points inside the image is an address,
    /// and an address is the one thing a rebuild is free to move. Wildcarding a byte that is not
    /// really an address costs a little precision; not wildcarding a real one costs the whole pattern.
    /// </summary>
    private static byte[] Mask(IBinaryImage image, byte[] bytes, int offset, byte[] head, uint rva)
    {
        var mask = new byte[head.Length];
        Array.Fill(mask, (byte)0xFF);

        uint width(uint relocationWidth) => relocationWidth is 0 or > 8 ? 4u : relocationWidth;

        foreach (var relocation in image.Relocations)
        {
            if (relocation.Rva < rva || relocation.Rva >= rva + (uint)head.Length)
            {
                continue;
            }

            Wildcard(mask, (int)(relocation.Rva - rva), (int)width(relocation.Width));
        }

        // Not every word that lands inside the image is an address: a small constant lands there too,
        // and calling it one would wildcard the immediates of half the functions in the file. No
        // linker puts a section in the first page — the headers live there, and zero is not an
        // address — so below one page a value stays a constant unless a relocation says otherwise.
        const uint smallestPlausibleAddress = 0x1000;

        for (int i = 0; i + 4 <= head.Length; i++)
        {
            int at = offset + i;
            if (at + 4 > bytes.Length)
            {
                break;
            }

            uint value = (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24));
            if (value >= smallestPlausibleAddress && LooksLikeAnAddress(image, value))
            {
                Wildcard(mask, i, 4);
            }
        }

        return mask;
    }

    /// <summary>
    /// Whether a word could be a pointer into this image. A linked PE stores virtual addresses
    /// (<c>base + rva</c>), a linked ELF stores RVAs, and a word that is neither is a constant: it
    /// has to be checked both ways, because the same four bytes mean different things in the two.
    /// </summary>
    private static bool LooksLikeAnAddress(IBinaryImage image, uint value)
    {
        if (image.ContainsRva(value))
        {
            return true;
        }

        ulong imageBase = image.ImageBase;
        return imageBase != 0 && value >= imageBase && image.ContainsRva((uint)(value - imageBase));
    }

    private static void Wildcard(byte[] mask, int at, int count)
    {
        for (int i = at; i < at + count && i < mask.Length; i++)
        {
            if (i >= 0)
            {
                mask[i] = 0;
            }
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
