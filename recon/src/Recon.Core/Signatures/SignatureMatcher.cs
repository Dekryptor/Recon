using Recon.Images;

namespace Recon.Signatures;

/// <summary>What a pattern matched, and what it claims about the function it matched.</summary>
public sealed class SignatureMatch
{
    public required string Name { get; init; }

    public string? Library { get; init; }

    public string? Unit { get; init; }

    public uint Size { get; init; }

    /// <summary>How many of the pattern's bytes are bytes a link cannot change: the strength of the match.</summary>
    public int FixedBytes { get; init; }
}

/// <summary>
/// Matches the head of a function against a set of patterns.
/// </summary>
/// <remarks>
/// Two rules keep this from being a guessing game. A match only counts when exactly one name claims
/// it — several patterns agreeing on one name is fine, two names sharing a pattern is not — and the
/// matcher never invents a function: it names addresses the analysis already found, which is why it
/// runs inside the analysis rather than as a separate pass over the bytes.
/// </remarks>
public sealed class SignatureMatcher
{
    private readonly SignatureDatabase _database;
    private readonly List<FunctionSignature> _firstByteAny = [];
    private readonly Dictionary<byte, List<FunctionSignature>> _byFirstByte = [];

    /// <summary>Addresses where more than one name matched. Reported, not guessed.</summary>
    public List<string> Ambiguous { get; } = [];

    public SignatureMatcher(SignatureDatabase database)
    {
        _database = database;

        foreach (var entry in database.Entries)
        {
            byte[] head = FromHex(entry.Bytes);
            byte[] mask = FromHex(entry.Mask);
            if (head.Length == 0 || head.Length != mask.Length)
            {
                continue;
            }

            // A pattern whose first byte a link may change cannot be indexed by it.
            if (mask[0] == 0)
            {
                _firstByteAny.Add(entry);
            }
            else if (!_byFirstByte.TryGetValue(head[0], out var list))
            {
                _byFirstByte[head[0]] = [entry];
            }
            else
            {
                list.Add(entry);
            }
        }
    }

    /// <summary>
    /// The name of the function at this address, or null when no pattern — or more than one name —
    /// claims it.
    /// </summary>
    public SignatureMatch? At(IBinaryImage image, byte[] bytes, uint rva)
    {
        int? offset = image.RvaToOffset(rva);
        if (offset is null || offset.Value >= bytes.Length)
        {
            return null;
        }

        int length = Math.Min(_database.PatternLength, bytes.Length - offset.Value);
        var candidates = new List<FunctionSignature>();
        if (_byFirstByte.TryGetValue(bytes[offset.Value], out var listed))
        {
            candidates.AddRange(listed);
        }

        candidates.AddRange(_firstByteAny);

        string? name = null;
        SignatureMatch? match = null;
        bool disputed = false;

        foreach (var candidate in candidates)
        {
            byte[] head = FromHex(candidate.Bytes);
            byte[] mask = FromHex(candidate.Mask);
            if (head.Length > length || !Matches(bytes, offset.Value, head, mask))
            {
                continue;
            }

            if (name is null)
            {
                name = candidate.Name;
                match = new SignatureMatch
                {
                    Name = candidate.Name,
                    Library = candidate.Library,
                    Unit = candidate.Unit,
                    Size = candidate.Size,
                    FixedBytes = candidate.FixedBytes,
                };
            }
            else if (!string.Equals(name, candidate.Name, StringComparison.Ordinal))
            {
                disputed = true;
            }
            else if (match is not null && candidate.FixedBytes > match.FixedBytes)
            {
                // The same name, carried by a stronger pattern: the longer one is the better answer.
                match = new SignatureMatch
                {
                    Name = candidate.Name,
                    Library = candidate.Library,
                    Unit = candidate.Unit,
                    Size = candidate.Size,
                    FixedBytes = candidate.FixedBytes,
                };
            }
        }

        if (match is null)
        {
            return null;
        }

        if (disputed)
        {
            Ambiguous.Add($"0x{rva:X}: {match.Name} is one of several names whose patterns match these bytes");
            return null;
        }

        return match;
    }

    private static bool Matches(byte[] bytes, int offset, byte[] head, byte[] mask)
    {
        for (int i = 0; i < head.Length; i++)
        {
            if (mask[i] != 0 && bytes[offset + i] != head[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            return [];
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
