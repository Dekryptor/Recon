using System.Text.Json.Serialization;

namespace Recon.Reporting;

/// <summary>
/// What <c>recon migrate</c> found: every configuration file, the schema version it carries, and
/// whether that is the version this build writes.
/// </summary>
public sealed class MigrateDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "0.1";

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    /// <summary>The version this build of the tool writes.</summary>
    [JsonPropertyName("current_version")]
    public int CurrentVersion { get; set; }

    /// <summary>Whether <c>--write</c> was given: the caller asked for files to be rewritten.</summary>
    [JsonPropertyName("write_requested")]
    public bool WriteRequested { get; set; }

    /// <summary>
    /// How many files were actually rewritten. Zero in this build: no migration path between schema
    /// versions is defined yet, so the command reports and never changes a file.
    /// </summary>
    [JsonPropertyName("rewritten")]
    public int Rewritten { get; set; }

    [JsonPropertyName("files")]
    public List<MigrateFile> Files { get; set; } = [];

    /// <summary>How many files are not at the current version.</summary>
    [JsonPropertyName("outdated")]
    public int Outdated { get; set; }

    /// <summary>What to do about it, when there is something to do.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class MigrateFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary><c>project</c>, <c>local</c> or <c>profile</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("schema_version")]
    public int Version { get; set; }

    [JsonPropertyName("current")]
    public bool Current { get; set; }
}
