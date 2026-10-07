using System.Text.Json.Serialization;

namespace Recon.Reporting;

/// <summary>
/// What <c>recon link</c> produced: the relinked image, and how much of it is reconstructed code
/// rather than bytes carried over from the original.
/// </summary>
public sealed class LinkDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "0.1";

    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;

    /// <summary>How many pieces the delink plan cut the original into.</summary>
    [JsonPropertyName("pieces")]
    public int Pieces { get; set; }

    /// <summary>How many of those pieces a unit claims to provide rather than the original's bytes.</summary>
    [JsonPropertyName("rebuilt")]
    public int Rebuilt { get; set; }

    /// <summary>
    /// The pieces that were actually filled from a unit's build, with what each one took. A claim is
    /// not a supply: a unit can claim a piece and have nothing that fits it, and the difference is the
    /// most important number in this document.
    /// </summary>
    [JsonPropertyName("rebuilt_pieces")]
    public List<LinkRebuiltPiece> RebuiltPieces { get; set; } = [];

    /// <summary>Why a claimed piece kept the original's bytes, if any did.</summary>
    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class LinkRebuiltPiece
{
    [JsonPropertyName("piece")]
    public string Piece { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    /// <summary>The unit's source, relative to the project.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public int Bytes { get; set; }

    /// <summary>Addresses inside the rebuild that became references to names in the image.</summary>
    [JsonPropertyName("references")]
    public int References { get; set; }

    /// <summary>Bytes of the original kept after the rebuild, when the rebuild is shorter.</summary>
    [JsonPropertyName("original_bytes_kept")]
    public int OriginalBytesKept { get; set; }
}
