using System.Security.Cryptography;
using System.Text;
using Recon.Config;
using Recon.Project;
using Recon.Toolchains;

namespace Recon.Build;

/// <summary>
/// Turns a project into a build: one command per unit plus one link, with a content-hash cache key
/// per unit. Nothing here runs anything: the plan is inspected (<c>--dry-run</c>), rendered as Ninja
/// (<see cref="NinjaWriter"/>), or executed by <see cref="BuildRunner"/>.
/// </summary>
public static class BuildPlanner
{
    /// <summary>Prefix of every cache key, so the scheme can change without old keys looking valid.</summary>
    public const string KeyScheme = "recon-build-key-v1";

    public static BuildPlan Plan(ProjectContext context, BuildOptions options, BuildManifest? previous = null, Diagnostics? diagnostics = null)
    {
        var project = context.Project;
        string root = project.RootDirectory;
        var problems = new List<string>();
        var warnings = new List<string>();

        string buildDirectory = Path.GetFullPath(Path.Combine(root, project.Paths.Build));
        var plan = new BuildPlan
        {
            ProjectName = project.Project.Name,
            Root = root,
            BuildDirectory = buildDirectory,
            ObjectsDirectory = Path.Combine(buildDirectory, "obj"),
            OutputPath = Path.Combine(buildDirectory, project.Project.Name + OutputExtension(project.Target.Format)),
            NinjaPath = Path.Combine(buildDirectory, "build.ninja"),
            ManifestPath = Path.Combine(buildDirectory, "build.json"),
        };

        var units = new List<UnitSpec>(project.Units);
        if (options.UnitFilter is { Length: > 0 } filter)
        {
            units = [.. units.Where(u => string.Equals(u.Name, filter, StringComparison.Ordinal))];
            if (units.Count == 0)
            {
                problems.Add($"no unit named \"{filter}\" (units: {Names(project.Units)})");
            }
        }

        if (project.Units.Count == 0)
        {
            problems.Add("project.toml has no [[unit]] entries: there is nothing to build");
        }

        var toolchains = new Dictionary<string, BuildToolchainUse>(StringComparer.Ordinal);
        var objectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only computed if a unit needs it: a project that names its toolchain never loads the image
        // just to be told what it already says.
        var detected = new Lazy<ToolchainProfile?>(() => DetectToolchain(context));

        foreach (var unit in units)
        {
            var planned = PlanUnit(context, unit, options, previous, toolchains, plan, warnings, problems, detected);
            if (planned is null)
            {
                continue;
            }

            if (!objectNames.Add(planned.ObjectFileName))
            {
                problems.Add($"unit[{unit.Name}]: two units produce the same object file \"{planned.ObjectFileName}\"");
            }

            plan.Units.Add(planned);
        }

        plan.Toolchains = [.. toolchains.Values.OrderBy(t => t.Id, StringComparer.Ordinal)];
        PlanLink(plan, project, options, warnings, problems);

        plan.Warnings = warnings;
        plan.Problems = problems;
        if (diagnostics is not null)
        {
            foreach (var warning in warnings)
            {
                diagnostics.Warning(project.FilePath, 0, "build", warning);
            }

            foreach (var problem in problems)
            {
                diagnostics.Error(project.FilePath, 0, "build", problem);
            }
        }

        return plan;
    }

    private static PlannedUnit? PlanUnit(
        ProjectContext context,
        UnitSpec unit,
        BuildOptions options,
        BuildManifest? previous,
        Dictionary<string, BuildToolchainUse> toolchains,
        BuildPlan plan,
        List<string> warnings,
        List<string> problems,
        Lazy<ToolchainProfile?> detected)
    {
        var project = context.Project;
        string root = project.RootDirectory;
        var planned = new PlannedUnit
        {
            Name = unit.Name,
            Source = unit.Source.Replace('\\', '/'),
            Provider = unit.Provider,
            Status = unit.Status,
            Covers = [.. unit.Covers],
            IsOriginal = string.Equals(unit.Provider, "original", StringComparison.Ordinal),
        };

        planned.SourcePath = Path.GetFullPath(Path.Combine(root, planned.Source));

        string toolchainId = project.ResolveToolchain(unit);
        if (toolchainId.Length == 0)
        {
            // Nothing names a toolchain, which is the normal state of a project that has just been
            // pointed at a binary. The file says who built it, so it is asked rather than the user.
            var guess = detected.Value;
            if (guess is null)
            {
                problems.Add($"unit[{unit.Name}]: no toolchain: set unit.toolchain or target.default_toolchain");
                return null;
            }

            toolchainId = guess.Id;
            warnings.Add($"unit[{unit.Name}]: no toolchain named, using \"{toolchainId}\" — the profile this binary's own evidence points at (set unit.toolchain to override)");
        }

        planned.ToolchainId = toolchainId;

        var profile = context.Registry.GetResolved(toolchainId);

        if (profile is null)
        {
            problems.Add($"unit[{unit.Name}].toolchain: unknown profile \"{toolchainId}\" (known: {string.Join(", ", context.Registry.KnownIds)})");
            return null;
        }

        if (profile.IsAbstract)
        {
            problems.Add($"unit[{unit.Name}].toolchain: profile \"{toolchainId}\" is abstract and cannot be used as a toolchain");
            return null;
        }

        if (!toolchains.TryGetValue(toolchainId, out var toolchain))
        {
            var install = context.Local.Toolchains.TryGetValue(toolchainId, out var localInstall) ? localInstall : null;
            toolchain = ToolResolver.Resolve(profile, install, root);
            toolchains[toolchainId] = toolchain;
        }

        planned.Toolchain = toolchain;
        planned.Profile = profile;
        planned.ObjectFileName = Sanitize(unit.Name) + toolchain.ObjectExtension;
        planned.ObjectPath = Path.Combine(plan.ObjectsDirectory, planned.ObjectFileName);
        planned.DepFilePath = Path.ChangeExtension(planned.ObjectPath, ".d");

        if (!File.Exists(planned.SourcePath))
        {
            problems.Add($"unit[{unit.Name}].source: file not found: {planned.Source}");
            return null;
        }

        if (profile.Compile is null)
        {
            problems.Add($"unit[{unit.Name}].toolchain: profile \"{toolchainId}\" has no [compile] section, so it cannot build \"{planned.Source}\"");
            return null;
        }

        if (toolchain.CcProblem is not null)
        {
            warnings.Add($"{toolchain.CcProblem} (unit \"{unit.Name}\")");
        }

        if (!profile.AppliesTo(project.Target.Format, project.Target.Arch))
        {
            warnings.Add($"toolchain \"{toolchainId}\" does not declare a target for {project.Target.Format}/{project.Target.Arch}");
        }

        planned.Includes = [.. project.Paths.Include.Select(include =>
        {
            string path = Path.GetFullPath(Path.Combine(root, include));
            if (!Directory.Exists(path))
            {
                warnings.Add($"include directory not found: {include}");
            }

            return path.Replace(Path.DirectorySeparatorChar, '/');
        })];

        // Flag merge order (plan A.2): profile defaults, then [defaults], then the unit's own flags.
        planned.Flags = [.. profile.Compile.DefaultFlags.Concat(project.Defaults.Flags).Concat(unit.Flags)];
        if (options.OptimizationLevel is { } level && profile.OptimizationLevels.Count > 0)
        {
            // Which level a compiler was asked for is part of what made the bytes, so the search gets
            // to change it. Substituting in place keeps the flags in the order the profile wrote
            // them; the cache key hashes this list, so the object is rebuilt and not reused.
            planned.Flags = [.. profile.WithOptimizationLevel(planned.Flags, level)];
        }

        planned.Defines = [.. project.Defaults.Defines.Concat(unit.Defines).Distinct(StringComparer.Ordinal)];

        var previousUnit = previous?.Units.FirstOrDefault(u => string.Equals(u.Name, unit.Name, StringComparison.Ordinal));
        planned.Dependencies = previousUnit?.Dependencies ?? [];

        if (planned.IsOriginal)
        {
            warnings.Add($"unit[{unit.Name}]: provider = original, so its bytes stay as they are in the original image and nothing is compiled for it (delink/relink is M5)");
        }

        planned.CacheKey = CacheKey(planned, project, previousUnit, out var parts, out string reason);
        planned.Parts = parts;
        planned.CacheReason = reason;
        planned.Cached = !options.Force
                         && previousUnit is not null
                         && string.Equals(previousUnit.CacheKey, planned.CacheKey, StringComparison.Ordinal)
                         && File.Exists(planned.ObjectPath);
        if (options.Force)
        {
            planned.CacheReason = "--force";
        }
        else if (planned.Cached)
        {
            planned.CacheReason = "up to date";
        }
        else if (previousUnit is not null && string.Equals(previousUnit.CacheKey, planned.CacheKey, StringComparison.Ordinal))
        {
            planned.CacheReason = "object file is missing";
        }

        return planned;
    }

    /// <summary>
    /// The cache key covers everything the object file is a function of: the source bytes, the flags,
    /// the toolchain (profile plus installed tools), the environment, and every dependency the
    /// toolchain reported last time. A change to any of them recompiles the unit.
    /// </summary>
    private static string CacheKey(PlannedUnit unit, ProjectConfig project, BuildUnitInfo? previousUnit, out Dictionary<string, string> parts, out string reason)
    {
        parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = HashFileOrMissing(unit.SourcePath),
            ["toolchain"] = Hash(unit.Toolchain.Fingerprint + "|" + ToolResolver.ToolIdentity(unit.Toolchain.Cc) + "|" + Environment(unit.Toolchain)),
            ["flags"] = Hash(string.Join('\u0001', unit.Flags)),
            ["defines"] = Hash(string.Join('\u0001', unit.Defines)),
            ["includes"] = Hash(string.Join('\u0001', unit.Includes)),
        };

        var dependencies = new List<BuildDependency>();
        var changed = new List<string>();
        foreach (var recorded in unit.Dependencies.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            string path = Path.GetFullPath(Path.Combine(project.RootDirectory, recorded.Path));
            string current = HashFileOrMissing(path);
            dependencies.Add(new BuildDependency { Path = recorded.Path, Sha256 = current == "missing" ? null : current });
            if (current != recorded.Sha256)
            {
                changed.Add(recorded.Path);
            }
        }

        // A toolchain that cannot write a depfile (MSVC has no equivalent of -MMD) has no recorded
        // dependencies; hashing the include directories keeps those projects correct too.
        if (unit.Dependencies.Count == 0 && unit.Profile?.Compile?.DepfileFlag.Length == 0)
        {
            dependencies.AddRange(ScanIncludes(unit.Includes, project.RootDirectory));
        }

        unit.Dependencies = dependencies;
        parts["dependencies"] = Hash(DependenciesText(unit));
        unit.Parts = parts;

        reason = previousUnit is null
            ? "first build of this unit"
            : changed.Count > 0
                ? $"changed dependency: {changed[0]}"
                : "inputs changed";

        return Hash(KeyText(unit, parts));
    }

    /// <summary>
    /// The toolchain the image itself points at, when the project names none. Detection is cheap
    /// enough to run per plan, and it is the difference between a project that works when pointed at
    /// a new binary and one that refuses until someone names the compiler by hand.
    /// </summary>
    private static ToolchainProfile? DetectToolchain(ProjectContext context)
    {
        var loaded = context.LoadImage();
        if (loaded.Image is null)
        {
            return null;
        }

        var debug = context.LoadDebugInfo(loaded.Image);
        return ToolchainSelector.Choose(context.Registry, null, loaded.Image, debug, context.Dwarf, concreteOnly: true);
    }

    private static void PlanLink(
        BuildPlan plan,
        ProjectConfig project,
        BuildOptions options,
        List<string> warnings,
        List<string> problems)
    {
        var rebuildable = plan.Units.Where(u => !u.IsOriginal).ToList();
        if (options.UnitFilter is { Length: > 0 })
        {
            // A filter selects part of the image, and part of an image is not an image: linking one
            // unit's object against the runtime would fail, or worse, produce something that looks
            // linked. What a filtered build is for is producing the object — which is what `recon link`
            // places at the original's addresses.
            plan.Links = false;
            plan.LinkNote = rebuildable.Count == 0
                ? "the filter selected nothing to compile"
                : $"nothing is linked: --unit builds {rebuildable.Count} unit(s), and an image is every unit (\u0060recon link\u0060 places a built unit at the original's addresses)";
            return;
        }

        if (rebuildable.Count == 0)
        {
            plan.Links = false;
            plan.LinkNote = plan.Units.Count == 0 ? "no units to build" : "every unit keeps its original bytes";
            return;
        }

        var linker = rebuildable[0].Toolchain;
        var linkProfile = rebuildable[0].Profile;
        var families = rebuildable.Select(u => u.Toolchain.Family).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (families.Count > 1)
        {
            warnings.Add($"units mix toolchain families ({string.Join(", ", families)}): linking with \"{linker.Id}\"");
        }

        if (linkProfile?.Link is null)
        {
            problems.Add($"toolchain \"{linker.Id}\" has no [link] section, so the units cannot be linked into an image");
            plan.Links = false;
            return;
        }

        plan.LinkToolchainId = linker.Id;
        plan.LinkExecutable = linker.Link;
        plan.LinkWine = linker.Wine;
        plan.LinkEnvironment = new Dictionary<string, string>(linker.Env, StringComparer.Ordinal);
        // The project's link flags come after the profile's, the same order the compile flags use.
        plan.LinkFlags = [.. linkProfile.Link.DefaultFlags.Concat(project.Defaults.LinkFlags)];
        plan.LinkOutputFlag = linkProfile.Link.OutputFlag;
        plan.Links = true;

        var compiled = plan.Units.Count(u => !u.IsOriginal);
        var omitted = plan.Units.Count(u => u.IsOriginal);
        if (omitted > 0)
        {
            plan.LinkNote = $"{omitted} of {plan.Units.Count} unit(s) keep their original bytes and are not linked in (M5 relinks them)";
        }

        if (plan.LinkOutputFlag.Length == 0)
        {
            problems.Add($"toolchain \"{linker.Id}\": its [link] section has no output_flag, so the output path cannot be passed to the linker");
            plan.Links = false;
        }

        if (linker.LinkProblem is not null)
        {
            warnings.Add(linker.LinkProblem);
        }

        if (compiled == 0)
        {
            plan.Links = false;
            plan.LinkNote = "there is nothing to link";
        }
    }

    /// <summary>
    /// The cache key of a unit whose dependencies the compiler has just measured. Storing the key
    /// that includes them is what lets the next run answer "nothing changed" instead of rebuilding.
    /// </summary>
    public static string RecomputeKey(PlannedUnit unit)
    {
        unit.Parts["dependencies"] = Hash(DependenciesText(unit));
        return Hash(KeyText(unit));
    }

    private static string DependenciesText(PlannedUnit unit)
        => string.Join('\u0001', unit.Dependencies
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .Select(d => $"{d.Path}={d.Sha256}"));

    private static string KeyText(PlannedUnit unit, Dictionary<string, string>? parts = null)
    {
        var parts0 = parts ?? unit.Parts;
        var text = new StringBuilder();
        text.Append(KeyScheme).Append('\n');
        text.Append("unit=").Append(unit.Name).Append('\n');
        foreach (var (name, value) in parts0.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Everything the unit passes as flags: the profile's, the project's, the unit's own, then the
    /// defines and include directories rendered through the profile's templates. The order is fixed
    /// so that a cache key and a Ninja edge agree on what "the same flags" means.
    /// </summary>
    public static List<string> CompileFlags(PlannedUnit unit)
    {
        var compile = unit.Profile?.Compile ?? new CompileSpec();
        var flags = new List<string>(unit.Flags);
        foreach (var define in unit.Defines)
        {
            flags.AddRange(SplitFlag(Expand(compile.DefineFlag, ("name", define))));
        }

        foreach (var include in unit.Includes)
        {
            flags.AddRange(SplitFlag(Expand(compile.IncludeFlag, ("path", include))));
        }

        return flags;
    }

    /// <summary>The profile's output template with this unit's object file filled in.</summary>
    public static string CompileOutputFlag(PlannedUnit unit, BuildPlan plan)
        => Expand(unit.Profile?.Compile?.OutputFlag ?? string.Empty, ("obj", Slash(unit.ObjectPath)));

    /// <summary>The depfile flag, empty when the toolchain cannot write one.</summary>
    public static string CompileDepFlag(PlannedUnit unit, BuildPlan plan)
        => unit.Profile?.Compile is { DepfileFlag.Length: > 0 } compile
            ? Expand(compile.DepfileFlag, ("dep", Slash(unit.DepFilePath)))
            : string.Empty;

    public static string Slash(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The compile command for a unit, with the profile's flag templates filled in.</summary>
    public static BuildCommand CompileCommand(PlannedUnit unit, BuildPlan plan)
    {
        var command = new BuildCommand
        {
            Kind = "cc",
            Executable = unit.Toolchain.Cc,
            WorkingDirectory = plan.Root,
            Wine = unit.Toolchain.Wine,
            Environment = unit.Toolchain.Env,
        };

        command.Arguments.AddRange(CompileFlags(unit));
        command.Arguments.Add(Slash(unit.SourcePath));
        command.Arguments.AddRange(SplitFlag(CompileOutputFlag(unit, plan)));
        command.Arguments.AddRange(SplitFlag(CompileDepFlag(unit, plan)));

        // A template that uses a placeholder the tool never fills in would otherwise reach the
        // compiler verbatim, where it fails in a way that is hard to read.
        foreach (var argument in command.Arguments.Where(a => a.Contains('{')))
        {
            plan.Problems.Add($"unit[{unit.Name}]: {argument} came from a flag template with a placeholder recon does not fill in");
        }

        return command;
    }

    /// <summary>The link command: the profile's link flags, then the project's, then the objects.</summary>
    public static BuildCommand LinkCommand(BuildPlan plan)
    {
        var command = new BuildCommand
        {
            Kind = "link",
            Executable = plan.LinkExecutable,
            WorkingDirectory = plan.Root,
            Wine = plan.LinkWine,
            Environment = plan.LinkEnvironment,
        };

        command.Arguments.AddRange(plan.LinkFlags);
        command.Arguments.AddRange(plan.Units.Where(u => !u.IsOriginal).Select(u => Slash(u.ObjectPath)));
        command.Arguments.AddRange(SplitFlag(LinkOutputFlag(plan)));
        return command;
    }

    /// <summary>The profile's link output template with the plan's output path filled in.</summary>
    public static string LinkOutputFlag(BuildPlan plan)
        => Expand(plan.LinkOutputFlag, ("exe", Slash(plan.OutputPath)));

    /// <summary>Fills a profile flag template such as <c>-D{name}</c> or <c>-o {obj}</c>.</summary>
    public static string Expand(string template, params (string Key, string Value)[] values)
    {
        string result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>One template may expand to several arguments, for example <c>-MMD -MF {dep}</c>.</summary>
    public static List<string> SplitFlag(string text)
        => [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        }

        return builder.Length == 0 ? "unit" : builder.ToString();
    }

    private static List<BuildDependency> ScanIncludes(List<string> includes, string root)
    {
        var result = new List<BuildDependency>();
        foreach (var directory in includes)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal).Take(20_000))
            {
                result.Add(new BuildDependency
                {
                    Path = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                    Sha256 = HashFileOrMissing(file),
                });
            }
        }

        return result;
    }

    private static string Environment(BuildToolchainUse toolchain)
        => string.Join('\u0001', toolchain.Env.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    public static string OutputExtension(string format) => format switch
    {
        "pe32" or "pe64" => ".exe",
        _ => string.Empty,
    };

    private static string Names(List<UnitSpec> units) => units.Count == 0 ? "none" : string.Join(", ", units.Select(u => u.Name));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashFileOrMissing(string path)
    {
        try
        {
            return File.Exists(path) ? Pe.PeImage.HashFile(path) : "missing";
        }
        catch (IOException)
        {
            return "unreadable";
        }
    }
}
