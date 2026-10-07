using System.Text.Json.Serialization;

namespace Recon.Delink;

/// <summary>Version of the delink contract. Independent of the TOML files' <c>schema_version</c>.</summary>
public static class DelinkSchema
{
    public const string Version = "0.1";
}

/// <summary>What a piece is: why it exists and who is allowed to provide it.</summary>
public static class PieceKinds
{
    public const string Function = "function";

    public const string Data = "data";

    public const string JumpTable = "jump_table";

    /// <summary>
    /// Bytes nobody claims: padding between functions, unlabelled data, the tail of a section.
    /// They are still emitted, because an image that is missing its gaps is not the same image.
    /// </summary>
    public const string Filler = "filler";
}

/// <summary>Where a piece's bytes come from. The plan's per-function provider decision.</summary>
public static class Providers
{
    /// <summary>The original's bytes, kept as they are.</summary>
    public const string Original = "original";

    /// <summary>Provided by a unit's rebuild, which has to fit the space the original used.</summary>
    public const string Rebuilt = "rebuilt";
}

public sealed class DelinkBinary
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("image_base")]
    /// <summary>
    /// The address the image wants to be loaded at, in full. It is 64 bits because an image's base is:
    /// MSVC's own x64 DLLs are based at 0x180000000, and a 32-bit field would keep only 0x80000000 of
    /// that - a wrong base in the plan, in the linker script and on the command line, and a wrong
    /// entry symbol with it.
    /// </summary>
    public ulong ImageBase { get; set; }

    [JsonPropertyName("entry_rva")]
    public uint EntryRva { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = "pe32";

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = "x86";

    [JsonPropertyName("is_dll")]
    public bool IsDll { get; set; }
}

public sealed class DelinkSectionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("virtual_size")]
    public uint VirtualSize { get; set; }

    [JsonPropertyName("raw_size")]
    public uint RawSize { get; set; }

    /// <summary>
    /// Zero-filled bytes this section has in memory beyond the bytes the plan writes back.
    ///
    /// A linker handed bytes cannot know the original had more room than it was given: an MSVC
    /// image's `.data` carries the program's uninitialised data in its virtual size, and without this
    /// the section comes out short and every section after it lands somewhere else. Measured on a real
    /// MSVC x64 DLL: `.data` is 0x1948 bytes in memory and 0xa00 in the file, and the 0xf48 missing
    /// bytes are exactly the tail. It is emitted as uninitialised space, not as zeroes: putting them
    /// in the file would put bytes there the original does not have.
    /// </summary>
    [JsonPropertyName("tail_bytes")]
    public uint TailBytes { get; set; }

    /// <summary>The assembly file this section is emitted into, relative to the delink directory.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>
    /// The name the section is emitted under, which is the original's name when the assembler will
    /// take it: a section called <c>/4</c> — a long debug-section name — has to be renamed.
    /// </summary>
    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; set; }

    [JsonPropertyName("pieces")]
    public int Pieces { get; set; }

    /// <summary>
    /// The assembler section the bytes are written into. It is never the output name: the assembler
    /// writes sections of its own (.debug_line for the file it is assembling, for one), and the
    /// linker drops an input section it cannot place into the output section of the same name —
    /// which would put the assembler's bytes in front of ours.
    /// </summary>
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; set; }

    /// <summary>
    /// The GNU as section flags the bytes are written with: <c>x</c> for code, <c>r</c> or <c>w</c>
    /// for read-only or writable, <c>b</c> for a section with no bytes on disk. Taken from the
    /// original's section characteristics so the relinked image is flagged the way it was.
    /// </summary>
    [JsonPropertyName("flags")]
    public string Flags { get; set; } = string.Empty;

    /// <summary>False for a section the plan records but does not write, such as one the linker provides.</summary>
    [JsonPropertyName("emitted")]
    public bool Emitted { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

/// <summary>
/// One absolute address inside a piece that the linker has to fix up. Emitting it as a symbol
/// reference rather than as four bytes is what lets the same pieces be relinked at another base,
/// and what lets a rebuilt function move the thing it points at.
/// </summary>
public sealed class DelinkFixup
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("width")]
    public byte Width { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "HIGHLOW";

    [JsonPropertyName("raw_value")]
    public uint RawValue { get; set; }

    [JsonPropertyName("target_rva")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetRva { get; set; }

    /// <summary>The symbol the emitted reference names, when the target has one.</summary>
    [JsonPropertyName("target_symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetSymbol { get; set; }

    /// <summary>False when the fixup is narrower or odder than a plain absolute address.</summary>
    [JsonPropertyName("emitted")]
    public bool Emitted { get; set; }
}

/// <summary>
/// A name at an address with no bytes of its own. A linked image's symbol table usually says where a
/// variable is but not how big it is, so the name cannot claim a range — it still has to be written,
/// because a reference to it is a reference to a name, and a name the relinked image does not have is
/// a difference that is not there.
/// </summary>
public sealed class DelinkLabel
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>What the name names: <c>function</c> or <c>data</c>, which is what .type says.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "data";
}

public sealed class DelinkPiece
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = PieceKinds.Filler;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("size")]
    public uint Size { get; set; }

    [JsonPropertyName("section")]
    public string Section { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = Providers.Original;

    /// <summary>The unit that claimed this piece, when one did.</summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    /// <summary>The symbol this piece is emitted under, when it has one.</summary>
    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; set; }

    /// <summary>True when the name is not a plain symbol, so the assembler is told how to spell it.</summary>
    [JsonPropertyName("symbol_renamed")]
    public bool SymbolRenamed { get; set; }

    [JsonPropertyName("fixups")]
    public List<DelinkFixup> Fixups { get; set; } = [];

    /// <summary>Names that belong to addresses inside this piece but claim none of its bytes.</summary>
    [JsonPropertyName("labels")]
    public List<DelinkLabel> Labels { get; set; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
}

public sealed class DelinkSymbolInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "function";

    /// <summary>
    /// True for the symbols the plan invents for addresses nothing names. They carry no meaning and
    /// exist only so a reference has something to point at.
    /// </summary>
    [JsonPropertyName("synthetic")]
    public bool Synthetic { get; set; }
}

public sealed class DelinkCounts
{
    [JsonPropertyName("sections")]
    public int Sections { get; set; }

    [JsonPropertyName("pieces")]
    public int Pieces { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }

    [JsonPropertyName("data")]
    public int Data { get; set; }

    [JsonPropertyName("jump_tables")]
    public int JumpTables { get; set; }

    [JsonPropertyName("fillers")]
    public int Fillers { get; set; }

    [JsonPropertyName("original")]
    public int Original { get; set; }

    [JsonPropertyName("rebuilt")]
    public int Rebuilt { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("fixups")]
    public int Fixups { get; set; }

    /// <summary>Names written as labels because the symbol table says where they are, not how big.</summary>
    [JsonPropertyName("labels")]
    public int Labels { get; set; }

    [JsonPropertyName("fixups_not_emitted")]
    public int FixupsNotEmitted { get; set; }
}

/// <summary>
/// The delinking plan: the original image cut into pieces that a linker can put back at the same
/// addresses, with a provider decision for each one. It is both the input to <c>recon link</c> and
/// the artefact that says what the tool thinks the image is made of.
/// </summary>
public sealed class DelinkDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = DelinkSchema.Version;

    [JsonPropertyName("generator")]
    public Inventory.GeneratorInfo Generator { get; set; } = new();

    [JsonPropertyName("binary")]
    public DelinkBinary Binary { get; set; } = new();

    /// <summary>The symbol the linker is told to start at, which is an address, not a name.</summary>
    [JsonPropertyName("entry_symbol")]
    public string EntrySymbol { get; set; } = "recon_entry";

    [JsonPropertyName("sections")]
    public List<DelinkSectionInfo> Sections { get; set; } = [];

    [JsonPropertyName("pieces")]
    public List<DelinkPiece> Pieces { get; set; } = [];

    [JsonPropertyName("symbols")]
    public List<DelinkSymbolInfo> Symbols { get; set; } = [];

    [JsonPropertyName("counts")]
    public DelinkCounts Counts { get; set; } = new();

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
}
