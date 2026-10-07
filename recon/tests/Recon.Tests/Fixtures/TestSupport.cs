using System.Diagnostics;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>Paths and temporary directories used by the tests.</summary>
public static class TestPaths
{
    /// <summary>The repository root, found by walking up until the marker files appear.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string CorpusDirectory => Path.Combine(RepositoryRoot, "tests", "corpus", "mingw");

    public static string Corpus(string fileName) => Path.Combine(CorpusDirectory, fileName);

    public static bool CorpusExists(string fileName) => File.Exists(Corpus(fileName));

    /// <summary>The corpus <em>sources</em>, which live beside the built binaries: a build test compiles these.</summary>
    public static string CorpusSourceDirectory => Path.Combine(RepositoryRoot, "tests", "corpus");

    public static string CorpusSource(string fileName) => Path.Combine(CorpusSourceDirectory, fileName);

    public static bool CorpusSourceExists(string fileName) => File.Exists(CorpusSource(fileName));

    /// <summary>The ELF corpus: built by tools/build-elf-corpus.sh with the host compiler.</summary>
    public static string ElfCorpusDirectory => Path.Combine(RepositoryRoot, "tests", "corpus", "elf");

    public static string ElfCorpus(string fileName) => Path.Combine(ElfCorpusDirectory, fileName);

    public static bool ElfCorpusExists(string fileName) => File.Exists(ElfCorpus(fileName));

    /// <summary>
    /// The Mach-O corpus: built by tools/build-macho-corpus.sh, which needs clang and ld64.lld and
    /// skips without them, so the tests that use it have to skip too.
    /// </summary>
    public static string MachoCorpusDirectory => Path.Combine(RepositoryRoot, "tests", "corpus", "macho");

    public static string MachoCorpus(string fileName) => Path.Combine(MachoCorpusDirectory, fileName);

    public static bool MachoCorpusExists(string fileName) => File.Exists(MachoCorpus(fileName));

    /// <summary>The MSVC-ABI corpus: built by tools/build-msvc-corpus.sh, not by the MinGW one.</summary>
    public static string MsvcCorpusDirectory => Path.Combine(RepositoryRoot, "tests", "corpus", "msvc");

    public static bool MsvcCorpusExists(string fileName) => File.Exists(Path.Combine(MsvcCorpusDirectory, fileName));

    private static string FindRepositoryRoot()
    {
        // The output directory is normally inside the checkout, and the walk below finds the
        // repository from it. A run whose binaries were placed outside the checkout cannot be walked
        // from, so it says where the repository is — `tools/env.sh` sets this when it moves the build
        // output out of the workspace.
        if (Environment.GetEnvironmentVariable("RECON_REPO_ROOT") is { Length: > 0 } named
            && Directory.Exists(Path.Combine(named, "src", "Recon.Core")))
        {
            return Path.GetFullPath(named);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "Recon.Core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"cannot find the repository root above {AppContext.BaseDirectory}");
    }
}

/// <summary>A temporary directory that deletes itself, so tests never litter the workspace.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string prefix = "recon-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string PathOf(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes a file (creating directories) and returns its path.</summary>
    public string Write(string name, string content)
    {
        string path = PathOf(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Write(string name, byte[] content)
    {
        string path = PathOf(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not fail a test run.
        }
    }
}

/// <summary>Runs the CLI in-process and captures what it printed.</summary>
public sealed class CliRun
{
    private CliRun(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public string All => StandardOutput + StandardError;

    /// <summary>
    /// One stream for the whole process, so two runs at once would capture each other's text.
    /// Callers are in the "cli" collection as well; this lock is what makes that guarantee hold
    /// even if a class forgets the attribute.
    /// </summary>
    private static readonly object ConsoleGate = new();

    public static CliRun Run(params string[] args)
    {
        lock (ConsoleGate)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            int exitCode;
            try
            {
                Console.SetOut(output);
                Console.SetError(error);
                exitCode = global::EntryPoint.Run(args);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            return new CliRun(exitCode, output.ToString(), error.ToString());
        }
    }
}

public static class TestJson
{
    /// <summary>Pretty-prints JSON so a diff of two documents is readable.</summary>
    public static string Normalize(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }) + "\n";
    }

    public static string Quote(string value) => new StringBuilder().Append('"').Append(value).Append('"').ToString();
}

public static class ToolDetection
{
    /// <summary>True when the tool is on PATH; used to skip corpus tests instead of failing them.</summary>
    public static bool Exists(string tool)
    {
        try
        {
            var startInfo = new ProcessStartInfo(tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process is { ExitCode: 0 };
        }
        catch (Exception)
        {
            return false;
        }
    }
}
