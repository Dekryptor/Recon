using System.Diagnostics;
using Recon.Images;
using Recon.Config;
using Recon.Inventory;
using Recon.Pe;
using Recon.Project;
using Recon.Toolchains;

namespace Recon.Compare;

/// <summary>
/// One binary of a comparison, ready to be read: the image, its bytes, the inventory M1 produced and
/// the address index built from it. A side can be a project (project.toml, so its hashes, profiles
/// and debug inputs are used) or a bare executable, which gets a throwaway project so that the same
/// code path produces its inventory.
/// </summary>
public sealed class ComparisonSide
{
    public required string Label { get; init; }

    public required IBinaryImage Image { get; init; }

    public required byte[] Bytes { get; init; }

    public required InventoryDocument Inventory { get; init; }

    public required SideIndex Index { get; init; }

    public required string BinaryPath { get; init; }

    public string? ProjectName { get; init; }

    public string? ProjectFile { get; init; }

    /// <summary>The role the input has in its project: original, reference, or none for a bare file.</summary>
    public string? Role { get; init; }

    public string? Sha256 { get; init; }

    public string? ToolchainId { get; init; }

    /// <summary>The resolved toolchain profile, which the compare settings come from.</summary>
    public ToolchainProfile? Profile { get; init; }

    public long AnalysisMs { get; init; }

    /// <summary>
    /// Where the inventory came from: <c>built</c> when the analysis ran for this side, <c>read</c> when
    /// it was read back from the document <c>recon inventory</c> had already written. Reported because a
    /// cache hit and a build are the same answer at different costs, and a report that did not say which
    /// one happened would be hiding the cost rather than saving it.
    /// </summary>
    public string InventorySource { get; init; } = "built";

    public int FunctionCount => Inventory.Functions.Count;

    /// <summary>
    /// Loads the binary an input of a project points at. The debug input is used only for the
    /// original: a rebuilt binary has its own debug info or none, and borrowing the original's PDB
    /// would silently attribute the original's symbols to it.
    /// </summary>
    public static ComparisonSide FromProjectInput(string label, string projectFile, string role, Diagnostics diagnostics)
    {
        var context = ProjectContext.Load(projectFile, diagnostics);
        var input = context.Project.Inputs.FirstOrDefault(i => i.Role == role)
                    ?? throw new ConfigException(
                        $"cannot compare",
                        [new Diagnostic(DiagnosticSeverity.Error, projectFile, 0, "compare.input", $"the project has no input with role \"{role}\"")]);

        string path = context.ResolveInputPath(input);
        if (!File.Exists(path))
        {
            throw new ConfigException(
                "cannot compare",
                [new Diagnostic(DiagnosticSeverity.Error, projectFile, 0, "compare.input", $"input \"{input.Id}\" ({role}) is missing: {path}")]);
        }

        var load = ImageLoader.Load(path);
        if (load.Image is null)
        {
            throw new ConfigException(
                "cannot compare",
                [new Diagnostic(DiagnosticSeverity.Error, path, 0, "compare.binary", load.Problems.FirstOrDefault() ?? "not a readable image")]);
        }

        var (document, elapsed, profile, source) = BuildInventory(
            context, load.Image, load.Bytes, isOriginal: role == "original", binaryPath: path);
        return new ComparisonSide
        {
            Label = label,
            Image = load.Image,
            Bytes = load.Bytes,
            Inventory = document,
            Index = new SideIndex(load.Image, document),
            BinaryPath = path,
            ProjectName = context.Project.Project.Name,
            ProjectFile = projectFile,
            Role = role,
            Sha256 = document.Binary.Sha256,
            ToolchainId = document.Binary.ConfiguredToolchain,
            Profile = profile,
            AnalysisMs = elapsed,
            InventorySource = source,
        };
    }

    /// <summary>Loads a project directory, comparing its original input unless another role is asked for.</summary>
    public static ComparisonSide FromProject(string label, string directory, Diagnostics diagnostics, string role = "original")
        => FromProjectInput(label, Path.GetFullPath(Path.Combine(directory, ProjectContext.ProjectFileName)), role, diagnostics);

    /// <summary>Loads a bare executable: everything is decided from the file itself.</summary>
    public static ComparisonSide FromBinary(string label, string binaryPath, Diagnostics diagnostics)
    {
        string full = Path.GetFullPath(binaryPath);
        if (!File.Exists(full))
        {
            throw new ConfigException("cannot compare", [new Diagnostic(DiagnosticSeverity.Error, full, 0, "compare.binary", "file not found")]);
        }

        string workspace = Path.Combine(Path.GetTempPath(), "recon-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string profiles = Path.Combine(workspace, "profiles");
        BuiltInProfiles.WriteTo(profiles);

        string project = Path.Combine(workspace, ProjectContext.ProjectFileName);
        File.WriteAllText(project, $"""
            schema_version = 1

            [project]
            name = "{Path.GetFileNameWithoutExtension(full)}"

            [target]
            format = "pe32"
            arch = "x86"

            [[input]]
            id = "main"
            role = "original"
            file = "{Path.GetFileName(full)}"
            sha256 = "{PeImage.HashFile(full)}"

            [paths]
            profiles = ["{profiles.Replace("\\", "/")}"]
            """);
        File.WriteAllText(Path.Combine(workspace, ProjectContext.LocalFileName), $"""
            schema_version = 1

            [inputs]
            dir = "{Path.GetDirectoryName(full)!.Replace("\\", "/")}"
            """);

        var side = FromProjectInput(label, project, "original", diagnostics);
        return new ComparisonSide
        {
            Label = side.Label,
            Image = side.Image,
            Bytes = side.Bytes,
            Inventory = side.Inventory,
            Index = side.Index,
            BinaryPath = full,
            ProjectName = side.ProjectName,
            ProjectFile = null,
            Role = null,
            Sha256 = side.Sha256,
            ToolchainId = side.ToolchainId,
            Profile = side.Profile,
            AnalysisMs = side.AnalysisMs,
            InventorySource = side.InventorySource,
        };
    }

    /// <summary>
    /// The inventory of one side. Debug information is read for both sides, but a project's
    /// configured <c>debug</c> and <c>map</c> inputs belong to the original and are only used for it:
    /// a rebuild is described by its own symbols, when it has any, and otherwise by its code.
    /// </summary>
    private static (InventoryDocument Document, long ElapsedMs, ToolchainProfile? Profile, string Source) BuildInventory(
        ProjectContext context,
        IBinaryImage image,
        byte[] bytes,
        bool isOriginal,
        string binaryPath)
    {
        var stopwatch = Stopwatch.StartNew();
        var debug = context.LoadDebugInfo(image, includeConfiguredInputs: isOriginal);
        string? configured = context.Project.Target.DefaultToolchain;

        // The document `recon inventory` wrote for this side, when there is one and it is this build's
        // reading of this binary. It is the same document the analysis would produce — same builder, same
        // profile resolution — so using it does not change the comparison, only what it costs; the guard
        // is the one the listing uses, and a document that cannot be read is not an error, it is a build.
        string build = Path.IsPathRooted(context.Project.Paths.Build)
            ? context.Project.Paths.Build
            : Path.Combine(context.RootDirectory, context.Project.Paths.Build);
        DateTime? binaryTime = File.Exists(binaryPath) ? File.GetLastWriteTimeUtc(binaryPath) : null;

        // The directory is checked first because hashing an 11.8 MB file is 40 ms worth paying only when
        // there is a candidate document to check it against.
        //
        // The hash the document has to be about is the hash of the bytes being compared, not the hash
        // the project records for that input: a project's `sha256` is a statement about the file when
        // `recon init` read it, and an input edited since then still has a document that describes it
        // exactly — that is what the document's own `binary.sha256` is for. Checking the other one
        // refused the right document for the wrong reason (a comparison against a rebuilt file), which
        // is how this was noticed.
        var cached = Directory.Exists(build)
            ? InventoryCache.DocumentIn(build, PeImage.HashBytes(bytes), ToolVersion.Current, binaryTime)
            : null;
        if (cached is not null)
        {
            var cachedProfile = context.ResolveProfile(configured)
                                ?? ToolchainSelector.Choose(context.Registry, configured, image, debug, context.Dwarf);
            cached.Binary.ConfiguredToolchain = configured ?? cachedProfile?.Id ?? cached.Binary.ConfiguredToolchain;
            stopwatch.Stop();

            // The debug info is *not* read on this route, and that is the point of it: the document
            // already carries what the reader found in the debug input.
            return (cached, stopwatch.ElapsedMilliseconds, cachedProfile, "read");
        }

        var document = InventoryBuilder.Build(new InventoryInputs
        {
            Project = context.Project,
            Local = context.Local,
            Image = image,
            Bytes = bytes,
            Debug = debug,
            Dwarf = isOriginal ? context.Dwarf : null,
            Registry = context.Registry,
            Profile = context.ResolveProfile(configured),
            Options = context.BuildAnalysisOptions(),
            Command = "compare",
        });

        // Which toolchain's habits the comparison should assume: the one the project names, else the
        // one this file's own evidence points at, else the format's default. The same rule the
        // inventory command uses, so a file is described the same way wherever it is looked at.
        var profile = context.ResolveProfile(configured)
                      ?? ToolchainSelector.Choose(context.Registry, configured, image, debug, context.Dwarf);

        document.Binary.ConfiguredToolchain = configured ?? profile?.Id ?? document.Binary.ConfiguredToolchain;
        stopwatch.Stop();
        return (document, stopwatch.ElapsedMilliseconds, profile, "built");
    }

}
