using System.Text.Json.Serialization;
using Recon.Inventory;

namespace Recon.Compare;

/// <summary>
/// The comparison document: what two binaries have in common, function by function, and where they
/// differ. Addresses are RVAs. Everything the comparison could not decide is reported explicitly
/// rather than glossed over: an unmatched function says so, a difference names the operand that
/// changed, and a reference that could not be resolved to a symbol keeps a section-relative
/// identity instead of pretending to be equal to something else.
/// </summary>
public sealed class ComparisonDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "0.1";

    [JsonPropertyName("generator")]
    public GeneratorInfo Generator { get; set; } = new();

    [JsonPropertyName("left")]
    public ComparisonSideInfo Left { get; set; } = new();

    [JsonPropertyName("right")]
    public ComparisonSideInfo Right { get; set; } = new();

    [JsonPropertyName("model")]
    public ComparisonModelInfo Model { get; set; } = new();

    [JsonPropertyName("summary")]
    public ComparisonSummary Summary { get; set; } = new();

    [JsonPropertyName("functions")]
    public List<FunctionComparison> Functions { get; set; } = [];

    [JsonPropertyName("data")]
    public DataComparisonSummary Data { get; set; } = new();

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

/// <summary>One binary of the comparison: what it is, and how much of it was readable.</summary>
public sealed class ComparisonSideInfo
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("project_file")]
    public string? ProjectFile { get; set; }

    [JsonPropertyName("binary")]
    public string Binary { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = "pe32";

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = "x86";

    [JsonPropertyName("image_size")]
    public uint ImageSize { get; set; }

    [JsonPropertyName("entry_rva")]
    public uint EntryRva { get; set; }

    [JsonPropertyName("functions")]
    public int Functions { get; set; }

    [JsonPropertyName("debug_kind")]
    public string DebugKind { get; set; } = "none";

    [JsonPropertyName("toolchain")]
    public string? Toolchain { get; set; }

    /// <summary>Seconds the analysis of this side took. Reported because it is the interesting cost.</summary>
    [JsonPropertyName("analysis_ms")]
    public long AnalysisMs { get; set; }

    /// <summary>
    /// Whether the side's inventory was built for this run or read back from the document the
    /// inventory command had already written. Same document either way; the field says which cost
    /// was paid, so a report cannot hide a slow run behind a fast-looking number.
    /// </summary>
    [JsonPropertyName("inventory_source")]
    public string InventorySource { get; set; } = "built";
}

/// <summary>
/// The abstract relocation model the comparison is built on, with counts. Section 3.1 of the plan
/// asks for one internal form for PE, ELF and Mach-O relocations; these are the classes everything
/// reduces to, and the counts say how much of the comparison actually rested on them.
/// </summary>
public sealed class ComparisonModelInfo
{
    [JsonPropertyName("relocation_classes")]
    public Dictionary<string, int> RelocationClasses { get; set; } = [];

    /// <summary>How many referenced targets could be named, and how many stayed section-relative.</summary>
    [JsonPropertyName("references_named")]
    public int ReferencesNamed { get; set; }

    [JsonPropertyName("references_unnamed")]
    public int ReferencesUnnamed { get; set; }

    /// <summary>
    /// Address operands with no name that are identified by what is *at* them rather than by where
    /// they are: a string literal, a table of constants, a table of addresses. These are the ones the
    /// comparison can pair across two builds that put them in different places, and the count of them
    /// is the measure of how much of a side's own layout the engine had to look past.
    /// </summary>
    [JsonPropertyName("references_identified")]
    public int ReferencesIdentified { get; set; }

    /// <summary>
    /// The container's relocation kinds, each reduced to the abstract class it belongs to. Plan
    /// section 3.1 asks for one internal form for "absolute, relative, GOT/PLT-style, import thunk";
    /// this is where a reader can check that a PE <c>HIGHLOW</c> and an ELF <c>R_386_32</c> were
    /// understood as the same thing.
    /// </summary>
    [JsonPropertyName("relocation_model")]
    public Dictionary<string, string> RelocationModel { get; set; } = [];


    /// <summary>Referenced targets by class: import, jump table, absolute.</summary>
    [JsonPropertyName("reference_classes")]
    public Dictionary<string, int> ReferenceClasses { get; set; } = [];

    /// <summary>Functions paired by symbol name, by identical normalized body, or by similarity.</summary>
    [JsonPropertyName("matched_by")]
    public Dictionary<string, int> MatchedBy { get; set; } = [];

    /// <summary>
    /// Pairs whose bodies were proved identical by the key the pairing already used, so the comparison
    /// did not decode them again. Reported because it is the difference between two runs that cost very
    /// different amounts for the same answer — on the client it is 301 of 304 pairs — and because a
    /// report that did not say would leave a reader unable to tell which of the two they got.
    /// </summary>
    [JsonPropertyName("bodies_proven_identical")]
    public int BodiesProvenIdentical { get; set; }

    [JsonPropertyName("similarity_threshold")]
    public double SimilarityThreshold { get; set; }

    /// <summary>Padding instructions ignored at function boundaries (<c>int3</c>, <c>nop</c>).</summary>
    [JsonPropertyName("ignore_padding")]
    public bool IgnorePadding { get; set; } = true;

    [JsonPropertyName("difference_limit")]
    public int DifferenceLimit { get; set; } = 24;

    /// <summary>The toolchain profile whose compare settings were used, when there was one.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>The engine's settings, never serialized: they are the model's parameters, not output.</summary>
    [JsonIgnore]
    public ComparisonOptions Options { get; set; } = new();
}

/// <summary>The headline numbers: what a progress report would show.</summary>
public sealed class ComparisonSummary
{
    [JsonPropertyName("functions_left")]
    public int FunctionsLeft { get; set; }

    [JsonPropertyName("functions_right")]
    public int FunctionsRight { get; set; }

    [JsonPropertyName("matched")]
    public int Matched { get; set; }

    /// <summary>Matched with every instruction equal after relocation normalization.</summary>
    [JsonPropertyName("exact")]
    public int Exact { get; set; }

    /// <summary>Matched, but some instruction differs.</summary>
    [JsonPropertyName("changed")]
    public int Changed { get; set; }

    /// <summary>
    /// Functions with no counterpart of their own whose body is identical to one that is already
    /// paired: identical-code folding gave two names to one body.
    /// </summary>
    [JsonPropertyName("folded")]
    public int Folded { get; set; }

    [JsonPropertyName("only_left")]
    public int OnlyLeft { get; set; }

    [JsonPropertyName("only_right")]
    public int OnlyRight { get; set; }

    [JsonPropertyName("instructions_left")]
    public int InstructionsLeft { get; set; }

    [JsonPropertyName("instructions_right")]
    public int InstructionsRight { get; set; }

    [JsonPropertyName("instructions_equal")]
    public int InstructionsEqual { get; set; }

    /// <summary>Equal instructions over the larger instruction count: 1.0 is a perfect match.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("data_symbols")]
    public int DataSymbols { get; set; }

    [JsonPropertyName("data_identical")]
    public int DataIdentical { get; set; }

    [JsonPropertyName("data_changed")]
    public int DataChanged { get; set; }
}

/// <summary>One function of the comparison: the pair, how it was paired, and how it differs.</summary>
public sealed class FunctionComparison
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "only_left";

    /// <summary>How the pair was found: <c>name</c>, <c>alias</c>, <c>body</c> or <c>similarity</c>.</summary>
    [JsonPropertyName("match")]
    public string? Match { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("moved")]
    public bool Moved { get; set; }

    [JsonPropertyName("left")]
    public FunctionSideInfo? Left { get; set; }

    [JsonPropertyName("right")]
    public FunctionSideInfo? Right { get; set; }

    [JsonPropertyName("instructions")]
    public InstructionCountInfo Instructions { get; set; } = new();

    [JsonPropertyName("differences")]
    public List<InstructionDifference> Differences { get; set; } = [];

    /// <summary>
    /// The whole aligned body, only when the caller asked for one function by name: every
    /// instruction of both sides in reading order, so a difference can be seen in context.
    /// </summary>
    [JsonPropertyName("aligned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AlignedInstruction>? Aligned { get; set; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
}

/// <summary>The facts about one side of a matched pair, or about an unpaired function.</summary>
public sealed class FunctionSideInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("rva")]
    public uint Rva { get; set; }

    [JsonPropertyName("size")]
    public uint Size { get; set; }

    [JsonPropertyName("section")]
    public string Section { get; set; } = string.Empty;

    [JsonPropertyName("calling_convention")]
    public string? CallingConvention { get; set; }

    [JsonPropertyName("flags")]
    public List<string> Flags { get; set; } = [];
}

public sealed class InstructionCountInfo
{
    [JsonPropertyName("left")]
    public int Left { get; set; }

    [JsonPropertyName("right")]
    public int Right { get; set; }

    [JsonPropertyName("equal")]
    public int Equal { get; set; }

    [JsonPropertyName("relocated")]
    public int Relocated { get; set; }
}

/// <summary>One step of the aligned body of a matched pair.</summary>
public sealed class AlignedInstruction
{
    /// <summary><c>equal</c>, <c>changed</c>, <c>added</c> or <c>removed</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "equal";

    [JsonPropertyName("left_rva")]
    public uint? LeftRva { get; set; }

    [JsonPropertyName("left_text")]
    public string? LeftText { get; set; }

    [JsonPropertyName("right_rva")]
    public uint? RightRva { get; set; }

    [JsonPropertyName("right_text")]
    public string? RightText { get; set; }
}

/// <summary>One place where two matched functions disagree.</summary>
public sealed class InstructionDifference
{
    /// <summary>What kind of difference: opcode, register, immediate, reference, addressing, alignment.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "operand";

    [JsonPropertyName("left_rva")]
    public uint? LeftRva { get; set; }

    [JsonPropertyName("right_rva")]
    public uint? RightRva { get; set; }

    [JsonPropertyName("left_text")]
    public string? LeftText { get; set; }

    [JsonPropertyName("right_text")]
    public string? RightText { get; set; }

    /// <summary>Symbolic identity of the operand, when the difference is about what is referenced.</summary>
    [JsonPropertyName("left_reference")]
    public string? LeftReference { get; set; }

    [JsonPropertyName("right_reference")]
    public string? RightReference { get; set; }
}

/// <summary>
/// Data is compared too, but only by what a reconstruction can act on: the same named symbol, and
/// the same bytes once relocated words are reduced to the symbols they point at.
/// </summary>
public sealed class DataComparisonSummary
{
    [JsonPropertyName("identical")]
    public int Identical { get; set; }

    [JsonPropertyName("changed")]
    public int Changed { get; set; }

    [JsonPropertyName("only_left")]
    public int OnlyLeft { get; set; }

    [JsonPropertyName("only_right")]
    public int OnlyRight { get; set; }

    [JsonPropertyName("symbols")]
    public List<DataComparison> Symbols { get; set; } = [];
}

public sealed class DataComparison
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "identical";

    [JsonPropertyName("left_rva")]
    public uint? LeftRva { get; set; }

    [JsonPropertyName("right_rva")]
    public uint? RightRva { get; set; }

    [JsonPropertyName("left_size")]
    public uint? LeftSize { get; set; }

    [JsonPropertyName("right_size")]
    public uint? RightSize { get; set; }

    /// <summary>Symbolic form of the data: literals kept, relocated words replaced by references.</summary>
    [JsonPropertyName("left_words")]
    public List<string>? LeftWords { get; set; }

    [JsonPropertyName("right_words")]
    public List<string>? RightWords { get; set; }
}
