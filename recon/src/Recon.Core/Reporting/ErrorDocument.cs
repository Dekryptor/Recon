using System.Text.Json.Serialization;

namespace Recon.Reporting;

/// <summary>
/// What a failed run says about itself, for a caller that is a program.
/// </summary>
/// <remarks>
/// Every command that emits JSON emits its own document, and an agent reading stdout wants exactly
/// one. So this is only produced when a run failed *and* printed no document of its own: a
/// <c>verify</c> that found a bad hash reports it in its own report, while a <c>permute</c> that was
/// given no function reports it in this one. The rule is "one document per run, and it explains the
/// exit code".
/// </remarks>
public sealed class ErrorDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "0.1";

    /// <summary>The command line that failed, so a log of several runs can be told apart.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public CommandError Error { get; set; } = new();

    /// <summary>The same number the process exits with, so it does not have to be captured twice.</summary>
    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }
}

/// <summary>Why a run failed.</summary>
public sealed class CommandError
{
    /// <summary>
    /// One of <c>usage</c>, <c>configuration</c>, <c>check_failed</c> or <c>internal</c>: the same
    /// four kinds the exit codes are.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The first thing that went wrong, which is usually the only one that matters.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Every line the run wrote to stderr, in order. Prose, but complete.</summary>
    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// Structured diagnostics, when the failure came from loading a configuration file: the file,
    /// the line, the key and the code, so a caller can point at the thing to fix instead of parsing
    /// <see cref="Message"/>.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public List<ErrorDiagnostic> Diagnostics { get; set; } = [];
}

/// <summary>One problem in one configuration file, as <c>recon</c> reported it.</summary>
public sealed class ErrorDiagnostic
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>The key path inside the file, as in <c>unit[2].toolchain</c>.</summary>
    [JsonPropertyName("key_path")]
    public string KeyPath { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
