using Recon.Toml;

namespace Recon.Config;

public static class KnownValues
{
    public static readonly string[] Roles = ["original", "debug", "map", "reference"];
    public static readonly string[] Formats = ["pe32", "pe64", "elf32", "elf64", "macho32", "macho64"];
    public static readonly string[] Architectures = ["x86", "x64", "arm", "arm64"];
    public static readonly string[] InstructionSets = ["x86", "x64", "arm", "arm64", "vb6-pcode"];
    public static readonly string[] Providers = ["rebuilt", "original"];
    public static readonly string[] Statuses = ["not_started", "wip", "matched", "approx", "library", "skip"];
    public static readonly string[] Confidences = ["low", "medium", "high"];
}

public sealed class ProjectMeta
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
}

public sealed class TargetSpec
{
    public string Format { get; set; } = string.Empty;

    public string Arch { get; set; } = string.Empty;

    /// <summary>Instruction set: usually the same as <see cref="Arch"/>, but not always (VB6 p-code).</summary>
    public string Isa { get; set; } = string.Empty;

    public string? DefaultToolchain { get; set; }

    public static TargetSpec FromScope(TableScope scope)
    {
        var target = new TargetSpec
        {
            Format = scope.Enum("format", KnownValues.Formats, string.Empty) ?? string.Empty,
            Arch = scope.Enum("arch", KnownValues.Architectures, string.Empty) ?? string.Empty,
        };
        target.Isa = scope.Enum("isa", KnownValues.InstructionSets, target.Arch) ?? target.Arch;
        target.DefaultToolchain = scope.String("default_toolchain");
        return target;
    }
}

public sealed class InputSpec
{
    public string Id { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    /// <summary>File name only: the directory comes from <c>local.toml</c>.</summary>
    public string File { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    /// <summary>For debug info and maps: the id of the input they describe.</summary>
    public string? For { get; set; }
}

public sealed class PathsSpec
{
    public string Source { get; set; } = "src";

    public List<string> Include { get; set; } = ["include"];

    public string Build { get; set; } = "build";

    public List<string> Profiles { get; set; } = ["toolchains"];
}

public sealed class DefaultsSpec
{
    public List<string> Flags { get; set; } = [];

    public List<string> Defines { get; set; } = [];

    /// <summary>Appended to the linker command line after the profile's own link flags.</summary>
    public List<string> LinkFlags { get; set; } = [];
}

public sealed class AnalysisSpec
{
    public string MinFunctionConfidence { get; set; } = "low";

    /// <summary>
    /// Path of a pattern file (<c>recon sigs build</c>) to name functions from, relative to the
    /// project directory. Patterns name only what no symbol named, so leaving this set is safe.
    /// </summary>
    public string? Signatures { get; set; }

    public List<uint> ExtraEntryPoints { get; set; } = [];

    public List<string> NoReturn { get; set; } = [];

    public List<DataRangeSpec> DataRanges { get; set; } = [];
}

public sealed class DataRangeSpec
{
    public uint Rva { get; set; }

    public uint Size { get; set; }

    public string Kind { get; set; } = "data";
}

public sealed class CoverSpec
{
    public string? Symbol { get; set; }

    public uint? Rva { get; set; }

    public uint? Size { get; set; }
}

public sealed class UnitSpec
{
    public string Name { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? Toolchain { get; set; }

    public List<string> Flags { get; set; } = [];

    public List<string> Defines { get; set; } = [];

    public string Provider { get; set; } = "rebuilt";

    public string Status { get; set; } = "not_started";

    public List<CoverSpec> Covers { get; set; } = [];
}

public sealed class ReportSpec
{
    public string Output { get; set; } = "build/report";

    public bool History { get; set; }
}

/// <summary>
/// The portable, committed project file. Everything machine-specific lives in <see cref="LocalConfig"/>.
/// </summary>
public sealed class ProjectConfig
{
    public int SchemaVersion { get; set; }

    public ProjectMeta Project { get; set; } = new();

    public TargetSpec Target { get; set; } = new();

    public List<InputSpec> Inputs { get; set; } = [];

    public PathsSpec Paths { get; set; } = new();

    public DefaultsSpec Defaults { get; set; } = new();

    public AnalysisSpec Analysis { get; set; } = new();

    public List<UnitSpec> Units { get; set; } = [];

    public ReportSpec Report { get; set; } = new();

    /// <summary>Path of the file this was loaded from.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Directory containing <c>project.toml</c>. All relative paths resolve against it.</summary>
    public string RootDirectory { get; set; } = string.Empty;

    public InputSpec? Original => Inputs.FirstOrDefault(i => i.Role == "original");

    public InputSpec? DebugInfo => Inputs.FirstOrDefault(i => i.Role == "debug");

    public InputSpec? MapFile => Inputs.FirstOrDefault(i => i.Role == "map");

    public InputSpec? FindInput(string id) => Inputs.FirstOrDefault(i => i.Id == id);

    public string ResolveToolchain(UnitSpec unit) => unit.Toolchain ?? Target.DefaultToolchain ?? string.Empty;

    public static ProjectConfig Load(string path, Diagnostics diagnostics)
    {
        var document = TomlLoader.LoadDocument(path, diagnostics);
        var root = TableScope.Root(document, diagnostics);

        var config = new ProjectConfig
        {
            FilePath = Path.GetFullPath(path),
            RootDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
        };

        config.SchemaVersion = (int)root.RequireInteger("schema_version");
        SchemaVersions.CheckSchemaVersion(config.SchemaVersion, document.File, root.Line, "schema_version", diagnostics);

        var project = root.Child("project");
        config.Project.Name = project.RequireString("name");
        config.Project.Description = project.String("description");
        project.RejectUnknownKeys();

        var target = root.Child("target", required: true);
        config.Target = TargetSpec.FromScope(target);
        target.RejectUnknownKeys();

        foreach (var scope in root.Tables("input"))
        {
            config.Inputs.Add(new InputSpec
            {
                Id = scope.RequireString("id"),
                Role = scope.Enum("role", KnownValues.Roles, string.Empty) ?? string.Empty,
                File = scope.RequireString("file"),
                Sha256 = scope.RequireString("sha256").ToLowerInvariant(),
                For = scope.String("for"),
            });
            scope.RejectUnknownKeys();
        }

        var paths = root.Child("paths");
        if (paths.Present)
        {
            config.Paths.Source = paths.String("source", config.Paths.Source) ?? config.Paths.Source;
            config.Paths.Include = paths.Has("include") ? paths.StringArray("include") : config.Paths.Include;
            config.Paths.Build = paths.String("build", config.Paths.Build) ?? config.Paths.Build;
            config.Paths.Profiles = paths.Has("profiles") ? paths.StringArray("profiles") : config.Paths.Profiles;
            paths.RejectUnknownKeys();
        }

        var defaults = root.Child("defaults");
        if (defaults.Present)
        {
            config.Defaults.Flags = defaults.StringArray("flags");
            config.Defaults.Defines = defaults.StringArray("defines");
            config.Defaults.LinkFlags = defaults.StringArray("link_flags");
            defaults.RejectUnknownKeys();
        }

        var analysis = root.Child("analysis");
        if (analysis.Present)
        {
            config.Analysis.MinFunctionConfidence = analysis.Enum("min_function_confidence", KnownValues.Confidences, "low") ?? "low";
            config.Analysis.Signatures = analysis.String("signatures");
            config.Analysis.ExtraEntryPoints = analysis.UInt32Array("extra_entry_points");
            config.Analysis.NoReturn = analysis.StringArray("no_return");
            foreach (var range in analysis.Tables("data_range"))
            {
                config.Analysis.DataRanges.Add(new DataRangeSpec
                {
                    Rva = range.UInt32("rva") ?? 0,
                    Size = range.UInt32("size") ?? 0,
                    Kind = range.String("kind", "data") ?? "data",
                });
                range.RejectUnknownKeys();
            }

            analysis.RejectUnknownKeys();
        }

        foreach (var scope in root.Tables("unit"))
        {
            var unit = new UnitSpec
            {
                Name = scope.RequireString("name"),
                Source = scope.RequireString("source"),
                Toolchain = scope.String("toolchain"),
                Flags = scope.StringArray("flags"),
                Defines = scope.StringArray("defines"),
                Provider = scope.Enum("provider", KnownValues.Providers, "rebuilt") ?? "rebuilt",
                Status = scope.Enum("status", KnownValues.Statuses, "not_started") ?? "not_started",
            };

            foreach (var cover in scope.Tables("covers"))
            {
                unit.Covers.Add(new CoverSpec
                {
                    Symbol = cover.String("symbol"),
                    Rva = cover.UInt32("rva"),
                    Size = cover.UInt32("size"),
                });
                cover.RejectUnknownKeys();
            }

            scope.RejectUnknownKeys();
            config.Units.Add(unit);
        }

        var report = root.Child("report");
        if (report.Present)
        {
            config.Report.Output = report.String("output", config.Report.Output) ?? config.Report.Output;
            config.Report.History = report.Bool("history", false) ?? false;
            report.RejectUnknownKeys();
        }

        root.RejectUnknownKeys();

        ValidateProject(config, diagnostics);
        return config;
    }

    private static void ValidateProject(ProjectConfig config, Diagnostics diagnostics)
    {
        if (config.Inputs.Count == 0)
        {
            diagnostics.Error(config.FilePath, 0, "input", "at least one input is required");
        }

        if (config.Inputs.Count(i => i.Role == "original") != 1)
        {
            diagnostics.Error(
                config.FilePath,
                0,
                "input",
                $"exactly one input with role \"original\" is required (found {config.Inputs.Count(i => i.Role == "original")})");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in config.Inputs)
        {
            if (!ids.Add(input.Id))
            {
                diagnostics.Error(config.FilePath, 0, "input.id", $"duplicate input id \"{input.Id}\"");
            }

            if (!SchemaVersions.IsSha256(input.Sha256))
            {
                diagnostics.Error(config.FilePath, 0, $"input[{input.Id}].sha256", "must be a lowercase SHA-256 hex digest (use `recon hash` or `recon init`)");
            }

            if (input.File.Contains('/') || input.File.Contains('\\'))
            {
                diagnostics.Error(config.FilePath, 0, $"input[{input.Id}].file", "must be a file name without a directory; the directory comes from local.toml");
            }

            if (input.For is not null && config.FindInput(input.For) is null)
            {
                diagnostics.Error(config.FilePath, 0, $"input[{input.Id}].for", $"\"{input.For}\" does not match any input id");
            }
        }

        foreach (var unit in config.Units)
        {
            if (unit.Covers.Any(c => c.Symbol is null && (c.Rva is null || c.Size is null)))
            {
                diagnostics.Error(config.FilePath, 0, $"unit[{unit.Name}].covers", "each entry needs either a symbol or both rva and size");
            }
        }
    }
}
