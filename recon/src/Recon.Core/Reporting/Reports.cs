using System.Text.Json.Serialization;
using Recon.Inventory;

namespace Recon.Reporting;

/// <summary>
/// Machine-readable output for the <c>inspect</c> family. These reuse the inventory DTOs so that
/// what <c>inspect</c> prints is exactly what the inventory contains.
/// </summary>
public sealed class SectionsReport
{
    [JsonPropertyName("sections")]
    public List<SectionInfo> Sections { get; set; } = [];

    [JsonPropertyName("image_base")]
    public ulong ImageBase { get; set; }

    [JsonPropertyName("entry_rva")]
    public uint EntryRva { get; set; }
}

public sealed class ImportsReport
{
    [JsonPropertyName("imports")]
    public List<ImportInfo> Imports { get; set; } = [];

    [JsonPropertyName("dlls")]
    public int Dlls { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }
}

public sealed class ExportsReport
{
    [JsonPropertyName("exports")]
    public List<ExportInfo> Exports { get; set; } = [];
}

public sealed class RelocsReport
{
    [JsonPropertyName("relocations")]
    public List<RelocationInfo> Relocations { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("unresolved")]
    public int Unresolved { get; set; }
}

/// <summary>
/// The data side of the inventory. Symbols come first because they are what a reconstruction has to
/// reproduce; the section ranges are the fallback the loader records so that unknown stays visible
/// rather than empty, and they are kept in a list of their own so a view can leave them out.
/// </summary>
public sealed class DataReport
{
    [JsonPropertyName("symbols")]
    public List<DataInfo> Symbols { get; set; } = [];

    [JsonPropertyName("section_ranges")]
    public List<DataInfo> SectionRanges { get; set; } = [];

    [JsonPropertyName("named")]
    public int Named { get; set; }

    [JsonPropertyName("total_bytes")]
    public ulong TotalBytes { get; set; }
}

public sealed class FunctionsReport
{
    [JsonPropertyName("functions")]
    public List<FunctionInfo> Functions { get; set; } = [];

    [JsonPropertyName("statistics")]
    public Dictionary<string, int> Statistics { get; set; } = [];
}

public sealed class XrefsReport
{
    [JsonPropertyName("xrefs")]
    public List<XrefInfo> Xrefs { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class ProducersReport
{
    [JsonPropertyName("producers")]
    public List<ProducerInfo> Producers { get; set; } = [];

    [JsonPropertyName("suggestions")]
    public List<ToolchainSuggestionInfo> Suggestions { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class DebugReport
{
    [JsonPropertyName("debug")]
    public DebugInfoInfo Debug { get; set; } = new();

    [JsonPropertyName("compilands")]
    public List<CompilandReport> Compilands { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class CompilandReport
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("object_file")]
    public string? ObjectFile { get; set; }

    [JsonPropertyName("producer")]
    public string? Producer { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }
}

public sealed class TlsReport
{
    [JsonPropertyName("callbacks")]
    public List<uint> Callbacks { get; set; } = [];
}

public sealed class StatsReport
{
    [JsonPropertyName("statistics")]
    public Dictionary<string, int> Statistics { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class DisasmReport
{
    [JsonPropertyName("start_rva")]
    public uint StartRva { get; set; }

    [JsonPropertyName("function")]
    public string? Function { get; set; }

    [JsonPropertyName("instructions")]
    public List<DisasmInstruction> Instructions { get; set; } = [];
}

public sealed class DisasmInstruction
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("bytes")]
    public string Bytes { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("xref_target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? XrefTarget { get; set; }

    [JsonPropertyName("annotation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Annotation { get; set; }
}

public sealed class ToolchainListReport
{
    [JsonPropertyName("profiles")]
    public List<ToolchainProfileReport> Profiles { get; set; } = [];
}

public sealed class ToolchainProfileReport
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("abstract")]
    public bool Abstract { get; set; }

    [JsonPropertyName("extends")]
    public string? Extends { get; set; }

    [JsonPropertyName("chain")]
    public List<string> Chain { get; set; } = [];

    [JsonPropertyName("targets")]
    public List<string> Targets { get; set; } = [];

    /// <summary>
    /// For a profile that describes a Visual Basic 5/6 program, which kind it describes: <c>native</c>
    /// for compiled x86, <c>pcode</c> for a stream MSVBVM60.DLL interprets. Null for everything else.
    /// A profile that names one kind is only ever suggested for that kind, which is what makes this
    /// field worth reading rather than the display name.
    /// </summary>
    [JsonPropertyName("code_kind")]
    public string? CodeKind { get; set; }

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SectionsReport))]
[JsonSerializable(typeof(ImportsReport))]
[JsonSerializable(typeof(ExportsReport))]
[JsonSerializable(typeof(RelocsReport))]
[JsonSerializable(typeof(FunctionsReport))]
[JsonSerializable(typeof(DataReport))]
[JsonSerializable(typeof(XrefsReport))]
[JsonSerializable(typeof(ProducersReport))]
[JsonSerializable(typeof(DebugReport))]
[JsonSerializable(typeof(TlsReport))]
[JsonSerializable(typeof(StatsReport))]
[JsonSerializable(typeof(DisasmReport))]
[JsonSerializable(typeof(ToolchainListReport))]
[JsonSerializable(typeof(ToolchainProfileReport))]
[JsonSerializable(typeof(Recon.Verify.VerifyReport))]
[JsonSerializable(typeof(Recon.DoctorReport))]
[JsonSerializable(typeof(Recon.Compare.ComparisonDocument))]
[JsonSerializable(typeof(Recon.Build.BuildManifest))]
[JsonSerializable(typeof(Recon.Reporting.ProgressReport))]
[JsonSerializable(typeof(Recon.Delink.DelinkDocument))]
[JsonSerializable(typeof(Recon.Permute.PermuteDocument))]
[JsonSerializable(typeof(Recon.Reporting.ErrorDocument))]
[JsonSerializable(typeof(Recon.Reporting.InitDocument))]
[JsonSerializable(typeof(Recon.Reporting.MigrateDocument))]
[JsonSerializable(typeof(Recon.Reporting.LinkDocument))]
[JsonSerializable(typeof(Recon.Reporting.LibraryReport))]
[JsonSerializable(typeof(Recon.Reporting.Vb6Report))]
[JsonSerializable(typeof(Recon.Reporting.OpcodesReport))]
[JsonSerializable(typeof(Recon.Reporting.PcodeReport))]
[JsonSerializable(typeof(Recon.Reporting.StringsReport))]
[JsonSerializable(typeof(List<Recon.Compare.AlignedInstruction>))]
[JsonSerializable(typeof(Recon.Reporting.ProgressSnapshot))]
[JsonSerializable(typeof(Recon.Reporting.SignatureBuildReport))]
[JsonSerializable(typeof(Recon.Reporting.SignatureApplyReport))]
public partial class ReportsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// What <c>recon opcodes</c> read out of a Visual Basic runtime: its 256 p-code opcodes, each with
/// the handler the runtime dispatches it to, what that handler calls, and a name when the evidence
/// gives exactly one.
/// </summary>
public sealed class OpcodesReport
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The runtime version the file declares, which is what makes a table attributable.</summary>
    [JsonPropertyName("runtime_version")]
    public string RuntimeVersion { get; set; } = string.Empty;

    [JsonPropertyName("table_rva")]
    public uint TableRva { get; set; }

    [JsonPropertyName("dispatch_sites")]
    public int DispatchSites { get; set; }

    /// <summary>
    /// Every table the runtime dispatches through, the primary one first. A runtime that has lead
    /// bytes has more than one, and each is a table of 256 rows in its own right: a reader that
    /// expects the primary table to describe the whole instruction set reads the lead instructions
    /// wrong, which is what publishing one table used to do.
    /// </summary>
    [JsonPropertyName("tables")]
    public List<OpcodeTableRow> Tables { get; set; } = [];

    [JsonPropertyName("summary")]
    public OpcodesSummary Summary { get; set; } = new();

    [JsonPropertyName("opcodes")]
    public List<OpcodeRow> Opcodes { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

/// <summary>One dispatch table of the runtime, as published: what it is and how far it was read.</summary>
public sealed class OpcodeTableRow
{
    /// <summary>The lead byte that selects this table, or null for the primary table.</summary>
    [JsonPropertyName("lead")]
    public int? Lead { get; set; }

    [JsonPropertyName("table_rva")]
    public uint TableRva { get; set; }

    /// <summary>How many places in the interpreter dispatch through this table.</summary>
    [JsonPropertyName("dispatch_sites")]
    public int DispatchSites { get; set; }

    /// <summary>
    /// The primary opcodes whose handlers dispatch through this table: the evidence for the pairing
    /// of a lead byte with a table, which a reader can check against <c>lead</c>.
    /// </summary>
    [JsonPropertyName("claimed_by")]
    public List<int> ClaimedBy { get; set; } = [];

    [JsonPropertyName("opcodes")]
    public int Opcodes { get; set; }

    /// <summary>Rows of this table measured at a single length.</summary>
    [JsonPropertyName("measured")]
    public int Measured { get; set; }
}

public sealed class OpcodesSummary
{
    [JsonPropertyName("opcodes")]
    public int Opcodes { get; set; }

    /// <summary>Dispatch tables published, the primary table included: one per lead byte plus one.</summary>
    [JsonPropertyName("tables")]
    public int Tables { get; set; }

    /// <summary>Rows published across all of them.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    /// <summary>Slots the interpreter sends to its shared unhandled-case handler.</summary>
    [JsonPropertyName("unhandled")]
    public int Unhandled { get; set; }

    /// <summary>Opcodes given a name because their handler calls exactly one thing that is not shared.</summary>
    [JsonPropertyName("named")]
    public int Named { get; set; }

    /// <summary>Distinct names derived.</summary>
    [JsonPropertyName("distinct_names")]
    public int DistinctNames { get; set; }

    /// <summary>
    /// Calls made by so many opcodes that they cannot be what distinguishes them. Reported because it
    /// is the basis on which a name is derived, and a reader should be able to disagree with it.
    /// </summary>
    /// <summary>How many rows have a single stack effect, and how many can raise.</summary>
    [JsonPropertyName("with_stack_effect")]
    public int WithStackEffect { get; set; }

    [JsonPropertyName("raising")]
    public int Raising { get; set; }

    /// <summary>
    /// The runtime's raiser: the one function the handlers call when they raise, as an RVA — 0x3852C,
    /// or 0x6603852C with the image base this file is loaded at. It is where the <c>raises</c> flag of
    /// every row comes from, so a reader can check the flag rather than trust it.
    /// </summary>
    [JsonPropertyName("raiser_rva")]
    public uint RaiserRva { get; set; }

    [JsonPropertyName("shared_calls")]
    public List<string> SharedCalls { get; set; } = [];

    [JsonPropertyName("handlers_analysed")]
    public int HandlersAnalysed { get; set; }
}

public sealed class OpcodeRow
{
    [JsonPropertyName("opcode")]
    public int Opcode { get; set; }

    /// <summary>
    /// The lead byte that selects the table this row belongs to, or null when the row is the primary
    /// table's — where the opcode is the first byte of the instruction. Where it is set, the opcode is
    /// the byte *after* the lead byte, and the instruction begins with the lead.
    /// </summary>
    [JsonPropertyName("lead")]
    public int? Lead { get; set; }

    /// <summary>The dispatch table this row was read from.</summary>
    [JsonPropertyName("table_rva")]
    public uint TableRva { get; set; }

    [JsonPropertyName("handler_rva")]
    public uint HandlerRva { get; set; }

    [JsonPropertyName("unhandled")]
    public bool Unhandled { get; set; }

    [JsonPropertyName("instruction_size")]
    public int? InstructionSize { get; set; }

    [JsonPropertyName("sizes")]
    public List<int> Sizes { get; set; } = [];

    /// <summary>
    /// Whether the instruction's length is not one number: its handler loops over operand bytes of its
    /// own, so the length is the operand word's. The measured size is then the first pass.
    /// </summary>
    [JsonPropertyName("counted")]
    public bool Counted { get; set; }

    /// <summary>What one pass of that loop consumes, in bytes.</summary>
    [JsonPropertyName("counted_unit")]
    public int CountedUnit { get; set; }

    /// <summary>
    /// What the bytes after the opcode are: <c>none</c> when the instruction has no operand,
    /// <c>data</c> when the handler reads them as a value, <c>target</c> when it reads them as where
    /// control goes. Derived from the handler's own instructions, not from a table of names.
    /// </summary>
    [JsonPropertyName("operand")]
    public string Operand { get; set; } = "none";

    /// <summary>How many bytes of operand the handler reads, or -1 when its length is not one number.</summary>
    [JsonPropertyName("operand_bytes")]
    public int OperandBytes { get; set; } = -1;

    /// <summary>
    /// Where in the operand the word the kind is about sits, in bytes from the first byte after the
    /// opcode. Not always zero: a For/Next instruction takes its counter's frame slot first and the
    /// distance it branches by second.
    /// </summary>
    [JsonPropertyName("operand_at")]
    public int OperandAt { get; set; } = -1;

    /// <summary>
    /// Where in the operand the word of a frame slot sits, or -1 when the handler sign-extends none.
    /// Separate from <c>operand_at</c> because an operand can hold both: a For/Next instruction names
    /// the frame slot of its counter and then the distance it branches by.
    /// </summary>
    [JsonPropertyName("operand_slot_at")]
    public int OperandSlotAt { get; set; } = -1;

    /// <summary>Why the operand is that kind, in words: the instructions that decided it.</summary>
    [JsonPropertyName("operand_basis")]
    public string OperandBasis { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public int Instructions { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("calls")]
    public List<string> Calls { get; set; } = [];

    /// <summary>
    /// What the instruction does to the operand stack, in 4-byte slots: negative for the values it
    /// takes off, positive for the one it leaves. Empty where the handler's code does not fix the
    /// amount, and more than one entry where its paths disagree.
    /// </summary>
    [JsonPropertyName("stack_effects")]
    public List<int> StackEffects { get; set; } = [];

    /// <summary>The single stack effect, or null where there is none or more than one.</summary>
    [JsonPropertyName("stack_effect")]
    public int? StackEffect { get; set; }

    /// <summary>Whether a path through the handler reaches the runtime's error path.</summary>
    [JsonPropertyName("raises")]
    public bool Raises { get; set; }

    /// <summary>
    /// Addresses outside the handler's own code that its paths jump to — the shared code the
    /// interpreter owns. Which of them are error paths is decided across the whole runtime.
    /// </summary>
    [JsonPropertyName("outside_jumps")]
    public List<uint> OutsideJumps { get; set; } = [];

    /// <summary>Why the stack effect is that number, or why there is none.</summary>
    [JsonPropertyName("stack_basis")]
    public string StackBasis { get; set; } = string.Empty;

    /// <summary>
    /// Runtime functions the handler reaches through a helper rather than calling itself: a handler
    /// that calls one routine which is a preface to one export names the opcode just as well, and the
    /// two lists are kept apart so that second-hand evidence is visible as second-hand.
    /// </summary>
    [JsonPropertyName("reached_calls")]
    public List<string> ReachedCalls { get; set; } = [];

    [JsonPropertyName("internal_calls")]
    public int InternalCalls { get; set; }

    [JsonPropertyName("indirect_calls")]
    public int IndirectCalls { get; set; }

    [JsonPropertyName("basis")]
    public string Basis { get; set; } = string.Empty;
}

/// <summary>
/// What <c>recon vb6</c> read out of a Visual Basic 5/6 program: the header, the project data behind
/// it, and the objects the project is made of.
///
/// This is the project's shape rather than its code, because that is what is the same in both
/// compilation modes — and because the method table, which is where they differ, is only filled in by
/// the compiler that needs it. A p-code program is reported as such and its code region left alone.
/// </summary>
public sealed class Vb6Report
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("header")]
    public Vb6HeaderRow Header { get; set; } = new();

    [JsonPropertyName("project")]
    public Vb6ProjectRow Project { get; set; } = new();

    [JsonPropertyName("objects")]
    public List<Vb6ObjectRow> Objects { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class Vb6HeaderRow
{
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("runtime_build")]
    public ushort RuntimeBuild { get; set; }

    [JsonPropertyName("runtime_dll_version")]
    public ushort RuntimeDllVersion { get; set; }

    [JsonPropertyName("language_dll")]
    public string LanguageDll { get; set; } = string.Empty;

    [JsonPropertyName("language_id")]
    public uint LanguageId { get; set; }

    [JsonPropertyName("sub_main_rva")]
    public uint SubMainRva { get; set; }
}

public sealed class Vb6ProjectRow
{
    [JsonPropertyName("template_version")]
    public uint TemplateVersion { get; set; }

    [JsonPropertyName("isa")]
    public string Isa { get; set; } = string.Empty;

    [JsonPropertyName("code_start_rva")]
    public uint CodeStartRva { get; set; }

    [JsonPropertyName("code_end_rva")]
    public uint CodeEndRva { get; set; }

    [JsonPropertyName("code_size")]
    public uint CodeSize { get; set; }

    [JsonPropertyName("data_size")]
    public uint DataSize { get; set; }

    [JsonPropertyName("native_code_rva")]
    public uint NativeCodeRva { get; set; }

    [JsonPropertyName("build_path")]
    public string BuildPath { get; set; } = string.Empty;

    [JsonPropertyName("compile_state")]
    public int CompileState { get; set; }

    [JsonPropertyName("objects_declared")]
    public int ObjectsDeclared { get; set; }

    [JsonPropertyName("objects_reported")]
    public int ObjectsReported { get; set; }

    /// <summary>
    /// Objects the table says were compiled. Not always equal to the number it declares — one real
    /// program says 76 against 73 — so it is reported as the file's own number.
    /// </summary>
    [JsonPropertyName("objects_compiled")]
    public int ObjectsCompiled { get; set; }

    /// <summary>What the tool can do with this program's code, said rather than implied.</summary>
    [JsonPropertyName("code_note")]
    public string CodeNote { get; set; } = string.Empty;
}

public sealed class Vb6ObjectRow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("type_flags")]
    public uint TypeFlags { get; set; }

    [JsonPropertyName("methods")]
    public int Methods { get; set; }

    [JsonPropertyName("descriptor_rva")]
    public uint DescriptorRva { get; set; }
}

/// <summary>
/// What <c>recon lib</c> read out of a COFF archive. An archive is not a program — no entry point,
/// no sections to decode — so this is a list of members and what each one is, rather than an
/// inventory. The interesting field is <c>comp_tool</c>: the compiler that built each object, read
/// from the <c>@comp.id</c> symbol that every Microsoft object carries.
/// </summary>
public sealed class LibraryReport
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("members")]
    public List<LibraryMemberRow> Members { get; set; } = [];

    [JsonPropertyName("summary")]
    public LibrarySummary Summary { get; set; } = new();

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class LibraryMemberRow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("raw_name")]
    public string RawName { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public long Offset { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("date")]
    public long Date { get; set; }

    [JsonPropertyName("machine")]
    public string? Machine { get; set; }

    [JsonPropertyName("timestamp")]
    public uint? Timestamp { get; set; }

    [JsonPropertyName("comp_id")]
    public uint? CompId { get; set; }

    [JsonPropertyName("comp_tool")]
    public string? CompTool { get; set; }

    [JsonPropertyName("comp_build")]
    public int? CompBuild { get; set; }

    [JsonPropertyName("sections")]
    public List<string> Sections { get; set; } = [];

    [JsonPropertyName("symbols")]
    public List<string> Symbols { get; set; } = [];

    [JsonPropertyName("import_dll")]
    public string? ImportDll { get; set; }

    [JsonPropertyName("import_symbol")]
    public string? ImportSymbol { get; set; }

    [JsonPropertyName("import_ordinal")]
    public int? ImportOrdinal { get; set; }

    [JsonPropertyName("describe")]
    public string Describe { get; set; } = string.Empty;
}

public sealed class LibrarySummary
{
    [JsonPropertyName("members")]
    public int Members { get; set; }

    [JsonPropertyName("objects")]
    public int Objects { get; set; }

    [JsonPropertyName("imports")]
    public int Imports { get; set; }

    [JsonPropertyName("compilers")]
    public List<string> Compilers { get; set; } = [];

    [JsonPropertyName("imported_dlls")]
    public List<string> ImportedDlls { get; set; } = [];
}

/// <summary>What <c>recon sigs build</c> produced: how many patterns, from how many functions.</summary>
public sealed class SignatureBuildReport
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("patterns")]
    public int Patterns { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }

    [JsonPropertyName("library")]
    public string? Library { get; set; }

    /// <summary>What was dropped and why: patterns too short, too wild, or shared by two functions.</summary>
    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

/// <summary>What <c>recon sigs apply</c> named: the functions a pattern file recognized.</summary>
public sealed class SignatureApplyReport
{
    [JsonPropertyName("signatures")]
    public int Signatures { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }

    [JsonPropertyName("named")]
    public int Named { get; set; }

    [JsonPropertyName("matches")]
    public List<SignatureMatchInfo> Matches { get; set; } = [];
}

/// <summary>One function named from a pattern, with the library the pattern belongs to.</summary>
public sealed class SignatureMatchInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("size")]
    public uint Size { get; set; }

    [JsonPropertyName("library")]
    public string? Library { get; set; }
}

public static class Reports
{
    public static string Serialize<T>(T value)
        => System.Text.Json.JsonSerializer.Serialize(value, typeof(T), ReportsJsonContext.Default) as string ?? string.Empty;

    /// <summary>
    /// One object on one line. The same documents, written for a file that is read line by line:
    /// the history is appended to, so it cannot be pretty-printed, or every entry would be spread
    /// over twenty lines and none of them would parse.
    /// </summary>
    public static string SerializeLine<T>(T value)
        => System.Text.Json.JsonSerializer.Serialize(value, CompactTypeInfo<T>());

    /// <summary>
    /// The same source-generated metadata as <see cref="Serialize{T}"/>, asked for without the
    /// indentation: one object per line. Building the options from the context rather than from a
    /// reflection call is what lets this run under NativeAOT, where there is no runtime code
    /// generation to fall back on.
    /// </summary>
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> CompactTypeInfo<T>()
        => (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Compact.GetTypeInfo(typeof(T));

    private static readonly System.Text.Json.JsonSerializerOptions Compact = new()
    {
        TypeInfoResolver = ReportsJsonContext.Default,
        WriteIndented = false,
    };
}

/// <summary>
/// What <c>recon pcode</c> read out of a Visual Basic 6 program compiled to p-code: every procedure,
/// where its byte stream is, how long it is, and — for the one asked about — the instructions in it.
/// </summary>
public sealed class PcodeReport
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The runtime the instruction lengths were measured from.</summary>
    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = string.Empty;

    [JsonPropertyName("runtime_version")]
    public string RuntimeVersion { get; set; } = string.Empty;

    /// <summary>
    /// The runtime build the program itself was compiled against, from the <c>wRuntimeBuild</c> word
    /// in its own <c>VB5!</c> header. It is a second, independent statement about the interpreter
    /// beside the file's own version resource: the lengths come from the runtime on this machine, and
    /// these two numbers say whether that runtime is the one the program expects.
    /// </summary>
    [JsonPropertyName("runtime_build_the_program_was_built_against")]
    public ushort ProgramRuntimeBuild { get; set; }

    /// <summary>
    /// The build the runtime file itself reports, from its <c>FileVersion</c> (<c>6.00.9848</c> says
    /// 9848), or null when that file's version resource does not carry three numeric fields.
    /// </summary>
    [JsonPropertyName("runtime_file_build")]
    public int? RuntimeFileBuild { get; set; }

    /// <summary>
    /// Whether the runtime the lengths were measured from is the build the program was built against,
    /// or null when either number is unknown. A p-code program names the runtime build it expects in
    /// its own header, so a reader that has both numbers can say whether they are the same one.
    /// </summary>
    [JsonIgnore]
    public bool? SameRuntimeBuild => RuntimeFileBuild is { } file && ProgramRuntimeBuild != 0
        ? ProgramRuntimeBuild == file
        : null;

    /// <summary>
    /// The two runtime builds as one clause for the line that names the runtime, or empty when the
    /// file's version resource does not say. A program built against a runtime other than the one in
    /// front of us is ordinary — the runtime is a service-packed DLL that is updated on its own
    /// schedule — so this is stated rather than warned about: whether the decode is right is what the
    /// counts above the line say.
    /// </summary>
    public string RuntimeBuildNote() => RuntimeFileBuild is null
        ? string.Empty
        : SameRuntimeBuild == true
            ? $"; the program was built against runtime build {ProgramRuntimeBuild}, the same one"
            : $"; the program was built against runtime build {ProgramRuntimeBuild}";

    [JsonPropertyName("summary")]
    public PcodeSummary Summary { get; set; } = new();

    [JsonPropertyName("procedures")]
    public List<PcodeProcedureRow> Procedures { get; set; } = [];

    /// <summary>The instructions of the selected procedure, when one was selected.</summary>
    [JsonPropertyName("instructions")]
    public List<PcodeInstructionRow> Instructions { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class PcodeSummary
{
    [JsonPropertyName("objects")]
    public int Objects { get; set; }

    [JsonPropertyName("procedures")]
    public int Procedures { get; set; }

    /// <summary>Streams that land exactly on their descriptor.</summary>
    [JsonPropertyName("exact")]
    public int Exact { get; set; }

    /// <summary>Streams with one to four bytes of padding before the descriptor.</summary>
    [JsonPropertyName("padded")]
    public int Padded { get; set; }

    /// <summary>Streams that could not be followed, by reason.</summary>
    [JsonPropertyName("undecodable")]
    public Dictionary<string, int> Undecodable { get; set; } = [];

    /// <summary>Procedures whose last instruction is ExitProcHresult, which is how one ends.</summary>
    [JsonPropertyName("ended_with_exit")]
    public int EndedWithExit { get; set; }

    [JsonPropertyName("instructions")]
    public int Instructions { get; set; }

    [JsonPropertyName("code_bytes")]
    public int CodeBytes { get; set; }

    /// <summary>Method table slots with no address: procedures the project declares and never writes.</summary>
    [JsonPropertyName("empty_slots")]
    public int EmptySlots { get; set; }

    /// <summary>Slots whose entry does not name this object, so it is not one of its procedures.</summary>
    [JsonPropertyName("entries_not_procedures")]
    public int EntriesNotProcedures { get; set; }

    /// <summary>Streams admitting more than one reading, which their descriptor alone does not settle.</summary>
    [JsonPropertyName("resolved_readings")]
    public int ResolvedReadings { get; set; }
}

public sealed class PcodeProcedureRow
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("object_index")]
    public int ObjectIndex { get; set; }

    [JsonPropertyName("method")]
    public int Method { get; set; }

    [JsonPropertyName("descriptor_rva")]
    public uint DescriptorRva { get; set; }

    [JsonPropertyName("code_rva")]
    public uint CodeRva { get; set; }

    [JsonPropertyName("code_size")]
    public int CodeSize { get; set; }

    [JsonPropertyName("frame_size")]
    public int FrameSize { get; set; }

    [JsonPropertyName("argument_size")]
    public int ArgumentSize { get; set; }

    [JsonPropertyName("instructions")]
    public int Instructions { get; set; }

    [JsonPropertyName("padding")]
    public int Padding { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("ends_with_exit")]
    public bool EndsWithExit { get; set; }

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class PcodeInstructionRow
{
    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("opcode")]
    public string Opcode { get; set; } = string.Empty;

    /// <summary>The instruction's bytes, in hex — what is in the stream, not what was made of it.</summary>
    [JsonPropertyName("bytes")]
    public string Bytes { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>
    /// What the bytes after the opcode are, as the runtime's handler reads them: <c>none</c>,
    /// <c>data</c>, <c>slot</c> or <c>target</c>. The same vocabulary as <c>recon opcodes</c>, from the
    /// same measurement, so the two reports cannot disagree about an opcode.
    /// </summary>
    [JsonPropertyName("operand")]
    public string Operand { get; set; } = "none";

    /// <summary>How many bytes of operand the instruction has, or -1 when its length is not one number.</summary>
    [JsonPropertyName("operand_bytes")]
    public int OperandBytes { get; set; } = -1;

    /// <summary>Where in the operand the word the kind is about sits. Not always zero.</summary>
    [JsonPropertyName("operand_at")]
    public int OperandAt { get; set; }

    /// <summary>Where a target operand goes, as an rva of the program.</summary>
    [JsonPropertyName("target")]
    public uint? Target { get; set; }

    /// <summary>
    /// Whether that target lands on an instruction start of the same procedure. Every branch operand of
    /// the 42-program corpus does; a false is a target this reading has no instruction at.
    /// </summary>
    [JsonPropertyName("target_is_an_instruction_start")]
    public bool TargetIsAnInstructionStart { get; set; }

    /// <summary>
    /// The frame slot the operand names, sign-extended: for a <c>slot</c> operand the word itself, and
    /// for a <c>target</c> operand the slot its <c>frame_slot_at</c> word names, which is the counter
    /// of a For/Next.
    /// </summary>
    [JsonPropertyName("frame_slot")]
    public int? FrameSlot { get; set; }

    /// <summary>
    /// Where in the operand that slot word sits, in bytes from the first byte after the opcode, or -1
    /// when the handler sign-extends none.
    /// </summary>
    [JsonPropertyName("frame_slot_at")]
    public int FrameSlotAt { get; set; } = -1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("unhandled")]
    public bool Unhandled { get; set; }
}

/// <summary>
/// What <c>recon strings</c> found in a binary. A reversing session usually starts here, and the
/// fields are the three questions asked of a hit: where is it (<c>offset</c> for a hex editor,
/// <c>rva</c> for a disassembler), what section is it in, and did anything point at it — which is
/// what <c>recon inspect xrefs</c> answers for the address this reports.
/// </summary>
public sealed class StringsReport
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>The runs, in the order they lie in the file.</summary>
    [JsonPropertyName("strings")]
    public List<StringRow> Strings { get; set; } = [];

    [JsonPropertyName("summary")]
    public StringsSummary Summary { get; set; } = new();

    /// <summary>Anything that stopped the reading from being complete, said rather than swallowed.</summary>
    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class StringRow
{
    [JsonPropertyName("offset")]
    public long Offset { get; set; }

    [JsonPropertyName("rva")]
    public uint? Rva { get; set; }

    [JsonPropertyName("section")]
    public string Section { get; set; } = string.Empty;

    /// <summary><c>ascii</c> or <c>utf16</c>: one byte per character, or two.</summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = string.Empty;

    /// <summary>Characters, which is what a human counts.</summary>
    [JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary>Bytes in the file, which is what an offset needs.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class StringsSummary
{
    /// <summary>Runs reported, after any filter or limit.</summary>
    [JsonPropertyName("reported")]
    public int Reported { get; set; }

    /// <summary>Runs found before the limit, so a truncated report says so.</summary>
    [JsonPropertyName("found")]
    public int Found { get; set; }

    [JsonPropertyName("ascii")]
    public int Ascii { get; set; }

    [JsonPropertyName("utf16")]
    public int Utf16 { get; set; }

    /// <summary>Distinct texts among the runs found: how much of it is repetition.</summary>
    [JsonPropertyName("distinct")]
    public int Distinct { get; set; }

    [JsonPropertyName("bytes_scanned")]
    public long BytesScanned { get; set; }

    [JsonPropertyName("min_length")]
    public int MinLength { get; set; }

    /// <summary>The sections the scan covered, in file order; empty means the whole file.</summary>
    [JsonPropertyName("sections_scanned")]
    public List<string> SectionsScanned { get; set; } = [];
}
