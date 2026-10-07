using System.Text.Json.Serialization;
using Recon.Build;
using Recon.Compare;
using Recon.Config;
using Recon.Inventory;

namespace Recon.Reporting;

/// <summary>Version of the progress contract. Independent of the TOML files' schema_version.</summary>
public static class ProgressSchema
{
    public const string Version = "0.1";
}

/// <summary>What part of the original one unit is responsible for, as <c>project.toml</c> says.</summary>
public sealed class ProgressCover
{
    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; set; }

    [JsonPropertyName("rva")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Rva { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Size { get; set; }

    /// <summary>How many original functions this cover resolved to.</summary>
    [JsonPropertyName("functions")]
    public int Functions { get; set; }

    /// <summary>Set when the cover named something the original does not contain.</summary>
    [JsonPropertyName("unresolved")]
    public bool Unresolved { get; set; }
}

public sealed class ProgressCounts
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("equal")]
    public int Equal { get; set; }

    [JsonPropertyName("changed")]
    public int Changed { get; set; }

    /// <summary>In the original, with nothing to compare against: the work still to do.</summary>
    [JsonPropertyName("missing")]
    public int Missing { get; set; }
}

/// <summary>One unit's share of the reconstruction.</summary>
public sealed class UnitProgress
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "not_started";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "rebuilt";

    [JsonPropertyName("toolchain")]
    public string Toolchain { get; set; } = string.Empty;

    [JsonPropertyName("covers")]
    public List<ProgressCover> Covers { get; set; } = [];

    [JsonPropertyName("functions")]
    public ProgressCounts Functions { get; set; } = new();

    [JsonPropertyName("instructions")]
    public ProgressCounts Instructions { get; set; } = new();

    /// <summary>
    /// Equal instructions over all the original instructions this unit covers: 1.0 means the unit's
    /// part of the original is reproduced exactly. Null when the unit claims nothing measurable.
    /// </summary>
    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Progress { get; set; }

    /// <summary>Functions this unit covers that have no counterpart in the rebuild yet.</summary>
    [JsonPropertyName("missing_functions")]
    public List<string> MissingFunctions { get; set; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
}

/// <summary>The three numbers the plan's step 5 asks a progress report to separate.</summary>
public sealed class ProgressTotals
{
    [JsonPropertyName("instructions_original")]
    public int InstructionsOriginal { get; set; }

    [JsonPropertyName("instructions_rebuilt")]
    public int InstructionsRebuilt { get; set; }

    [JsonPropertyName("instructions_equal")]
    public int InstructionsEqual { get; set; }

    /// <summary>Equal instructions over the original's: how much of the original is reproduced byte for byte.</summary>
    [JsonPropertyName("instruction_exact")]
    public double InstructionExact { get; set; }

    /// <summary>Functions that match exactly, over the original's functions.</summary>
    [JsonPropertyName("verified")]
    public double Verified { get; set; }

    /// <summary>The compare engine's own score: equal over the larger of the two sides.</summary>
    [JsonPropertyName("similarity")]
    public double Similarity { get; set; }

    [JsonPropertyName("functions_original")]
    public int FunctionsOriginal { get; set; }

    [JsonPropertyName("functions_exact")]
    public int FunctionsExact { get; set; }

    [JsonPropertyName("functions_changed")]
    public int FunctionsChanged { get; set; }

    [JsonPropertyName("functions_only_left")]
    public int FunctionsOnlyLeft { get; set; }

    [JsonPropertyName("functions_only_right")]
    public int FunctionsOnlyRight { get; set; }

    [JsonPropertyName("functions_folded")]
    public int FunctionsFolded { get; set; }

    [JsonPropertyName("data_symbols")]
    public int DataSymbols { get; set; }

    [JsonPropertyName("data_identical")]
    public int DataIdentical { get; set; }

    [JsonPropertyName("data_changed")]
    public int DataChanged { get; set; }
}

/// <summary>One recorded point in time, appended to <c>history.jsonl</c> when the project asks for it.</summary>
public sealed class ProgressSnapshot
{
    [JsonPropertyName("at")]
    public string At { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("instructions_equal")]
    public int InstructionsEqual { get; set; }

    [JsonPropertyName("instructions_original")]
    public int InstructionsOriginal { get; set; }

    [JsonPropertyName("functions_exact")]
    public int FunctionsExact { get; set; }

    [JsonPropertyName("functions_original")]
    public int FunctionsOriginal { get; set; }

    [JsonPropertyName("units")]
    public List<ProgressSnapshotUnit> Units { get; set; } = [];
}

public sealed class ProgressSnapshotUnit
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }
}

/// <summary>
/// How far along the reconstruction is. The comparison says what matches; this says what that means
/// per unit, and what is left: instructions in the original with no counterpart, and instructions no
/// unit claims at all.
/// </summary>
public sealed class ProgressReport
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = ProgressSchema.Version;

    [JsonPropertyName("generator")]
    public GeneratorInfo Generator { get; set; } = new();

    [JsonPropertyName("project")]
    public BuildProjectInfo Project { get; set; } = new();

    /// <summary>Which comparison this was measured from, so a number can always be traced back.</summary>
    [JsonPropertyName("comparison")]
    public ProgressComparisonRef Comparison { get; set; } = new();

    [JsonPropertyName("totals")]
    public ProgressTotals Totals { get; set; } = new();

    [JsonPropertyName("units")]
    public List<UnitProgress> Units { get; set; } = [];

    /// <summary>Original functions no unit claims: nobody's work yet, which is why they are not zero progress.</summary>
    [JsonPropertyName("uncovered")]
    public ProgressCounts Uncovered { get; set; } = new();

    /// <summary>The previous recorded snapshot and how much moved since it, when history is kept.</summary>
    [JsonPropertyName("previous")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProgressSnapshot? Previous { get; set; }

    [JsonPropertyName("history")]
    public List<ProgressSnapshot> History { get; set; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class ProgressComparisonRef
{
    [JsonPropertyName("left")]
    public string Left { get; set; } = string.Empty;

    [JsonPropertyName("right")]
    public string Right { get; set; } = string.Empty;

    [JsonPropertyName("left_sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeftSha256 { get; set; }

    [JsonPropertyName("right_sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RightSha256 { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("toolchain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Toolchain { get; set; }
}

/// <summary>
/// Measures progress. Everything here is derived: the comparison says which functions match and how
/// many instructions of each are equal, and <c>project.toml</c>'s <c>[[unit]].covers</c> say who owns
/// which part of the original. Nothing is estimated.
/// </summary>
public static class ProgressBuilder
{
    public static ProgressReport Build(
        ProjectConfig project,
        ComparisonDocument comparison,
        string toolVersion,
        IEnumerable<ProgressSnapshot>? history = null)
    {
        var report = new ProgressReport
        {
            Generator = new GeneratorInfo { Version = toolVersion, Command = "report" },
            Project = new BuildProjectInfo
            {
                Name = project.Project.Name,
                Root = project.RootDirectory,
                ProjectFile = project.FilePath,
            },
            Comparison = new ProgressComparisonRef
            {
                Left = comparison.Left.Binary,
                Right = comparison.Right.Binary,
                LeftSha256 = comparison.Left.Sha256,
                RightSha256 = comparison.Right.Sha256,
                Score = comparison.Summary.Score,
                Toolchain = comparison.Left.Toolchain,
            },
        };

        var original = comparison.Functions.Where(f => f.Left is not null).ToList();
        int originalInstructions = comparison.Functions.Sum(f => f.Instructions.Left);

        report.Totals = new ProgressTotals
        {
            InstructionsOriginal = originalInstructions,
            InstructionsRebuilt = comparison.Functions.Sum(f => f.Instructions.Right),
            InstructionsEqual = comparison.Summary.InstructionsEqual,
            InstructionExact = originalInstructions == 0 ? 0 : (double)comparison.Summary.InstructionsEqual / originalInstructions,
            Verified = original.Count == 0 ? 0 : (double)comparison.Summary.Exact / original.Count,
            Similarity = comparison.Summary.Score,
            FunctionsOriginal = original.Count,
            FunctionsExact = comparison.Summary.Exact,
            FunctionsChanged = comparison.Summary.Changed,
            FunctionsOnlyLeft = comparison.Summary.OnlyLeft,
            FunctionsOnlyRight = comparison.Summary.OnlyRight,
            FunctionsFolded = comparison.Summary.Folded,
            DataSymbols = comparison.Summary.DataSymbols,
            DataIdentical = comparison.Summary.DataIdentical,
            DataChanged = comparison.Summary.DataChanged,
        };

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var units = new List<UnitProgress>();

        if (project.Units.Count == 0)
        {
            report.Notes.Add(
                "project.toml declares no [[unit]] entries, so progress is reported for the whole image only; " +
                "add a unit with covers to see which part of the original each source is responsible for");
        }

        foreach (var unit in project.Units)
        {
            var progress = MeasureUnit(project, unit, comparison, claimed);
            units.Add(progress);
        }

        report.Units = units;

        var unclaimed = original.Where(f => !claimed.Contains(f.Left!.Id)).ToList();
        report.Uncovered = new ProgressCounts
        {
            Total = unclaimed.Count,
            Equal = unclaimed.Sum(f => f.Instructions.Equal),
            Changed = unclaimed.Sum(f => Math.Max(0, f.Instructions.Left - f.Instructions.Equal)),
            Missing = unclaimed.Where(f => f.Right is null).Sum(f => f.Instructions.Left),
        };

        var covered = original.Count - unclaimed.Count;
        if (covered > 0 && unclaimed.Count > 0)
        {
            report.Notes.Add($"{unclaimed.Count} of {original.Count} original function(s) are not covered by any unit's covers");
        }

        if (history is not null)
        {
            report.History = [.. history];
            report.Previous = report.History.Count > 0 ? report.History[^1] : null;
        }

        foreach (var problem in comparison.Problems)
        {
            report.Problems.Add(problem);
        }

        return report;
    }

    /// <summary>
    /// One unit's share: the original functions its covers resolve to, and how much of them the
    /// rebuild reproduces. A unit that covers nothing cannot be measured, and says so.
    /// </summary>
    private static UnitProgress MeasureUnit(
        ProjectConfig project,
        UnitSpec unit,
        ComparisonDocument comparison,
        HashSet<string> claimed)
    {
        var progress = new UnitProgress
        {
            Name = unit.Name,
            Source = unit.Source,
            Status = unit.Status,
            Provider = unit.Provider,
            Toolchain = project.ResolveToolchain(unit),
        };

        var matched = new List<FunctionComparison>();
        foreach (var cover in unit.Covers)
        {
            var resolved = Resolve(cover, comparison);
            progress.Covers.Add(new ProgressCover
            {
                Symbol = cover.Symbol,
                Rva = cover.Rva,
                Size = cover.Size,
                Functions = resolved.Count,
                Unresolved = resolved.Count == 0,
            });

            foreach (var function in resolved)
            {
                claimed.Add(function.Left!.Id);
                if (!matched.Contains(function))
                {
                    matched.Add(function);
                }
            }

            if (resolved.Count == 0)
            {
                string what = cover.Symbol is not null ? $"symbol \"{cover.Symbol}\"" : $"range 0x{cover.Rva ?? 0:X}";
                progress.Notes.Add($"covers {what} matches nothing in the original");
            }
        }

        if (unit.Covers.Count == 0)
        {
            progress.Notes.Add("declares no covers, so its share of the original cannot be measured");
            return progress;
        }

        progress.Functions = new ProgressCounts
        {
            Total = matched.Count,
            Equal = matched.Count(f => f.Status == "exact"),
            Changed = matched.Count(f => f.Status is "changed" or "folded"),
            Missing = matched.Count(f => f.Right is null),
        };

        int instructions = matched.Sum(f => f.Instructions.Left);
        int equal = matched.Sum(f => f.Instructions.Equal);
        progress.Instructions = new ProgressCounts
        {
            Total = instructions,
            Equal = equal,
            Changed = Math.Max(0, instructions - equal),
            Missing = matched.Where(f => f.Right is null).Sum(f => f.Instructions.Left),
        };

        progress.Progress = instructions == 0 ? null : (double)equal / instructions;
        progress.MissingFunctions = [.. matched.Where(f => f.Right is null).Select(f => Name(f))];
        return progress;
    }

    /// <summary>
    /// What a cover means. A symbol matches the function of that name, and if the original's names are
    /// mangled it also matches the demangled form; a range matches every function it overlaps.
    /// </summary>
    /// <summary>
    /// What a cover means. The answer is shared with the delinker (<see cref="Covers"/>), because a
    /// cover that earns progress for a unit has to be the same cover that lets that unit's rebuild
    /// take the function's place in the relinked image.
    /// </summary>
    private static List<FunctionComparison> Resolve(CoverSpec cover, ComparisonDocument comparison)
    {
        var functions = comparison.Functions.Where(f => f.Left is not null).ToList();
        var candidates = functions
            .Select(f => new Covers.CoverCandidate(f.Left!.Name, f.Left.Rva, f.Left.Size))
            .ToList();

        return [.. Covers.Resolve(cover, candidates).Select(index => functions[index])];
    }

    public static ProgressSnapshot Snapshot(ProgressReport report, DateTimeOffset? at = null)
        => new()
        {
            At = (at ?? DateTimeOffset.UtcNow).ToString("O"),
            Score = report.Totals.Similarity,
            InstructionsEqual = report.Totals.InstructionsEqual,
            InstructionsOriginal = report.Totals.InstructionsOriginal,
            FunctionsExact = report.Totals.FunctionsExact,
            FunctionsOriginal = report.Totals.FunctionsOriginal,
            Units = [.. report.Units.Select(u => new ProgressSnapshotUnit { Name = u.Name, Progress = u.Progress })],
        };

    private static string Name(FunctionComparison function)
        => function.Left?.Name ?? function.Right?.Name ?? $"0x{function.Left?.Rva ?? function.Right?.Rva ?? 0:X}";
}

/// <summary>
/// The history file: one JSON object per line, appended on every run that asks for it. A line-oriented
/// file is used rather than a growing array because a history is only ever read as a series and
/// appended to, and this way one bad run cannot corrupt the ones before it.
/// </summary>
public static class ProgressHistory
{
    public const string FileName = "history.jsonl";

    public static string PathOf(string reportDirectory) => Path.Combine(reportDirectory, FileName);

    public static List<ProgressSnapshot> Read(string reportDirectory)
    {
        string path = PathOf(reportDirectory);
        if (!File.Exists(path))
        {
            return [];
        }

        var snapshots = new List<ProgressSnapshot>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                var snapshot = System.Text.Json.JsonSerializer.Deserialize(
                    line,
                    ReportsJsonContext.Default.ProgressSnapshot);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A history line that cannot be read is dropped, not fatal: it is a record, not input.
            }
        }

        return snapshots;
    }

    /// <summary>Appends one snapshot and returns the whole history, including it.</summary>
    public static List<ProgressSnapshot> Append(string reportDirectory, ProgressSnapshot snapshot)
    {
        Directory.CreateDirectory(reportDirectory);
        File.AppendAllText(
            PathOf(reportDirectory),
            Reports.SerializeLine(snapshot) + "\n");
        return Read(reportDirectory);
    }
}
