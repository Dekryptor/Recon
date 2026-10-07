using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Recon.Inventory;

/// <summary>
/// Source-generated serialization: the tool is published with NativeAOT, so reflection-based
/// serialization is not available. Every machine-readable command goes through this context.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(InventoryDocument))]
public partial class InventoryJsonContext : JsonSerializerContext
{
}

public static class InventoryJson
{
    /// <summary>
    /// Serializes through the generated JsonTypeInfo rather than the reflection overload: the tool
    /// ships as a NativeAOT binary, and the reflection path would be trimmed away.
    /// </summary>
    public static string Serialize(InventoryDocument document)
    {
        JsonTypeInfo<InventoryDocument> info = InventoryJsonContext.Default.InventoryDocument;
        return JsonSerializer.Serialize(document, info);
    }

    public static InventoryDocument? Deserialize(string json)
        => JsonSerializer.Deserialize(json, InventoryJsonContext.Default.InventoryDocument);

    /// <summary>
    /// The same, from UTF-8 bytes. A reader that has the document as bytes — the cache, which trims a
    /// property off it before this point — does not have to decode it to a string first and encode it
    /// again: on a 71.7 MB inventory that round trip is 143 MB of allocations for nothing.
    /// </summary>
    public static InventoryDocument? Deserialize(ReadOnlySpan<byte> utf8)
        => JsonSerializer.Deserialize(utf8, InventoryJsonContext.Default.InventoryDocument);
}
