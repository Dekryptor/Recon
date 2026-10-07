using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Recon.Config;
using Recon.Inventory;
using Recon.Project;

namespace Recon.Build;

/// <summary>
/// Runs a plan: compiles what the cache says is out of date (in parallel), links when the plan says to,
/// and writes <c>build/build.json</c>, which is both the report and the cache for the next run.
/// </summary>
public static class BuildRunner
{
    public static BuildResult Run(ProjectContext context, BuildOptions options, BuildPlan plan, IBuildLog log)
    {
        var started = Stopwatch.StartNew();
        Directory.CreateDirectory(plan.BuildDirectory);
        Directory.CreateDirectory(plan.ObjectsDirectory);

        var results = new ConcurrentDictionary<string, UnitResult>(StringComparer.Ordinal);
        var compile = plan.Units.Where(u => !u.IsOriginal && !u.Cached).ToList();

        if (options.DryRun)
        {
            foreach (var unit in plan.Units)
            {
                log.Step(DescribeUnit(plan, unit, dryRun: true));
            }

            if (plan.Links)
            {
                log.Step("LINK " + BuildPlanner.LinkCommand(plan).Display);
            }
        }
        else
        {
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Jobs) };
            var failed = 0;
            Parallel.ForEach(compile, parallelOptions, unit =>
            {
                if (Volatile.Read(ref failed) > 0 && !options.KeepGoing)
                {
                    results[unit.Name] = new UnitResult { Result = "skipped", Diagnostics = ["skipped after an earlier failure"] };
                    return;
                }

                var command = BuildPlanner.CompileCommand(unit, plan);
                var outcome = ProcessRunner.Run(command);
                if (outcome.ExitCode != 0)
                {
                    Interlocked.Increment(ref failed);
                }

                results[unit.Name] = new UnitResult
                {
                    Result = outcome.ExitCode == 0 ? "compiled" : "failed",
                    ExitCode = outcome.ExitCode,
                    DurationMs = outcome.DurationMs,
                    Diagnostics = outcome.Diagnostics,
                };

                log.Step(DescribeUnit(plan, unit, dryRun: false) + (outcome.ExitCode == 0 ? string.Empty : "  FAILED"));
                foreach (var line in outcome.Diagnostics)
                {
                    log.Error("  " + line);
                }
            });

            // A successful compile has just told us which headers it used; recording them is what makes
            // the next run able to say "nothing changed" instead of rebuilding everything.
            foreach (var unit in compile.Where(u => results.TryGetValue(u.Name, out var r) && r.Result == "compiled"))
            {
                var dependencies = ReadDependencies(unit, context.Project.RootDirectory);
                if (dependencies.Count > 0)
                {
                    unit.Dependencies = dependencies;
                    unit.CacheKey = BuildPlanner.RecomputeKey(unit);
                }
            }
        }

        var manifest = Assemble(context, options, plan, results, started);
        if (options.DryRun)
        {
            foreach (var warning in manifest.Warnings)
            {
                log.Warn(warning);
            }

            foreach (var problem in manifest.Problems)
            {
                log.Error(problem);
            }

            return new BuildResult { Manifest = manifest };
        }

        WriteManifest(plan.ManifestPath, manifest);
        if (!options.SkipNinja)
        {
            File.WriteAllText(plan.NinjaPath, NinjaWriter.Write(plan, options));
        }

        foreach (var warning in manifest.Warnings)
        {
            log.Warn(warning);
        }

        if (manifest.Link is { Result: "failed" } link)
        {
            foreach (var line in link.Diagnostics)
            {
                log.Error("  " + line);
            }
        }

        log.Info(Summary(plan, manifest));
        return new BuildResult { Manifest = manifest };
    }

    /// <summary>One line per unit: what it is, which toolchain builds it, and whether it was needed.</summary>
    public static string DescribeUnit(BuildPlan plan, PlannedUnit unit, bool dryRun)
    {
        if (unit.IsOriginal)
        {
            return $"ORIG {unit.Source} (provider = original, kept as is)";
        }

        string verb = unit.Cached ? "CACHED" : "CC";
        string detail = unit.Cached
            ? unit.CacheReason
            : dryRun
                ? BuildPlanner.CompileCommand(unit, plan).Display
                : $"{unit.Toolchain.Id} -> {plan.Relative(unit.ObjectPath)}";
        return $"{verb,-6} {unit.Source} ({detail})";
    }

    private static BuildManifest Assemble(
        ProjectContext context,
        BuildOptions options,
        BuildPlan plan,
        ConcurrentDictionary<string, UnitResult> results,
        Stopwatch started)
    {
        var manifest = new BuildManifest
        {
            Generator = new GeneratorInfo { Version = options.ToolVersion, Command = "build" },
            Project = new BuildProjectInfo
            {
                Name = plan.ProjectName,
                Root = plan.Root,
                ProjectFile = context.Project.FilePath,
            },
            Settings = new BuildSettingsInfo
            {
                Jobs = options.Jobs,
                Force = options.Force,
                KeepGoing = options.KeepGoing,
                DryRun = options.DryRun,
                Output = plan.Relative(plan.OutputPath),
            },
            Warnings = [.. plan.Warnings],
            Problems = [.. plan.Problems],
        };

        foreach (var toolchain in plan.Toolchains)
        {
            var info = new BuildToolchainInfo
            {
                Id = toolchain.Id,
                Family = toolchain.Family,
                Root = toolchain.Root,
                Cc = toolchain.Cc,
                Link = toolchain.Link,
                Wine = toolchain.Wine,
                Fingerprint = toolchain.Fingerprint,
            };

            if (toolchain.CcProblem is not null)
            {
                info.Problems.Add(toolchain.CcProblem);
            }

            if (toolchain.LinkProblem is not null)
            {
                info.Problems.Add(toolchain.LinkProblem);
            }

            manifest.Toolchains.Add(info);
        }

        foreach (var unit in plan.Units)
        {
            results.TryGetValue(unit.Name, out var result);
            var info = new BuildUnitInfo
            {
                Name = unit.Name,
                Source = unit.Source,
                Object = plan.Relative(unit.ObjectPath),
                Toolchain = unit.ToolchainId,
                Provider = unit.Provider,
                Status = unit.Status,
                Flags = [.. unit.Flags],
                Defines = [.. unit.Defines],
                Includes = [.. unit.Includes.Select(plan.Relative)],
                CacheKey = unit.CacheKey,
                Cached = unit.Cached,
                CacheReason = unit.CacheReason,
                Inputs = new Dictionary<string, string>(unit.Parts, StringComparer.Ordinal),
                Dependencies = [.. unit.Dependencies],
                Covers = [.. unit.Covers.Select(c => new BuildCoverInfo { Symbol = c.Symbol, Rva = c.Rva, Size = c.Size })],
                Result = options.DryRun
                    ? "planned"
                    : unit.IsOriginal
                        ? "original"
                        : unit.Cached
                            ? "cached"
                            : result?.Result ?? "skipped",
                ExitCode = result?.ExitCode,
                DurationMs = result?.DurationMs ?? 0,
                Diagnostics = result?.Diagnostics ?? [],
            };

            manifest.Units.Add(info);
        }

        if (plan.Links && !options.DryRun)
        {
            var command = BuildPlanner.LinkCommand(plan);
            var link = new BuildLinkInfo
            {
                Output = plan.Relative(plan.OutputPath),
                Executable = command.Executable,
                Arguments = [.. command.Arguments],
            };

            bool failed = manifest.Units.Any(u => u.Result == "failed");
            bool skipped = manifest.Units.Count(u => u.Result is "skipped") > 0;
            bool upToDate = manifest.Units.All(u => u.Result is "cached" or "original")
                            && File.Exists(plan.OutputPath);
            if (failed || skipped)
            {
                link.Result = "skipped";
                link.Diagnostics.Add(failed ? "a unit failed to compile" : "a unit was skipped");
            }
            else if (upToDate)
            {
                // Nothing changed and the image is still there: linking again would produce a new
                // file (linkers stamp their output) without producing a different one.
                link.Result = "cached";
                link.Diagnostics.Add("nothing changed, so the existing image was kept");
            }
            else
            {
                var outcome = ProcessRunner.Run(command);
                link.Result = outcome.ExitCode == 0 ? "linked" : "failed";
                link.ExitCode = outcome.ExitCode;
                link.DurationMs = outcome.DurationMs;
                link.Diagnostics = outcome.Diagnostics;
            }

            manifest.Link = link;
        }
        else if (plan.Links && options.DryRun)
        {
            manifest.Link = new BuildLinkInfo
            {
                Output = plan.Relative(plan.OutputPath),
                Executable = plan.LinkExecutable,
                Arguments = [.. BuildPlanner.LinkCommand(plan).Arguments],
                Result = "planned",
            };
        }

        var summary = manifest.Summary;
        summary.Units = manifest.Units.Count;
        summary.Compiled = manifest.Units.Count(u => u.Result == "compiled");
        summary.Cached = manifest.Units.Count(u => u.Result == "cached");
        summary.Failed = manifest.Units.Count(u => u.Result == "failed");
        summary.Skipped = manifest.Units.Count(u => u.Result is "skipped" or "original");
        summary.Output = plan.Relative(plan.OutputPath);
        summary.Ok = summary.Failed == 0 && manifest.Problems.Count == 0;

        if (!options.DryRun && summary.Ok && File.Exists(plan.OutputPath))
        {
            var info = new FileInfo(plan.OutputPath);
            summary.OutputSize = info.Length;
            summary.OutputSha256 = Pe.PeImage.HashFile(plan.OutputPath);
        }

        if (manifest.Link is { Result: "failed" })
        {
            summary.Ok = false;
        }

        summary.DurationMs = started.ElapsedMilliseconds;
        return manifest;
    }

    /// <summary>The one-line summary the human-readable output ends with.</summary>
    public static string Summary(BuildPlan plan, BuildManifest manifest)
    {
        var summary = manifest.Summary;
        var text = new StringBuilder();
        text.Append($"{summary.Units} unit(s): {summary.Compiled} compiled, {summary.Cached} cached");
        if (summary.Skipped > 0)
        {
            text.Append($", {summary.Skipped} not compiled");
        }

        if (summary.Failed > 0)
        {
            text.Append($", {summary.Failed} FAILED");
        }

        text.Append($" in {summary.DurationMs} ms");
        if (manifest.Link is not null)
        {
            text.Append($"; link {manifest.Link.Result}");
        }

        if (summary.OutputSize is { } size)
        {
            text.Append($"; {summary.Output} ({size} bytes, sha256 {summary.OutputSha256![..12]})");
        }

        return text.ToString();
    }

    private static List<BuildDependency> ReadDependencies(PlannedUnit unit, string root)
    {
        var dependencies = new List<BuildDependency>();
        if (!File.Exists(unit.DepFilePath))
        {
            return dependencies;
        }

        foreach (string path in DepFile.Parse(File.ReadAllText(unit.DepFilePath)))
        {
            string full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(unit.ObjectPath)!, path));
            if (!File.Exists(full))
            {
                // The object is the product of what was there; a depfile naming a file that has since
                // gone is recorded as missing so that the next run rebuilds.
                full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(unit.ObjectPath)!, path));
            }

            dependencies.Add(new BuildDependency
            {
                Path = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/'),
                Sha256 = File.Exists(full) ? Pe.PeImage.HashFile(full) : null,
            });
        }

        return dependencies;
    }

    private sealed class UnitResult
    {
        public string Result { get; set; } = "skipped";

        public int? ExitCode { get; set; }

        public long DurationMs { get; set; }

        public List<string> Diagnostics { get; set; } = [];
    }

    /// <summary>Writes the manifest: the report a user reads and the cache the next build reads.</summary>
    public static void WriteManifest(string path, BuildManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, Reporting.ReportsJsonContext.Default.BuildManifest) + "\n");
    }

    /// <summary>Serializes a manifest the way <c>--json</c> prints it.</summary>
    public static string ToJson(BuildManifest manifest)
        => JsonSerializer.Serialize(manifest, Reporting.ReportsJsonContext.Default.BuildManifest);
}
