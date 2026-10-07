using System.Reflection;

namespace Recon.Schemas;

/// <summary>The JSON Schema documents that ship with the tool.</summary>
public static class BuiltInSchemas
{
    private const string Prefix = "Recon.Schemas.";

    public static IReadOnlyList<string> Names => All().Select(s => s.Name).ToList();

    public static IReadOnlyList<(string Name, string Content)> All()
    {
        var assembly = typeof(BuiltInSchemas).Assembly;
        var result = new List<(string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            string name = resource[Prefix.Length..].Replace(".schema.json", string.Empty, StringComparison.Ordinal).Replace(".json", string.Empty, StringComparison.Ordinal);
            result.Add((name, reader.ReadToEnd()));
        }

        return result;
    }

    public static string? Get(string name)
        => All().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Content;
}
