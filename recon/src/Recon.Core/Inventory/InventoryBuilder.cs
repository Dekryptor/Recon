using Recon.Images;
using Recon.Analysis;
using Recon.Vb6;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Pe;
using Recon.Toolchains;

namespace Recon.Inventory;

/// <summary>Everything the builder needs, already loaded by the caller.</summary>
public sealed class InventoryInputs
{
    public required ProjectConfig Project { get; init; }

    public LocalConfig? Local { get; init; }

    public required IBinaryImage Image { get; init; }

    public byte[] Bytes { get; init; } = [];

    public DebugInfoResult? Debug { get; init; }

    /// <summary>DWARF sections of the image, read once and shared by every consumer.</summary>
    public DwarfInfo? Dwarf { get; init; }

    /// <summary>
    /// Patterns to name functions by (<c>recon sigs build</c>). Applied after every other source and
    /// only to functions nothing else named, because a pattern is weaker evidence than a symbol.
    /// </summary>
    public Recon.Signatures.SignatureDatabase? Signatures { get; init; }

    public ToolchainRegistry? Registry { get; init; }

    public ToolchainProfile? Profile { get; init; }

    /// <summary>
    /// Where the Visual Basic runtime that interprets a p-code program is, when the caller found one.
    /// A p-code program's instructions can only be measured against the runtime that will run them, so
    /// this is what turns a list of procedure extents into an instruction count.
    /// </summary>
    public string? PcodeRuntimePath { get; init; }

    public AnalysisOptions? Options { get; init; }

    public string? Command { get; init; }

    public bool IncludeTimestamp { get; init; }

    public string ToolVersion { get; init; } = Recon.ToolVersion.Current;
}

/// <summary>Assembles the inventory document from the loaders and the analyser.</summary>
public static class InventoryBuilder
{
    /// <summary>
    /// The inventory of a program whose instruction set this tool does not decode as machine code.
    /// Today that is Visual Basic 6 p-code and nothing else, and what it produces is the program's own
    /// procedure table (see <see cref="Recon.Vb6.PcodeInventory"/>); the x86 analysis is not run at all,
    /// because decoding interpreter tokens as instructions would invent a program.
    /// </summary>
    private static AnalysisResult? AnalyzeOtherInstructionSet(InventoryInputs inputs)
    {
        if (inputs.Image.Pe is not { } pe || !string.Equals(inputs.Image.Isa, "vb6-pcode", StringComparison.Ordinal))
        {
            return null;
        }

        // Not null in practice and still checked: `binary.isa` is `vb6-pcode` because this very header
        // was read (see `PeBinaryImage.Isa`), and the check is here so that a reader of this code can
        // see that the fall-through to the machine-code analysis is unreachable rather than guess it.
        // Next to nothing else in the image can make a p-code program unreadable, but a truncated file
        // can, and an answer of "no procedures, and here is why" beats decoding the tokens.
        var program = Vb6Program.Read(pe, inputs.Bytes);
        if (program is null)
        {
            return new AnalysisResult
            {
                Problems =
                [
                    "binary.isa is vb6-pcode, from the VB5! header in this image, but the program's project " +
                    "data could not be read: its method tables are unknown and nothing is decoded for it",
                ],
            };
        }

        var tables = PcodeTables(inputs);
        string? note = tables is null
            ? "the runtime that interprets this program was not found, so each procedure's byte stream is " +
              "published from the program's own method tables and its instructions are not measured: pass " +
              "--runtime=<msvbvm60.dll>, put one beside the program, or put one in the project's input " +
              "directory"
            : null;

        var result = PcodeInventory.Analyze(pe, inputs.Image, inputs.Bytes, program, tables, note);
        foreach (var problem in program.Problems)
        {
            result.Problems.Add(problem);
        }

        return result;
    }

    /// <summary>
    /// The runtime's opcode tables, when a runtime can be found. Measured out of the runtime's own
    /// handlers rather than read from a published table, so this is where a p-code program's instruction
    /// lengths come from; a missing runtime is not an error, it is one fewer thing known.
    /// </summary>
    private static IReadOnlyList<PcodeTableEvidence>? PcodeTables(InventoryInputs inputs)
    {
        string? path = inputs.PcodeRuntimePath;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var loaded = ImageLoader.Load(path);
        if (loaded.Image?.Pe is not { } pe)
        {
            return null;
        }

        var sections = pe.Sections
            .Select(s => new PcodeSection(s.Name, s.Rva, s.RawOffset, s.VirtualSize, s.IsCode))
            .ToList();
        var runtime = PcodeRuntime.Read(loaded.Bytes, path, pe.ImageBase, sections);
        return runtime.IsPcodeRuntime
            ? PcodeProgram.ReadTables(loaded.Image, pe, loaded.Bytes, runtime)
            : null;
    }

    public static InventoryDocument Build(InventoryInputs inputs)
    {
        var image = inputs.Image;
        var options = inputs.Options ?? new AnalysisOptions();
        var registry = inputs.Registry ?? new ToolchainRegistryFactory().CreateEmpty();
        var producers = ProducerDetector.Detect(image, inputs.Debug, registry, inputs.Dwarf);

        // One instruction set at a time: a p-code program's procedures come from the program's own
        // tables, and everything else goes through the machine-code analysis.
        var analysis = AnalyzeOtherInstructionSet(inputs)
            ?? new InventoryAnalyzer(image, inputs.Bytes, inputs.Profile, options).Analyze(inputs.Debug, inputs.Signatures);

        var document = new InventoryDocument
        {
            Generator = new GeneratorInfo
            {
                Version = inputs.ToolVersion,
                Command = inputs.Command,
                GeneratedAt = inputs.IncludeTimestamp ? DateTimeOffset.UtcNow.ToString("O") : null,
            },
            Project = new ProjectInfo
            {
                Name = inputs.Project.Project.Name,
                Root = inputs.Project.RootDirectory,
                ProjectFile = inputs.Project.FilePath,
            },
            Binary = BuildBinaryInfo(inputs, producers, registry),
            Statistics = analysis.Statistics,
            Problems = [.. analysis.Problems],
        };

        document.Problems.AddRange(producers.Problems);
        if (inputs.Debug is not null)
        {
            document.Problems.AddRange(inputs.Debug.Problems);
        }

        foreach (var section in image.Sections)
        {
            document.Sections.Add(new SectionInfo
            {
                Name = section.Name,
                Rva = section.Rva,
                VirtualSize = section.VirtualSize,
                RawSize = section.RawSize,
                RawOffset = section.RawOffset,
                Characteristics = $"0x{section.Characteristics:X8}",
                Flags = section.FlagNames,
                Code = section.IsCode,
            });
        }

        foreach (var import in image.Imports)
        {
            document.Imports.Add(new ImportInfo
            {
                Dll = import.Module,
                Name = import.Name,
                Ordinal = import.Name is null ? import.Ordinal : null,
                IatRva = import.SlotRva,
            });
        }

        foreach (var export in image.Exports)
        {
            document.Exports.Add(new ExportInfo
            {
                Name = export.Name,
                Ordinal = export.Ordinal,
                Rva = export.Rva,
                Forwarder = export.Forwarder,
            });
        }

        var functionByRva = analysis.Functions.ToDictionary(f => f.Start, f => f);
        var dataRanges = analysis.Data
            .Where(d => d.Kind != "jump_table")
            .ToList();

        foreach (var relocation in image.Relocations)
        {
            string? inFunction = analysis.Functions.FirstOrDefault(f => f.Covers(relocation.Rva))?.Id;
            string? inData = dataRanges.FirstOrDefault(d => relocation.Rva >= d.Rva && relocation.Rva < d.Rva + Math.Max(d.Size, 1))?.Name;

            document.Relocations.Add(new RelocationInfo
            {
                Rva = relocation.Rva,
                Kind = relocation.Kind,
                Width = relocation.Width,
                RawValue = relocation.RawValue,
                TargetRva = relocation.TargetRva,
                InFunction = inFunction,
                InData = inFunction is null ? inData : null,
            });
        }

        var unitToolchains = ProducerDetector.DetectUnits(inputs.Debug, producers);
        string? configuredToolchain = inputs.Project.Target.DefaultToolchain;
        var binarySuggestion = producers.Suggestions.FirstOrDefault();

        foreach (var function in analysis.Functions)
        {
            var info = new FunctionInfo
            {
                Id = function.Id,
                Name = function.Name,
                Demangled = function.Demangled,
                Section = function.Section,
                Isa = function.Isa,
                Unit = function.Unit,
                FoundBy = [.. function.FoundBy.Select(f => f.Value)],
                Confidence = function.Confidence,
                Flags = [.. function.Flags],
                Aliases = [.. function.Aliases],
                ImportThunk = function.ImportThunk,
                Unknowns = [.. function.Unknowns],
                CallingConvention = new CallingConventionInfoDto
                {
                    Value = function.CallingConvention.Value,
                    Confidence = function.CallingConvention.Confidence,
                    Evidence = [.. function.CallingConvention.Evidence],
                },
                Ranges = [.. function.Ranges.Select(r => new RangeInfo { Rva = r.Rva, Size = r.Size })],
            };

            if (function.Unit is not null && unitToolchains.TryGetValue(function.Unit, out var unitMatch))
            {
                info.Toolchain = new ToolchainRefInfo
                {
                    Id = unitMatch.ProfileId,
                    Confidence = unitMatch.Confidence,
                    Evidence = [.. unitMatch.Evidence],
                };
            }
            else if (configuredToolchain is not null && !string.IsNullOrEmpty(configuredToolchain))
            {
                info.Toolchain = new ToolchainRefInfo
                {
                    Id = configuredToolchain,
                    Confidence = "configured",
                    Evidence = ["project.toml"],
                    Configured = true,
                };
            }
            else if (binarySuggestion is not null)
            {
                info.Toolchain = new ToolchainRefInfo
                {
                    Id = binarySuggestion.ProfileId,
                    Confidence = Downgrade(binarySuggestion.Confidence),
                    Evidence = [.. binarySuggestion.Evidence],
                };
                info.Unknowns.Add("toolchain_detected_at_binary_scope");
            }
            else
            {
                info.Toolchain = new ToolchainRefInfo { Id = null, Confidence = "unknown", Evidence = [] };
                info.Unknowns.Add("toolchain_unknown");
            }

            document.Functions.Add(info);
        }

        foreach (var entry in analysis.Data)
        {
            document.Data.Add(new DataInfo
            {
                Rva = entry.Rva,
                Size = entry.Size,
                Name = entry.Name,
                Kind = entry.Kind,
                Source = entry.Source,
            });
        }

        foreach (var table in analysis.JumpTables)
        {
            document.JumpTables.Add(new JumpTableInfo
            {
                Rva = table.Rva,
                Entries = table.Entries,
                Kind = table.Kind,
                Owner = table.Owner,
                UsedAtRva = table.UsedAtRva,
                Targets = [.. table.Targets],
            });
        }

        foreach (var xref in analysis.Xrefs)
        {
            document.Xrefs.Add(new XrefInfo
            {
                FromRva = xref.FromRva,
                ToRva = xref.ToRva,
                Kind = xref.Kind,
                ViaReloc = xref.ViaReloc,
                InFunction = xref.InFunction,
                ToFunction = xref.ToFunction,
            });
        }

        _ = functionByRva;
        return document;
    }

    private static BinaryInfo BuildBinaryInfo(InventoryInputs inputs, ProducerReport producers, ToolchainRegistry registry)
    {
        var image = inputs.Image;
        var debugRecord = image.Pe?.DebugEntries.FirstOrDefault(d => d.Type == 2 && d.PdbPath is not null);

        var info = new BinaryInfo
        {
            Format = image.Format,
            Arch = image.ArchName,
            Isa = image.Isa,
            Sha256 = image.Sha256,
            File = Path.GetFileName(image.Path),
            ImageBase = image.ImageBase,
            EntryRva = image.EntryPointRva,
            SizeOfImage = image.SizeOfImage,
            Subsystem = image.Subsystem,
            Dll = image.IsSharedObject,
            LinkerVersion = image.LinkerVersion,
            Timestamp = image.Timestamp,
            HasRelocations = image.Relocations.Count > 0,
            TlsCallbacks = [.. image.TlsCallbackRvas],
            ConfiguredToolchain = string.IsNullOrEmpty(inputs.Project.Target.DefaultToolchain) ? null : inputs.Project.Target.DefaultToolchain,
            Vb6 = image.Pe?.Vb6 is { } vb
                ? new Vb6Info
                {
                    Signature = vb.Signature,
                    HeaderRva = vb.HeaderRva,
                    RuntimeBuild = vb.RuntimeBuild,
                    LanguageDll = vb.LanguageDll,
                    RuntimeDllVersion = vb.RuntimeDllVersion,
                    LanguageId = vb.LanguageId,
                    TemplateVersion = vb.TemplateVersion,
                    CodeStartRva = vb.CodeStartRva,
                    CodeEndRva = vb.CodeEndRva,
                    ExceptionHandlerRva = vb.ExceptionHandlerRva,
                    NativeCodeRva = vb.NativeCodeRva,
                    IsPcode = vb.IsPcode,
                }
                : null,
            Debug = new DebugInfoInfo
            {
                Kind = inputs.Debug?.Kind ?? "none",
                Sources = [.. inputs.Debug?.Sources ?? []],
                Path = inputs.Debug?.FilePath,
                Sha256 = inputs.Debug?.FilePath is not null && File.Exists(inputs.Debug.FilePath)
                    ? PeImage.HashFile(inputs.Debug.FilePath)
                    : null,
                Guid = (inputs.Debug?.Guid ?? debugRecord?.PdbGuid)?.ToString("D"),
                Age = inputs.Debug?.Age ?? debugRecord?.PdbAge,
                EmbeddedPath = debugRecord?.PdbPath,
                MatchesBinary = MatchDebug(inputs.Debug, debugRecord),
                Symbols = inputs.Debug?.Symbols.Count ?? 0,
                Functions = inputs.Debug?.FunctionCount ?? 0,
                Compilands = inputs.Debug?.Compilands.Count ?? 0,
            },
            Producers = [.. producers.Evidence.Select(e => new ProducerInfo
            {
                Kind = e.Kind,
                Tool = e.Tool,
                Version = e.Version,
                Count = e.Count,
                Detail = e.Detail,
                Unit = e.Unit,
                Matches = [.. e.Matches],
            })],
            ToolchainSuggestions = [.. producers.Suggestions.Select(s => new ToolchainSuggestionInfo
            {
                Id = s.ProfileId,
                Confidence = s.Confidence,
                Scope = s.Scope,
                Evidence = [.. s.Evidence],
                Unit = s.Unit,
                Note = string.IsNullOrEmpty(s.Note) ? null : s.Note,
            })],
        };

        _ = registry;
        return info;
    }

    private static bool? MatchDebug(DebugInfoResult? debug, PeDebugEntry? debugRecord)
    {
        if (debug is null || debugRecord is null)
        {
            return null;
        }

        if (debug.Guid is null || debugRecord.PdbGuid is null)
        {
            return null;
        }

        return debug.Guid == debugRecord.PdbGuid && (debug.Age is null || debugRecord.PdbAge is null || debug.Age == debugRecord.PdbAge);
    }

    /// <summary>Binary-level detection is weaker evidence per function than per-unit detection.</summary>
    private static string Downgrade(string confidence) => confidence switch
    {
        "high" => "medium",
        "medium" => "low",
        _ => "low",
    };
}

/// <summary>Small factory so callers can build an empty registry without loading files.</summary>
public sealed class ToolchainRegistryFactory
{
    public ToolchainRegistry CreateEmpty() => ToolchainRegistry.Empty();

    public ToolchainRegistry Create(IEnumerable<string> directories, Diagnostics diagnostics)
        => ToolchainRegistry.Load(directories, diagnostics);
}
