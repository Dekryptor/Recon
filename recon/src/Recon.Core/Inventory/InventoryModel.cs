using System.Text.Json.Serialization;

namespace Recon.Inventory;

/// <summary>
/// The inventory document. This is the contract the compare engine and reports will build on:
/// field names and meanings follow the project plan (appendix on the schema) and are versioned by
/// <c>schema_version</c>. Addresses are RVAs unless a field says otherwise.
/// </summary>
public sealed class InventoryDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = InventorySchema.Version;

    [JsonPropertyName("generator")]
    public GeneratorInfo Generator { get; set; } = new();

    [JsonPropertyName("project")]
    public ProjectInfo Project { get; set; } = new();

    [JsonPropertyName("binary")]
    public BinaryInfo Binary { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<SectionInfo> Sections { get; set; } = [];

    [JsonPropertyName("imports")]
    public List<ImportInfo> Imports { get; set; } = [];

    [JsonPropertyName("exports")]
    public List<ExportInfo> Exports { get; set; } = [];

    [JsonPropertyName("relocations")]
    public List<RelocationInfo> Relocations { get; set; } = [];

    [JsonPropertyName("functions")]
    public List<FunctionInfo> Functions { get; set; } = [];

    [JsonPropertyName("data")]
    public List<DataInfo> Data { get; set; } = [];

    [JsonPropertyName("jump_tables")]
    public List<JumpTableInfo> JumpTables { get; set; } = [];

    [JsonPropertyName("xrefs")]
    public List<XrefInfo> Xrefs { get; set; } = [];

    [JsonPropertyName("statistics")]
    public Dictionary<string, int> Statistics { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public static class InventorySchema
{
    /// <summary>Version of the JSON contract. Independent of the TOML files' integer schema_version.</summary>
    public const string Version = "0.1";
}

public sealed class GeneratorInfo
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "recon";

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("generated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedAt { get; set; }
}

public sealed class ProjectInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("project_file")]
    public string ProjectFile { get; set; } = string.Empty;
}

public sealed class DebugInfoInfo
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "none";

    /// <summary>Every source that named a symbol; empty when nothing did.</summary>
    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> Sources { get; set; } = [];

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }

    [JsonPropertyName("guid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Guid { get; set; }

    [JsonPropertyName("age")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Age { get; set; }

    [JsonPropertyName("embedded_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmbeddedPath { get; set; }

    /// <summary>True when the debug info matches the binary's CodeView record.</summary>
    [JsonPropertyName("matches_binary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MatchesBinary { get; set; }

    [JsonPropertyName("symbols")]
    public int Symbols { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }

    [JsonPropertyName("compilands")]
    public int Compilands { get; set; }
}

public sealed class ProducerInfo
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Count { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    [JsonPropertyName("matches")]
    public List<string> Matches { get; set; } = [];
}

public sealed class ToolchainSuggestionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "binary";

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

public sealed class BinaryInfo
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "pe32";

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = "x86";

    [JsonPropertyName("isa")]
    public string Isa { get; set; } = "x86";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("image_base")]
    public ulong ImageBase { get; set; }

    [JsonPropertyName("entry_rva")]
    public uint EntryRva { get; set; }

    [JsonPropertyName("size_of_image")]
    public uint SizeOfImage { get; set; }

    [JsonPropertyName("subsystem")]
    public ushort Subsystem { get; set; }

    [JsonPropertyName("dll")]
    public bool Dll { get; set; }

    [JsonPropertyName("linker_version")]
    public string LinkerVersion { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public uint Timestamp { get; set; }

    [JsonPropertyName("has_relocations")]
    public bool HasRelocations { get; set; }

    [JsonPropertyName("tls_callbacks")]
    public List<uint> TlsCallbacks { get; set; } = [];

    [JsonPropertyName("debug")]
    public DebugInfoInfo Debug { get; set; } = new();

    [JsonPropertyName("producers")]
    public List<ProducerInfo> Producers { get; set; } = [];

    [JsonPropertyName("toolchain_suggestions")]
    public List<ToolchainSuggestionInfo> ToolchainSuggestions { get; set; } = [];

    [JsonPropertyName("configured_toolchain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfiguredToolchain { get; set; }

    /// <summary>
    /// The VB5! header of a Visual Basic 5/6 program, present only for one. It is here because it
    /// decides what the program is: when <c>is_pcode</c> is true the code region holds interpreter
    /// tokens, and an x86 decoding of it would be fiction.
    /// </summary>
    [JsonPropertyName("vb6")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Vb6Info? Vb6 { get; set; }

    /// <summary>One line for the human-readable report: which VB made this, and native or p-code.</summary>
    [JsonIgnore]
    public string Vb6Summary =>
        Vb6 is null
            ? string.Empty
            : $"{Vb6.LanguageDll} template=0x{Vb6.TemplateVersion:X} " +
              (Vb6.IsPcode ? "p-code" : $"native=0x{Vb6.NativeCodeRva:X}");
}

/// <summary>The facts read out of a Visual Basic 5/6 header, for programs that have one.</summary>
public sealed class Vb6Info
{
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("header_rva")]
    public uint HeaderRva { get; set; }

    [JsonPropertyName("runtime_build")]
    public ushort RuntimeBuild { get; set; }

    [JsonPropertyName("language_dll")]
    public string LanguageDll { get; set; } = string.Empty;

    [JsonPropertyName("runtime_dll_version")]
    public ushort RuntimeDllVersion { get; set; }

    [JsonPropertyName("language_id")]
    public uint LanguageId { get; set; }

    [JsonPropertyName("template_version")]
    public uint TemplateVersion { get; set; }

    [JsonPropertyName("code_start_rva")]
    public uint CodeStartRva { get; set; }

    [JsonPropertyName("code_end_rva")]
    public uint CodeEndRva { get; set; }

    [JsonPropertyName("exception_handler_rva")]
    public uint ExceptionHandlerRva { get; set; }

    /// <summary>0 when the program is p-code: the field that says so.</summary>
    [JsonPropertyName("native_code_rva")]
    public uint NativeCodeRva { get; set; }

    [JsonPropertyName("is_pcode")]
    public bool IsPcode { get; set; }
}

public sealed class SectionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("virtual_size")]
    public uint VirtualSize { get; set; }

    [JsonPropertyName("raw_size")]
    public uint RawSize { get; set; }

    [JsonPropertyName("raw_offset")]
    public uint RawOffset { get; set; }

    [JsonPropertyName("characteristics")]
    public string Characteristics { get; set; } = string.Empty;

    [JsonPropertyName("flags")]
    public List<string> Flags { get; set; } = [];

    [JsonPropertyName("code")]
    public bool Code { get; set; }
}

public sealed class ImportInfo
{
    [JsonPropertyName("dll")]
    public string Dll { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("ordinal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ushort? Ordinal { get; set; }

    [JsonPropertyName("iat_rva")]
    public uint IatRva { get; set; }
}

public sealed class ExportInfo
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("ordinal")]
    public uint Ordinal { get; set; }

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("forwarder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Forwarder { get; set; }
}

public sealed class RelocationInfo
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "HIGHLOW";

    [JsonPropertyName("width")]
    public byte Width { get; set; } = 4;

    [JsonPropertyName("raw_value")]
    public uint RawValue { get; set; }

    [JsonPropertyName("target_rva")]
    public uint? TargetRva { get; set; }

    [JsonPropertyName("in_function")]
    public string? InFunction { get; set; }

    [JsonPropertyName("in_data")]
    public string? InData { get; set; }
}

public sealed class RangeInfo
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("size")]
    public uint Size { get; set; }
}

public sealed class CallingConventionInfoDto
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "unknown";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];
}

public sealed class ToolchainRefInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    /// <summary>True when the id came from project.toml rather than from detection.</summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }
}

public sealed class FunctionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("demangled")]
    public string? Demangled { get; set; }

    [JsonPropertyName("ranges")]
    public List<RangeInfo> Ranges { get; set; } = [];

    [JsonPropertyName("section")]
    public string Section { get; set; } = string.Empty;

    [JsonPropertyName("isa")]
    public string Isa { get; set; } = "x86";

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("toolchain")]
    public ToolchainRefInfo Toolchain { get; set; } = new();

    [JsonPropertyName("found_by")]
    public List<string> FoundBy { get; set; } = [];

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("calling_convention")]
    public CallingConventionInfoDto CallingConvention { get; set; } = new();

    [JsonPropertyName("flags")]
    public List<string> Flags { get; set; } = [];

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonPropertyName("import_thunk")]
    public string? ImportThunk { get; set; }

    [JsonPropertyName("unknowns")]
    public List<string> Unknowns { get; set; } = [];
}

public sealed class DataInfo
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("size")]
    public uint Size { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "data";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "section";
}

public sealed class JumpTableInfo
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("entries")]
    public int Entries { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "absolute";

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("used_at_rva")]
    public uint UsedAtRva { get; set; }

    [JsonPropertyName("targets")]
    public List<uint> Targets { get; set; } = [];
}

public sealed class XrefInfo
{
    [JsonPropertyName("from_rva")]
    public uint FromRva { get; set; }

    [JsonPropertyName("to_rva")]
    public uint ToRva { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "data_ref";

    [JsonPropertyName("via_reloc")]
    public bool ViaReloc { get; set; }

    [JsonPropertyName("in_function")]
    public string? InFunction { get; set; }

    [JsonPropertyName("to_function")]
    public string? ToFunction { get; set; }
}
