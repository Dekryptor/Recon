using System.Text.Json.Serialization;
using Recon.Config;
using Recon.Inventory;
using Recon.Toolchains;

namespace Recon.Build;

/// <summary>
/// What one <c>recon build</c> run needs to know. Everything else comes from the project: the units
/// say which toolchain each source uses, and <c>local.toml</c> says where that toolchain lives here.
/// </summary>
public sealed class BuildOptions
{
    /// <summary>Build only the unit with this name.</summary>
    public string? UnitFilter { get; set; }

    public int Jobs { get; set; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Plan and print, run nothing, write nothing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Ignore the cache and recompile everything.</summary>
    public bool Force { get; set; }

    /// <summary>Keep compiling other units after one fails.</summary>
    public bool KeepGoing { get; set; }

    /// <summary>Do not write <c>build/build.ninja</c>.</summary>
    public bool SkipNinja { get; set; }

    /// <summary>
    /// Compile at this optimization level instead of whatever the flags ask for — one of the levels
    /// the unit's profile declares, each with its own spelling. Asking for a level the profile does
    /// not declare changes nothing, which is how a toolchain with no such flag stays honest.
    /// </summary>
    public string? OptimizationLevel { get; set; }

    public string ToolVersion { get; set; } = Recon.ToolVersion.Current;
}

/// <summary>Version of the build manifest contract, independent of the TOML files' schema_version.</summary>
public static class BuildSchema
{
    public const string Version = "0.1";
}

/// <summary>
/// A toolchain bound to this machine: the profile plus the install that <c>local.toml</c> points at,
/// with the compiler and linker resolved to concrete commands.
/// </summary>
public sealed class BuildToolchainUse
{
    public string Id { get; set; } = string.Empty;

    public string Family { get; set; } = string.Empty;

    /// <summary>Install root from <c>local.toml</c>, if any.</summary>
    public string? Root { get; set; }

    /// <summary>Run the tools through Wine (the profile says <c>wine = true</c> in local.toml).</summary>
    public bool Wine { get; set; }

    public string Cc { get; set; } = string.Empty;

    public string Link { get; set; } = string.Empty;

    /// <summary>Environment every command of this toolchain runs with, with <c>{root}</c> substituted.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Hash of the parts of the profile that affect compilation and linking.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Set when the compiler could not be located; the build fails when it tries to run it.</summary>
    public string? CcProblem { get; set; }

    public string? LinkProblem { get; set; }

    public string ObjectExtension { get; set; } = ".o";
}

/// <summary>One file a unit was compiled from, as recorded by the toolchain's depfile.</summary>
public sealed class BuildDependency
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Hash at the time of the build; null when the file had disappeared.</summary>
    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }
}

/// <summary>A unit after planning: source in, object out, with the cache verdict already decided.</summary>
public sealed class PlannedUnit
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Source path relative to the project root, as written in <c>project.toml</c>.</summary>
    public string Source { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string ObjectFileName { get; set; } = string.Empty;

    public string ObjectPath { get; set; } = string.Empty;

    public string DepFilePath { get; set; } = string.Empty;

    public string ToolchainId { get; set; } = string.Empty;

    /// <summary>The resolved profile, kept so the compile command can be rendered without another lookup.</summary>
    public ToolchainProfile? Profile { get; set; }

    /// <summary>Sub-hashes of the cache key, so a rebuild can name what changed.</summary>
    public Dictionary<string, string> Parts { get; set; } = new(StringComparer.Ordinal);

    public string Provider { get; set; } = "rebuilt";

    public string Status { get; set; } = "not_started";

    public List<string> Flags { get; set; } = [];

    public List<string> Defines { get; set; } = [];

    public List<string> Includes { get; set; } = [];

    public string CacheKey { get; set; } = string.Empty;

    public bool Cached { get; set; }

    /// <summary>Why this unit is or is not a cache hit: shown by <c>--dry-run</c> and stored.</summary>
    public string CacheReason { get; set; } = string.Empty;

    public List<BuildDependency> Dependencies { get; set; } = [];

    /// <summary>Everything the unit contributes to the final image, in the order it was declared.</summary>
    public List<CoverSpec> Covers { get; set; } = [];

    public BuildToolchainUse Toolchain { get; set; } = new();

    /// <summary>A unit with <c>provider = "original"</c> keeps its bytes from the original image.</summary>
    public bool IsOriginal { get; set; }

    public bool ToolchainIsMissing => Toolchain.CcProblem is not null;
}

/// <summary>A command line the plan says to run, with everything it needs to be reproducible.</summary>
public sealed class BuildCommand
{
    public string Kind { get; set; } = "cc";

    public string Executable { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>True when the compiler should run through Wine.</summary>
    public bool Wine { get; set; }

    /// <summary>The command line a user could paste into a shell.</summary>
    public string Display => string.Join(' ', new[] { Wine ? "wine " + Quote(Executable) : Quote(Executable) }
        .Concat(Arguments.Select(Quote)));

    public static string Quote(string value)
        => value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'') ? value : "\"" + value.Replace("\"", "\\\"") + "\"";
}

/// <summary>The whole build, resolved but not yet run (or run, if the runner filled in results).</summary>
public sealed class BuildPlan
{
    public string ProjectName { get; set; } = string.Empty;

    public string Root { get; set; } = string.Empty;

    public string BuildDirectory { get; set; } = string.Empty;

    /// <summary>Where per-unit object files and depfiles go.</summary>
    public string ObjectsDirectory { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string NinjaPath { get; set; } = string.Empty;

    public string ManifestPath { get; set; } = string.Empty;

    public List<PlannedUnit> Units { get; set; } = [];

    public List<BuildToolchainUse> Toolchains { get; set; } = [];

    /// <summary>Set when the plan expects a link step, with the reason when it does not.</summary>
    public bool Links { get; set; }

    public string? LinkNote { get; set; }

    public string LinkToolchainId { get; set; } = string.Empty;

    public string LinkExecutable { get; set; } = string.Empty;

    public List<string> LinkFlags { get; set; } = [];

    public string LinkOutputFlag { get; set; } = string.Empty;

    public bool LinkWine { get; set; }

    public Dictionary<string, string> LinkEnvironment { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public PlannedUnit? Find(string name) => Units.FirstOrDefault(u => u.Name == name);

    public string Relative(string path)
        => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>The plan, the result and the cache: <c>build/build.json</c>.</summary>
public sealed class BuildManifest
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = BuildSchema.Version;

    [JsonPropertyName("generator")]
    public GeneratorInfo Generator { get; set; } = new();

    [JsonPropertyName("project")]
    public BuildProjectInfo Project { get; set; } = new();

    [JsonPropertyName("settings")]
    public BuildSettingsInfo Settings { get; set; } = new();

    [JsonPropertyName("toolchains")]
    public List<BuildToolchainInfo> Toolchains { get; set; } = [];

    [JsonPropertyName("units")]
    public List<BuildUnitInfo> Units { get; set; } = [];

    [JsonPropertyName("link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BuildLinkInfo? Link { get; set; }

    [JsonPropertyName("summary")]
    public BuildSummaryInfo Summary { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class BuildProjectInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("project_file")]
    public string ProjectFile { get; set; } = string.Empty;
}

public sealed class BuildSettingsInfo
{
    [JsonPropertyName("jobs")]
    public int Jobs { get; set; }

    [JsonPropertyName("force")]
    public bool Force { get; set; }

    [JsonPropertyName("keep_going")]
    public bool KeepGoing { get; set; }

    [JsonPropertyName("dry_run")]
    public bool DryRun { get; set; }

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;
}

public sealed class BuildToolchainInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; set; }

    [JsonPropertyName("cc")]
    public string Cc { get; set; } = string.Empty;

    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;

    [JsonPropertyName("wine")]
    public bool Wine { get; set; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class BuildUnitInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("toolchain")]
    public string Toolchain { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "rebuilt";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "not_started";

    [JsonPropertyName("flags")]
    public List<string> Flags { get; set; } = [];

    [JsonPropertyName("defines")]
    public List<string> Defines { get; set; } = [];

    [JsonPropertyName("includes")]
    public List<string> Includes { get; set; } = [];

    [JsonPropertyName("cache_key")]
    public string CacheKey { get; set; } = string.Empty;

    [JsonPropertyName("cached")]
    public bool Cached { get; set; }

    [JsonPropertyName("cache_reason")]
    public string CacheReason { get; set; } = string.Empty;

    /// <summary>Sub-hashes of the cache key, so a rebuild can name the part that changed.</summary>
    [JsonPropertyName("inputs")]
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("dependencies")]
    public List<BuildDependency> Dependencies { get; set; } = [];

    [JsonPropertyName("covers")]
    public List<BuildCoverInfo> Covers { get; set; } = [];

    /// <summary>cached | compiled | failed | skipped | original | planned.</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "planned";

    [JsonPropertyName("exit_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("diagnostics")]
    public List<string> Diagnostics { get; set; } = [];
}

public sealed class BuildCoverInfo
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
}

public sealed class BuildLinkInfo
{
    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("executable")]
    public string Executable { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = [];

    [JsonPropertyName("result")]
    public string Result { get; set; } = "planned";

    [JsonPropertyName("exit_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("diagnostics")]
    public List<string> Diagnostics { get; set; } = [];
}

public sealed class BuildSummaryInfo
{
    [JsonPropertyName("units")]
    public int Units { get; set; }

    [JsonPropertyName("compiled")]
    public int Compiled { get; set; }

    [JsonPropertyName("cached")]
    public int Cached { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("output_size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputSize { get; set; }

    [JsonPropertyName("output_sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputSha256 { get; set; }
}

/// <summary>Reads and writes <c>build/build.json</c>, the cache and the record of the last build.</summary>
public static class BuildManifestStore
{
    public static BuildManifest? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(path),
                Reporting.ReportsJsonContext.Default.BuildManifest);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>A place for build progress to go, so the core stays independent of the CLI's output rules.</summary>
public interface IBuildLog
{
    void Info(string text);

    void Step(string text);

    void Warn(string text);

    void Error(string text);
}

/// <summary>The result of running a plan: the manifest plus whether the build succeeded.</summary>
public sealed class BuildResult
{
    public required BuildManifest Manifest { get; init; }

    public bool Failed => Manifest.Summary.Failed > 0 || Manifest.Problems.Count > 0;
}
