using System.Text.Json.Serialization;

namespace Recon.Permute;

/// <summary>One candidate source: the original text with a single semantics-preserving edit.</summary>
public sealed class SourceVariant
{
    /// <summary>A stable handle: the kind of edit and where it was made, never a hash of the text.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>How the edit was produced: <c>statement-swap</c>, <c>operand-swap</c>, and so on.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>One line a human can read to decide whether to adopt this variant.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The whole source file with the edit applied.</summary>
    [JsonIgnore]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The optimization level this candidate is built at, when the candidate is a change to the build
    /// rather than to the source. A flag candidate's text is the source unchanged: what it changes is
    /// how that source is compiled, so there is nothing to adopt but the flag.
    /// </summary>
    [JsonIgnore]
    public string? Optimization { get; set; }

    public int Line { get; set; }
}

/// <summary>
/// What one variant scored. A permuter is only useful if it says how close a variant got, so the
/// score is the compare engine's own — the same number <c>recon diff</c> prints for that function —
/// and <see cref="Exact"/> is byte equality after relocation normalization, which is the only result
/// that means "this source reproduces the original".
/// </summary>
public sealed class VariantResult
{
    [JsonPropertyName("variant_id")]
    public string VariantId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The function that was scored, as the original binary spells it.</summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    /// <summary>0..1, the compare engine's similarity for this function.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>True when the rebuilt bytes are the original's bytes, relocations aside.</summary>
    [JsonPropertyName("exact")]
    public bool Exact { get; set; }

    /// <summary>The compare engine's status for the pair: <c>match</c>, <c>changed</c>, <c>only_*</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The optimization level this variant was built at, when the variant is a change to the build
    /// rather than to the source. Null for a variant that edits the text.
    /// </summary>
    [JsonPropertyName("optimization")]
    public string? Optimization { get; set; }

    /// <summary>The build failed for this variant; it is reported, never silently dropped.</summary>
    [JsonPropertyName("build_error")]
    public string? BuildError { get; set; }

    /// <summary>How many instruction-level differences the compare engine found.</summary>
    [JsonPropertyName("differences")]
    public int Differences { get; set; }

    [JsonPropertyName("built")]
    public bool Built => BuildError is null;
}

/// <summary>
/// The result of a permutation run. Everything the run did is here — what was tried, what it scored,
/// and what was left untried — because a search that reports only its winner cannot be checked.
/// </summary>
public sealed class PermuteDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "0.1";

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    /// <summary>The score of the unit's source as it stands, so a variant is only interesting if it beats it.</summary>
    [JsonPropertyName("baseline_score")]
    public double BaselineScore { get; set; }

    [JsonPropertyName("baseline_exact")]
    public bool BaselineExact { get; set; }

    [JsonPropertyName("variants")]
    public List<VariantResult> Variants { get; set; } = [];

    [JsonPropertyName("candidate_count")]
    public int CandidateCount { get; set; }

    [JsonPropertyName("tried")]
    public int Tried { get; set; }

    /// <summary>Why the run stopped: the budget, an exact match, or running out of candidates.</summary>
    [JsonPropertyName("stopped_because")]
    public string StoppedBecause { get; set; } = string.Empty;

    /// <summary>
    /// The optimization levels this unit's toolchain offers, spelled the way its compiler spells
    /// them, when <c>--flags</c> asked for them to be tried. Empty when flags were not permuted, or
    /// when the profile declares none — a toolchain with no such flag is a fact, not an omission.
    /// </summary>
    [JsonPropertyName("optimization_levels")]
    public List<string> OptimizationLevels { get; set; } = [];

    /// <summary>
    /// The level the winning variant was built at, when what won was a change to the build rather
    /// than to the source. An agent that reads this knows what to put in the project's flags.
    /// </summary>
    [JsonPropertyName("best_optimization")]
    public string? BestOptimization => Best?.Optimization;

    /// <summary>The best-scoring variant, or null when nothing could be built.</summary>
    [JsonPropertyName("best")]
    public VariantResult? Best => Variants
        .Where(v => v.Built)
        .OrderByDescending(v => v.Score)
        .ThenByDescending(v => v.Exact)
        .ThenBy(v => v.VariantId, StringComparer.Ordinal)
        .FirstOrDefault();

    /// <summary>
    /// Whether the best variant scores above the source as it stands. Read this before
    /// <see cref="Best"/>: when the baseline is already exact, a variant that also scores 1.0 has
    /// found nothing — it merely wrote the function a different way.
    /// </summary>
    /// <summary>
    /// Where the winning source was written for a human to adopt, or null when nothing was: either
    /// nothing beat the baseline, or it could not be written. An agent reads this instead of
    /// guessing where the file went.
    /// </summary>
    [JsonPropertyName("best_source")]
    public string? BestSource { get; set; }

    /// <summary>Why the winning source was not written, when it was not. Never fatal.</summary>
    [JsonPropertyName("best_write_error")]
    public string? BestWriteError { get; set; }

    [JsonPropertyName("best_beats_baseline")]
    public bool BestBeatsBaseline => Best is { } best && best.Score > BaselineScore + 1e-9;
}

/// <summary>What a permutation run is allowed to do.</summary>
public sealed class PermuteOptions
{
    /// <summary>How many variants to build. Building is the expensive part, so this is the budget.</summary>
    public int Budget { get; set; } = 24;

    /// <summary>Stop as soon as a variant reproduces the function byte for byte.</summary>
    public bool StopOnExact { get; set; } = true;

    /// <summary>Also try the optimization levels the unit's profile offers. Flags are part of "which build made these bytes".</summary>
    public bool PermuteFlags { get; set; }

    /// <summary>The name of a unit to permute, instead of the one covering the function.</summary>
    public string? Unit { get; set; }
}
