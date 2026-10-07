using System.Reflection;

namespace Recon.Toolchains;

/// <summary>
/// The profiles that ship inside the executable. <c>recon init</c> writes them into a project so a
/// new project starts with working detection rules instead of an empty directory.
/// </summary>
public static class BuiltInProfiles
{
    private const string Prefix = "Recon.Toolchains.profiles.";

    public static IReadOnlyList<(string FileName, string Content)> All()
    {
        var assembly = typeof(BuiltInProfiles).Assembly;
        var result = new List<(string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".toml", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            result.Add((resource[Prefix.Length..], reader.ReadToEnd()));
        }

        return result;
    }

    public static int WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        int written = 0;
        foreach (var (fileName, content) in All())
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                continue;
            }

            File.WriteAllText(path, content);
            written++;
        }

        return written;
    }
}
