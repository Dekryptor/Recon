using Recon.Config;
using Recon.Inventory;
using Recon.Pe;
using Recon.Project;
using Xunit;

namespace Recon.Tests.Fixtures;

/// <summary>
/// Loads the MSVC-ABI corpus (tools/build-msvc-corpus.sh) the way a user would: a project whose
/// inputs are the executable and the PDB beside it. Shared by the corpus tests and the golden one.
/// </summary>
public static class MsvcCorpus
{
    public static bool Available(string stem)
        => TestPaths.MsvcCorpusExists(stem + ".exe") && TestPaths.MsvcCorpusExists(stem + ".pdb");

    /// <summary>
    /// Builds the inventory and reports the directory the project file was written to, so a caller
    /// that compares documents can scrub that machine-specific path.
    /// </summary>
    public static (InventoryDocument Document, string ProjectDirectory) Build(string stem)
    {
        using var temp = new TempDir("recon-msvc");
        var diagnostics = new Diagnostics();
        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "corpus-{stem}"

            [target]
            format = "pe32"
            arch = "x86"

            [[input]]
            id = "main"
            role = "original"
            file = "{stem}.exe"
            sha256 = "{PeImage.HashFile(Path.Combine(TestPaths.MsvcCorpusDirectory, stem + ".exe"))}"

            [[input]]
            id = "debug"
            role = "debug"
            file = "{stem}.pdb"
            sha256 = "{PeImage.HashFile(Path.Combine(TestPaths.MsvcCorpusDirectory, stem + ".pdb"))}"

            [paths]
            profiles = ["{Path.Combine(TestPaths.RepositoryRoot, "src", "Recon.Core", "Toolchains", "profiles").Replace("\\", "/")}"]
            """);
        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.MsvcCorpusDirectory.Replace("\\", "/")}"
            """);

        var context = ProjectContext.Load(temp.PathOf("project.toml"), diagnostics);
        Assert.Empty(diagnostics.Errors);
        var image = context.LoadImage();
        Assert.NotNull(image.Image);

        var document = InventoryBuilder.Build(new InventoryInputs
        {
            Project = context.Project,
            Local = context.Local,
            Image = image.Image!,
            Bytes = image.Bytes,
            Debug = context.LoadDebugInfo(image.Image!),
            Dwarf = context.Dwarf,
            Registry = context.Registry,
            Profile = context.Registry.All("pe32", "x86").FirstOrDefault(),
            Options = context.BuildAnalysisOptions(),
        });

        return (document, temp.Path);
    }
}
