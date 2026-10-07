using Recon.Build;
using Recon.Compare;
using Recon.Config;
using Recon.Project;

namespace Recon.Permute;

/// <summary>What a permutation run needs. The CLI fills it in; the tests fill in the same thing.</summary>
public sealed class PermuterInputs
{
    public required ProjectContext Context { get; init; }

    /// <summary>The function the run is trying to reproduce. Optional when <see cref="PermuteOptions.Unit"/> names one.</summary>
    public string? Function { get; init; }

    public PermuteOptions Options { get; init; } = new();

    /// <summary>Build chatter. Silence is a legitimate choice: a run may build dozens of variants.</summary>
    public IBuildLog? Log { get; init; }

    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>
    /// Where the best variant's source is written, when there is one. Nothing is ever written over
    /// the unit's own source: the search leaves the tree exactly as it found it.
    /// </summary>
    public string? OutputDirectory { get; init; }
}

/// <summary>
/// The permutation loop: write a variant, build it, score it against the original, and keep the
/// ranking. It uses the project's own build plan and the compare engine unmodified — a permuter that
/// had its own idea of "the same bytes" would be measuring something else than <c>recon diff</c>
/// does, and the number would not be worth anything.
///
/// The score is that engine's per-function similarity, and "exact" is its <c>exact</c> status: byte
/// equality after relocation normalization. Nothing else counts as a match.
/// </summary>
public static class Permuter
{
    public static PermuteDocument Run(PermuterInputs inputs)
    {
        var context = inputs.Context;
        var options = inputs.Options;
        var diagnostics = new Diagnostics();

        var unit = ResolveUnit(context, options.Unit, inputs.Function);
        string sourcePath = Path.GetFullPath(Path.Combine(context.RootDirectory, unit.Source));
        if (!File.Exists(sourcePath))
        {
            throw new ConfigException(
                "cannot permute",
                [new Diagnostic(DiagnosticSeverity.Error, sourcePath, 0, "permute.source", $"unit[{unit.Name}].source is missing: {sourcePath}")]);
        }

        string original = File.ReadAllText(sourcePath);
        var candidates = VariantGenerator.Generate(original);
        string function = inputs.Function
            ?? unit.Covers.FirstOrDefault(c => c.Symbol is not null)?.Symbol
            ?? string.Empty;

        var levels = options.PermuteFlags ? FlagVariants(context, unit, original, candidates) : new List<string>();

        var document = new PermuteDocument
        {
            Command = "recon permute",
            ToolVersion = inputs.ToolVersion,
            Unit = unit.Name,
            Source = unit.Source,
            Function = function,
            CandidateCount = candidates.Count,
            OptimizationLevels = levels,
        };

        if (candidates.Count == 0)
        {
            document.StoppedBecause = "no variant of this source could be proved equivalent";
            return document;
        }

        // The side everything is measured against, built once.
        var originalSide = ComparisonSide.FromProjectInput(
            "original", Path.Combine(context.RootDirectory, ProjectContext.ProjectFileName), "original", diagnostics);
        var comparisonOptions = ComparisonOptions.FromProfile(originalSide.Profile);

        try
        {
            var baseline = Score(context, sourcePath, original, originalSide, comparisonOptions, function, inputs);
            document.BaselineScore = baseline.Score;
            document.BaselineExact = baseline.Exact;
            if (baseline.BuildError is not null)
            {
                document.StoppedBecause = "the project does not build as it stands: " + baseline.BuildError;
                return document;
            }

            if (baseline.Exact && options.StopOnExact)
            {
                // Nothing can beat an exact baseline — 1.0 is the ceiling — so the honest answer is
                // "this source already reproduces it", which costs one build instead of a budget's
                // worth of them.
                document.StoppedBecause = $"the source as it stands already reproduces \"{function}\" exactly";
                return document;
            }

            foreach (var variant in candidates)
            {
                if (options.Budget > 0 && document.Tried >= options.Budget)
                {
                    document.StoppedBecause = $"budget of {options.Budget} variant(s) reached";
                    break;
                }

                var result = Score(context, sourcePath, original, originalSide, comparisonOptions, function, inputs, variant);
                document.Variants.Add(result);
                document.Tried++;

                if (options.StopOnExact && result.Exact)
                {
                    document.StoppedBecause = "a variant reproduced the function exactly";
                    break;
                }
            }

            if (document.StoppedBecause.Length == 0)
            {
                document.StoppedBecause = $"all {document.Tried} candidate(s) tried";
            }

            WriteBest(document, candidates, inputs, sourcePath);
            return document;
        }
        finally
        {
            // Whatever happened — a failed build, an exception, a budget — the source goes back.
            File.WriteAllText(sourcePath, original);
        }
    }

    /// <summary>
    /// Adds a candidate for every optimization level the unit's toolchain offers, in front of the
    /// edits: the level changes every function in the image at once, so while it is wrong no edit to
    /// one function can be exact, and there are a handful of levels rather than dozens of edits.
    /// Returns the levels the profile declared, which the document publishes.
    /// </summary>
    private static List<string> FlagVariants(
        ProjectContext context,
        UnitSpec unit,
        string original,
        List<SourceVariant> candidates)
    {
        var levels = OptimizationLevelsOf(context, unit, out string? current);
        var flagVariants = new List<SourceVariant>();

        foreach (string level in levels)
        {
            if (string.Equals(level, current, StringComparison.Ordinal))
            {
                // That is the baseline: what the project builds today.
                continue;
            }

            flagVariants.Add(new SourceVariant
            {
                Id = "flags:" + level,
                Kind = "optimization-level",
                Description = current is null
                    ? $"build with {level} — the flags ask for no level today"
                    : $"build with {level} instead of {current}",
                Text = original,
                Optimization = level,
            });
        }

        candidates.InsertRange(0, flagVariants);
        return levels;
    }

    /// <summary>
    /// Which levels the unit's toolchain offers, and which one its flags ask for today. Both come
    /// from the planner rather than from reading the profile here: the flags a unit is compiled with
    /// are the profile's, then <c>[defaults]</c>', then the unit's own, and a second copy of that
    /// merge order would drift from the first.
    /// </summary>
    private static List<string> OptimizationLevelsOf(ProjectContext context, UnitSpec unit, out string? current)
    {
        current = null;
        var plan = BuildPlanner.Plan(context, new BuildOptions { SkipNinja = true }, diagnostics: new Diagnostics());
        var planned = plan.Units.FirstOrDefault(u => string.Equals(u.Name, unit.Name, StringComparison.Ordinal));
        if (planned?.Profile is null)
        {
            return [];
        }

        current = planned.Profile.OptimizationLevelOf(planned.Flags);
        return [.. planned.Profile.OptimizationLevels];
    }

    /// <summary>
    /// Builds the project with one variant in place (or with the original, for the baseline) and
    /// scores the function. The variant's text is written, the build runs, and the original text is
    /// put back before the next caller sees the file.
    /// </summary>
    private static VariantResult Score(
        ProjectContext context,
        string sourcePath,
        string original,
        ComparisonSide originalSide,
        ComparisonOptions comparisonOptions,
        string function,
        PermuterInputs inputs,
        SourceVariant? variant = null)
    {
        var result = new VariantResult
        {
            VariantId = variant?.Id ?? "baseline",
            Kind = variant?.Kind ?? "baseline",
            Description = variant?.Description ?? "the unit's source as it stands",
            Function = function,
            Optimization = variant?.Optimization,
        };

        try
        {
            if (variant is not null)
            {
                File.WriteAllText(sourcePath, variant.Text);
            }

            // No unit filter: a permutation changes the bytes of the whole image, not only of the
            // object it came from, so every unit is compiled and the image is linked. Filtering to
            // one unit would relink nothing and compare a stale binary.
            var buildOptions = new BuildOptions
            {
                ToolVersion = inputs.ToolVersion,
                SkipNinja = true,
                Force = false,
                KeepGoing = false,
                OptimizationLevel = variant?.Optimization,
            };

            string manifestPath = Path.Combine(context.RootDirectory, context.Project.Paths.Build, "build.json");
            var previous = BuildManifestStore.Load(manifestPath);
            var plan = BuildPlanner.Plan(context, buildOptions, previous, context.Diagnostics);
            var build = BuildRunner.Run(context, buildOptions, plan, inputs.Log ?? new SilentBuildLog());

            if (build.Failed)
            {
                string problem = build.Manifest.Problems.FirstOrDefault()
                    ?? $"{build.Manifest.Summary.Failed} unit(s) failed to build";
                result.BuildError = problem;
                return result;
            }

            string binary = plan.OutputPath;
            if (!File.Exists(binary))
            {
                result.BuildError = $"the build produced no output at {plan.Relative(binary)}";
                return result;
            }

            var diagnostics = new Diagnostics();
            var side = ComparisonSide.FromBinary(variant?.Id ?? "baseline", binary, diagnostics);
            var comparison = ComparisonBuilder.Build(originalSide, side, comparisonOptions);

            var pair = comparison.Functions.FirstOrDefault(f =>
                string.Equals(f.Left?.Name, function, StringComparison.Ordinal)
                || string.Equals(f.Right?.Name, function, StringComparison.Ordinal));

            if (pair is null)
            {
                result.Status = "missing";
                result.BuildError = $"the rebuilt binary has no function named \"{function}\"";
                return result;
            }

            result.Score = pair.Score;
            result.Status = pair.Status;
            result.Exact = string.Equals(pair.Status, "exact", StringComparison.Ordinal);
            result.Differences = pair.Differences.Count;
            return result;
        }
        catch (ConfigException ex)
        {
            result.BuildError = string.Join("; ", ex.Diagnostics.Select(d => d.Message));
            return result;
        }
        finally
        {
            if (variant is not null)
            {
                File.WriteAllText(sourcePath, original);
            }
        }
    }

    /// <summary>
    /// Writes the best variant next to the project so a human can adopt it. Never over the unit's
    /// own source: a permuter that edits the reconstruction behind your back is not a tool, it is a
    /// hazard.
    /// </summary>
    private static void WriteBest(
        PermuteDocument document,
        List<SourceVariant> candidates,
        PermuterInputs inputs,
        string sourcePath)
    {
        var best = document.Best;
        if (best is null || best.VariantId == "baseline")
        {
            return;
        }

        if (best.Score <= document.BaselineScore && !(best.Exact && !document.BaselineExact))
        {
            return;
        }

        if (best.Optimization is not null)
        {
            // What won is a change to how the source is compiled, not to the source: writing the
            // file out unchanged would look like a result when there is nothing to adopt but the
            // flag, and an agent that copied it would learn nothing.
            document.BestWriteError =
                $"the best variant is an optimization level, not an edit — the source is unchanged; rebuild with {best.Optimization}";
            return;
        }

        var variant = candidates.FirstOrDefault(v => v.Id == best.VariantId);
        if (variant is null)
        {
            return;
        }

        if (inputs.OutputDirectory is null)
        {
            document.BestWriteError = "no output directory was given";
            return;
        }

        // The project's own link output lives under [paths].build too, and a project named after
        // what it builds can put an executable exactly where the variants were going to go. Say so
        // rather than crashing: the search already did its job, only the writing failed.
        if (File.Exists(inputs.OutputDirectory))
        {
            document.BestWriteError = $"{inputs.OutputDirectory} is a file, not a directory - name another one with -o";
            return;
        }

        Directory.CreateDirectory(inputs.OutputDirectory);
        string name = Path.GetFileName(sourcePath);
        string path = Path.Combine(inputs.OutputDirectory, name);
        File.WriteAllText(path, variant.Text);
        document.BestSource = path;
    }

    /// <summary>
    /// Which unit to permute: the one named, the one whose <c>covers</c> names the function, or
    /// nothing at all — and saying which is ambiguous is better than guessing.
    /// </summary>
    private static UnitSpec ResolveUnit(ProjectContext context, string? unitName, string? function)
    {
        if (unitName is not null)
        {
            var named = context.Project.Units.FirstOrDefault(u => string.Equals(u.Name, unitName, StringComparison.Ordinal));
            if (named is null)
            {
                throw new ConfigException(
                    "cannot permute",
                    [new Diagnostic(
                        DiagnosticSeverity.Error,
                        Path.Combine(context.RootDirectory, ProjectContext.ProjectFileName),
                        0,
                        "permute.unit",
                        $"no unit named \"{unitName}\" (have: {string.Join(", ", context.Project.Units.Select(u => u.Name))})")]);
            }

            return named;
        }

        if (function is null)
        {
            throw new ConfigException(
                "cannot permute",
                [new Diagnostic(
                    DiagnosticSeverity.Error,
                    Path.Combine(context.RootDirectory, ProjectContext.ProjectFileName),
                    0,
                    "permute.unit",
                    "name either a --unit or a --function")]);
        }

        var covering = context.Project.Units
            .Where(u => u.Covers.Any(c => string.Equals(c.Symbol, function, StringComparison.Ordinal)))
            .ToList();

        if (covering.Count == 0)
        {
            throw new ConfigException(
                "cannot permute",
                [new Diagnostic(
                    DiagnosticSeverity.Error,
                    Path.Combine(context.RootDirectory, ProjectContext.ProjectFileName),
                    0,
                    "permute.unit",
                    $"no unit's [[covers]] names \"{function}\"; say which one with --unit")]);
        }

        if (covering.Count > 1)
        {
            throw new ConfigException(
                "cannot permute",
                [new Diagnostic(
                    DiagnosticSeverity.Error,
                    Path.Combine(context.RootDirectory, ProjectContext.ProjectFileName),
                    0,
                    "permute.unit",
                    $"\"{function}\" is covered by more than one unit ({string.Join(", ", covering.Select(u => u.Name))}); say which one with --unit")]);
        }

        return covering[0];
    }

    /// <summary>A build log that says nothing, for callers that are measuring rather than watching.</summary>
    private sealed class SilentBuildLog : IBuildLog
    {
        public void Info(string text)
        {
        }

        public void Step(string text)
        {
        }

        public void Warn(string text)
        {
        }

        public void Error(string text)
        {
        }
    }
}
