using System.Reflection;

namespace Recon;

/// <summary>
/// The tool's version, in one place, because a document has to be readable by the run that wrote it
/// and by every run after it.
///
/// There used to be three answers to "which version wrote this file". The command line asked the
/// *entry* assembly, which is the CLI when a person runs `recon` and the test host when the tests run
/// the same command in-process — so the same inventory came out stamped `1.0.0` or `1.0.0.0`
/// depending on who hosted it. The two library defaults were `0.1.0`, a fourth of nothing in
/// particular. Goldens in this repository carry both of the first two, which is what a version string
/// that means "whatever was hosting the process" looks like after a while.
///
/// It matters because the version is what makes a document this build's reading of a binary rather
/// than a plausible-looking wrong answer from an older build, and `recon inventory` writes documents
/// other commands read back — the listing and, since the comparison learned to, `recon diff`. A guard
/// that compares against a host-dependent string is a guard that refuses the tool's own files in one
/// context and accepts them in another.
///
/// So: the version of the assembly that defines the document format, without the build metadata an
/// informational version may carry (a `+<commit>` suffix is about where the source came from, not
/// about which reading of a file this is).
/// </summary>
public static class ToolVersion
{
    /// <summary>What every document records and every reader checks, and what `recon --version` prints.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(ToolVersion).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? version = informational ?? assembly.GetName().Version?.ToString();
        if (string.IsNullOrEmpty(version))
        {
            return "0.0.0";
        }

        int metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}
