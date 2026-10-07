using System.Reflection;
using Recon.Cli;

return EntryPoint.Run(args);

internal static class EntryPoint
{
    public const string ToolName = "recon";

    /// <summary>
    /// The one version string: see <see cref="Recon.ToolVersion"/>. It used to ask the entry assembly,
    /// which answered differently depending on whether a person or a test host started the process, and
    /// the documents this tool writes are read back by runs of it.
    /// </summary>
    public static string Version => Recon.ToolVersion.Current;

    public static int Run(string[] args)
    {
        var commandLine = CommandLine.Parse(args);
        var output = new Output(commandLine.Has("--json"), commandLine.Has("--verbose"), commandLine.Has("--quiet"));

        if (commandLine.Has("--version") || commandLine.Command.Contains("version"))
        {
            Console.WriteLine($"{ToolName} {Version}");
            return ExitCodes.Ok;
        }

        if (commandLine.Has("--help") || commandLine.Command.Count == 0 || commandLine.Command[0] is "help")
        {
            Help.Print(Console.Out, commandLine.Command.Count > 1 ? commandLine.Command[1] : null);
            return ExitCodes.Ok;
        }

        string command = string.Join(" ", new[] { ToolName }.Concat(args));

        try
        {
            var commands = new Commands(output, commandLine);
            int exit = commandLine.Command[0] switch
            {
                "init" => commands.Init(),
                "verify" => commands.Verify(),
                "validate" => commands.Validate(),
                "doctor" => commands.Doctor(),
                "inspect" => commands.Inspect(),
                "inventory" => commands.Inventory(),
                "sigs" => commands.Sigs(),
                "disasm" => commands.Disasm(),
                "build" => commands.Build(),
                "permute" => commands.Permute(),
                "diff" => commands.Diff(),
                "delink" => commands.Delink(),
                "link" => commands.Link(),
                "report" => commands.Report(),
                "serve" => commands.Serve(),
                "toolchain" => commands.Toolchain(),
                "lib" => commands.Lib(),
                "vb6" => commands.Vb6(),
                "pcode" => commands.Pcode(),
                "opcodes" => commands.Opcodes(),
                "hash" => commands.Hash(),
                "migrate" => commands.Migrate(),
                "schema" => commands.Schema(),
                "gen-docs" => commands.GenerateDocs(),
                _ => Unknown(commandLine.Command[0]),
            };

            output.EmitFailure(command, exit);
            return exit;
        }
        catch (Recon.Config.ConfigException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
            {
                output.Error(diagnostic.ToString());
            }

            output.AddDiagnostics(ex.Diagnostics);
            output.EmitFailure(command, ExitCodes.Configuration);
            return ExitCodes.Configuration;
        }
        catch (Exception ex)
        {
            output.Error($"{ToolName}: internal error: {ex.Message}");
            if (commandLine.Has("--verbose"))
            {
                output.Error(ex.ToString());
            }

            output.EmitFailure(command, ExitCodes.Internal);
            return ExitCodes.Internal;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"{ToolName}: unknown command \"{command}\"");
        Console.Error.WriteLine($"run `{ToolName} --help` for the list of commands");
        return ExitCodes.Usage;
    }
}

internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>A check failed: verification mismatch, schema violation, doctor problem.</summary>
    public const int CheckFailed = 1;

    public const int Usage = 2;

    public const int Configuration = 3;

    public const int Internal = 4;
}

/// <summary>Output routing: human-readable text, JSON, or both, with quiet/verbose levels.</summary>
internal sealed class Output(bool json, bool verbose, bool quiet)
{
    public bool Json { get; } = json;

    public bool Verbose { get; } = verbose;

    public bool Quiet { get; } = quiet;

    public void Line(string text)
    {
        if (!Json && !Quiet)
        {
            Console.WriteLine(text);
        }
    }

    public void Debug(string text)
    {
        if (Verbose && !Json)
        {
            Console.Error.WriteLine($"debug: {text}");
        }
    }

    public void Warn(string text)
    {
        if (!Json)
        {
            Console.Error.WriteLine($"warning: {text}");
        }
    }

    public void Error(string text)
    {
        _errors.Add(text);
        Console.Error.WriteLine(text);
    }

    /// <summary>
    /// Everything the run wrote to stderr, in order. A <c>--json</c> caller gets it as
    /// <c>error.errors[]</c> when the run failed without printing a document of its own.
    /// </summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Whether this run has already printed a JSON document of its own.</summary>
    public bool EmittedJson { get; private set; }

    /// <summary>Emits a JSON document when --json was given.</summary>
    public void EmitJson<T>(T value)
    {
        if (Json)
        {
            EmittedJson = true;
            Console.WriteLine(Recon.Reporting.Reports.Serialize(value));
        }
    }

    /// <summary>Emits a document that was serialized elsewhere, for types with their own context.</summary>
    public void EmitJsonText(string json)
    {
        if (Json)
        {
            EmittedJson = true;
            Console.WriteLine(json);
        }
    }

    /// <summary>
    /// Explains a failed run to a program. Only when nothing was emitted already: a run that
    /// printed its own document has said everything it has to say, and two documents on stdout
    /// would not be a contract.
    /// </summary>
    public void EmitFailure(string command, int exitCode)
    {
        if (!Json || EmittedJson || exitCode == ExitCodes.Ok)
        {
            return;
        }

        var error = new Recon.Reporting.CommandError
        {
            Kind = exitCode switch
            {
                ExitCodes.CheckFailed => "check_failed",
                ExitCodes.Usage => "usage",
                ExitCodes.Configuration => "configuration",
                _ => "internal",
            },
            Message = _errors.Count > 0 ? _errors[0] : $"recon {command} failed with exit code {exitCode}",
            Errors = [.. _errors],
            Diagnostics = [.. _diagnostics.Select(d => new Recon.Reporting.ErrorDiagnostic
            {
                File = d.File,
                Line = d.Line,
                KeyPath = d.KeyPath,
                Severity = d.Severity == Recon.Config.DiagnosticSeverity.Error ? "error" : "warning",
                Message = d.Message,
            })],
        };

        EmittedJson = true;
        Console.WriteLine(Recon.Reporting.Reports.Serialize(new Recon.Reporting.ErrorDocument
        {
            Command = command,
            ToolVersion = EntryPoint.Version,
            Error = error,
            ExitCode = exitCode,
        }));
    }

    /// <summary>Structured diagnostics, attached when the failure came from a configuration file.</summary>
    public void AddDiagnostics(IEnumerable<Recon.Config.Diagnostic> diagnostics)
    {
        _diagnostics.AddRange(diagnostics);
    }

    private readonly List<string> _errors = [];
    private readonly List<Recon.Config.Diagnostic> _diagnostics = [];

    public IReadOnlyList<Recon.Config.Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Prints a table, unless the caller asked for JSON or for no human-readable output.</summary>
    public void Table(IEnumerable<string[]> rows, params string[] headers)
    {
        if (!Json && !Quiet)
        {
            ConsoleFormat.Table(rows, headers);
        }
    }
}

internal static class ConsoleFormat
{
    public static void Table(IEnumerable<string[]> rows, params string[] headers)
    {
        var all = new List<string[]> { headers };
        all.AddRange(rows);
        var widths = new int[headers.Length];
        foreach (var row in all)
        {
            for (int i = 0; i < row.Length && i < widths.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i]?.Length ?? 0);
            }
        }

        foreach (var row in all)
        {
            var cells = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                cells[i] = (i < row.Length ? row[i] : string.Empty).PadRight(widths[i]);
            }

            Console.WriteLine(string.Join("  ", cells).TrimEnd());
        }
    }
}
