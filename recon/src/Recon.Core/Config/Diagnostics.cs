namespace Recon.Config;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One problem found while loading a configuration file. Rendered as
/// <c>file.toml:12 unit[2].toolchain: unknown profile "x" (known: ...)</c>.
/// </summary>
public sealed record Diagnostic(DiagnosticSeverity Severity, string File, int Line, string KeyPath, string Message)
{
    public override string ToString()
    {
        string location = Line > 0 ? $"{File}:{Line}" : File;
        string path = string.IsNullOrEmpty(KeyPath) ? string.Empty : KeyPath + ": ";
        string severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"{location} {severity}: {path}{Message}";
    }
}

public sealed class Diagnostics
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Error(string file, int line, string keyPath, string message)
        => _items.Add(new Diagnostic(DiagnosticSeverity.Error, file, line, keyPath, message));

    public void Warning(string file, int line, string keyPath, string message)
        => _items.Add(new Diagnostic(DiagnosticSeverity.Warning, file, line, keyPath, message));

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    public IEnumerable<Diagnostic> Errors => _items.Where(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<Diagnostic> Warnings => _items.Where(d => d.Severity == DiagnosticSeverity.Warning);

    /// <summary>Throws when any error was recorded. Call this at the end of a load phase.</summary>
    public void ThrowIfErrors(string what)
    {
        if (HasErrors)
        {
            throw new ConfigException($"invalid {what}", _items);
        }
    }
}

/// <summary>
/// Raised when one or more configuration files are not valid. Carries every diagnostic, so a
/// single run can report all problems instead of the first one.
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message, IReadOnlyList<Diagnostic> diagnostics)
        : base(message + ": " + string.Join("; ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
