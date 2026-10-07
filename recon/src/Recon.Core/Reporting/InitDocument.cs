using System.Text.Json.Serialization;

namespace Recon.Reporting;

/// <summary>
/// What <c>recon init</c> made, for a caller that has to find the files afterwards. The paths are
/// absolute because the directory it was asked for is not necessarily the working directory.
/// </summary>
public sealed class InitDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "0.1";

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    /// <summary>The project directory, as an absolute path.</summary>
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("project_file")]
    public string ProjectFile { get; set; } = string.Empty;

    /// <summary>Null when local.toml already existed: <c>init</c> never overwrites the machine's own.</summary>
    [JsonPropertyName("local_file")]
    public string? LocalFile { get; set; }

    [JsonPropertyName("inputs_directory")]
    public string InputsDirectory { get; set; } = string.Empty;

    /// <summary>Null with <c>--no-profiles</c>, or when the directory already had them.</summary>
    [JsonPropertyName("profiles_directory")]
    public string? ProfilesDirectory { get; set; }

    [JsonPropertyName("profiles_written")]
    public int ProfilesWritten { get; set; }

    /// <summary>Every file the run wrote, so a caller can read back exactly what changed.</summary>
    [JsonPropertyName("files")]
    public List<WrittenFile> Files { get; set; } = [];

    /// <summary>The binary named with <c>--binary</c>: its name and hash, ready to paste into a project.</summary>
    [JsonPropertyName("binary")]
    public InitBinary? Binary { get; set; }

    /// <summary>
    /// What the caller still has to do. The binary is never copied, and that is the one thing it
    /// would be wrong to leave unsaid.
    /// </summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
}

public sealed class WrittenFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary><c>project</c>, <c>local</c>, <c>profile</c> or <c>gitignore</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>False when the file was already there and left alone.</summary>
    [JsonPropertyName("written")]
    public bool Written { get; set; }
}

public sealed class InitBinary
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Where <c>recon verify</c> will look for it, which is not where it is now.</summary>
    [JsonPropertyName("expected_at")]
    public string ExpectedAt { get; set; } = string.Empty;

    [JsonPropertyName("copied")]
    public bool Copied { get; set; }
}
