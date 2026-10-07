using System.Diagnostics;
using System.Text.RegularExpressions;
using Recon.Toml;

namespace Recon.Config;

/// <summary>
/// Every configuration file is versioned. The tool refuses a newer major version and offers
/// <c>recon migrate</c> for older ones.
/// </summary>
public static partial class SchemaVersions
{
    public const int Current = 1;

    public static void CheckSchemaVersion(int found, string file, int line, string keyPath, Diagnostics diagnostics)
    {
        if (found <= 0)
        {
            diagnostics.Error(file, line, keyPath, "required key is missing");
            return;
        }

        if (found > Current)
        {
            diagnostics.Error(file, line, keyPath, $"schema_version {found} is newer than this build supports ({Current}); upgrade the tool");
            return;
        }

        if (found < Current)
        {
            diagnostics.Warning(file, line, keyPath, $"schema_version {found} is older than the current version ({Current}); run `recon migrate`");
        }
    }

    public static bool IsSha256(string value) => Sha256Pattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}

/// <summary>Reads a TOML file and turns parse errors into diagnostics.</summary>
public static class TomlLoader
{
    public static TomlDocument LoadDocument(string path, Diagnostics diagnostics)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            diagnostics.Error(path, 0, string.Empty, $"cannot read file: {ex.Message}");
            diagnostics.ThrowIfErrors("configuration");
            throw new UnreachableException();
        }

        try
        {
            return TomlParser.Parse(text, path);
        }
        catch (TomlParseException ex)
        {
            diagnostics.Error(path, ex.Line, string.Empty, ex.Message);
            diagnostics.ThrowIfErrors("configuration");
            throw new UnreachableException();
        }
    }
}
