using Recon.Analysis;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Pe;
using Recon.Toolchains;

namespace Recon.Project;

/// <summary>
/// Loads and holds everything a command needs: the project file, the machine-local file, the
/// toolchain profiles found in <c>paths.profiles</c>, and lazily the binary and its debug info.
/// </summary>
public sealed class ProjectContext
{
    public const string ProjectFileName = "project.toml";
    public const string LocalFileName = "local.toml";

    private LoadedImage? _image;
    private byte[]? _bytes;
    private DebugInfoResult? _debug;
    private bool _debugLoaded;
    private DwarfInfo? _dwarf;
    private bool _dwarfLoaded;

    public required ProjectConfig Project { get; init; }

    public required LocalConfig Local { get; init; }

    public required ToolchainRegistry Registry { get; init; }

    public Diagnostics Diagnostics { get; } = new();

    public string RootDirectory => Project.RootDirectory;

    /// <summary>Finds <c>project.toml</c> in the given directory or by walking up from it.</summary>
    public static string? FindProjectFile(string? startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ProjectFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static ProjectContext Load(string projectFile, Diagnostics diagnostics)
    {
        var project = ProjectConfig.Load(projectFile, diagnostics);
        diagnostics.ThrowIfErrors("project.toml");

        var local = LoadLocal(project, diagnostics);

        var profileDirectories = project.Paths.Profiles
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(project.RootDirectory, p))
            .ToList();

        var registry = ToolchainRegistry.Load(profileDirectories, diagnostics);

        foreach (var unit in project.Units)
        {
            registry.ValidateReference(unit.Toolchain, project.FilePath, 0, $"unit[{unit.Name}].toolchain", diagnostics);
        }

        registry.ValidateReference(project.Target.DefaultToolchain, project.FilePath, 0, "target.default_toolchain", diagnostics);
        diagnostics.ThrowIfErrors("configuration");

        return new ProjectContext { Project = project, Local = local, Registry = registry };
    }

    private static LocalConfig LoadLocal(ProjectConfig project, Diagnostics diagnostics)
    {
        string localPath = Path.Combine(project.RootDirectory, LocalFileName);
        if (File.Exists(localPath))
        {
            return LocalConfig.Load(localPath, diagnostics);
        }

        diagnostics.Warning(project.FilePath, 0, string.Empty, "local.toml is missing; using defaults (inputs are looked up under ./inputs)");
        return LocalConfig.Empty(project.RootDirectory);
    }

    /// <summary>Absolute path of an input file: file name from the project, directory from local.toml.</summary>
    public string ResolveInputPath(InputSpec input) => Path.Combine(Local.InputDirectory, input.File);

    public string? ResolveInputPath(string? inputId)
    {
        if (inputId is null)
        {
            return null;
        }

        var input = Project.FindInput(inputId);
        return input is null ? null : ResolveInputPath(input);
    }

    /// <summary>
    /// The primary binary, loaded by sniffing the file rather than trusting <c>target.format</c>:
    /// an ELF file declared as PE is still an ELF file, and the honest thing is to say so.
    /// </summary>
    public LoadedImage LoadImage()
    {
        if (_image is not null)
        {
            return _image;
        }

        var original = Project.Original;
        if (original is null)
        {
            _image = new LoadedImage { Problems = ["project.toml has no input with role \"original\""] };
            return _image;
        }

        _image = ImageLoader.Load(ResolveInputPath(original));
        _bytes = _image.Bytes;
        return _image;
    }

    /// <summary>
    /// DWARF sections of the primary input, read at most once. Zero cost when the image has no
    /// DWARF, which is the MSVC case.
    /// </summary>
    public DwarfInfo? Dwarf
    {
        get
        {
            if (!_dwarfLoaded)
            {
                _dwarfLoaded = true;
                var image = LoadImage().Image;
                if (image is not null)
                {
                    _bytes ??= File.Exists(image.Path) ? File.ReadAllBytes(image.Path) : [];
                    _dwarf = DwarfOf(image, _bytes);
                }
            }

            return _dwarf;
        }
    }

    /// <summary>
    /// The DWARF of one image. A Mach-O image carries none in itself: clang leaves the debug map in
    /// the executable and <c>dsymutil</c> puts the DWARF in a <c>.dSYM</c> bundle beside it, whose
    /// <c>Contents/Resources/DWARF</c> holds one more Mach-O file with nothing but debug sections in
    /// it. Reading that is reading the same DWARF out of a different file, which is the whole of what
    /// the bundle is for.
    /// </summary>
    private static DwarfInfo? DwarfOf(IBinaryImage image, byte[] bytes)
    {
        var dwarf = DwarfReader.Read(image, bytes);
        if (image.Macho is null || dwarf is { Functions.Count: > 0 })
        {
            return dwarf;
        }

        string? bundleFile = FindDsym(image.Path);
        if (bundleFile is null)
        {
            return dwarf;
        }

        var loaded = ImageLoader.Load(bundleFile);
        if (!loaded.Ok || loaded.Image is null)
        {
            return dwarf;
        }

        var fromBundle = DwarfReader.Read(loaded.Image, File.ReadAllBytes(bundleFile));
        return fromBundle ?? dwarf;
    }

    /// <summary>
    /// The debug file inside a <c>.dSYM</c> bundle: <c>Contents/Resources/DWARF/</c> holds one, named
    /// after the binary. Which file it is comes from the directory rather than from the binary's name,
    /// because a build that renamed the executable renamed the bundle and not necessarily the file in
    /// it.
    /// </summary>
    private static string? FindDsym(string binaryPath)
    {
        string directory = Path.Combine(
            Path.GetDirectoryName(binaryPath) ?? ".",
            Path.GetFileName(binaryPath) + ".dSYM",
            "Contents",
            "Resources",
            "DWARF");

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault()
            : null;
    }

    /// <summary>
    /// Loads debug information: the configured debug input first, then the map input, then PDBs and
    /// map files sitting next to the binary. Absent debug info is a normal state, not an error.
    /// </summary>
    public DebugInfoResult? LoadDebugInfo(IBinaryImage image) => LoadDebugInfo(image, includeConfiguredInputs: true);

    /// <summary>
    /// Debug information for one image. The <c>debug</c> and <c>map</c> inputs of a project describe
    /// the original, so they are only used for it: a second binary — the rebuild — is read on its
    /// own, from whatever COFF symbols, DWARF or PDB belong to it. Comparing a rebuild through the
    /// original's symbols would name its functions after a program it is not.
    /// </summary>
    public DebugInfoResult? LoadDebugInfo(IBinaryImage image, bool includeConfiguredInputs)
    {
        byte[] bytes = File.Exists(image.Path) ? File.ReadAllBytes(image.Path) : [];

        if (!includeConfiguredInputs)
        {
            return ReadFromImage(image, bytes, DwarfOf(image, bytes))?.WithSources();
        }

        if (_debugLoaded)
        {
            return _debug;
        }

        _debugLoaded = true;
        _bytes ??= bytes;

        var debug = Project.DebugInfo;
        if (debug is not null)
        {
            string path = ResolveInputPath(debug);
            if (File.Exists(path))
            {
                _debug = (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    ? PdbReader.Read(path, image.Sections.Select(s => s.Rva))
                    : MapFileReader.Read(path, image))?.WithSources();
                return _debug;
            }

            Diagnostics.Warning(Project.FilePath, 0, "input.debug", $"debug input {debug.File} was not found at {path}");
        }

        var map = Project.MapFile;
        if (map is not null)
        {
            string path = ResolveInputPath(map);
            if (File.Exists(path))
            {
                _debug = MapFileReader.Read(path, image)?.WithSources();
                return _debug;
            }

            Diagnostics.Warning(Project.FilePath, 0, "input.map", $"map input {map.File} was not found at {path}");
        }

        return ReadFromImage(image, bytes, Dwarf, remember: true)?.WithSources();
    }

    /// <summary>
    /// What the image itself carries: a PDB beside it, DWARF inside it, then COFF symbols. COFF is
    /// merged in rather than chosen over, because DWARF describes only what a compiler emitted —
    /// hand-written assembly and linker-generated stubs are in the COFF table and nowhere else.
    /// </summary>
    private DebugInfoResult? ReadFromImage(IBinaryImage image, byte[] bytes, DwarfInfo? dwarf, bool remember = false)
    {
        DebugInfoResult? table = image.Pe is { SymbolTableOffset: > 0, SymbolCount: > 0 } pe
            ? CoffSymbols.Read(pe, bytes)
            : image.Macho is not null
                ? MachoSymbols.Read(image)
                : ElfSymbols.Read(image);
        string baseDirectory = Path.GetDirectoryName(image.Path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(image.Path);

        DebugInfoResult? result = null;

        // A PDB next to the binary is the richest source, and the one the plan calls for first.
        string sidePdb = Path.Combine(baseDirectory, stem + ".pdb");
        if (File.Exists(sidePdb))
        {
            result = PdbReader.Read(sidePdb, image.Sections.Select(s => s.Rva));
        }
        else if (dwarf is { Functions.Count: > 0 })
        {
            var fromDwarf = DwarfSymbols.Read(image, dwarf);
            if (fromDwarf.Symbols.Count > 0)
            {
                result = Merge(fromDwarf, table);
            }
        }

        if (result is null && table is not null && table.Symbols.Count > 0)
        {
            result = table;
        }

        // A map file beside the binary is read whenever it names more than what the image itself
        // carries, not only when the image carries nothing: a stripped Mach-O image still has a
        // symbol table, with the linker's own synthesized symbol in it, and one name is no reason
        // to ignore a file that names every function in the image.
        //
        // Debug information is the exception, and it is not a close call. A map lists names and
        // addresses; DWARF and PDB also say which source file each function came from, which line it
        // starts on, how big it is and how it was inlined. Trading that for the three extra names a
        // map has — the linker's stubs, which the symbol table names anyway — would be a bad bargain
        // made in the dark, so a map only competes with a symbol table, never with debug info.
        string? sideMapPath = new[] { stem + ".map", stem + ".map.txt" }
            .Select(name => Path.Combine(baseDirectory, name))
            .FirstOrDefault(File.Exists);

        if (sideMapPath is not null)
        {
            var fromMap = MapFileReader.Read(sideMapPath, image);
            bool hasDebugInfo = result is not null && result.Kind is "pdb" or "dwarf";
            if (result is null || (!hasDebugInfo && result.FunctionCount < fromMap.FunctionCount))
            {
                result = Merge(fromMap, result);
            }
        }

        if (remember)
        {
            _debug = result;
        }

        return result;
    }

    /// <summary>
    /// Adds what a lower-priority source knows and the primary one does not: data symbols, and code
    /// the compiler did not describe (assembly stubs, linker-generated trampolines).
    /// </summary>
    private static DebugInfoResult Merge(DebugInfoResult primary, DebugInfoResult? secondary)
    {
        if (secondary is null || secondary.Symbols.Count == 0)
        {
            return primary;
        }

        var starts = primary.Symbols.Where(s => !s.IsData).Select(s => s.Rva).ToHashSet();
        var known = primary.Symbols
            .Where(s => !s.IsData && s.Size is > 0)
            .Select(s => (Start: s.Rva, End: s.Rva + s.Size!.Value))
            .ToList();

        foreach (var symbol in secondary.Symbols)
        {
            if (symbol.IsData)
            {
                if (!primary.Symbols.Any(s => s.IsData && s.Rva == symbol.Rva))
                {
                    primary.Symbols.Add(symbol);
                }

                continue;
            }

            if (starts.Contains(symbol.Rva))
            {
                continue; // the same function, already described better
            }

            if (known.Any(range => symbol.Rva > range.Start && symbol.Rva < range.End))
            {
                continue; // inside a function another source already describes
            }

            primary.Symbols.Add(symbol);
            starts.Add(symbol.Rva);
        }

        foreach (var compiland in secondary.Compilands)
        {
            if (!primary.Compilands.Any(c => string.Equals(c.Unit, compiland.Unit, StringComparison.OrdinalIgnoreCase)))
            {
                primary.Compilands.Add(compiland);
            }
        }

        foreach (var problem in secondary.Problems)
        {
            if (!primary.Problems.Contains(problem))
            {
                primary.Problems.Add(problem);
            }
        }

        // Sources are not patched here: they are derived from the symbols that survived the merge, so
        // a source that contributed nothing is not credited. See DebugInfoResult.WithSources.
        return primary;
    }

    public ToolchainProfile? ResolveProfile(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return Registry.GetResolved(id);
    }

    public AnalysisOptions BuildAnalysisOptions()
    {
        var analysis = Project.Analysis;
        return new AnalysisOptions
        {
            MinFunctionConfidence = analysis.MinFunctionConfidence,
            ExtraEntryPoints = [.. analysis.ExtraEntryPoints],
            NoReturn = [.. analysis.NoReturn],
            DataRanges = [.. analysis.DataRanges],
        };
    }
}
