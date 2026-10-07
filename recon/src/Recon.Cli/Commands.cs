using Recon.Images;
using Recon.Analysis;
using Recon.Build;
using Recon.Compare;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Inventory;
using Recon.Pe;
using Recon.Project;
using Recon.Reporting;
using Recon.Schemas;
using Recon.Toolchains;
using Recon.Verify;

namespace Recon.Cli;

internal sealed class Commands(Output output, CommandLine commandLine)
{
    private readonly Output _out = output;
    private readonly CommandLine _args = commandLine;

    /// <summary>
    /// Emits a JSON document, and checks it against the schema it claims to follow when
    /// <c>--check-schema</c> was given. Every command that prints a document does it this way, so
    /// every published contract is checked by the thing that publishes it.
    /// </summary>
    private void EmitWithSchemaCheck<T>(T document, string schema)
    {
        _out.EmitJson(document);

        if (!_args.Has("--check-schema"))
        {
            return;
        }

        int violations = ValidateAgainstSchema(schema, Recon.Reporting.Reports.Serialize(document));
        _out.Line(violations == 0
            ? $"{schema} document matches schema 0.1"
            : $"{schema} document has {violations} schema violation(s)");
    }

    // ---------------------------------------------------------------- init

    public int Init()
    {
        // `--project` names the directory to create the project in. Every other command resolves it
        // to a project.toml and opens that; here the file does not exist yet, so the only thing it
        // can mean is where to put one — and falling through to the current directory instead would
        // write a project wherever the user happens to be standing, which is what it used to do.
        string? fromProject = _args.ProjectPath is null ? null : InitDirectory(_args.ProjectPath);
        string directory = _args.Arguments.FirstOrDefault() ?? _args.Value("--dir") ?? fromProject ?? Directory.GetCurrentDirectory();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);

        string projectFile = Path.Combine(directory, ProjectContext.ProjectFileName);
        string localFile = Path.Combine(directory, ProjectContext.LocalFileName);
        bool force = _args.Has("--force");

        var document = new Recon.Reporting.InitDocument
        {
            Command = "recon init",
            ToolVersion = EntryPoint.Version,
            Directory = directory,
        };

        if (File.Exists(projectFile) && !force)
        {
            _out.Error($"{projectFile} already exists (use --force to overwrite)");
            return ExitCodes.Usage;
        }

        string name = _args.Value("--name") ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "sample";
        }

        string? binaryPath = _args.Value("--binary");
        string? binaryName = null;
        string? binaryHash = null;
        if (binaryPath is not null)
        {
            if (!File.Exists(binaryPath))
            {
                _out.Error($"binary not found: {binaryPath}");
                return ExitCodes.Usage;
            }

            binaryName = Path.GetFileName(binaryPath);
            binaryHash = PeImage.HashFile(binaryPath);
        }

        document.Name = name;
        document.ProjectFile = projectFile;

        string projectContent = BuildProjectTemplate(name, binaryName, binaryHash);
        File.WriteAllText(projectFile, projectContent);
        _out.Line($"wrote {projectFile}");
        document.Files.Add(new Recon.Reporting.WrittenFile { Path = projectFile, Kind = "project", Written = true });

        if (!File.Exists(localFile))
        {
            File.WriteAllText(localFile, BuildLocalTemplate());
            _out.Line($"wrote {localFile} (machine-specific; keep it out of version control)");
            document.LocalFile = localFile;
            document.Files.Add(new Recon.Reporting.WrittenFile { Path = localFile, Kind = "local", Written = true });
        }

        string inputsDirectory = Path.Combine(directory, "inputs");
        Directory.CreateDirectory(inputsDirectory);
        document.InputsDirectory = inputsDirectory;

        if (!_args.Has("--no-profiles"))
        {
            string profilesDirectory = Path.Combine(directory, "toolchains");
            int written = BuiltInProfiles.WriteTo(profilesDirectory);
            _out.Line(written > 0
                ? $"wrote {written} toolchain profile(s) to {profilesDirectory}"
                : $"toolchain profiles already present in {profilesDirectory}");
            document.ProfilesDirectory = profilesDirectory;
            document.ProfilesWritten = written;
        }

        string gitignore = Path.Combine(directory, ".gitignore");
        var ignore = new List<string>();
        if (File.Exists(gitignore))
        {
            ignore.AddRange(File.ReadAllLines(gitignore));
        }

        bool changed = false;
        foreach (var entry in new[] { "local.toml", "build/", "inputs/" })
        {
            if (!ignore.Contains(entry))
            {
                ignore.Add(entry);
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllLines(gitignore, ignore);
            _out.Line($"updated {gitignore}");
            document.Files.Add(new Recon.Reporting.WrittenFile { Path = gitignore, Kind = "gitignore", Written = true });
        }

        if (binaryPath is not null)
        {
            string target = Path.Combine(inputsDirectory, binaryName!);
            _out.Line($"note: the binary itself is never copied. Move it to {target} so `recon verify` finds it,");
            _out.Line("      or point inputs.dir in local.toml at the directory that already holds it.");

            document.Binary = new Recon.Reporting.InitBinary
            {
                File = binaryName!,
                Sha256 = binaryHash!,
                ExpectedAt = target,
                Copied = false,
            };
            document.Notes.Add($"the binary was hashed but not copied; move it to {target} so `recon verify` finds it");
        }

        EmitWithSchemaCheck(document, "init");
        return ExitCodes.Ok;
    }

    private static string BuildProjectTemplate(string name, string? binaryName, string? binaryHash)
    {
        string file = binaryName ?? "original.exe";
        string hash = binaryHash ?? new string('0', 64);
        return $"""
        schema_version = 1

        [project]
        name = "{name}"
        description = "Reconstruction of {file}"

        [target]
        format = "pe32"
        arch = "x86"
        isa = "x86"
        # default_toolchain = "msvc-2008"

        [[input]]
        id = "main"
        role = "original"
        file = "{file}"
        sha256 = "{hash}"

        [paths]
        source = "src"
        include = ["include"]
        build = "build"
        profiles = ["toolchains"]

        [analysis]
        min_function_confidence = "low"

        [report]
        output = "build/report"
        history = true
        """;
    }

    private static string BuildLocalTemplate() => """
        schema_version = 1

        # Machine-specific paths. This file is not committed.
        [inputs]
        dir = "inputs"

        # [toolchain.msvc-2008]
        # root = "C:/tools/msvc2008"
        # env = { SDK = "C:/tools/winsdk61" }
        """;

    // ---------------------------------------------------------------- verify / validate / doctor

    public int Verify()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        var report = Verifier.Verify(context, includeToolchains: !_args.Has("--no-toolchains"));
        EmitWithSchemaCheck(report, "verify");

        _out.Table(
            report.Inputs.Select(i => new[]
            {
                i.Id,
                i.Role,
                i.Exists ? "found" : "missing",
                i.Ok ? "hash ok" : i.Actual is null ? "-" : $"{i.Actual[..12]}...",
                i.Message ?? string.Empty,
            }),
            "input", "role", "file", "sha256", "note");

        if (report.Units.Count > 0)
        {
            _out.Table(
                report.Units.Select(u => new[]
                {
                    u.Name,
                    u.Source,
                    u.Exists ? "found" : "missing",
                    u.Toolchain.Length == 0 ? "-" : u.Toolchain,
                    u.Ok ? "buildable" : u.Message ?? string.Empty,
                }),
                "unit", "source", "file", "toolchain", "note");
        }

        foreach (var check in report.Toolchains)
        {
            _out.Line($"toolchain {check.Id}: {(check.Ok ? "ok" : "failed")} ({check.FilesChecked} files checked)");
        }

        if (report.DebugMatchesBinary is false)
        {
            _out.Error("debug info does not belong to this binary (GUID/age differ)");
        }

        foreach (var message in report.Messages)
        {
            _out.Warn(message);
        }

        _out.Line(report.Ok ? "verify: ok" : "verify: FAILED");
        return report.Ok ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    public int Validate()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        int problems = 0;
        foreach (var diagnostic in context.Diagnostics.Items)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                _out.Error(diagnostic.ToString());
                problems++;
            }
            else
            {
                _out.Warn(diagnostic.ToString());
            }
        }

        _out.Line($"project.toml: {(problems == 0 ? "valid" : "invalid")}");
        _out.Line($"local.toml: {(context.Local.IsPresent ? "valid" : "absent")}");
        _out.Line($"toolchain profiles: {string.Join(", ", context.Registry.KnownIds)}");

        string? inventoryPath = _args.Value("--inventory");
        if (inventoryPath is null)
        {
            string defaultPath = Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "inventory.json");
            if (File.Exists(defaultPath))
            {
                inventoryPath = defaultPath;
            }
        }

        if (inventoryPath is not null)
        {
            if (!File.Exists(inventoryPath))
            {
                _out.Error($"inventory not found: {inventoryPath}");
                return ExitCodes.Usage;
            }

            var violations = ValidateInventoryFile(inventoryPath);
            foreach (var violation in violations)
            {
                _out.Error($"{inventoryPath}: {violation}");
            }

            _out.Line($"{inventoryPath}: {(violations.Count == 0 ? "matches inventory schema 0.1" : $"{violations.Count} schema violation(s)")}");
            problems += violations.Count;
        }

        _out.Line(problems == 0 ? "validate: ok" : "validate: FAILED");
        return problems == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    /// <summary>
    /// The three things a listing needs from an inventory: the functions, the imports and the data.
    /// A command that only lists instructions asks for these rather than for the document.
    /// </summary>
    private sealed class Listing
    {
        public List<(uint Start, uint Size, string? Name, string? Demangled)> Functions { get; } = [];

        public List<(uint Rva, string Name)> Imports { get; } = [];

        public List<(uint Rva, string Name)> Data { get; } = [];

        public static Listing FromDocument(InventoryDocument document)
        {
            var listing = new Listing();
            foreach (var function in document.Functions)
            {
                var range = function.Ranges[0];
                listing.Functions.Add((range.Rva, range.Size, function.Name, function.Demangled));
            }

            foreach (var import in document.Imports)
            {
                listing.Imports.Add((import.IatRva, $"{import.Dll}!{import.Name ?? "#" + import.Ordinal}"));
            }

            foreach (var entry in document.Data)
            {
                listing.Data.Add((entry.Rva, entry.Name ?? entry.Kind));
            }

            return listing;
        }
    }

    /// <summary>
    /// The inventory already on disk, if it is one this build wrote about this binary — otherwise null,
    /// and the caller runs the analysis. The `--verbose` line says which of the two happened, because a
    /// listing that came from a stale document should be impossible to mistake for a fresh one.
    /// </summary>
    private Listing? ReadListing(ProjectContext context)
    {
        var input = context.Project.Inputs.FirstOrDefault(i => i.Role == "original");
        if (input is null || input.Sha256.Length == 0)
        {
            return null;
        }

        string path = Path.Combine(context.RootDirectory, context.Project.Paths.Build, "inventory.json");
        var cached = InventoryCache.Read(path, input.Sha256, EntryPoint.Version);
        if (cached is null)
        {
            return null;
        }

        // The file has to be newer than the binary it describes: a rebuilt original makes the document
        // beside it a reading of the previous build.
        string? binary = context.ResolveInputPath(input);
        if (binary is not null && File.Exists(binary) && File.GetLastWriteTimeUtc(binary) > File.GetLastWriteTimeUtc(path))
        {
            return null;
        }

        _out.Debug($"listed from {path}");

        var listing = new Listing();
        foreach (var function in cached.Functions)
        {
            listing.Functions.Add((function.Start, function.Size, function.Name, null));
        }

        listing.Imports.AddRange(cached.Imports.Select(i => (i.Rva, i.Name)));
        listing.Data.AddRange(cached.Data.Select(d => (d.Rva, d.Name)));
        return listing;
    }

    /// <summary>Milliseconds since a start stamp, for the phase lines a verbose run prints.</summary>
    private static long Millis(long start) => (long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private static List<string> ValidateInventoryFile(string path)
    {
        var schemaJson = BuiltInSchemas.Get("inventory");
        if (schemaJson is null)
        {
            return ["the inventory schema is missing from this build"];
        }

        var validator = Recon.Schema.JsonSchemaValidator.Parse(schemaJson);
        return validator.Validate(File.ReadAllText(path)).Select(v => v.ToString()).ToList();
    }

    public int Doctor()
    {
        var diagnostics = new Diagnostics();
        string? projectFile = ResolveProjectFile(_args.ProjectPath);
        ProjectContext? context = projectFile is null ? null : ProjectContext.Load(projectFile, diagnostics);

        var report = Recon.Doctor.Diagnose(context, EntryPoint.Version);
        EmitWithSchemaCheck(report, "doctor");

        _out.Table(
            report.Checks.Select(c => new[] { c.Status.ToUpperInvariant(), c.Name, c.Detail }),
            "status", "check", "detail");

        foreach (var check in report.Checks.Where(c => c.Hint is not null))
        {
            _out.Line($"  hint: {check.Hint}");
        }

        _out.Line(report.Ok ? "doctor: ok" : "doctor: problems found");
        return report.Ok ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    // ---------------------------------------------------------------- inventory

    public int Inventory()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        // An inventory belongs to the project it is written into: the paths, the signatures and the
        // declared input all come from there, and the file it reads is the one the project names. A
        // positional argument that names some *other* file used to be ignored in silence — the command
        // inventoried the project's input and said nothing about the file it had been handed, which is
        // how a loop over 42 programs measured one of them 42 times. It is a usage error now.
        if (_args.Arguments.FirstOrDefault() is { } argument
            && !IsTheProjectsInput(context, argument))
        {
            string declared = context.Project.Original is { } original
                ? context.ResolveInputPath(original)
                : "(none declared)";
            _out.Error(
                $"inventory reads the input its project declares, which is {declared}; \"{argument}\" is not " +
                "that file. Point a project at it (recon init <dir> --binary <file>) and pass that project");
            return ExitCodes.Usage;
        }

        // Timed at this level too, because the analysis is not the whole cost: a large inventory is also
        // a large JSON document, and a reader asking "why does this take half a minute" needs to see the
        // writing of the file as a line of its own.
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var signatures = LoadSignatures(context, required: false);
        var document = BuildInventory(context, includeXrefs: !_args.Has("--no-xrefs"), signatures: signatures);
        if (document is null)
        {
            return ExitCodes.CheckFailed;
        }

        _out.Debug($"inventory built in {Millis(started)} ms");

        string json = InventoryJson.Serialize(document);
        _out.Debug($"serialized {json.Length / 1024 / 1024} MB in {Millis(started)} ms");
        string path = _args.Output ?? Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "inventory.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        _out.Debug($"written to {path} in {Millis(started)} ms");

        int violations = 0;
        if (_args.Has("--check-schema"))
        {
            var schemaJson = BuiltInSchemas.Get("inventory");
            if (schemaJson is not null)
            {
                var validator = Recon.Schema.JsonSchemaValidator.Parse(schemaJson);
                var problems = validator.Validate(json);
                foreach (var problem in problems)
                {
                    _out.Error($"schema violation: {problem}");
                }

                violations = problems.Count;
                _out.Line(violations == 0 ? "inventory matches schema 0.1" : $"inventory has {violations} schema violation(s)");
            }
        }

        // The inventory has its own source-generated serializer, so the text is already written.
        _out.EmitJsonText(json);
        _out.Line($"wrote {path}");
        foreach (var (key, value) in document.Statistics.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            _out.Line($"  {key,-32} {value}");
        }

        foreach (var problem in document.Problems)
        {
            _out.Warn(problem);
        }

        return violations == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    private InventoryDocument? BuildInventory(
        ProjectContext context,
        bool includeXrefs,
        bool? timestamp = null,
        Recon.Signatures.SignatureDatabase? signatures = null)
    {
        var image = context.LoadImage();
        if (image.Image is null)
        {
            foreach (var problem in image.Problems)
            {
                _out.Error(problem);
            }

            return null;
        }

        var debug = context.LoadDebugInfo(image.Image);
        var options = context.BuildAnalysisOptions();
        options.BuildXrefs = includeXrefs;
        if (_out.Verbose && !_out.Json)
        {
            options.Phase = (name, elapsed) => _out.Debug($"  {name,-12} {elapsed.TotalMilliseconds,7:F0} ms");
        }

        var profile = ToolchainSelector.Choose(
            context.Registry,
            context.Project.Target.DefaultToolchain,
            image.Image,
            debug,
            context.Dwarf);

        var document = InventoryBuilder.Build(new InventoryInputs
        {
            PcodeRuntimePath = PcodeRuntimeFor(context, image.Image),
            Project = context.Project,
            Local = context.Local,
            Image = image.Image,
            Bytes = image.Bytes,
            Debug = debug,
            Dwarf = context.Dwarf,
            Signatures = signatures,
            Registry = context.Registry,
            Profile = profile,
            Options = options,
            Command = _args.JoinedCommand,
            IncludeTimestamp = timestamp ?? _args.Has("--timestamp"),
            ToolVersion = EntryPoint.Version,
        });

        document.Binary.ConfiguredToolchain = profile?.Id ?? document.Binary.ConfiguredToolchain;
        return document;
    }

    // ---------------------------------------------------------------- inspect

    public int Inspect()
    {
        string? subcommand = _args.Subcommand;
        if (subcommand is null)
        {
            _out.Error("usage: recon inspect <sections|imports|exports|relocs|data|functions|xrefs|producers|debug|tls|stats>");
            return ExitCodes.Usage;
        }

        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        var document = BuildInventory(
            context,
            includeXrefs: subcommand is "xrefs" or "stats",
            signatures: LoadSignatures(context, required: false));
        if (document is null)
        {
            return ExitCodes.CheckFailed;
        }

        int limit = _args.Count() ?? 100;
        string? filter = _args.Value("--filter");
        var binary = document.Binary;

        switch (subcommand)
        {
            case "sections":
                {
        EmitWithSchemaCheck(new SectionsReport
                    {
                        Sections = document.Sections,
                        ImageBase = binary.ImageBase,
                        EntryRva = binary.EntryRva,
                    }, "inspect-sections");
                    _out.Table(
                        document.Sections.Select(s => new[]
                        {
                            s.Name,
                            $"0x{s.Rva:X8}",
                            $"0x{s.VirtualSize:X}",
                            $"0x{s.RawSize:X}",
                            string.Join(",", s.Flags),
                        }),
                        "name", "rva", "vsize", "rawsize", "flags");
                    return ExitCodes.Ok;
                }

            case "imports":
                {
                    var imports = document.Imports;
        EmitWithSchemaCheck(new ImportsReport
                    {
                        Imports = imports,
                        Dlls = imports.Select(i => i.Dll).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        Functions = imports.Count,
                    }, "inspect-imports");
                    _out.Table(
                        imports.Take(limit).Select(i => new[] { i.Dll, i.Name ?? $"#{i.Ordinal}", $"0x{i.IatRva:X8}" }),
                        "dll", "function", "iat_rva");
                    return ExitCodes.Ok;
                }

            case "exports":
                {
        EmitWithSchemaCheck(new ExportsReport { Exports = document.Exports }, "inspect-exports");
                    _out.Table(
                        document.Exports.Take(limit).Select(e => new[] { e.Name ?? $"#{e.Ordinal}", $"0x{e.Rva:X8}", e.Forwarder ?? string.Empty }),
                        "name", "rva", "forwarder");
                    return ExitCodes.Ok;
                }

            case "relocs":
                {
        EmitWithSchemaCheck(new RelocsReport
                    {
                        Relocations = document.Relocations,
                        Total = document.Relocations.Count,
                        Unresolved = document.Relocations.Count(r => r.TargetRva is null),
                    }, "inspect-relocs");
                    _out.Table(
                        document.Relocations.Take(limit).Select(r => new[]
                        {
                            $"0x{r.Rva:X8}",
                            r.Kind,
                            r.TargetRva is null ? "unresolved" : $"0x{r.TargetRva:X8}",
                            r.InFunction ?? r.InData ?? string.Empty,
                        }),
                        "at", "kind", "target", "owner");
                    return ExitCodes.Ok;
                }

            case "data":
                {
                    bool all = _args.Has("--all");
                    var symbols = document.Data.Where(d => !string.Equals(d.Source, "section", StringComparison.Ordinal)).ToList();
                    var sectionRanges = document.Data.Where(d => string.Equals(d.Source, "section", StringComparison.Ordinal)).ToList();
                    if (filter is not null)
                    {
                        symbols = [.. symbols.Where(d => d.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)];
                    }

        EmitWithSchemaCheck(new DataReport
                    {
                        Symbols = symbols,
                        SectionRanges = all ? sectionRanges : [],
                        Named = symbols.Count(d => d.Name is not null),
                        TotalBytes = symbols.Aggregate(0UL, (sum, d) => sum + d.Size),
                    }, "inspect-data");

                    if (symbols.Count == 0)
                    {
                        _out.Line("no data symbols");
                    }

                    _out.Table(
                        symbols.Take(limit).Select(d => new[]
                        {
                            $"0x{d.Rva:X8}",
                            d.Size == 0 ? string.Empty : $"0x{d.Size:X}",
                            d.Name ?? string.Empty,
                            d.Kind,
                            d.Source,
                        }),
                        "rva", "size", "name", "kind", "source");

                    if (symbols.Count > limit || all)
                    {
                        _out.Line($"  {symbols.Count} symbol(s), {sectionRanges.Count} section range(s) " +
                                  $"(section ranges come from the section table; --all lists them)");
                    }

                    if (all)
                    {
                        _out.Table(
                            sectionRanges.Take(limit).Select(d => new[]
                            {
                                $"0x{d.Rva:X8}",
                                $"0x{d.Size:X}",
                                d.Name ?? string.Empty,
                                d.Kind,
                                d.Source,
                            }),
                            "rva", "size", "name", "kind", "source");
                    }

                    return ExitCodes.Ok;
                }

            case "functions":
                {
                    var functions = document.Functions
                        .Where(f => filter is null
                                    || (f.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                                    || (f.Demangled?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();

        EmitWithSchemaCheck(new FunctionsReport { Functions = functions, Statistics = document.Statistics }, "inspect-functions");
                    _out.Table(
                        functions.Take(limit).Select(f => new[]
                        {
                            f.Id,
                            f.Name ?? "(unnamed)",
                            $"0x{f.Ranges[0].Rva:X8}",
                            f.Ranges[0].Size.ToString(),
                            f.Confidence,
                            f.CallingConvention.Value,
                            string.Join(",", f.Flags),
                        }),
                        "id", "name", "rva", "size", "conf", "cc", "flags");
                    if (functions.Count > limit)
                    {
                        _out.Line($"... {functions.Count - limit} more (use --limit)");
                    }

                    return ExitCodes.Ok;
                }

            case "xrefs":
                {
        EmitWithSchemaCheck(new XrefsReport { Xrefs = document.Xrefs, Total = document.Xrefs.Count }, "inspect-xrefs");
                    _out.Table(
                        document.Xrefs.Take(limit).Select(x => new[]
                        {
                            $"0x{x.FromRva:X8}",
                            $"0x{x.ToRva:X8}",
                            x.Kind,
                            x.ViaReloc ? "reloc" : string.Empty,
                            x.InFunction ?? string.Empty,
                            x.ToFunction ?? string.Empty,
                        }),
                        "from", "to", "kind", "via", "in_function", "to_function");
                    if (document.Xrefs.Count > limit)
                    {
                        _out.Line($"... {document.Xrefs.Count - limit} more (use --limit)");
                    }

                    return ExitCodes.Ok;
                }

            case "producers":
                {
        EmitWithSchemaCheck(new ProducersReport
                    {
                        Producers = binary.Producers,
                        Suggestions = binary.ToolchainSuggestions,
                        Problems = document.Problems,
                    }, "inspect-producers");
                    _out.Table(
                        binary.Producers.Select(p => new[]
                        {
                            p.Kind,
                            p.Tool,
                            p.Version ?? string.Empty,
                            p.Count?.ToString() ?? string.Empty,
                            p.Detail ?? string.Empty,
                            p.Matches.Count > 0 ? string.Join(",", p.Matches) : string.Empty,
                        }),
                        "kind", "tool", "version", "count", "detail", "matches");
                    _out.Line(string.Empty);
                    _out.Line("toolchain suggestions (evidence, not conclusions):");
                    foreach (var suggestion in binary.ToolchainSuggestions)
                    {
                        _out.Line($"  {suggestion.Id}: {suggestion.Confidence} via {string.Join(", ", suggestion.Evidence)}");
                    }

                    if (binary.ToolchainSuggestions.Count == 0)
                    {
                        _out.Line("  none: no evidence matched a profile. This is recorded, not hidden.");
                    }

                    return ExitCodes.Ok;
                }

            case "debug":
                {
                    var compilands = BuildCompilandReport(context, document);
        EmitWithSchemaCheck(new DebugReport
                    {
                        Debug = binary.Debug,
                        Compilands = compilands,
                        Problems = document.Problems,
                    }, "inspect-debug");
                    _out.Line($"kind: {binary.Debug.Kind}");
                    _out.Line($"path: {binary.Debug.Path ?? "(none)"}");
                    _out.Line($"symbols: {binary.Debug.Symbols} ({binary.Debug.Functions} functions), compilands: {binary.Debug.Compilands}");
                    if (binary.Debug.EmbeddedPath is not null)
                    {
                        _out.Line($"path recorded in the binary: {binary.Debug.EmbeddedPath}");
                    }

                    if (binary.Debug.MatchesBinary is bool matches)
                    {
                        _out.Line($"matches binary: {matches}");
                    }

                    _out.Table(
                        compilands.Take(limit).Select(c => new[] { c.Unit, c.ObjectFile ?? string.Empty, c.Producer ?? string.Empty, c.Functions.ToString() }),
                        "unit", "object", "producer", "functions");
                    return ExitCodes.Ok;
                }

            case "tls":
                {
        EmitWithSchemaCheck(new TlsReport { Callbacks = binary.TlsCallbacks }, "inspect-tls");
                    _out.Table(binary.TlsCallbacks.Select(r => new[] { $"0x{r:X8}" }), "callback");
                    return ExitCodes.Ok;
                }

            case "stats":
                {
        EmitWithSchemaCheck(new StatsReport { Statistics = document.Statistics, Problems = document.Problems }, "inspect-stats");
                    _out.Table(document.Statistics.OrderBy(s => s.Key).Select(s => new[] { s.Key, s.Value.ToString() }), "metric", "value");
                    return ExitCodes.Ok;
                }

            default:
                _out.Error($"unknown inspect target \"{subcommand}\"");
                return ExitCodes.Usage;
        }
    }

    private static List<CompilandReport> BuildCompilandReport(ProjectContext context, InventoryDocument document)
    {
        var image = context.LoadImage();
        var debug = image.Image is null ? null : context.LoadDebugInfo(image.Image);
        if (debug is null)
        {
            return [];
        }

        var byUnit = document.Functions
            .Where(f => f.Unit is not null)
            .GroupBy(f => f.Unit!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return debug.Compilands.Select(c => new CompilandReport
        {
            Unit = c.Unit,
            ObjectFile = c.ObjectFile,
            Producer = c.Producer,
            Functions = byUnit.GetValueOrDefault(c.Unit),
        }).ToList();
    }

    // ---------------------------------------------------------------- disasm

    public int Disasm()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        string? target = _args.Arguments.FirstOrDefault();
        if (target is null)
        {
            _out.Error("usage: recon disasm <rva|name|substring>");
            return ExitCodes.Usage;
        }

        var image = context.LoadImage();
        if (image.Image is null)
        {
            foreach (var problem in image.Problems)
            {
                _out.Error(problem);
            }

            return ExitCodes.CheckFailed;
        }

        // Whether this image can be decoded at all is asked first, because it is cheap and it is the one
        // refusal that needs nothing from an inventory: a p-code Visual Basic program or an instruction
        // set this tool does not read is turned away in milliseconds rather than after an analysis.
        var decoder = new CodeDecoder(image.Image) { Data = image.Bytes };
        if (decoder.DecodeProblem is { } decodeProblem)
        {
            // Neither a usage error nor a damaged file: the image is fine and this tool does not
            // speak its instruction set. Say so, and say what is still there.
            _out.Error($"{image.Image.Isa}: {decodeProblem}");
            _out.Error(string.Equals(image.Image.Isa, "vb6-pcode", StringComparison.Ordinal)
                // A p-code program has instructions, just not these: they are the interpreter's, and
                // the commands that read them are named here rather than left to be discovered.
                ? "nothing to disassemble as machine code; `recon pcode` lists this program's procedures and " +
                  "their p-code streams, and `recon inspect functions` lists them from the inventory"
                : "nothing to disassemble; `recon inventory` still lists this binary's sections, symbols and relocations");
            return ExitCodes.CheckFailed;
        }

        // What a listing needs from the inventory: where the functions are, what the imports are called,
        // and what the data is called. An inventory on disk that this build wrote about this binary has
        // all three, and reading them is a fraction of a second against seconds for building the
        // document again — which matters because a reversing session is one build and then a great many
        // listings. When there is no such file, the analysis is run the way it always was.
        var listing = ReadListing(context);
        if (listing is null)
        {
            var document = BuildInventory(context, includeXrefs: true, signatures: LoadSignatures(context, required: false));
            if (document is null)
            {
                return ExitCodes.CheckFailed;
            }

            listing = Listing.FromDocument(document);
        }

        uint start;
        string? functionName = null;
        if (TryParseAddress(target, out uint rva))
        {
            start = rva;
            functionName = listing.Functions.FirstOrDefault(f => f.Start <= rva && rva < f.Start + f.Size).Name;
        }
        else
        {
            // A demangled name is only in the inventory, so a cached listing has the C name and the
            // matching is done on what it has; a fresh one matches both.
            (uint Start, uint Size, string? Name, string? Demangled) match =
                listing.Functions.FirstOrDefault(f =>
                    string.Equals(f.Name, target, StringComparison.Ordinal)
                    || string.Equals(f.Demangled, target, StringComparison.Ordinal));

            if (match.Name is null && match.Start == 0)
            {
                match = listing.Functions.FirstOrDefault(f =>
                    (f.Name?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (f.Demangled?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (match.Name is null)
            {
                _out.Error($"no function matches \"{target}\"");
                return ExitCodes.CheckFailed;
            }

            start = match.Start;
            functionName = match.Name;
        }

        int count = _args.Count("--count") ?? 32;
        var instructions = decoder.DecodeRange(image.Bytes, image.Image.RvaToOffset(start) ?? 0, Math.Max(16, count * 16), start).Take(count).ToList();

        // Labels and aliases mean several entries can share a start address; the first name wins.
        var functionNames = new Dictionary<uint, string?>();
        foreach (var function in listing.Functions)
        {
            functionNames.TryAdd(function.Start, function.Name);
        }

        var importNames = new Dictionary<uint, string>();
        foreach (var import in listing.Imports)
        {
            importNames.TryAdd(import.Rva, import.Name);
        }

        var dataNames = new Dictionary<uint, string>();
        foreach (var entry in listing.Data)
        {
            dataNames.TryAdd(entry.Rva, entry.Name);
        }

        var report = new DisasmReport { StartRva = start, Function = functionName };
        var rows = new List<string[]>();
        foreach (var insn in instructions)
        {
            var entry = new DisasmInstruction
            {
                Rva = insn.Rva,
                Bytes = Convert.ToHexString(image.Bytes.AsSpan(image.Image.RvaToOffset(insn.Rva) ?? 0, insn.Length)).ToLowerInvariant(),
                Text = insn.Text,
            };

            if (insn.DirectTargetRva is uint direct && functionNames.TryGetValue(direct, out string? name) && name is not null)
            {
                entry.Annotation = name;
            }
            else if (insn.BranchMemory?.Rva is uint memRva)
            {
                if (importNames.TryGetValue(memRva, out string? importName))
                {
                    entry.Annotation = importName;
                }
                else if (dataNames.TryGetValue(memRva, out string? dataName))
                {
                    entry.Annotation = $"data:{dataName}";
                }
            }

            entry.XrefTarget = insn.DirectTargetRva;
            report.Instructions.Add(entry);
            rows.Add([$"0x{insn.Rva:X8}", entry.Bytes, insn.Text, entry.Annotation ?? string.Empty]);
        }

        EmitWithSchemaCheck(report, "disasm");

        if (!_out.Json)
        {
            _out.Line($"{functionName ?? "(no function)"} at 0x{start:X8}");
            _out.Table(rows, "rva", "bytes", "instruction", "annotation");
        }

        return ExitCodes.Ok;
    }

    private static bool TryParseAddress(string text, out uint rva)
    {
        rva = 0;
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out rva);
        }

        if (trimmed.All(c => Uri.IsHexDigit(c)) && trimmed.Length is 4 or 6 or 8 && trimmed.Any(char.IsLetter))
        {
            return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out rva);
        }

        return uint.TryParse(trimmed, out rva);
    }

    // ---------------------------------------------------------------- toolchain

    // ------------------------------------------------------------------ signatures

    /// <summary>
    /// Where a pattern file comes from: the flag if it was given, otherwise the project's
    /// <c>[analysis] signatures</c>. A project that names one applies it to every inventory, which is
    /// what makes naming a stripped binary a settled thing rather than a flag someone has to remember.
    /// </summary>
    private Recon.Signatures.SignatureDatabase? LoadSignatures(ProjectContext context, bool required)
    {
        string? path = _args.Value("--signatures") ?? context.Project.Analysis.Signatures;
        if (path is null)
        {
            if (required)
            {
                _out.Error("no pattern file: pass --signatures=PATH, or set [analysis] signatures in project.toml");
            }

            return null;
        }

        string full = Path.IsPathRooted(path) ? path : Path.Combine(context.Project.RootDirectory, path);
        if (!File.Exists(full))
        {
            _out.Error($"pattern file {full} does not exist");
            return null;
        }

        try
        {
            return Recon.Signatures.SignatureFile.Load(full);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            _out.Error($"cannot read pattern file {full}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <c>recon sigs</c>: build patterns from a binary the tool can read, and apply them to one it
    /// cannot. The second half is the point: a released binary has no symbols, and the runtime code in
    /// it — the CRT, a static library — is code nobody has to reconstruct.
    /// </summary>
    public int Sigs()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        string action = _args.Subcommand ?? "build";
        switch (action)
        {
            case "build":
                return SigsBuild(context);

            case "apply":
                return SigsApply(context);

            default:
                _out.Error($"unknown sigs action \"{action}\": expected build or apply");
                return ExitCodes.Usage;
        }
    }

    private int SigsBuild(ProjectContext context)
    {
        var image = context.LoadImage();
        if (image.Image is null)
        {
            foreach (var problem in image.Problems)
            {
                _out.Error(problem);
            }

            return ExitCodes.CheckFailed;
        }

        var debug = context.LoadDebugInfo(image.Image);
        if (debug is null || debug.Symbols.Count == 0)
        {
            _out.Error("this binary has no debug information to build patterns from: patterns are the bytes of functions something else named");
            return ExitCodes.CheckFailed;
        }

        int length = _args.Count("--length") ?? Recon.Signatures.SignatureDatabase.DefaultPatternLength;
        int minFixed = _args.Count("--min-fixed") ?? Recon.Signatures.SignatureDatabase.DefaultMinFixedBytes;

        var database = Recon.Signatures.SignatureBuilder.Build(
            image.Image,
            image.Bytes,
            debug,
            new Recon.Signatures.SignatureBuilder.Options
            {
                PatternLength = length,
                MinFixedBytes = minFixed,
                Library = _args.Value("--library"),
                UnitFilters = _args.List("--unit"),
            });

        database.Generator = $"recon {EntryPoint.Version}";
        string json = Recon.Signatures.SignatureFile.Serialize(database);
        string path = _args.Output ?? Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "signatures.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);

        var report = new SignatureBuildReport
        {
            Path = path,
            Patterns = database.Entries.Count,
            Library = _args.Value("--library"),
            Functions = debug.Symbols.Count(s => !s.IsData),
            Problems = database.Problems,
        };

        EmitWithSchemaCheck(report, "sigs-build");
        _out.Line($"patterns: {report.Patterns} from {report.Functions} named function(s)");
        _out.Line($"wrote {path}");
        foreach (var problem in database.Problems)
        {
            _out.Warn(problem);
        }

        return ExitCodes.Ok;
    }

    private int SigsApply(ProjectContext context)
    {
        var database = LoadSignatures(context, required: true);
        if (database is null)
        {
            return ExitCodes.Configuration;
        }

        var document = BuildInventory(context, includeXrefs: false, signatures: database);
        if (document is null)
        {
            return ExitCodes.CheckFailed;
        }

        var named = document.Functions.Where(f => f.FoundBy.Contains("signature")).ToList();
        var report = new SignatureApplyReport
        {
            Signatures = database.Entries.Count,
            Functions = document.Functions.Count,
            Named = named.Count,
            Matches = [.. named.Select(f => new SignatureMatchInfo
            {
                Name = f.Name,
                Rva = f.Ranges.Count > 0 ? f.Ranges[0].Rva : 0,
                Size = f.Ranges.Count > 0 ? f.Ranges[0].Size : 0,
                Library = f.Unit,
            })],
        };

        EmitWithSchemaCheck(report, "sigs-apply");
        _out.Line($"patterns: {report.Signatures}, functions: {report.Functions}, named by them: {report.Named}");
        _out.Table(
            report.Matches.Select(m => new[]
            {
                $"0x{m.Rva:X}",
                m.Name ?? string.Empty,
                m.Size.ToString(),
                m.Library ?? "-",
            }),
            "rva", "name", "size", "unit");

        foreach (var problem in document.Problems.Where(p => p.StartsWith("signature:", StringComparison.Ordinal)))
        {
            _out.Warn(problem);
        }

        return ExitCodes.Ok;
    }

    public int Toolchain()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        string? subcommand = _args.Subcommand ?? "list";
        switch (subcommand)
        {
            case "list":
                {
                    var report = new ToolchainListReport();
                    foreach (var profile in context.Registry.All())
                    {
                        report.Profiles.Add(Describe(profile));
                    }

        EmitWithSchemaCheck(report, "toolchain-list");
                    _out.Table(
                        report.Profiles.Select(p => new[]
                        {
                            p.Id,
                            p.DisplayName,
                            p.Family,
                            string.Join(",", p.Targets),
                            p.Abstract ? "abstract" : string.Empty,
                            string.Join(" -> ", p.Chain),
                        }),
                        "id", "display name", "family", "targets", "kind", "chain");
                    return ExitCodes.Ok;
                }

            case "show":
                {
                    string? id = _args.Arguments.FirstOrDefault();
                    if (id is null)
                    {
                        _out.Error("usage: recon toolchain show <id> [--raw]");
                        return ExitCodes.Usage;
                    }

                    // What a profile resolves to is the answer worth printing: the ABI, the padding
                    // and the debug format come from the profile it extends, and a view that leaves
                    // them out says "mangling: none" about a toolchain that mangles everything.
                    // `--raw` prints the file's own contents, with what it inherits marked as
                    // inherited rather than as absent.
                    bool raw = _args.Has("--raw");
                    var profile = raw ? context.Registry.Get(id) : context.Registry.GetResolved(id);
                    if (profile is null)
                    {
                        _out.Error($"unknown profile \"{id}\" (known: {string.Join(", ", context.Registry.KnownIds)})");
                        return ExitCodes.CheckFailed;
                    }

                    string Inherited(string key, string value) => raw && !profile.IsExplicit(key) ? "(inherited)" : value;

        EmitWithSchemaCheck(Describe(profile), "toolchain-profile");
                    _out.Line($"id:            {profile.Id}");
                    _out.Line($"display name:  {profile.DisplayName}");
                    _out.Line($"family:        {profile.Family}");
                    _out.Line($"inherits:      {(raw ? profile.Extends ?? "(nothing)" : string.Join(" -> ", profile.InheritanceChain.DefaultIfEmpty(profile.Id)))}");
                    _out.Line($"targets:       {Inherited("targets", string.Join(", ", profile.Targets.Select(t => $"{t.Format}/{t.Arch}")))}");
                    _out.Line($"mangling:      {Inherited("abi.mangling", $"{profile.Abi.Mangling}, default cc {profile.Abi.DefaultCc}, member cc {profile.Abi.MemberCc}")}");
                    _out.Line($"codegen:       align {Inherited("codegen.function_align", profile.Codegen.FunctionAlign.ToString())}, padding {Inherited("codegen.padding_bytes", string.Join(" ", profile.Codegen.PaddingBytes.Select(b => $"0x{b:X2}")))}");
                    _out.Line($"prologues:     {Inherited("codegen.prologue_hints", string.Join(" | ", profile.Codegen.PrologueHints))}");
                    _out.Line($"linker:        folding {Inherited("linker.identical_code_folding", profile.Linker.IdenticalCodeFolding)}, comdat {Inherited("linker.comdat", profile.Linker.Comdat.ToString())}");
                    _out.Line($"eh / debug:    {Inherited("eh.model", profile.Eh.Model)} / {Inherited("debug.format", profile.Debug.Format)}");
                    _out.Line(
                        $"detection:     rich {profile.Detect.RichHeader.Count}, compiland {profile.Detect.PdbCompiland.Count}, comment {profile.Detect.CommentSection.Count}, dwarf {profile.Detect.DwarfProducer.Count}, linker-version {profile.Detect.LinkerVersion.Count}, dll {profile.Detect.ImportDll.Count}"
                        + (profile.Vb6CodeKind is { } kind ? $", vb6-header {kind}" : string.Empty));
                    if (profile.Install is { Files.Count: > 0 })
                    {
                        _out.Line($"install files: {profile.Install.Files.Count} hashed");
                    }

                    return ExitCodes.Ok;
                }

            case "detect":
                {
                    var image = context.LoadImage();
                    if (image.Image is null)
                    {
                        foreach (var problem in image.Problems)
                        {
                            _out.Error(problem);
                        }

                        return ExitCodes.CheckFailed;
                    }

                    var debug = context.LoadDebugInfo(image.Image);
                    var report = ProducerDetector.Detect(image.Image, debug, context.Registry, context.Dwarf);
                    var document = new ProducersReport
                    {
                        Producers = [],
                        Suggestions = [.. report.Suggestions.Select(s => new ToolchainSuggestionInfo
                        {
                            Id = s.ProfileId,
                            Confidence = s.Confidence,
                            Scope = s.Scope,
                            Evidence = [.. s.Evidence],
                            Unit = s.Unit,
                            Note = string.IsNullOrEmpty(s.Note) ? null : s.Note,
                        })],
                        Problems = report.Problems,
                    };

        EmitWithSchemaCheck(document, "inspect-producers");
                    _out.Line("detected toolchains (suggestions; project.toml always wins):");
                    foreach (var suggestion in document.Suggestions)
                    {
                        _out.Line($"  {suggestion.Id,-18} {suggestion.Confidence,-7} {string.Join(", ", suggestion.Evidence)}");
                    }

                    if (document.Suggestions.Count == 0)
                    {
                        _out.Line("  none");
                    }

                    var units = ProducerDetector.DetectUnits(debug, report);
                    if (units.Count > 0)
                    {
                        _out.Line("per compilation unit:");
                        foreach (var (unit, match) in units.OrderBy(u => u.Key, StringComparer.Ordinal))
                        {
                            _out.Line($"  {unit,-32} {match.ProfileId} ({match.Confidence})");
                        }
                    }

                    return ExitCodes.Ok;
                }

            case "check":
                {
                    string? id = _args.Arguments.FirstOrDefault();
                    if (id is null)
                    {
                        _out.Error("usage: recon toolchain check <id>");
                        return ExitCodes.Usage;
                    }

                    if (!context.Local.Toolchains.TryGetValue(id, out var install))
                    {
                        _out.Error($"local.toml has no [toolchain.{id}] section: nothing to check");
                        return ExitCodes.CheckFailed;
                    }

                    var profile = context.Registry.GetResolved(id);
                    if (profile is null)
                    {
                        _out.Error($"unknown profile \"{id}\" (known: {string.Join(", ", context.Registry.KnownIds)})");
                        return ExitCodes.CheckFailed;
                    }

                    var use = ToolResolver.Resolve(profile, install, context.Project.RootDirectory);
                    var check = VerifyToolchain(id, install.Root, profile, use);
        EmitWithSchemaCheck(check, "toolchain-check");

                    // An install root is one way to have a compiler; a toolchain installed by the
                    // system is another, and it has no root to show.
                    _out.Line(string.IsNullOrEmpty(check.Root)
                        ? "root:  (none — the profile's tools are looked up on PATH)"
                        : $"root:  {check.Root} ({(check.RootExists ? "exists" : "MISSING")})");
                    _out.Line(use.CcProblem is null
                        ? $"cc:    {use.Cc}"
                        : $"cc:    MISSING — {use.CcProblem}");
                    _out.Line(use.LinkProblem is null
                        ? $"link:  {use.Link}"
                        : $"link:  MISSING — {use.LinkProblem}");

                    foreach (var file in check.FilesMissing)
                    {
                        _out.Error($"  missing: {file}");
                    }

                    foreach (var file in check.FilesMismatched)
                    {
                        _out.Error($"  hash mismatch: {file}");
                    }

                    _out.Line(check.Ok
                        ? $"toolchain {id}: ok ({check.FilesChecked} install file(s) checked)"
                        : $"toolchain {id}: FAILED");
                    return check.Ok ? ExitCodes.Ok : ExitCodes.CheckFailed;
                }

            case "builtins":
                {
                    string directory = _args.Value("--install-dir") ?? Path.Combine(context.Project.RootDirectory, "toolchains");
                    int written = BuiltInProfiles.WriteTo(directory);
                    _out.Line($"wrote {written} profile(s) to {directory} (existing files were left alone)");
                    return ExitCodes.Ok;
                }

            default:
                _out.Error($"unknown toolchain subcommand \"{subcommand}\"");
                return ExitCodes.Usage;
        }
    }

    /// <summary>
    /// Whether this machine can build with this profile. The tools are what matter — a profile whose
    /// compiler and linker are found is usable — and the install's own files are only checked when the
    /// profile declares them and a root was configured, which is the Windows case.
    /// </summary>
    private static ToolchainCheck VerifyToolchain(string id, string root, ToolchainProfile? profile, BuildToolchainUse use)
    {
        var check = new ToolchainCheck
        {
            Id = id,
            Root = root,
            RootExists = !string.IsNullOrEmpty(root) && Directory.Exists(root),
            Cc = use.Cc,
            Link = use.Link,
            CcProblem = use.CcProblem,
            LinkProblem = use.LinkProblem,
        };

        if (use.CcProblem is not null || use.LinkProblem is not null)
        {
            check.Ok = false;
            return check;
        }

        bool declaresInstall = profile?.Install is { Files.Count: > 0 };
        if (!declaresInstall)
        {
            check.Ok = true;
            return check;
        }

        if (!check.RootExists)
        {
            check.Ok = false;
            return check;
        }

        foreach (var file in profile!.Install!.Files)
        {
            string path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            check.FilesChecked++;
            if (!File.Exists(path))
            {
                check.FilesMissing.Add(file.Path);
                check.Ok = false;
                continue;
            }

            if (!string.Equals(PeImage.HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                check.FilesMismatched.Add(file.Path);
                check.Ok = false;
            }
        }

        check.Ok = check.FilesMissing.Count == 0 && check.FilesMismatched.Count == 0
                   && use.CcProblem is null && use.LinkProblem is null;
        return check;
    }

    private static ToolchainProfileReport Describe(ToolchainProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        Family = profile.Family,
        Abstract = profile.IsAbstract,
        Extends = profile.Extends,
        Chain = [.. profile.InheritanceChain],
        Targets = [.. profile.Targets.Select(t => $"{t.Format}/{t.Arch}")],
        CodeKind = profile.Vb6CodeKind,
        File = profile.FilePath,
    };

    // ---------------------------------------------------------------- build

    /// <summary>
    /// Runs the reconstruction's own build. Each unit is compiled with the toolchain it names (or the
    /// project's default), the results are cached by content hash, and the plan is also written as
    /// <c>build/build.ninja</c> so the same build can be driven by ninja itself.
    /// </summary>
    public int Build()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        var options = new Recon.Build.BuildOptions
        {
            ToolVersion = EntryPoint.Version,
            UnitFilter = _args.Value("--unit"),
            DryRun = _args.Has("--dry-run"),
            Force = _args.Has("--force"),
            KeepGoing = _args.Has("--keep-going"),
            SkipNinja = _args.Has("--no-ninja"),
        };

        if (_args.Value("--jobs") is { } jobs)
        {
            if (!int.TryParse(jobs, out int parsed) || parsed < 1)
            {
                _out.Error($"--jobs expects a positive number, not \"{jobs}\"");
                return ExitCodes.Usage;
            }

            options.Jobs = parsed;
        }

        var previous = Recon.Build.BuildManifestStore.Load(
            Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "build.json"));

        var plan = Recon.Build.BuildPlanner.Plan(context, options, previous, context.Diagnostics);

        _out.Line($"build {plan.ProjectName}: {plan.Units.Count} unit(s), output {plan.Relative(plan.OutputPath)}");
        foreach (var toolchain in plan.Toolchains)
        {
            string state = toolchain.CcProblem
                ?? (toolchain.Root is null ? "ok, no install root in local.toml" : "ok");
            _out.Line($"  toolchain {toolchain.Id,-16} cc {toolchain.Cc} ({state})");
        }

        if (plan.LinkNote is { } note)
        {
            _out.Line("  " + note);
        }

        var result = Recon.Build.BuildRunner.Run(context, options, plan, new BuildLog(_out));
        var manifest = result.Manifest;

        if (!options.DryRun)
        {
            _out.Line($"  wrote {plan.Relative(plan.ManifestPath)}");
            if (!options.SkipNinja)
            {
                _out.Line($"  wrote {plan.Relative(plan.NinjaPath)}");
            }
        }

        if (options.DryRun)
        {
            _out.Line("dry run: nothing was compiled and no files were written");
        }

        _out.EmitJsonText(Recon.Build.BuildRunner.ToJson(manifest));

        int violations = 0;
        if (_args.Has("--check-schema"))
        {
            var schemaJson = BuiltInSchemas.Get("build");
            if (schemaJson is not null)
            {
                violations = Recon.Schema.JsonSchemaValidator.Parse(schemaJson)
                    .Validate(Recon.Build.BuildRunner.ToJson(manifest))
                    .Count;
                foreach (var violation in Recon.Schema.JsonSchemaValidator.Parse(schemaJson)
                             .Validate(Recon.Build.BuildRunner.ToJson(manifest)))
                {
                    _out.Error($"schema violation: {violation}");
                }
            }

            _out.Line(violations == 0 ? "build manifest matches schema 0.1" : $"build manifest has {violations} schema violation(s)");
        }

        foreach (var problem in manifest.Problems)
        {
            _out.Error(problem);
        }

        if (manifest.Summary.Failed > 0)
        {
            foreach (var unit in manifest.Units.Where(u => u.Result == "failed"))
            {
                _out.Error($"  {unit.Source}: compiler exited with {unit.ExitCode}");
                foreach (var line in unit.Diagnostics)
                {
                    _out.Error($"    {line}");
                }
            }
        }

        if (manifest.Link is { Result: "failed" } link)
        {
            _out.Error($"  link failed with {link.ExitCode}");
            foreach (var line in link.Diagnostics)
            {
                _out.Error($"    {line}");
            }
        }

        return manifest.Summary.Ok && manifest.Problems.Count == 0 && violations == 0
            ? ExitCodes.Ok
            : ExitCodes.CheckFailed;
    }

    // ---------------------------------------------------------------- permute

    /// <summary>
    /// The search: rebuild the unit's source written every way that means the same thing, and score
    /// each against the original. What it prints is the whole run — the baseline, every variant, and
    /// why it stopped — because a search that reported only its winner could not be checked.
    /// </summary>
    public int Permute()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        var options = new Recon.Permute.PermuteOptions
        {
            Unit = _args.Value("--unit"),
            StopOnExact = !_args.Has("--no-stop-on-exact"),
            // Which optimization level made the bytes is a question about flags, and it is the one a
            // reconstruction cannot answer by editing source.
            PermuteFlags = _args.Has("--flags"),
        };

        if (_args.Value("--budget") is { } budgetText)
        {
            if (!int.TryParse(budgetText, out int budget) || budget < 1)
            {
                _out.Error($"--budget expects a positive number, not \"{budgetText}\"");
                return ExitCodes.Usage;
            }

            options.Budget = budget;
        }

        string? function = _args.Value("--function");
        if (function is null && options.Unit is null)
        {
            _out.Error("usage: recon permute --function NAME (or --unit NAME)");
            return ExitCodes.Usage;
        }

        string outputDirectory = _args.Output
            ?? Path.Combine(context.RootDirectory, context.Project.Paths.Build, "permute");

        var document = Recon.Permute.Permuter.Run(new Recon.Permute.PermuterInputs
        {
            Context = context,
            Function = function,
            Options = options,
            // Quiet by default: N variants mean N builds, and their chatter would bury the
            // table. --verbose shows the compiler for every variant.
            Log = _args.Has("--verbose") ? new BuildLog(_out) : null,
            ToolVersion = EntryPoint.Version,
            OutputDirectory = outputDirectory,
        });

        int violations = 0;
        if (_args.Has("--check-schema"))
        {
            var schemaJson = BuiltInSchemas.Get("permute");
            if (schemaJson is not null)
            {
                string json = Recon.Reporting.Reports.Serialize(document);
                var validator = Recon.Schema.JsonSchemaValidator.Parse(schemaJson);
                var problems = validator.Validate(json);
                foreach (var problem in problems)
                {
                    _out.Error($"schema violation: {problem}");
                }

                violations = problems.Count;
                _out.Line(violations == 0 ? "permute matches schema 0.1" : $"permute has {violations} schema violation(s)");
            }
        }

        _out.EmitJsonText(Recon.Reporting.Reports.Serialize(document));
        _out.Line(
            $"permute {document.Unit} ({document.Source}): {document.CandidateCount} candidate(s), "
            + $"{document.Tried} tried — {document.StoppedBecause}");

        if (document.Tried > 0)
        {
            _out.Line(
                $"  baseline: {document.BaselineScore:F3}"
                + (document.BaselineExact ? " (exact)" : string.Empty)
                + $" — the source as it stands, for \"{document.Function}\"");
        }

        var rows = document.Variants
            .OrderByDescending(v => v.Score)
            .ThenBy(v => v.VariantId, StringComparer.Ordinal)
            .Select(v => new[]
            {
                v.VariantId,
                v.Kind,
                v.Built ? v.Score.ToString("F3") : "build failed",
                v.Exact ? "yes" : "no",
                v.Description,
            })
            .ToList();

        if (rows.Count > 0)
        {
            _out.Table(rows, "variant", "kind", "score", "exact", "description");
        }

        foreach (var variant in document.Variants.Where(v => v.BuildError is not null))
        {
            _out.Warn($"  {variant.VariantId}: {variant.BuildError}");
        }

        var best = document.Best;
        if (best is not null)
        {
            string verdict = document.BestBeatsBaseline
                ? "beats the baseline"
                : best.Exact && !document.BaselineExact
                    ? "is exact, and the baseline is not"
                    : "does not beat the baseline";
            _out.Line($"  best: {best.VariantId} ({best.Score:F3}) {verdict}");

            if (document.BestWriteError is not null)
            {
                _out.Warn($"  best source not written: {document.BestWriteError}");
            }
            else if (document.BestSource is not null)
            {
                _out.Line($"  wrote {document.BestSource}");
            }
        }

        return violations == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    /// <summary>Where build progress goes: the same three streams every other command uses.</summary>
    private sealed class BuildLog(Output output) : Recon.Build.IBuildLog
    {
        public void Info(string text) => output.Line(text);

        public void Step(string text) => output.Line("  " + text);

        public void Warn(string text) => output.Warn(text);

        public void Error(string text) => output.Error(text);
    }

    // ---------------------------------------------------------------- diff

    /// <summary>
    /// Compares two builds. The sides are given as project directories or as binaries; with fewer
    /// arguments the current project supplies what is missing, so the everyday case is
    /// <c>recon diff rebuilt.exe</c>: the project's original against a fresh build.
    /// </summary>
    public int Diff()
    {
        var diagnostics = new Diagnostics();
        ComparisonSide left;
        ComparisonSide right;

        try
        {
            var arguments = _args.Arguments;
            if (arguments.Count > 2)
            {
                _out.Error("usage: recon diff [left] [right] [-o PATH] [--threshold=F] [--min-score=F] [--function=NAME] [--summary] [--json]");
                return ExitCodes.Usage;
            }

            string? projectFile = FindProjectFile();
            if (arguments.Count == 2)
            {
                left = LoadSide("left", arguments[0], diagnostics);
                right = LoadSide("right", arguments[1], diagnostics);
            }
            else if (arguments.Count == 1)
            {
                if (projectFile is null)
                {
                    _out.Error("one side given, but there is no project to take the other side from (no project.toml found)");
                    return ExitCodes.Usage;
                }

                left = ComparisonSide.FromProjectInput("left", projectFile, "original", diagnostics);
                right = LoadSide("right", arguments[0], diagnostics);
            }
            else
            {
                if (projectFile is null)
                {
                    _out.Error("usage: recon diff [left] [right]");
                    return ExitCodes.Usage;
                }

                left = ComparisonSide.FromProjectInput("left", projectFile, "original", diagnostics);
                right = ComparisonSide.FromProjectInput("right", projectFile, "reference", diagnostics);
            }
        }
        catch (ConfigException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
            {
                _out.Error(diagnostic.ToString());
            }

            return ExitCodes.Configuration;
        }

        // The settings come from the original's toolchain profile (plan section 3.1: toolchain noise
        // is a profile setting), and the command line overrides what the caller says out loud.
        var options = ComparisonOptions.FromProfile(left.Profile);
        options.Phase = (name, elapsed) => _out.Debug($"  {name,-12} {elapsed.TotalMilliseconds,7:F0} ms");

        // Which route each side took, in stderr's debug words. A build and a cache hit are the same
        // comparison, and saying which one happened is how a reader tells a slow run from a fast one
        // without being told to trust the numbers in the report.
        _out.Debug($"  {left.Label,-12} inventory {left.InventorySource} ({left.AnalysisMs} ms)  {Short(left.Sha256)}");
        _out.Debug($"  {right.Label,-12} inventory {right.InventorySource} ({right.AnalysisMs} ms)  {Short(right.Sha256)}");
        options.AlignedFunction = _args.Value("--function");
        if (_args.Value("--threshold") is { } threshold)
        {
            if (!double.TryParse(threshold, out double parsed))
            {
                _out.Error($"--threshold expects a number, not \"{threshold}\"");
                return ExitCodes.Usage;
            }

            options.SimilarityThreshold = parsed;
        }

        if (_args.Count("--limit") is { } limit)
        {
            options.DifferenceLimit = limit;
        }

        if (options.SimilarityThreshold is <= 0 or > 1)
        {
            _out.Error("--threshold must be between 0 and 1");
            return ExitCodes.Usage;
        }

        var document = ComparisonBuilder.Build(left, right, options);

        string json = Reports.Serialize(document);
        int violations = 0;
        string? schemaMessage = null;
        if (_args.Has("--check-schema"))
        {
            var schemaJson = BuiltInSchemas.Get("comparison");
            if (schemaJson is not null)
            {
                var validator = Recon.Schema.JsonSchemaValidator.Parse(schemaJson);
                var problems = validator.Validate(json);
                foreach (var problem in problems)
                {
                    _out.Error($"schema violation: {problem}");
                }

                violations = problems.Count;
                schemaMessage = violations == 0
                    ? "comparison matches schema 0.1"
                    : $"comparison has {violations} schema violation(s)";
            }
        }

        string path = _args.Output ?? Path.Combine(Environment.CurrentDirectory, "build", "comparison.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        _out.EmitJsonText(json);

        if (!_out.Json)
        {
            Describe(document);
        }
        else
        {
            _out.Line($"wrote {path}");
        }

        if (schemaMessage is not null)
        {
            _out.Line(schemaMessage);
        }

        foreach (var problem in document.Problems)
        {
            _out.Warn(problem);
        }

        if (violations != 0)
        {
            return ExitCodes.CheckFailed;
        }

        if (_args.Value("--min-score") is { } minScore && double.TryParse(minScore, out double minimum))
        {
            if (document.Summary.Score < minimum)
            {
                _out.Error($"score {document.Summary.Score:0.####} is below --min-score {minimum:0.####}");
                return ExitCodes.CheckFailed;
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>A side is a project directory when it holds a project.toml, and a binary otherwise.</summary>
    private static ComparisonSide LoadSide(string label, string argument, Diagnostics diagnostics)
    {
        string full = Path.GetFullPath(argument);
        if (Directory.Exists(full))
        {
            return ComparisonSide.FromProject(label, full, diagnostics);
        }

        if (File.Exists(Path.Combine(full, ProjectContext.ProjectFileName)))
        {
            return ComparisonSide.FromProject(label, full, diagnostics);
        }

        return ComparisonSide.FromBinary(label, full, diagnostics);
    }

    private string? FindProjectFile() => ResolveProjectFile(_args.ProjectPath);

    /// <summary>The human-readable view: what matched, what did not, and what to look at first.</summary>
    private void Describe(ComparisonDocument document)
    {
        var summary = document.Summary;
        _out.Line($"diff {document.Left.Label} {Short(document.Left.Sha256)} vs {document.Right.Label} {Short(document.Right.Sha256)}");
        _out.Line($"  functions    {summary.FunctionsLeft} left, {summary.FunctionsRight} right, {summary.Matched} matched " +
                  $"({summary.Exact} exact, {summary.Changed} changed), {summary.Folded} folded, " +
                  $"{summary.OnlyLeft} only left, {summary.OnlyRight} only right");
        _out.Line($"  instructions {summary.InstructionsLeft} left, {summary.InstructionsRight} right, {summary.InstructionsEqual} equal");
        _out.Line($"  score        {summary.Score:0.####}");
        _out.Line($"  matched by   {string.Join(", ", document.Model.MatchedBy.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}"))}");
        _out.Line($"  data         {summary.DataSymbols} differing symbol(s): {summary.DataIdentical} identical, {summary.DataChanged} changed, " +
                  $"{document.Data.OnlyLeft} only left, {document.Data.OnlyRight} only right");
        _out.Line($"  references   {document.Model.ReferencesNamed} named, " +
            $"{document.Model.ReferencesIdentified} identified by content, {document.Model.ReferencesUnnamed} unnamed");
        _out.Line($"  model        threshold {document.Model.SimilarityThreshold:0.##}, ignore padding {Lower(document.Model.IgnorePadding)}, " +
                  $"profile {(document.Model.Profile ?? "built-in defaults")}");

        if (_args.Has("--summary"))
        {
            return;
        }

        string? function = _args.Value("--function");
        var listed = document.Functions
            .Where(f => function is null || string.Equals(f.Left?.Name, function, StringComparison.Ordinal) || string.Equals(f.Right?.Name, function, StringComparison.Ordinal))
            .Take(function is null ? 40 : int.MaxValue)
            .ToList();

        if (listed.Count == 0)
        {
            if (document.Summary.Changed == 0 && document.Summary.OnlyLeft == 0 && document.Summary.OnlyRight == 0)
            {
                _out.Line("  every function matched exactly");
            }

            return;
        }

        _out.Line(string.Empty);
        _out.Table(
            listed.Select(f => new[]
            {
                f.Status,
                f.Match ?? "-",
                f.Left?.Name ?? f.Right?.Name ?? "-",
                f.Left is null ? "--" : $"0x{f.Left.Rva:x8}",
                f.Right is null ? "--" : $"0x{f.Right.Rva:x8}",
                f.Score.ToString("0.####"),
                f.Differences.Count.ToString(),
                string.Join(",", f.Differences.Select(d => d.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            }),
            "status", "match", "name", "left", "right", "score", "diffs", "kinds");

        int total = document.Functions.Count(f => f.Status != "exact");
        if (function is null && total > listed.Count)
        {
            _out.Line($"  ... {total - listed.Count} further non-exact function(s); the comparison JSON has all of them");
        }

        if (function is null)
        {
            return;
        }

        foreach (var comparison in listed)
        {
            _out.Line(string.Empty);

            if (comparison.Aligned is { Count: > 0 } aligned)
            {
                _out.Table(
                    aligned.Select(row => new[]
                    {
                        Mark(row.Status),
                        row.LeftRva is null ? string.Empty : $"0x{row.LeftRva:x8}",
                        row.LeftText ?? string.Empty,
                        row.RightRva is null ? string.Empty : $"0x{row.RightRva:x8}",
                        row.RightText ?? string.Empty,
                    }),
                    "  ", "left", "left text", "right", "right text");
            }

            foreach (var difference in comparison.Aligned is { Count: > 0 } ? [] : comparison.Differences)
            {
                string side = difference.LeftText is null ? string.Empty : $"{difference.LeftRva:x8}  {difference.LeftText}";
                string other = difference.RightText is null ? string.Empty : $"{difference.RightRva:x8}  {difference.RightText}";
                _out.Line($"  {Mark(difference.Kind),-4} {difference.Kind,-10} {side,-52} | {other}");
            }

            foreach (string note in comparison.Notes)
            {
                _out.Line($"  note       {note}");
            }
        }
    }

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>One character per alignment row: unchanged, changed, only left, only right.</summary>
    private static string Mark(string status) => status switch
    {
        "equal" => " ",
        "changed" or "reference" or "opcode" or "operand" or "register" or "immediate" or "addressing" => "~",
        "added" => "+",
        "removed" => "-",
        _ => " ",
    };

    private static string Short(string? sha256)
        => sha256 is { Length: >= 12 } ? sha256[..12] : sha256 ?? "-";

    // ---------------------------------------------------------------- delink and relink

    /// <summary>
    /// The delinking plan: the original cut into pieces a linker can put back at the same addresses.
    /// Writing it out is worth doing on its own — it is the artefact that says what the tool thinks
    /// the image is made of, and who is allowed to provide each part.
    /// </summary>
    public int Delink()
    {
        var prepared = PrepareDelink();
        if (prepared is null)
        {
            return ExitCodes.Configuration;
        }

        var (context, plan, directory, image, debug) = prepared.Value;
        var files = Recon.Delink.AsmWriter.Emit(image.Image!.Pe!, image.Bytes, plan);
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(directory, name), text);
        }

        string script = Path.Combine(directory, "link.ld");
        File.WriteAllText(script, Recon.Delink.LinkerScriptWriter.Emit(plan));

        string planPath = Path.Combine(directory, "delink.json");
        string json = Recon.Reporting.Reports.Serialize(plan);
        File.WriteAllText(planPath, json + "\n");

        _out.EmitJsonText(json);
        if (!_out.Json)
        {
            var counts = plan.Counts;
            _out.Line($"delink {context.Project.Project.Name}: {counts.Sections} section(s), {counts.Pieces} piece(s), {counts.Bytes} byte(s)");
            _out.Line($"  pieces      {counts.Functions} function(s), {counts.Data} data, {counts.JumpTables} jump table(s), {counts.Fillers} filler(s)");
            _out.Line($"  providers   {counts.Original} original, {counts.Rebuilt} rebuilt");
            _out.Line($"  fixups      {counts.Fixups} absolute address(es) emitted as references" +
                      (counts.FixupsNotEmitted > 0 ? $", {counts.FixupsNotEmitted} left as literal bytes" : string.Empty));
            foreach (var file in files)
            {
                _out.Line($"  wrote {Relative(context, Path.Combine(directory, file.FileName))} ({file.Text.Length} byte(s))");
            }

            _out.Line($"  wrote {Relative(context, script)}");
            _out.Line($"  wrote {Relative(context, planPath)}");
            foreach (var problem in plan.Problems)
            {
                _out.Warn(problem);
            }

            foreach (var note in plan.Notes)
            {
                _out.Line("  " + note);
            }
        }

        int violations = 0;
        if (_args.Has("--check-schema"))
        {
            violations = ValidateAgainstSchema("delink", json);
            _out.Line(violations == 0
                ? "delink plan matches schema 0.1"
                : $"delink plan has {violations} schema violation(s)");
        }

        return violations == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    /// <summary>
    /// Relinks the pieces at their original addresses. Every piece is the original's bytes today, so
    /// the image this produces is the original rebuilt by a linker rather than by its own toolchain —
    /// which is the test the rest of the milestone rests on, and the state a reconstruction starts
    /// from before any unit has been rebuilt.
    /// </summary>
    public int Link()
    {
        var prepared = PrepareDelink();
        if (prepared is null)
        {
            return ExitCodes.Configuration;
        }

        var (context, plan, directory, image, debug) = prepared.Value;

        // The pieces a unit's own build can fill. Everything else keeps the original's bytes, and a
        // piece that was claimed but cannot be filled says why: the image this produces is honest
        // about which of its bytes are reconstructed.
        var rebuiltProblems = new List<string>();
        var rebuilt = Recon.Delink.RebuiltPieces.Collect(context, plan, rebuiltProblems);

        // Which linker is going to place the sections decides what their names have to be, so the
        // toolchain is resolved before a line of assembly is written.
        var toolchain = ResolveLinkToolchain(context, plan, image.Image, debug);
        if (toolchain is null)
        {
            return ExitCodes.Configuration;
        }

        var (tool, profile) = toolchain.Value;
        bool positional = PlacementOf(profile) == Placement.Alignment;

        var files = Recon.Delink.AsmWriter.Emit(image.Image!.Pe!, image.Bytes, plan, rebuilt, positional);
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(directory, name), text);
        }

        string scriptPath = Path.Combine(directory, "link.ld");
        File.WriteAllText(scriptPath, Recon.Delink.LinkerScriptWriter.Emit(plan));
        File.WriteAllText(Path.Combine(directory, "delink.json"), Recon.Reporting.Reports.Serialize(plan) + "\n");

        string output = _args.Output
            ?? Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "relinked.exe");
        output = Path.GetFullPath(output);

        string root = context.Project.RootDirectory;
        var objects = new List<string>();

        foreach (var (name, _) in files)
        {
            string source = Path.Combine(directory, name);
            string objectFile = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".o");
            objects.Add(objectFile);

            var assemble = new Recon.Build.BuildCommand
            {
                Kind = "as",
                Executable = tool.Cc,
                WorkingDirectory = root,
                Wine = tool.Wine,
                Environment = tool.Env,
            };
            assemble.Arguments.Add(AssembleOnlyFlag(profile));
            assemble.Arguments.Add(Relative(context, source));
            assemble.Arguments.AddRange(Recon.Build.BuildPlanner.SplitFlag(
                Recon.Build.BuildPlanner.Expand(profile.Compile?.OutputFlag ?? string.Empty, ("obj", Relative(context, objectFile)))));

            if (_args.Has("--dry-run"))
            {
                _out.Line(assemble.Display);
                continue;
            }

            var outcome = Recon.Build.ProcessRunner.Run(assemble);
            if (outcome.ExitCode != 0)
            {
                _out.Error($"assembling {Relative(context, source)} failed");
                foreach (var line in outcome.Diagnostics)
                {
                    _out.Error("  " + line);
                }

                return ExitCodes.Internal;
            }
        }

        var link = new Recon.Build.BuildCommand
        {
            Kind = "link",
            Executable = tool.Link,
            WorkingDirectory = root,
            Wine = tool.Wine,
            Environment = tool.Env,
        };

        if (PlacementOf(profile) == Placement.Alignment)
        {
            // A script-less linker: addresses come from the alignments and the order the pieces are
            // handed over in, which is how the original was linked too. Everything else the header
            // needs and this cannot say - the entry point, the import address table, the TLS
            // directory, the timestamp - is copied from the original afterwards, and the result is
            // compared with the original byte for byte rather than assumed.
            link.Arguments.Add("/nologo");
            link.Arguments.Add($"/base:0x{plan.Binary.ImageBase:x}");
            link.Arguments.Add($"/subsystem:{(image.Image!.Subsystem == 2 ? "windows" : "console")}");
            link.Arguments.Add($"/machine:{Machine(plan.Binary.Arch)}");
            if (plan.Binary.IsDll)
            {
                link.Arguments.Add("/dll");
            }
            link.Arguments.Add("/entry:" + plan.EntrySymbol);

            // The MS-DOS stub is the linker's own artifact and lld-link writes its own: 0x78 bytes, so
            // the NT headers land at 0x78 in the file. The original's stub is longer (0xf0), which is
            // where its e_lfanew says its headers are - so a byte comparison of the two images reads a
            // header that is 0x78 bytes out of place even when it says the same thing. The original's
            // stub is handed back verbatim, which restores e_lfanew and the stub's own bytes with it.
            uint stubLength = image.Bytes.Length >= 0x40 ? BitConverter.ToUInt32(image.Bytes, 0x3c) : 0;
            if (stubLength is >= 0x40 and <= 0x400)
            {
                string stubPath = Path.Combine(directory, "dosstub.bin");
                File.WriteAllBytes(stubPath, image.Bytes.AsSpan(0, (int)stubLength).ToArray());
                link.Arguments.Add("/stub:" + Relative(context, stubPath));
            }

            // Nothing from a runtime or a startup file: every byte of this image comes from the plan.
            link.Arguments.Add("/nodefaultlib");

            // The plan writes the original's relocation table back byte for byte, so the linker must
            // not write one of its own on top of it - the same thing the script path says with
            // --disable-reloc-section.
            link.Arguments.Add("/fixed");

            // A section whose size in memory is larger than what it has in the file - the zero-filled
            // tail delink writes as `.bss` - has to be told where that tail belongs. lld-link folds
            // `.bss` into `.data` on its own, and this image has no `.data` in the plan's own order:
            // unattended the tail became a section of its own and pushed everything after it. The merge
            // names the section the tail belongs to, and it is the section whose size says so.
            var tailOwner = plan.Sections.FirstOrDefault(s => s.Emitted && s.TailBytes > 0);
            if (tailOwner is not null)
            {
                string owner = Recon.Delink.AsmWriter.PositionalNames(plan)
                    .First(p => p.Original == tailOwner.Name).Positional;
                link.Arguments.Add($"/merge:.bss={owner}");
            }

            // The entry symbol the script would define. One object, one absolute symbol, no bytes:
            // the same value the script's line carries, so the entry point in the header is the
            // original's and no linker option had to be invented for it.
            string entrySource = Path.Combine(directory, "entry.s");
            File.WriteAllText(entrySource,
                "# generated by recon link - the entry the linker script would define\n" +
                $"\t.globl {plan.EntrySymbol}\n" +
                $"\t.set {plan.EntrySymbol}, 0x{(plan.Binary.ImageBase + plan.Binary.EntryRva):x}\n");
            string entryObject = Path.Combine(directory, "entry.o");
            var defineEntry = new Recon.Build.BuildCommand
            {
                Kind = "as",
                Executable = tool.Cc,
                WorkingDirectory = root,
                Wine = tool.Wine,
                Environment = tool.Env,
            };
            defineEntry.Arguments.Add(AssembleOnlyFlag(profile));
            defineEntry.Arguments.Add(Relative(context, entrySource));
            defineEntry.Arguments.AddRange(Recon.Build.BuildPlanner.SplitFlag(
                Recon.Build.BuildPlanner.Expand(profile.Compile?.OutputFlag ?? string.Empty, ("obj", Relative(context, entryObject)))));
            if (!_args.Has("--dry-run"))
            {
                var defined = Recon.Build.ProcessRunner.Run(defineEntry);
                if (defined.ExitCode != 0)
                {
                    _out.Error("assembling the entry symbol failed");
                    foreach (var line in defined.Diagnostics)
                    {
                        _out.Error("  " + line);
                    }

                    return ExitCodes.Internal;
                }
            }

            objects.Add(entryObject);
        }
        else
        {
            // Nothing from a runtime or a startup file: every byte of this image comes from the plan.
            link.Arguments.Add("-nostdlib");
            link.Arguments.Add("-Wl,-T," + Relative(context, scriptPath));
            link.Arguments.Add($"-Wl,--image-base,0x{plan.Binary.ImageBase:x}");
            link.Arguments.Add("-Wl,-e," + plan.EntrySymbol);
            link.Arguments.Add($"-Wl,--subsystem,{(image.Image!.Subsystem == 2 ? "windows" : "console")}");

            // The plan writes the original's relocation table back byte for byte, so ld must not write
            // one of its own on top of it: ld would append its entries to our section, and its table
            // describes the relocations it can see, not the ones the original's image had.
            link.Arguments.Add("-Wl,--disable-reloc-section");

            // A PE section name is eight bytes; a longer one is an offset into the string table, which
            // is what the original's .debug_* sections are. Without this ld truncates them instead.
            if (plan.Sections.Any(s => s.Emitted && (s.Output ?? s.Name).Length > 8))
            {
                link.Arguments.Add("-Wl,--enable-long-section-names");
            }
            // The image base is set explicitly, so ld must not pick one of its own: for a DLL it would,
            // and the plan's addresses would describe an image that is somewhere else.
            link.Arguments.Add("-Wl,--disable-auto-image-base");

            if (plan.Binary.IsDll)
            {
                link.Arguments.Add("-shared");

                // The plan writes the original's export table back. ld exporting every symbol it can see
                // would add a table of its own to it — and it cannot even find half of them, because the
                // names the original exported are names this plan emitted as labels, not objects.
                link.Arguments.Add("-Wl,--exclude-all-symbols");
            }

            // --dynamicbase is deliberately not passed, even when the original is based anywhere: it
            // makes ld write a relocation table of its own, appended to the one the plan wrote back.
            // The flag itself is copied into the header after the link, where the table already is.
        }

        link.Arguments.AddRange(objects.Select(o => Relative(context, o)));
        link.Arguments.AddRange(Recon.Build.BuildPlanner.SplitFlag(
            Recon.Build.BuildPlanner.Expand(profile.Link?.OutputFlag ?? string.Empty, ("exe", Relative(context, output)))));

        if (_args.Has("--dry-run"))
        {
            _out.Line(link.Display);
            return ExitCodes.Ok;
        }

        var linked = Recon.Build.ProcessRunner.Run(link);
        if (linked.ExitCode != 0)
        {
            _out.Error("linking failed");
            foreach (var line in linked.Diagnostics)
            {
                _out.Error("  " + line);
            }

            return ExitCodes.Internal;
        }

        if (!_out.Json)
        {
            _out.Line($"link {context.Project.Project.Name}: {plan.Counts.Pieces} piece(s) at 0x{plan.Binary.ImageBase:x}");
            _out.Line($"  wrote {Relative(context, output)}");

            // What was reconstructed and what was carried over, before the verdict, because the
            // verdict is about bytes and this is about who wrote them.
            if (plan.Counts.Rebuilt > 0)
            {
                var byUnit = rebuilt.Values
                    .GroupBy(c => c.Unit, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();
                _out.Line($"  rebuilt     {rebuilt.Count} of {plan.Counts.Rebuilt} claimed piece(s) from " +
                    $"{byUnit.Count} unit(s)");
                foreach (var group in byUnit)
                {
                    var codes = group.OrderBy(c => c.PieceId, StringComparer.Ordinal).ToList();
                    _out.Line($"    {group.Key} ({codes[0].Source}): {codes.Count} piece(s), " +
                        $"{codes.Sum(c => c.Bytes.Length)} byte(s), {codes.Sum(c => c.References.Count)} reference(s)" +
                        (codes.Sum(c => c.OriginalBytesKept) > 0
                            ? $", {codes.Sum(c => c.OriginalBytesKept)} byte(s) of the original kept"
                            : string.Empty));
                }
            }

            // The linker cannot know where the original's import address table or TLS directory are,
            // because it did not build them: it was handed their bytes. Their header entries — and
            // the build's timestamp, which is what matches an image to its symbols — come from the
            // original, which is safe precisely because the bytes they point at are already there.
            var completed = Recon.Delink.HeaderCompletion.Complete(
                output,
                image.Image.Pe!,
                positional ? Recon.Delink.AsmWriter.PositionalNames(plan) : null,
                image.Bytes);
            _out.Line("  header " + Recon.Delink.HeaderCompletion.Describe(completed));

            // And then the claim is checked rather than assumed: the relinked image is compared with
            // the original, section by section, and the result is printed whether or not it is good.
            var comparison = Recon.Delink.RelinkVerifier.Compare(image.Image.Pe!, image.Bytes, output);
            _out.Line("  relinked " + comparison.Verdict);
            foreach (var difference in comparison.Differences)
            {
                _out.Warn(difference);
            }

            foreach (var problem in plan.Problems.Concat(rebuiltProblems))
            {
                _out.Warn(problem);
            }

            foreach (var note in plan.Notes)
            {
                _out.Line("  " + note);
            }
        }
        else
        {
            EmitWithSchemaCheck(new Recon.Reporting.LinkDocument
            {
                Image = output,
                Pieces = plan.Counts.Pieces,
                Rebuilt = plan.Counts.Rebuilt,
                RebuiltPieces = [.. rebuilt.Values
                    .OrderBy(c => c.PieceId, StringComparer.Ordinal)
                    .Select(c => new Recon.Reporting.LinkRebuiltPiece
                    {
                        Piece = c.PieceId,
                        Unit = c.Unit,
                        Source = c.Source,
                        Bytes = c.Bytes.Length,
                        References = c.References.Count,
                        OriginalBytesKept = c.OriginalBytesKept,
                    })],
                Problems = rebuiltProblems,
            }, "link");
        }

        return ExitCodes.Ok;
    }

    /// <summary>What both delink commands need: a project, its original, an inventory, and a plan.</summary>
    private (ProjectContext Context, Recon.Delink.DelinkDocument Plan, string Directory, LoadedImage Image, DebugInfoResult? Debug)? PrepareDelink()
    {
        var context = OpenProject();
        if (context is null)
        {
            return null;
        }

        var image = context.LoadImage();
        if (image.Image is null)
        {
            foreach (var problem in image.Problems)
            {
                _out.Error(problem);
            }

            return null;
        }

        if (image.Image.Pe is null)
        {
            _out.Error($"recon delink works on PE images; {Path.GetFileName(image.Image.Path)} is {image.Image.Format}. "
                       + "The plan, the assembly and the linker script are all about PE sections and PE relocations.");
            return null;
        }

        var debug = context.LoadDebugInfo(image.Image);
        var document = BuildInventory(context, includeXrefs: true);
        if (document is null)
        {
            return null;
        }

        string directory = _args.Output is { } given
            ? Path.GetFullPath(given)
            : Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "delink");
        Directory.CreateDirectory(directory);

        var plan = Recon.Delink.DelinkPlanner.Plan(
            image.Image.Pe,
            document,
            context.Project,
            EntryPoint.Version,
            _args.JoinedCommand);
        return (context, plan, directory, image, debug);
    }

    /// <summary>
    /// Which toolchain assembles and links the pieces. The assembly is GNU-as syntax, so a toolchain
    /// whose compiler is not GCC or Clang is told so rather than handed a file it cannot read.
    /// </summary>
    private (Recon.Build.BuildToolchainUse Tool, ToolchainProfile Profile)? ResolveLinkToolchain(
        ProjectContext context,
        Recon.Delink.DelinkDocument plan,
        IBinaryImage image,
        DebugInfoResult? debug)
    {
        string? configured = context.Project.Target.DefaultToolchain;
        var profile = context.ResolveProfile(configured);
        string how = configured is null ? string.Empty : $"target.default_toolchain = \"{configured}\"";

        // Nothing named: ask the binary who built it, which is the same evidence `toolchain detect`
        // prints, and prefer the answer it is most confident about.
        if (profile is null)
        {
            var report = ProducerDetector.Detect(image, debug, context.Registry, context.Dwarf);
            foreach (var suggestion in report.Suggestions.OrderByDescending(s => Confidence(s.Confidence)))
            {
                var candidate = context.ResolveProfile(suggestion.ProfileId);
                if (candidate is not null && CanRelink(candidate))
                {
                    profile = candidate;
                    how = $"detected from the binary ({suggestion.Confidence} confidence: {string.Join(", ", suggestion.Evidence)})";
                    break;
                }
            }
        }

        // Nothing configured and nothing detected: any profile that can do the job and whose tools
        // are actually installed. The relink is mechanical — the plan says where every byte goes — so
        // it does not have to be the toolchain that built the image, it has to be one that is here.
        if (profile is null)
        {
            foreach (var candidate in context.Registry.All(plan.Binary.Format, plan.Binary.Arch).Where(CanRelink))
            {
                var use = Recon.Build.ToolResolver.Resolve(candidate, context.Local.Toolchains.GetValueOrDefault(candidate.Id), context.Project.RootDirectory);
                if (use.CcProblem is null && use.LinkProblem is null)
                {
                    profile = candidate;
                    how = "the only profile here that can assemble and place sections";
                    break;
                }
            }
        }

        if (profile is null)
        {
            _out.Error($"no toolchain that can relink {plan.Binary.Format}/{plan.Binary.Arch} is installed: " +
                       "relinking needs a compiler that reads GNU-as syntax and a linker that takes a " +
                       "linker script (GCC or Clang driving GNU ld). Point local.toml at one, or set " +
                       "target.default_toolchain to a profile whose tools are on this machine");
            return null;
        }

        if (!CanAssemble(profile))
        {
            _out.Error($"toolchain \"{profile.Id}\" assembles with {profile.Compile?.Exe ?? "nothing"}, which cannot read GNU-as syntax: " +
                       "the delinked pieces are emitted as GNU-as, so a GCC or Clang profile is needed");
            return null;
        }

        if (PlacementOf(profile) == Placement.None)
        {
            _out.Error($"toolchain \"{profile.Id}\" links with {profile.Link?.Exe ?? "nothing"}, which has no way to put a section " +
                       "at an address: either a GNU-ld-compatible linker that takes the plan's linker script, or an " +
                       $"MSVC-ABI linker (link.exe, lld-link) whose sections fall at alignment boundaries. " +
                       $"{profile.Link?.Exe ?? "it"} is neither. `recon delink` still writes the plan; only the relink " +
                       "needs one of the two");
            return null;
        }

        var local = context.Local.Toolchains.GetValueOrDefault(profile.Id);
        var tool = Recon.Build.ToolResolver.Resolve(profile, local, context.Project.RootDirectory);
        foreach (var problem in new[] { tool.CcProblem, tool.LinkProblem })
        {
            if (problem is null)
            {
                continue;
            }

            _out.Error(problem);
            return null;
        }

        if (how.Length > 0 && !_out.Json)
        {
            _out.Line($"  toolchain   {profile.Id} ({how})");
        }

        return (tool, profile);
    }

    private static int Confidence(string confidence) => confidence switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    /// <summary>
    /// Whether this profile's compiler can assemble what the writer emits. The family describes an
    /// ABI, not an assembler — clang targeting the MSVC ABI assembles GNU-as syntax happily — so the
    /// test is the compiler itself: Microsoft's own tools cannot read these files.
    /// </summary>
    private static bool CanAssemble(ToolchainProfile profile)
    {
        string compiler = Path.GetFileNameWithoutExtension(profile.Compile?.Exe ?? string.Empty).ToLowerInvariant();
        return compiler is not ("cl" or "cl.exe" or "ml" or "ml64" or "ml.exe");
    }

    /// <summary>How the linker is told where the sections go.</summary>
    private enum Placement
    {
        /// <summary>The linker is not one this tool knows how to drive.</summary>
        None,

        /// <summary>A linker script pins every section at its address: GNU ld, or a driver running it.</summary>
        Script,

        /// <summary>
        /// No script and no option for an address: sections land at alignment boundaries, in the order
        /// they are handed over — which is how link.exe and lld-link work, and how the original was
        /// built. Whether that reproduces the original's layout is not assumed: the plan hands over one
        /// section per original section, largest first is never in play because the order is the
        /// original's, and the relinked image is compared with the original byte for byte afterwards.
        /// On the MSVC binaries this path was built for the answer is that it does reproduce it.
        /// </summary>
        Alignment,
    }

    /// <summary>
    /// Whether this profile's toolchain can put the image back together: a compiler that reads what
    /// the writer emits, and a linker that can be told — by a script or by alignment — where a
    /// section goes.
    /// </summary>
    private static bool CanRelink(ToolchainProfile profile) => CanAssemble(profile) && PlacementOf(profile) != Placement.None;

    /// <summary>
    /// Which of the two ways this linker places a section, judged by the flags the profile says it
    /// uses: a driver that writes <c>-o</c> is a Unix-style one and takes <c>-Wl,-T</c>; an MSVC-ABI
    /// family writes <c>/OUT:</c> and places by alignment.
    /// </summary>
    private static Placement PlacementOf(ToolchainProfile profile)
    {
        string output = profile.Link?.OutputFlag ?? string.Empty;
        if (output.Length == 0)
        {
            return Placement.None;
        }

        if (output[0] == '-')
        {
            return Placement.Script;
        }

        return string.Equals(profile.Family, "msvc", StringComparison.OrdinalIgnoreCase)
            ? Placement.Alignment
            : Placement.None;
    }

    /// <summary>The linker's spelling of the target machine, from the plan's architecture.</summary>
    private static string Machine(string arch) => arch.ToLowerInvariant() switch
    {
        "x86" => "x86",
        "arm64" => "arm64",
        "arm" => "arm",
        _ => "x64",
    };

    /// <summary>Assemble without linking: the flag a C compiler uses to stop after the object file.</summary>
    private static string AssembleOnlyFlag(ToolchainProfile profile)
        => string.Equals(profile.Family, "msvc", StringComparison.OrdinalIgnoreCase) ? "/c" : "-c";

    private static string Relative(ProjectContext context, string path)
    {
        string relative = Path.GetRelativePath(context.Project.RootDirectory, path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    // ---------------------------------------------------------------- report and viewer

    /// <summary>
    /// How far along the reconstruction is. The comparison says what matches; this says what that
    /// means for each unit, and writes the progress site — one HTML file that needs nothing else.
    /// </summary>
    public int Report()
    {
        var loaded = LoadForReport();
        if (loaded is null)
        {
            return ExitCodes.Configuration;
        }

        var (context, comparison, left, right) = loaded.Value;
        var project = context?.Project ?? Synthesized(comparison);
        string directory = _args.Output
            ?? Path.GetFullPath(Path.Combine(project.RootDirectory, project.Report.Output));

        bool keepHistory = !_args.Has("--no-history")
                           && (context is not null ? project.Report.History : false);
        var history = Recon.Reporting.ProgressHistory.Read(directory);

        if (_args.Has("--aligned"))
        {
            AddAlignedRows(comparison, left, right);
        }

        var report = Recon.Reporting.ProgressBuilder.Build(project, comparison, EntryPoint.Version, history);

        Directory.CreateDirectory(directory);
        string progressPath = Path.Combine(directory, "progress.json");
        File.WriteAllText(progressPath, Recon.Reporting.Reports.Serialize(report) + "\n");

        if (keepHistory)
        {
            var snapshot = Recon.Reporting.ProgressBuilder.Snapshot(report);
            var updated = Recon.Reporting.ProgressHistory.Append(directory, snapshot);
            report.History = updated;
            report.Previous = updated.Count > 1 ? updated[^2] : null;
            File.WriteAllText(progressPath, Recon.Reporting.Reports.Serialize(report) + "\n");
        }

        string indexPath = Path.Combine(directory, Recon.Reporting.ReportSite.IndexFileName);
        File.WriteAllText(indexPath, Recon.Reporting.ReportSite.StaticHtml(comparison, report));

        _out.EmitJson(report);
        if (!_out.Json)
        {
            var totals = report.Totals;
            _out.Line($"report {project.Project.Name}: score {totals.Similarity:0.####}, " +
                      $"{totals.InstructionsEqual}/{totals.InstructionsOriginal} instructions exact " +
                      $"({Percent(totals.InstructionExact)}), " +
                      $"{totals.FunctionsExact}/{totals.FunctionsOriginal} functions verified");
            foreach (var unit in report.Units)
            {
                _out.Line($"  unit {unit.Name,-20} {Percent(unit.Progress),-8} " +
                          $"{unit.Instructions.Equal}/{unit.Instructions.Total} instructions, " +
                          $"{unit.Functions.Missing} function(s) missing");
            }

            // The uncovered count is already a note, and notes are printed as warnings: saying it
            // twice, once as a line and once as a warning, makes the summary look like two facts.
            foreach (var note in report.Notes)
            {
                _out.Warn(note);
            }

            _out.Line($"  wrote {Path.GetRelativePath(project.RootDirectory, progressPath).Replace(Path.DirectorySeparatorChar, '/')}");
            _out.Line($"  wrote {Path.GetRelativePath(project.RootDirectory, indexPath).Replace(Path.DirectorySeparatorChar, '/')}" +
                      (keepHistory ? " and history.jsonl" : string.Empty));
        }

        int violations = 0;
        if (_args.Has("--check-schema"))
        {
            violations = ValidateAgainstSchema("progress", Recon.Reporting.Reports.Serialize(report));
            _out.Line(violations == 0
                ? "progress report matches schema 0.1"
                : $"progress report has {violations} schema violation(s)");
        }

        return violations == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    /// <summary>
    /// The interactive version: serves the same viewer, and computes instruction alignment for one
    /// function at a time, so rebuilding and reloading shows the new state without regenerating
    /// anything.
    /// </summary>
    public int Serve()
    {
        var loaded = LoadForReport();
        if (loaded is null)
        {
            return ExitCodes.Configuration;
        }

        var (context, comparison, left, right) = loaded.Value;
        var project = context?.Project ?? Synthesized(comparison);
        var report = Recon.Reporting.ProgressBuilder.Build(project, comparison, EntryPoint.Version, null);

        int port = 8080;
        if (_args.Value("--port") is { } text)
        {
            if (!int.TryParse(text, out port) || port is < 1 or > 65535)
            {
                _out.Error($"--port expects a number between 1 and 65535, not \"{text}\"");
                return ExitCodes.Usage;
            }
        }

        using var server = new Recon.Reporting.ViewerServer(
            new System.Net.IPEndPoint(System.Net.IPAddress.Any, port),
            new Recon.Reporting.ViewerContent { Comparison = comparison, Progress = report, Left = left, Right = right },
            new BuildLog(_out));

        if (!_out.Json)
        {
            _out.Line($"viewer for {project.Project.Name}: {comparison.Functions.Count} function(s), score {comparison.Summary.Score:0.####}");
            if (left is null || right is null)
            {
                _out.Warn("the comparison came from a file, so per-instruction alignment is unavailable: " +
                          "the viewer shows the recorded differences instead");
            }
        }
        else
        {
            _out.EmitJsonText($"{{\"url\":\"{server.Url}\",\"port\":{server.Port}}}");
        }

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler stop = (_, args) =>
        {
            args.Cancel = true;
            cancel.Cancel();
        };

        Console.CancelKeyPress += stop;
        try
        {
            server.Run(cancel.Token);
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// What both report commands need: a comparison, and the two sides when the comparison was not
    /// read from a file. A comparison read from a file cannot be re-aligned, because aligning needs
    /// the binaries, and the viewer says so rather than showing an empty listing.
    /// </summary>
    private (ProjectContext? Context, ComparisonDocument Comparison, ComparisonSide? Left, ComparisonSide? Right)? LoadForReport()
    {
        var diagnostics = new Diagnostics();
        ProjectContext? context = null;
        string? projectFile = FindProjectFile();
        if (projectFile is not null)
        {
            try
            {
                context = ProjectContext.Load(projectFile, diagnostics);
            }
            catch (ConfigException ex)
            {
                foreach (var diagnostic in ex.Diagnostics)
                {
                    _out.Error(diagnostic.ToString());
                }

                return null;
            }
        }

        if (_args.Value("--comparison") is { } path)
        {
            if (!File.Exists(path))
            {
                _out.Error($"comparison not found: {path}");
                return null;
            }

            try
            {
                var document = System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    Recon.Reporting.ReportsJsonContext.Default.ComparisonDocument);
                if (document is null)
                {
                    _out.Error($"could not read a comparison document from {path}");
                    return null;
                }

                return (context, document, null, null);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _out.Error($"{path}: not a comparison document: {ex.Message}");
                return null;
            }
        }

        try
        {
            var arguments = _args.Arguments;
            if (arguments.Count > 2)
            {
                _out.Error("usage: recon report [left] [right]");
                return null;
            }

            ComparisonSide left;
            ComparisonSide right;
            if (arguments.Count == 2)
            {
                left = LoadSide("left", arguments[0], diagnostics);
                right = LoadSide("right", arguments[1], diagnostics);
            }
            else if (arguments.Count == 1 && projectFile is not null)
            {
                left = ComparisonSide.FromProjectInput("left", projectFile, "original", diagnostics);
                right = LoadSide("right", arguments[0], diagnostics);
            }
            else if (projectFile is not null)
            {
                left = ComparisonSide.FromProjectInput("left", projectFile, "original", diagnostics);
                right = ComparisonSide.FromProjectInput("right", projectFile, "reference", diagnostics);
            }
            else
            {
                _out.Error("no comparison given: pass two binaries, or run this inside a project, or pass --comparison=PATH");
                return null;
            }

            var options = Recon.Compare.ComparisonOptions.FromProfile(left.Profile);
            var comparison = Recon.Compare.ComparisonBuilder.Build(left, right, options);
            return (context, comparison, left, right);
        }
        catch (ConfigException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
            {
                _out.Error(diagnostic.ToString());
            }

            return null;
        }
    }

    /// <summary>
    /// Instruction alignment for every function that is not already exact, so a static report can be
    /// read without a server. Capped: a large binary would otherwise make a file no browser can open.
    /// </summary>
    private void AddAlignedRows(ComparisonDocument comparison, ComparisonSide? left, ComparisonSide? right)
    {
        if (left is null || right is null)
        {
            _out.Warn("--aligned needs the two binaries, and --comparison gave only a document: alignment is skipped");
            return;
        }

        const int limit = 400;
        int done = 0;
        foreach (var function in comparison.Functions)
        {
            if (function.Left is null || function.Right is null || function.Status == "exact")
            {
                continue;
            }

            if (done >= limit)
            {
                _out.Warn($"--aligned stops at {limit} functions; the rest are shown from their recorded differences");
                break;
            }

            function.Aligned = Recon.Compare.ComparisonBuilder.AlignedFor(left, right, function.Left.Id, function.Right.Id);
            done++;
        }
    }

    /// <summary>A project for commands that can also run on two loose binaries.</summary>
    private static ProjectConfig Synthesized(ComparisonDocument comparison)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(comparison.Left.Binary)) ?? Directory.GetCurrentDirectory();
        return new ProjectConfig
        {
            RootDirectory = directory,
            FilePath = Path.Combine(directory, "project.toml"),
            Project = new ProjectMeta { Name = comparison.Left.Label },
            Report = new ReportSpec { Output = "build/report" },
            Target = new TargetSpec { Format = comparison.Left.Format, Arch = comparison.Left.Arch },
        };
    }

    /// <summary>A share of work done, or a dash when there is nothing to measure it against.</summary>
    private static string Percent(double? value)
        => value is null ? "-" : $"{100 * value.Value:0.##}%";

    private int ValidateAgainstSchema(string name, string json)
    {
        var schemaJson = BuiltInSchemas.Get(name);
        if (schemaJson is null)
        {
            _out.Error($"the {name} schema is missing from this build");
            return 1;
        }

        // Parsed as a document, not as a node graph: validating an inventory of a large program used
        // to cost an object per value, which is what made `--check-schema` die on the documents it
        // was there to check.
        var problems = Recon.Schema.JsonSchemaValidator.Parse(schemaJson).Validate(json);
        foreach (var problem in problems)
        {
            _out.Error($"schema violation: {problem}");
        }

        return problems.Count;
    }

    // ---------------------------------------------------------------- hash / migrate / schema / docs

    /// <summary>
    /// The members of a COFF archive. A <c>.lib</c> is not a program — it has no entry point and
    /// nothing to decode — so it is not loaded as one; what it holds is the objects a link can pull
    /// in, and each of those carries an <c>@comp.id</c> naming the compiler that built it. That is the
    /// bill of materials of a runtime, and it is the reason to read one.
    ///
    /// The command takes a path rather than the project's input, because a library sits beside the
    /// binary under reconstruction rather than being it.
    /// </summary>
    /// <summary>
    /// The structure of a Visual Basic 5/6 program. A VB program is the one kind of PE whose
    /// interesting structure is reached entirely by pointers — entry point to the VB header, header
    /// to project data, project data to the object table — so this walks that chain and reports what
    /// each link says, refusing to follow a pointer that lands outside the image.
    ///
    /// It reports the project's *shape* rather than its code, and says which compilation mode the
    /// program is in: that is the difference the tool can see honestly, since the method table a
    /// decoder would need is one only the p-code compiler fills in.
    /// </summary>
    /// <summary>
    /// The runtime that interprets a p-code program, found without being asked: named by the command,
    /// beside the program, or in the project's input directory. It is only looked for when the binary
    /// actually is p-code, and finding none is not an error — a p-code inventory without a runtime lists
    /// the procedures the program states and says its instructions were not measured.
    /// </summary>
    private string? PcodeRuntimeFor(ProjectContext context, Recon.Images.IBinaryImage image)
    {
        if (!string.Equals(image.Isa, "vb6-pcode", StringComparison.Ordinal))
        {
            return null;
        }

        if (_args.Value("--runtime") is { Length: > 0 } named)
        {
            return File.Exists(named) ? named : null;
        }

        List<string> candidates = [];
        if (FindRuntime(image.Path) is { } beside)
        {
            candidates.Add(beside);
        }

        foreach (string root in new[] { context.RootDirectory, context.Local.InputDirectory })
        {
            foreach (string name in new[] { "msvbvm60.dll", "MSVBVM60.DLL", "msvbvm50.dll", "MSVBVM50.DLL" })
            {
                string candidate = Path.Combine(root, name);
                if (File.Exists(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// The 256 p-code opcodes of a Visual Basic runtime, each read from the handler the runtime
    /// dispatches it to. The opcode table says where each opcode goes; this says what is there — the
    /// runtime's own calls, and a name when the evidence gives exactly one.
    /// </summary>
    public int Opcodes()
    {
        string? file = _args.Arguments.FirstOrDefault();
        if (file is null)
        {
            _out.Error("usage: recon opcodes <msvbvm60.dll> [--opcode <n>] [--json] [--check-schema]");
            return ExitCodes.Usage;
        }

        if (!File.Exists(file))
        {
            _out.Error($"{file}: not found");
            return ExitCodes.Usage;
        }

        var loaded = Recon.Images.ImageLoader.Load(file);
        if (loaded.Image?.Pe is null)
        {
            _out.Error(loaded.Problems.Count > 0 ? loaded.Problems[0] : $"{file} is not a PE image");
            return ExitCodes.CheckFailed;
        }

        var sections = loaded.Image.Pe.Sections
            .Select(s => new Recon.Vb6.PcodeSection(s.Name, s.Rva, s.RawOffset, s.VirtualSize, s.IsCode))
            .ToList();

        var runtime = Recon.Vb6.PcodeRuntime.Read(loaded.Bytes, file, loaded.Image.Pe.ImageBase, sections);
        if (!runtime.IsPcodeRuntime)
        {
            _out.Error($"{file} is not a Visual Basic runtime: {string.Join("; ", runtime.Problems)}");
            return ExitCodes.CheckFailed;
        }

        var reading = Recon.Vb6.PcodeOpcodes.ReadAll(runtime, loaded.Image, loaded.Image.Pe, loaded.Bytes);
        var tables = reading.Tables;
        var opcodes = tables.SelectMany(t => t.Opcodes).ToList();
        var version = Recon.Pe.VersionResource.Read(loaded.Image.Pe, loaded.Bytes);

        int? wantedOpcode = null;
        if (_args.Value("--opcode") is string filter)
        {
            if (!TryParseOpcode(filter, out int wanted))
            {
                _out.Error($"--opcode wants a number from 0 to 255, not \"{filter}\"");
                return ExitCodes.Usage;
            }

            wantedOpcode = wanted;
        }

        int? wantedLead = null;
        if (_args.Value("--lead") is string leadFilter)
        {
            if (!TryParseOpcode(leadFilter, out int wanted))
            {
                _out.Error($"--lead wants a number from 0 to 255, not \"{leadFilter}\"");
                return ExitCodes.Usage;
            }

            if (!tables.Any(x => x.LeadOpcode == wanted))
            {
                _out.Error($"no table of this runtime is selected by 0x{wanted:X2}");
                return ExitCodes.CheckFailed;
            }

            wantedLead = wanted;
        }

        // Rows are keyed by table as well as opcode: the same byte means different things in different
        // tables, so a filter that named only the byte would answer with several instructions at once.
        var rows = tables
            .Where(x => wantedLead is null || x.LeadOpcode == wantedLead.Value)
            .SelectMany(x => x.Opcodes.Select(o => (Table: x, Opcode: o)))
            .ToList();

        var shown = wantedOpcode is null
            ? rows
            : rows.Where(r => r.Opcode.Opcode == wantedOpcode.Value).ToList();

        var document = new Recon.Reporting.OpcodesReport
        {
            Command = "recon opcodes",
            ToolVersion = EntryPoint.Version,
            File = file,
            Sha256 = PeImage.HashFile(file),
            RuntimeVersion = version.Display,
            TableRva = runtime.PrimaryTable?.TableRva ?? 0,
            DispatchSites = runtime.DispatchSites,
            Tables = tables.Select(x => new Recon.Reporting.OpcodeTableRow
            {
                Lead = x.IsPrimary ? null : x.LeadOpcode,
                TableRva = x.TableRva,
                DispatchSites = x.UsedBySites,
                ClaimedBy = x.ClaimedBy.ToList(),
                Opcodes = x.Opcodes.Count,
                Measured = x.Measured,
            }).ToList(),
            Summary = new Recon.Reporting.OpcodesSummary
            {
                Opcodes = tables.Count > 0 ? tables[0].Opcodes.Count : 0,
                Tables = tables.Count,
                Rows = opcodes.Count,
                Unhandled = opcodes.Count(o => o.IsUnhandled),
                Named = opcodes.Count(o => o.Name.Length > 0),
                DistinctNames = opcodes.Where(o => o.Name.Length > 0).Select(o => o.Name).Distinct().Count(),
                SharedCalls = opcodes
                    .SelectMany(o => o.Calls)
                    .GroupBy(c => c)
                    .Where(g => g.Count() >= Math.Max(8, opcodes.Count / 8))
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} ({g.Count()} opcodes)")
                    .ToList(),
                HandlersAnalysed = opcodes.Count(o => o.Instructions > 0),
                WithStackEffect = opcodes.Count(o => o.StackEffects.Count == 1),
                Raising = opcodes.Count(o => o.Raises),
                RaiserRva = reading.RaiserRva,
            },
            Opcodes = shown.Select(r => new Recon.Reporting.OpcodeRow
            {
                Opcode = r.Opcode.Opcode,
                Lead = r.Table.IsPrimary ? null : r.Table.LeadOpcode,
                TableRva = r.Table.TableRva,
                HandlerRva = r.Opcode.HandlerRva,
                Unhandled = r.Opcode.IsUnhandled,
                InstructionSize = r.Opcode.InstructionSize,
                Sizes = r.Opcode.Sizes.ToList(),
                Counted = r.Opcode.IsCounted,
                CountedUnit = r.Opcode.CountedUnit,
                Operand = r.Opcode.Operand,
                OperandAt = r.Opcode.OperandAt,
                OperandSlotAt = r.Opcode.OperandSlotAt,
                OperandBytes = r.Opcode.OperandBytes,
                OperandBasis = r.Opcode.OperandBasis,
                Instructions = r.Opcode.Instructions,
                Name = r.Opcode.Name,
                StackEffects = r.Opcode.StackEffects.ToList(),
                StackEffect = r.Opcode.StackEffects.Count == 1 ? r.Opcode.StackEffects[0] : null,
                Raises = r.Opcode.Raises,
                OutsideJumps = r.Opcode.RaisedJumps.ToList(),
                StackBasis = r.Opcode.StackBasis,
                Calls = r.Opcode.Calls.ToList(),
                ReachedCalls = r.Opcode.ReachedCalls.ToList(),
                InternalCalls = r.Opcode.InternalCalls,
                IndirectCalls = r.Opcode.IndirectCalls,
                Basis = r.Opcode.Basis,
            }).ToList(),
            Problems = runtime.Problems.ToList(),
        };

        EmitWithSchemaCheck(document, "opcodes");
        if (_out.Json)
        {
            return ExitCodes.Ok;
        }

        _out.Line($"{file}: {version.Display} — {document.Summary.Tables} table(s), {document.Summary.Rows} opcodes, "
            + $"{document.Summary.Named} named, {document.Summary.Unhandled} unhandled");
        _out.Line($"  dispatch table at rva 0x{document.TableRva:X}, reached from {document.DispatchSites} site(s)");

        foreach (var table in document.Tables.Where(x => x.Lead is not null))
        {
            string claimed = table.ClaimedBy.Count == 0
                ? "no dispatcher claims it"
                : $"claimed by {string.Join(", ", table.ClaimedBy.Select(c => $"0x{c:X2}"))}";
            _out.Line($"  lead byte 0x{table.Lead:X2}: table at rva 0x{table.TableRva:X}, {table.Measured} of {table.Opcodes} opcodes measured, {claimed}");
        }

        // The table shown is the one that was asked for: the primary table by default, and the table a
        // lead byte selects when one was named. Printing all six unasked would bury the 256 rows that
        // are the answer to the question that was asked under 1,280 that were not.
        int? shownLead = wantedLead;
        var head = document.Tables.FirstOrDefault(t => t.Lead == shownLead);
        if (document.Summary.RaiserRva != 0)
        {
            _out.Line($"  raises through rva 0x{document.Summary.RaiserRva:X}");
        }

        _out.Line(wantedLead is null && wantedOpcode is not null
            ? $"  that byte in each of the {document.Tables.Count} tables"
            : $"  table 0x{head?.TableRva ?? 0:X} ({head?.Opcodes ?? 0} opcodes)");

        _out.Table(
            shown.Select(r => new[]
            {
                r.Table.IsPrimary ? $"0x{r.Opcode.Opcode:X2}" : $"0x{r.Table.LeadOpcode:X2} 0x{r.Opcode.Opcode:X2}",
                r.Opcode.IsUnhandled ? "-" : $"0x{r.Opcode.HandlerRva:X}",
                r.Opcode.InstructionSize is int size
                    ? size.ToString()
                    : r.Opcode.Sizes.Count > 0 ? string.Join("/", r.Opcode.Sizes) : "?",
                r.Opcode.Name.Length > 0 ? r.Opcode.Name : r.Opcode.IsUnhandled ? "(unhandled)" : "(no name)",
                CallEvidence(r.Opcode),
                // Slots of operand stack, in dwords, or `?` where the code does not fix an amount — with
                // both amounts where the handler's paths disagree. `raise` is a column of its own because
                // it is a different kind of fact: not how much the opcode moves, but that it may not
                // come back.
                r.Opcode.StackEffects.Count switch
                {
                    0 => "?",
                    1 => Slots(r.Opcode.StackEffects[0]),
                    _ => string.Join("/", r.Opcode.StackEffects.Select(Slots)),
                },
                r.Opcode.Raises ? "raise" : string.Empty,
            }),
            "opcode", "handler", "size", "name", "calls", "stack", "raise");

        foreach (string shared in document.Summary.SharedCalls)
        {
            _out.Line($"  shared by opcodes: {shared}");
        }

        return ExitCodes.Ok;
    }

    /// <summary>One stack effect as a table cell: `+1`, `-2`, `0`.</summary>
    private static string Slots(int slots) => slots > 0 ? $"+{slots}" : slots.ToString();

    /// <summary>
    /// What an opcode's handler calls, as one cell of the table: what it calls itself, and — where it
    /// calls a helper instead — what that helper reaches, marked `via` so that second-hand evidence
    /// is not read as a direct call.
    /// </summary>
    private static string CallEvidence(Recon.Vb6.OpcodeEvidence opcode)
    {
        // An unimplemented slot's evidence is that it is unimplemented, and printing the error path it
        // reaches under "calls" would read as though the opcode called it.
        if (opcode.IsUnhandled)
        {
            return string.Empty;
        }

        var parts = opcode.Calls.Take(3).ToList();
        if (opcode.ReachedCalls.Count > 0)
        {
            parts.Add($"via {opcode.ReachedCalls[0]}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A Visual Basic 6 program compiled to p-code, read as the interpreter reads it.
    ///
    /// The program's structure comes from <see cref="Recon.Vb6.Vb6Program"/> — entry point, header,
    /// project data, object table — and the object descriptors it finds are the ones a p-code build
    /// fills in and a native build leaves empty. Each object names a method dispatch table, each entry
    /// of that table is a <c>ProcDscInfo</c>, and the code starts a recorded number of bytes before it.
    ///
    /// The instruction lengths are measured from a runtime (see <c>recon opcodes</c>) rather than taken
    /// from a table, so the runtime is named and its version reported: a stream decoded against a
    /// different build's interpreter would be a plausible listing of the wrong thing.
    /// </summary>
    public int Pcode()
    {
        string? file = _args.Arguments.FirstOrDefault();
        if (file is null)
        {
            _out.Error("usage: recon pcode <program.exe> [--runtime=<msvbvm60.dll>] [--object=NAME] [--procedure=N] [--json] [--check-schema]");
            return ExitCodes.Usage;
        }

        if (!File.Exists(file))
        {
            _out.Error($"{file}: not found");
            return ExitCodes.Usage;
        }

        var loaded = Recon.Images.ImageLoader.Load(file);
        if (loaded.Image?.Pe is null)
        {
            _out.Error(loaded.Problems.Count > 0 ? loaded.Problems[0] : $"{file} is not a PE image");
            return ExitCodes.CheckFailed;
        }

        var program = Recon.Vb6.Vb6Program.Read(loaded.Image.Pe, loaded.Bytes);
        if (program is null)
        {
            _out.Error($"{file} is not a Visual Basic program: {string.Join("; ", loaded.Problems)}");
            return ExitCodes.CheckFailed;
        }

        if (!program.Project.IsPcode)
        {
            _out.Error(
                $"{file} is a native build (native code at rva 0x{program.Project.NativeCodeRva:X}), not p-code: " +
                "its instructions are machine code, which `recon disasm` reads");
            return ExitCodes.CheckFailed;
        }

        string? runtimePath = _args.Value("--runtime") ?? FindRuntime(file);
        if (runtimePath is null)
        {
            _out.Error(
                $"{file} needs the runtime that interprets it: pass --runtime=<msvbvm60.dll>. The instruction " +
                "lengths are measured out of that file's handlers, and none is beside the program");
            return ExitCodes.CheckFailed;
        }

        var runtimeLoaded = Recon.Images.ImageLoader.Load(runtimePath);
        if (runtimeLoaded.Image?.Pe is null)
        {
            _out.Error($"{runtimePath}: {runtimeLoaded.Problems.FirstOrDefault() ?? "not a PE image"}");
            return ExitCodes.CheckFailed;
        }

        var sections = runtimeLoaded.Image.Pe.Sections
            .Select(s => new Recon.Vb6.PcodeSection(s.Name, s.Rva, s.RawOffset, s.VirtualSize, s.IsCode))
            .ToList();

        var runtime = Recon.Vb6.PcodeRuntime.Read(
            runtimeLoaded.Bytes, runtimePath, runtimeLoaded.Image.Pe.ImageBase, sections);
        if (!runtime.IsPcodeRuntime)
        {
            _out.Error($"{runtimePath} is not a Visual Basic runtime: {string.Join("; ", runtime.Problems)}");
            return ExitCodes.CheckFailed;
        }

        var tables = Recon.Vb6.PcodeProgram.ReadTables(
            runtimeLoaded.Image, runtimeLoaded.Image.Pe, runtimeLoaded.Bytes, runtime);

        string? objectFilter = _args.Value("--object");
        int? procedureFilter = null;
        if (_args.Value("--procedure") is string procedureText)
        {
            if (!int.TryParse(procedureText, out int wanted) || wanted < 0)
            {
                _out.Error($"--procedure wants a number, not \"{procedureText}\"");
                return ExitCodes.Usage;
            }

            procedureFilter = wanted;
        }

        // No `is not null` here: `runtimePath` was proven non-null above, and asking again would widen
        // it back to "maybe null" for the rest of the method — which is what the compiler said, in a
        // warning about the line that reports it.
        if (!File.Exists(runtimePath))
        {
            _out.Error($"{runtimePath}: not found");
            return ExitCodes.Usage;
        }

        if (procedureFilter is not null && objectFilter is null && program.Objects.Count > 1)
        {
            _out.Error("--procedure names a method of one object: pass --object=<name> with it");
            return ExitCodes.Usage;
        }

        bool wantInstructions = procedureFilter is not null;
        var decoded = Recon.Vb6.PcodeProgram.Read(
            loaded.Image.Pe, loaded.Bytes, program, tables, withInstructions: wantInstructions);

        var rows = decoded.Procedures.AsEnumerable();
        if (objectFilter is not null)
        {
            rows = rows.Where(p => string.Equals(p.ObjectName, objectFilter, StringComparison.OrdinalIgnoreCase));
        }

        var listed = rows.ToList();
        var selected = procedureFilter is null
            ? null
            : listed.FirstOrDefault(p => p.MethodIndex == procedureFilter.Value);

        if (procedureFilter is not null && selected is null)
        {
            _out.Error(
                objectFilter is null
                    ? $"no procedure {procedureFilter.Value} in this program"
                    : $"no procedure {procedureFilter.Value} in {objectFilter}");
            return ExitCodes.Usage;
        }

        var version = Recon.Pe.VersionResource.Read(runtimeLoaded.Image.Pe, runtimeLoaded.Bytes);
        var report = new Recon.Reporting.PcodeReport
        {
            Command = "recon pcode",
            ToolVersion = EntryPoint.Version,
            File = file,
            Sha256 = PeImage.HashFile(file),
            Runtime = runtimePath,
            RuntimeVersion = version.Display,
            ProgramRuntimeBuild = program.Header.RuntimeBuild,
            RuntimeFileBuild = version.Build,
            Summary = new Recon.Reporting.PcodeSummary
            {
                Objects = program.Objects.Count,
                Procedures = decoded.Procedures.Count,
                Exact = decoded.Procedures.Count(p => p.Status == "exact"),
                Padded = decoded.Procedures.Count(p => p.Status == "padded"),
                Undecodable = decoded.Procedures
                    .Where(p => p.Status is not ("exact" or "padded"))
                    .GroupBy(p => p.Status)
                    .ToDictionary(g => g.Key, g => g.Count()),
                EmptySlots = decoded.EmptySlots,
                EntriesNotProcedures = decoded.EntriesThatAreNotProcedures,
                ResolvedReadings = decoded.Procedures.Count(p => p.Readings > 1),
                EndedWithExit = decoded.Procedures.Count(p => p.EndsWithExit),
                Instructions = decoded.Procedures.Sum(p => p.InstructionCount),
                CodeBytes = decoded.Procedures.Sum(p => p.CodeBytes),
            },
            Procedures = listed.Select(p => new Recon.Reporting.PcodeProcedureRow
            {
                Object = p.ObjectName,
                ObjectIndex = p.ObjectIndex,
                Method = p.MethodIndex,
                DescriptorRva = p.DescriptorRva,
                CodeRva = p.CodeRva,
                CodeSize = p.CodeBytes,
                FrameSize = p.FrameSize,
                ArgumentSize = p.ArgumentSize,
                Instructions = p.InstructionCount,
                Padding = p.Padding,
                Status = p.Status,
                EndsWithExit = p.EndsWithExit,
                Problems = p.Problems.ToList(),
            }).ToList(),
            Instructions = selected?.Instructions.Select(i => new Recon.Reporting.PcodeInstructionRow
            {
                Rva = i.Rva,
                Opcode = i.Describe(),
                Bytes = i.Bytes,
                Size = i.Size,
                Operand = i.Operand,
                OperandBytes = i.OperandBytes,
                OperandAt = i.OperandAt,
                Target = i.Target,
                TargetIsAnInstructionStart = i.TargetIsInstructionStart,
                FrameSlot = i.FrameSlot,
                FrameSlotAt = i.FrameSlotAt,
                Name = i.Name,
                Unhandled = i.Unhandled,
            }).ToList() ?? [],
            Problems = decoded.Problems.ToList(),
        };

        EmitWithSchemaCheck(report, "pcode");
        if (_out.Json)
        {
            return ExitCodes.Ok;
        }

        _out.Line($"{file}: p-code, {report.Summary.Procedures} procedure(s) in {report.Summary.Objects} object(s)");
        _out.Line($"  instructions read against {Path.GetFileName(runtimePath)} {version.Display}{report.RuntimeBuildNote()}");
        _out.Line(
            $"  {report.Summary.Instructions} instruction(s) in {report.Summary.CodeBytes} bytes: " +
            $"{report.Summary.Exact} exact, {report.Summary.Padded} padded");

        foreach (var (status, count) in report.Summary.Undecodable.OrderByDescending(kv => kv.Value))
        {
            _out.Line($"  {count} could not be read ({status})");
        }

        _out.Table(
            listed.Select(p => new[]
            {
                p.ObjectName,
                p.MethodIndex.ToString(),
                $"0x{p.CodeRva:X}",
                p.CodeBytes.ToString(),
                p.InstructionCount.ToString(),
                p.Status,
                p.EndsWithExit ? "yes" : "no",
            }),
            "object", "method", "code", "bytes", "instructions", "status", "ends with exit");

        if (selected is not null)
        {
            _out.Line($"{selected.ObjectName}[{selected.MethodIndex}]: {selected.Describe()}");
            _out.Table(
                selected.Instructions.Select(i => new[]
                {
                    $"0x{i.Rva:X}",
                    i.Bytes,
                    i.Describe(),
                    i.Size.ToString(),
                    i.DescribeOperand(),
                    i.Name.Length > 0 ? i.Name : i.Unhandled ? "(unhandled)" : string.Empty,
                }),
                "rva", "bytes", "opcode", "size", "operand", "name");
        }

        foreach (string problem in report.Problems.Concat(selected?.Problems ?? []))
        {
            _out.Line($"  {problem}");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Whether a positional argument means the file this project declares: either the path it resolves
    /// to or the input's own id, because <c>--project</c> plus an input id is how the rest of the tool
    /// names one.
    /// </summary>
    private static bool IsTheProjectsInput(Recon.Project.ProjectContext context, string argument)
    {
        if (context.Project.Original is not { } original)
        {
            return false;
        }

        string declared = context.ResolveInputPath(original);
        if (string.Equals(argument, original.File, StringComparison.Ordinal)
            || string.Equals(argument, original.Role, StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, original.Id, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(argument), Path.GetFullPath(declared),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // Not a path at all: then it is not this project's input either.
            return false;
        }
    }

    /// <summary>
    /// The p-code interpreter a program runs on: the one named by <c>--runtime</c>, or one beside the
    /// program. A p-code program is not self-contained — it is a stream for the runtime to interpret —
    /// and the runtime has to be the one it was built against for its lengths to mean anything.
    /// </summary>
    private static string? FindRuntime(string program)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(program));
        foreach (string name in new[] { "msvbvm60.dll", "MSVBVM60.DLL", "msvbvm50.dll", "MSVBVM50.DLL" })
        {
            string candidate = Path.Combine(directory ?? ".", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>An opcode is a byte: <c>0x1F</c>, <c>1F</c> and <c>31</c> all mean the same one.</summary>
    private static bool TryParseOpcode(string text, out int value)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? text[2..] : text;
        return int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value)
            && value is >= 0 and <= 255;
    }

    public int Vb6()
    {
        string? file = _args.Arguments.FirstOrDefault();
        if (file is null)
        {
            _out.Error("usage: recon vb6 <file.exe> [--json] [--check-schema]");
            return ExitCodes.Usage;
        }

        if (!File.Exists(file))
        {
            _out.Error($"{file}: not found");
            return ExitCodes.Usage;
        }

        var loaded = Recon.Images.ImageLoader.Load(file);
        if (loaded.Image?.Pe is null)
        {
            _out.Error(loaded.Problems.Count > 0 ? loaded.Problems[0] : $"{file} is not a PE image");
            return ExitCodes.CheckFailed;
        }

        var program = Recon.Vb6.Vb6Program.Read(loaded.Image.Pe, loaded.Bytes);
        if (program is null)
        {
            // Not a VB program is not a failure of this program: it is an answer, and the entry point
            // is what makes it one.
            _out.Error($"{file} is not a Visual Basic 5/6 program: its entry point does not push a VB5! header");
            return ExitCodes.CheckFailed;
        }

        var document = new Recon.Reporting.Vb6Report
        {
            Command = "recon vb6",
            ToolVersion = EntryPoint.Version,
            File = file,
            Sha256 = PeImage.HashFile(file),
            Header = new Recon.Reporting.Vb6HeaderRow
            {
                Signature = program.Header.Signature,
                Rva = program.Header.HeaderRva,
                RuntimeBuild = program.Header.RuntimeBuild,
                RuntimeDllVersion = program.Header.RuntimeDllVersion,
                LanguageDll = program.Header.LanguageDll,
                LanguageId = program.Header.LanguageId,
                SubMainRva = program.Header.SubMainRva,
            },
            Project = new Recon.Reporting.Vb6ProjectRow
            {
                TemplateVersion = program.Project.Version,
                Isa = program.Project.IsPcode ? "vb6-pcode" : "x86",
                CodeStartRva = program.Project.CodeStartRva,
                CodeEndRva = program.Project.CodeEndRva,
                CodeSize = program.Project.CodeSize,
                DataSize = program.Project.DataSize,
                NativeCodeRva = program.Project.NativeCodeRva,
                BuildPath = program.Project.BuildPath,
                CompileState = program.CompileState,
                ObjectsDeclared = program.ObjectsDeclared,
                ObjectsReported = program.Objects.Count,
                ObjectsCompiled = program.ObjectsCompiled,
                CodeNote = program.Project.IsPcode
                    ? "p-code: the code region is interpreter input, not machine code, and this does not decode it"
                    : $"native code: the code region is x86 and recon decodes it",
            },
            Objects = program.Objects.Select(o => new Recon.Reporting.Vb6ObjectRow
            {
                Name = o.Name,
                Kind = o.Kind,
                TypeFlags = o.TypeFlags,
                Methods = o.MethodCount,
                DescriptorRva = o.DescriptorRva,
            }).ToList(),
            Problems = program.Problems.ToList(),
        };

        EmitWithSchemaCheck(document, "vb6");
        if (_out.Json)
        {
            return program.Ok ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        var header = program.Header;
        _out.Line($"{file}: {header.Describe()}");
        _out.Line($"  template 0x{program.Project.Version:X}, {program.Objects.Count} object(s), compiled from \"{program.Project.BuildPath}\"");
        _out.Line(string.Format(
            "  code rva 0x{0:X}..0x{1:X} ({2} bytes); {3}",
            program.Project.CodeStartRva,
            program.Project.CodeEndRva,
            program.Project.CodeSize,
            program.Project.IsPcode ? "p-code, not decoded" : "native x86"));

        _out.Table(
            program.Objects.Select(o => new[]
            {
                o.Name,
                o.Kind,
                o.MethodCount.ToString(),
                $"0x{o.TypeFlags:X8}",
            }),
            "object", "kind", "methods", "type flags");

        foreach (string problem in program.Problems)
        {
            _out.Warn(problem);
        }

        return program.Ok ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    public int Lib()
    {
        string? file = _args.Arguments.FirstOrDefault();
        if (file is null)
        {
            _out.Error("usage: recon lib <file.lib> [--json] [--check-schema]");
            return ExitCodes.Usage;
        }

        if (!File.Exists(file))
        {
            _out.Error($"{file}: not found");
            return ExitCodes.Usage;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            _out.Error($"cannot read {file}: {ex.Message}");
            return ExitCodes.Usage;
        }

        if (!Recon.Archive.CoffArchive.Sniff(bytes))
        {
            // Saying what it does start with is what makes the refusal useful: a COFF object is not
            // an archive, and the two are easy to hand to the wrong command.
            string magic = bytes.Length >= 8 ? System.Text.Encoding.ASCII.GetString(bytes, 0, 8).Replace("\n", "\\n") : "an empty file";
            _out.Error($"{file} is not a COFF archive: it begins with \"{magic}\", not \"!<arch>\\n\"");
            return ExitCodes.CheckFailed;
        }

        var archive = Recon.Archive.CoffArchive.Load(bytes, file);
        var document = new Recon.Reporting.LibraryReport
        {
            Command = "recon lib",
            ToolVersion = EntryPoint.Version,
            File = file,
            Sha256 = PeImage.HashFile(file),
            Members = archive.Members.Select(m => new Recon.Reporting.LibraryMemberRow
            {
                Name = m.Name,
                RawName = m.RawName,
                Kind = m.Kind.ToString().ToLowerInvariant(),
                Offset = m.DataOffset,
                Size = m.Size,
                Date = m.Date,
                Machine = m.Kind is Recon.Archive.ArchiveMemberKind.Object or Recon.Archive.ArchiveMemberKind.Import ? m.Machine : null,
                Timestamp = m.Kind is Recon.Archive.ArchiveMemberKind.Object or Recon.Archive.ArchiveMemberKind.Import ? m.TimeDateStamp : null,
                CompId = m.CompId,
                CompTool = m.CompTool,
                CompBuild = m.CompBuild,
                Sections = m.Sections.ToList(),
                Symbols = m.Symbols.ToList(),
                ImportDll = m.ImportDll,
                ImportSymbol = m.ImportSymbol,
                ImportOrdinal = m.ImportOrdinal,
                Describe = m.Describe(),
            }).ToList(),
            Problems = archive.Problems.ToList(),
        };

        document.Summary = new Recon.Reporting.LibrarySummary
        {
            Members = archive.Members.Count,
            Objects = archive.Objects.Count(),
            Imports = archive.Imports.Count(),
            Compilers = archive.CompilersUsed.ToList(),
            ImportedDlls = archive.Imports.Select(m => m.ImportDll!).Where(d => d is not null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        EmitWithSchemaCheck(document, "lib");
        if (_out.Json)
        {
            return archive.Problems.Count == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        _out.Line($"{file}: {document.Summary.Members} member(s), {document.Summary.Objects} object(s), {document.Summary.Imports} import record(s)");
        _out.Table(
            archive.Members.Select(m => new[]
            {
                m.Name,
                m.Kind.ToString().ToLowerInvariant(),
                m.Size.ToString(),
                m.Describe(),
            }),
            "member", "kind", "size", "what it is");

        foreach (string compiler in document.Summary.Compilers)
        {
            _out.Line($"  built by {compiler}");
        }

        foreach (string dll in document.Summary.ImportedDlls)
        {
            int count = archive.Imports.Count(m => string.Equals(m.ImportDll, dll, StringComparison.OrdinalIgnoreCase));
            _out.Line($"  imports {count} symbol(s) from {dll}");
        }

        foreach (string problem in archive.Problems)
        {
            _out.Warn(problem);
        }

        return archive.Problems.Count == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    public int Hash()
    {
        if (_args.Arguments.Count == 0)
        {
            _out.Error("usage: recon hash <file> [...]");
            return ExitCodes.Usage;
        }

        int failures = 0;
        foreach (var file in _args.Arguments)
        {
            if (!File.Exists(file))
            {
                _out.Error($"{file}: not found");
                failures++;
                continue;
            }

            _out.Line($"{PeImage.HashFile(file)}  {Path.GetFileName(file)}");
        }

        return failures == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    public int Migrate()
    {
        var context = OpenProject();
        if (context is null)
        {
            return ExitCodes.Configuration;
        }

        var files = new List<(string Path, int Version)>
        {
            (context.Project.FilePath, context.Project.SchemaVersion),
            (context.Local.FilePath, context.Local.SchemaVersion),
        };
        files.AddRange(context.Registry.Raw.Values.Select(p => (p.FilePath, p.SchemaVersion)));

        var document = new Recon.Reporting.MigrateDocument
        {
            Command = "recon migrate",
            ToolVersion = EntryPoint.Version,
            CurrentVersion = SchemaVersions.Current,
            WriteRequested = _args.Has("--write"),
        };

        int outdated = 0;
        foreach (var (path, version) in files.Where(f => !string.IsNullOrEmpty(f.Path)))
        {
            bool current = version == SchemaVersions.Current;
            if (!current)
            {
                outdated++;
                _out.Line($"{path}: schema_version {version} (current {SchemaVersions.Current})");
            }

            document.Files.Add(new Recon.Reporting.MigrateFile
            {
                Path = path,
                Kind = path == context.Project.FilePath ? "project"
                    : path == context.Local.FilePath ? "local"
                    : "profile",
                Version = version,
                Current = current,
            });
        }

        document.Outdated = outdated;

        if (outdated == 0)
        {
            document.Message = $"all configuration files are at schema_version {SchemaVersions.Current}; nothing to do";
            _out.Line(document.Message);
            EmitWithSchemaCheck(document, "migrate");
            return ExitCodes.Ok;
        }

        document.Message = "no migration path is defined between these versions in this build; review the changelog in docs/schema.md";
        _out.Line(document.Message);
        EmitWithSchemaCheck(document, "migrate");
        return _args.Has("--check") ? ExitCodes.CheckFailed : ExitCodes.Ok;
    }

    public int Schema()
    {
        string? subcommand = _args.Subcommand ?? "list";
        switch (subcommand)
        {
            case "list":
                foreach (var name in BuiltInSchemas.Names)
                {
                    _out.Line(name);
                }

                return ExitCodes.Ok;

            case "show":
                {
                    string? name = _args.Arguments.FirstOrDefault();
                    if (name is null)
                    {
                        _out.Error("usage: recon schema show <name>");
                        return ExitCodes.Usage;
                    }

                    var content = BuiltInSchemas.Get(name);
                    if (content is null)
                    {
                        _out.Error($"unknown schema \"{name}\" (known: {string.Join(", ", BuiltInSchemas.Names)})");
                        return ExitCodes.CheckFailed;
                    }

                    Console.WriteLine(content);
                    return ExitCodes.Ok;
                }

            case "export":
                {
                    string directory = _args.Value("--dir") ?? "schema";
                    Directory.CreateDirectory(directory);
                    foreach (var (name, content) in BuiltInSchemas.All())
                    {
                        string path = Path.Combine(directory, $"{name}.schema.json");
                        File.WriteAllText(path, content);
                        _out.Line($"wrote {path}");
                    }

                    return ExitCodes.Ok;
                }

            default:
                _out.Error($"unknown schema subcommand \"{subcommand}\"");
                return ExitCodes.Usage;
        }
    }

    public int GenerateDocs()
    {
        string path = _args.Output ?? Path.Combine("docs", "cli.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Help.GenerateMarkdown());
        _out.Line($"wrote {path}");
        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- helpers

    private ProjectContext? OpenProject()
    {
        var diagnostics = new Diagnostics();
        string? projectFile = ResolveProjectFile(_args.ProjectPath);
        if (projectFile is null)
        {
            _out.Error("no project.toml found; run `recon init` or pass --project <dir>");
            return null;
        }

        try
        {
            return ProjectContext.Load(projectFile, diagnostics);
        }
        catch (ConfigException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
            {
                _out.Error(diagnostic.ToString());
            }

            return null;
        }
    }

    /// <summary>
    /// The directory <c>--project</c> names for <c>init</c>: the path itself when it is, or will be,
    /// a directory, and the directory holding it when a project file was named. <c>/a/new</c> means
    /// <c>/a/new</c>; <c>/a/new/project.toml</c> and <c>/a/new/</c> both mean <c>/a/new</c>.
    /// </summary>
    private static string? InitDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            // An existing directory is the one to write into, whether or not it holds a project.
            return path;
        }

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path);
        }

        // Neither exists yet: a name ending in project.toml is a file to be created, so its
        // directory is the target; anything else is the target.
        return string.Equals(Path.GetFileName(path), ProjectContext.ProjectFileName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path)
            : path;
    }

    private static string? ResolveProjectFile(string? explicitPath)
    {
        if (explicitPath is null)
        {
            return ProjectContext.FindProjectFile(null);
        }

        if (Directory.Exists(explicitPath))
        {
            string candidate = Path.Combine(explicitPath, ProjectContext.ProjectFileName);
            return File.Exists(candidate) ? candidate : null;
        }

        return File.Exists(explicitPath) ? explicitPath : null;
    }

}
