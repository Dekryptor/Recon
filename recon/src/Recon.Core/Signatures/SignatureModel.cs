using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recon.Signatures;

/// <summary>
/// One function's pattern: the first bytes of its body, and which of those bytes count.
/// </summary>
/// <remarks>
/// This is the FLIRT idea, in the one form this tool can check: take the head of a function, mark
/// every byte a link is free to change — a relocation, or a word that holds an address — and remember
/// the rest. Two builds of the same library then share a pattern even though every address in them
/// differs, which is the same problem the compare engine's relocation model solves, one level down.
/// </remarks>
public sealed class FunctionSignature
{
    /// <summary>The name this pattern stands for.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The size of the function the pattern was taken from, which is a hint and not a claim.</summary>
    [JsonPropertyName("size")]
    public uint Size { get; set; }

    /// <summary>
    /// What the function belongs to — a runtime, a static library, a version of the CRT. The point of
    /// the whole exercise: a function the reconstruction does not have to write is one it can name.
    /// </summary>
    [JsonPropertyName("library")]
    public string? Library { get; set; }

    /// <summary>The object or archive the pattern was taken from, when the source said.</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>The bytes, as lower-case hex: two characters each.</summary>
    [JsonPropertyName("bytes")]
    public string Bytes { get; set; } = string.Empty;

    /// <summary>
    /// Which bytes count, as lower-case hex: <c>ff</c> for a byte that must match, <c>00</c> for one a
    /// link may change. Same length as <see cref="Bytes"/>.
    /// </summary>
    [JsonPropertyName("mask")]
    public string Mask { get; set; } = string.Empty;

    [JsonIgnore]
    public int Length => Bytes.Length / 2;

    [JsonIgnore]
    public int FixedBytes => Mask.Length / 2 - Wildcards(Mask);

    /// <summary>How many bytes of a mask a link is free to change.</summary>
    public static int Wildcards(string mask)
    {
        int count = 0;
        for (int i = 0; i + 1 < mask.Length; i += 2)
        {
            if (mask[i] == '0' && mask[i + 1] == '0')
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// A set of patterns, and the two rules that keep them honest: a pattern is long enough to be
/// unlikely by chance, and it is dropped when it does not say one thing.
/// </summary>
public sealed class SignatureDatabase
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>How many bytes of a function's head a pattern holds.</summary>
    public const int DefaultPatternLength = 32;

    /// <summary>
    /// How many of them must be bytes a link cannot change. A pattern of all wildcards matches
    /// everything, which is worse than no pattern at all.
    /// </summary>
    public const int DefaultMinFixedBytes = 16;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("generator")]
    public string Generator { get; set; } = string.Empty;

    [JsonPropertyName("pattern_length")]
    public int PatternLength { get; set; } = DefaultPatternLength;

    [JsonPropertyName("min_fixed_bytes")]
    public int MinFixedBytes { get; set; } = DefaultMinFixedBytes;

    [JsonPropertyName("entries")]
    public List<FunctionSignature> Entries { get; set; } = [];

    /// <summary>Notes from building the set: what was dropped, and why.</summary>
    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

/// <summary>
/// The pattern file's own reader and writer. NativeAOT ships without reflection-based serialization,
/// so this goes through a source-generated context of its own like the inventory does.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignatureDatabase))]
public partial class SignatureJsonContext : JsonSerializerContext
{
}

public static class SignatureFile
{
    public static string Serialize(SignatureDatabase database)
        => JsonSerializer.Serialize(database, SignatureJsonContext.Default.SignatureDatabase);

    public static SignatureDatabase? Deserialize(string json)
        => JsonSerializer.Deserialize(json, SignatureJsonContext.Default.SignatureDatabase);

    public static SignatureDatabase Load(string path)
    {
        var database = Deserialize(File.ReadAllText(path));
        return database ?? throw new InvalidDataException($"{path} is not a signature file");
    }
}
