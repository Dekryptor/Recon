using System.Text.Json;

namespace Recon.Inventory;

/// <summary>
/// Reading back an inventory that is already on disk, for the commands that need a part of it.
///
/// `recon disasm` is the reason this exists. Disassembling one function needs three things from an
/// inventory — where the functions are, where the imports are, and what the data is called — and it used
/// to answer them by building the whole document, cross-references and all. On an 11.8 MB Visual Basic
/// client that is 18 seconds to print four instructions, and the listing it printed was the same either
/// way. A reversing session is a build once and then dozens of `disasm` calls, so the calls read the
/// document that is already there.
///
/// What is *not* done here is trusting it. The cache is used only when it says it was written by this
/// build of this tool about this exact binary, because a name from an inventory of a different file is
/// exactly the plausible-looking wrong answer this tool exists to replace. Otherwise the caller builds
/// the inventory as it always did.
///
/// Nothing else in the document is parsed: the reader walks the top level and skips everything it was
/// not asked for, so the cost is a pass over the bytes rather than an object per value.
/// </summary>
public static class InventoryCache
{
    /// <summary>One function as the listing needs it: where it starts, how big it is, what it is called.</summary>
    public sealed record CachedFunction(uint Start, uint Size, string? Name);

    /// <summary>A name for an address: an import slot or a piece of data.</summary>
    public sealed record CachedName(uint Rva, string Name);

    public sealed class Contents
    {
        public List<CachedFunction> Functions { get; } = [];

        public List<CachedName> Imports { get; } = [];

        public List<CachedName> Data { get; } = [];
    }

    /// <summary>One buffer for the whole read: an inventory is one document, and a window per property is not needed.</summary>
    private const int ReadBuffer = 1 << 20;

    /// <summary>
    /// The parts of the inventory at <paramref name="path"/> that a listing uses, or null when the file
    /// is not there, is not readable, or was not written by this tool about <paramref name="sha256"/>.
    /// </summary>
    /// <param name="toolVersion">The version the file has to have been written by.</param>
    public static Contents? Read(string path, string? sha256, string toolVersion)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var reader = new Utf8JsonReader(ReadAll(stream), new JsonReaderOptions { AllowTrailingCommas = false });
            return Parse(ref reader, sha256, toolVersion);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A cache that cannot be read is not an error: the caller builds the real thing.
            return null;
        }
    }

    /// <summary>
    /// The whole inventory of one binary, read back from whichever document in
    /// <paramref name="directory"/> says it is this build's reading of it.
    ///
    /// This is <see cref="Read"/>'s rule applied to the whole document instead of to the three parts of
    /// it a listing needs, and to a directory instead of a file, because a project's build directory is
    /// where `recon inventory` puts its answer and the name it was given is the caller's business — a
    /// project with two inputs has two answers, and which one is read has to be decided by what the
    /// document says it is, never by which file happens to be first.
    ///
    /// The comparison is what this is for. `recon diff` builds an inventory for each side, which on an
    /// 11.8 MB program is 17 seconds a side, and if `recon inventory` has been run for that side the
    /// answer is already on disk. The comparison that comes out is the same one — which is checked by
    /// running both routes over the same pair and comparing the reports, not by assuming it.
    ///
    /// A document that is not about this binary, was not written by this build, or is older than the
    /// binary it claims to describe is skipped rather than used: a stale reading is exactly the
    /// plausible-looking wrong answer this tool exists to replace. Nothing here throws — a build
    /// directory that cannot be read is not an error, it is a build.
    /// </summary>
    /// <param name="directory">Where to look: a project's build directory.</param>
    /// <param name="sha256">The binary the document has to be about.</param>
    /// <param name="toolVersion">The version the document has to have been written by.</param>
    /// <param name="notOlderThan">The binary's timestamp: a document older than it is a reading of the previous build.</param>
    public static InventoryDocument? DocumentIn(string directory, string? sha256, string toolVersion, DateTime? notOlderThan = null)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // Sorted so that a directory holding two documents about the same binary answers the same way
        // twice: which of them is read must not depend on the order the file system hands them over.
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (notOlderThan is { } binary && File.GetLastWriteTimeUtc(path) < binary)
            {
                continue;
            }

            try
            {
                // Reading it twice is deliberate: the first pass settles whether this is the document
                // for this binary and stops as soon as it knows, and only the one that passes is parsed
                // into an inventory. build/ holds the comparison and the report as well, and neither is
                // an inventory of anything.
                if (SelfDescriptionAt(path) is not { } header
                    || header.Tool != "recon"
                    || header.Version != toolVersion
                    || header.SchemaVersion != InventorySchema.Version
                    || !header.HasFunctions
                    || string.IsNullOrEmpty(sha256)
                    || !string.Equals(header.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (InventoryJson.Deserialize(WithoutComparisonDoesNotRead(File.ReadAllBytes(path))) is { } document)
                {
                    return document;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Not a document this can use. The next candidate, or the real thing, is the answer.
                continue;
            }
        }

        return null;
    }

    /// <summary>
    /// The top-level properties a comparison never reads, dropped before the document is turned into
    /// objects.
    ///
    /// `xrefs` is the whole reason this exists. On an 11.8 MB Visual Basic 6 client an inventory is
    /// **71.7 MB, and 49.9 MB of it is the cross-reference list** — every place one function reads
    /// another, which is what a reversing session wants and what a comparison is not about: a
    /// comparison pairs functions by their bodies, compares operands as what they point at, and never
    /// asks who references whom. Reading two documents with their cross-references in them was enough
    /// to have a comparison killed by the kernel on this 2 GB machine, where the same comparison that
    /// built its inventories instead finished — which is how the cost was found.
    ///
    /// The trimming is a token-level copy and not a parse: the reader walks the top level, skips the
    /// values it does not want, and hands the rest through as raw bytes, so nothing is turned into
    /// objects on the way. <see cref="InventoryJson.Deserialize"/> itself is untouched — a document
    /// read for a listing, or by a test, still has its cross-references.
    /// </summary>
    private static byte[] WithoutComparisonDoesNotRead(byte[] json)
    {
        using var output = new MemoryStream(json.Length / 2);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = false });
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                string name = reader.GetString() ?? string.Empty;
                reader.Read();
                long start = reader.TokenStartIndex;
                reader.Skip();
                if (name == "xrefs")
                {
                    continue;
                }

                writer.WritePropertyName(name);
                writer.WriteRawValue(json.AsSpan((int)start, (int)(reader.BytesConsumed - start)), skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    /// <summary>
    /// What a document says about itself, before any of it is turned into objects: who wrote it, which
    /// binary it is about, which contract it claims, and whether it has the shape of an inventory at all.
    ///
    /// The last two are not decoration. A project's build directory holds more than one JSON document —
    /// the comparison, the report, the manifest — and a comparison carries a generator and a version
    /// too, so "tool, version and hash match" could accept one of those, deserialize it into an
    /// inventory object with nothing in it, and compare two empty sides. A document that does not
    /// declare the inventory schema version and does not carry a `functions` array is not an inventory;
    /// as with everything else in this file, the answer is then to build the real thing.
    /// </summary>
    private sealed record SelfDescription(string? Tool, string? Version, string? Sha256, string? SchemaVersion, bool HasFunctions);

    /// <summary>
    /// The generator and the binary of the document at <paramref name="path"/>, walking the top level and
    /// skipping everything else — which is the whole document, so this is a pass over the bytes and not an
    /// object per value.
    /// </summary>
    private static SelfDescription? SelfDescriptionAt(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var reader = new Utf8JsonReader(ReadAll(stream), new JsonReaderOptions { AllowTrailingCommas = false });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        string? tool = null;
        string? version = null;
        string? sha = null;
        string? schema = null;
        bool functions = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("generator"))
            {
                ReadGenerator(ref reader, ref tool, ref version);
            }
            else if (reader.ValueTextEquals("binary"))
            {
                ReadBinary(ref reader, ref sha);
            }
            else if (reader.ValueTextEquals("schema_version") && reader.Read())
            {
                schema = reader.GetString();
            }
            else if (reader.ValueTextEquals("functions"))
            {
                functions = true;
                reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        return new SelfDescription(tool, version, sha, schema, functions);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream(stream.Length <= int.MaxValue ? (int)stream.Length : 0);
        stream.CopyTo(buffer, ReadBuffer);
        return buffer.ToArray();
    }

    private static Contents? Parse(ref Utf8JsonReader reader, string? sha256, string toolVersion)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var contents = new Contents();
        string? fileVersion = null;
        string? fileTool = null;
        string? fileSha = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("generator"))
            {
                ReadGenerator(ref reader, ref fileTool, ref fileVersion);
            }
            else if (reader.ValueTextEquals("binary"))
            {
                ReadBinary(ref reader, ref fileSha);
            }
            else if (reader.ValueTextEquals("functions"))
            {
                ReadFunctions(ref reader, contents.Functions);
            }
            else if (reader.ValueTextEquals("imports"))
            {
                ReadNamed(ref reader, contents.Imports, "iat_rva");
            }
            else if (reader.ValueTextEquals("data"))
            {
                ReadNamed(ref reader, contents.Data, "rva");
            }
            else
            {
                reader.Skip();
            }
        }

        // The three checks that make this a reading of this binary rather than of some other one.
        if (fileTool != "recon" || fileVersion != toolVersion)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(sha256) && fileSha is not null && !string.Equals(fileSha, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return contents;
    }

    private static void ReadGenerator(ref Utf8JsonReader reader, ref string? tool, ref string? version)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("tool") && reader.Read())
            {
                tool = reader.GetString();
            }
            else if (reader.ValueTextEquals("version") && reader.Read())
            {
                version = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadBinary(ref Utf8JsonReader reader, ref string? sha)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("sha256") && reader.Read())
            {
                sha = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadFunctions(ref Utf8JsonReader reader, List<CachedFunction> into)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                continue;
            }

            string? name = null;
            uint start = 0;
            uint size = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("name") && reader.Read())
                {
                    name = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                }
                else if (reader.ValueTextEquals("ranges") && reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    // The first range is the function: the later ones are folded pieces of the same
                    // function, and a listing asks where a function starts and how far it runs.
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName)
                            {
                                continue;
                            }

                            if (reader.ValueTextEquals("rva") && reader.Read())
                            {
                                start = (uint)reader.GetInt64();
                            }
                            else if (reader.ValueTextEquals("size") && reader.Read())
                            {
                                size = (uint)reader.GetInt64();
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            into.Add(new CachedFunction(start, size, name));
        }
    }

    /// <summary>
    /// Names by address, for the two arrays that carry them: `imports` (named by `iat_rva`, described by
    /// `dll` and `name`/`ordinal`) and `data` (named by `rva`, described by `name` and `kind`).
    /// </summary>
    private static void ReadNamed(ref Utf8JsonReader reader, List<CachedName> into, string addressKey)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                continue;
            }

            uint address = 0;
            string? name = null;
            string? dll = null;
            string? kind = null;
            int? ordinal = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals(addressKey) && reader.Read())
                {
                    address = (uint)reader.GetInt64();
                }
                else if (reader.ValueTextEquals("name") && reader.Read())
                {
                    name = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                }
                else if (reader.ValueTextEquals("dll") && reader.Read())
                {
                    dll = reader.GetString();
                }
                else if (reader.ValueTextEquals("kind") && reader.Read())
                {
                    kind = reader.GetString();
                }
                else if (reader.ValueTextEquals("ordinal") && reader.Read())
                {
                    ordinal = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32();
                }
                else
                {
                    reader.Skip();
                }
            }

            string? shown = dll is not null
                ? $"{dll}!{name ?? "#" + ordinal?.ToString()}"
                : name ?? kind;
            if (address != 0 && shown is not null)
            {
                into.Add(new CachedName(address, shown));
            }
        }
    }
}
