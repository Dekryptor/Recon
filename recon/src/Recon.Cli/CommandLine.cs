namespace Recon.Cli;

/// <summary>
/// A small argument parser. Options take their value either as <c>--key=value</c> or as the next
/// argument; the set of value-taking options is explicit so that dashes are unambiguous.
/// </summary>
/// <remarks>
/// The set being explicit means an option that is read but not listed here is read as absent, and
/// the command goes on doing what it would have done without it. That is a quiet way to be wrong, so
/// every option a command reads through <see cref="Value"/> belongs here, and
/// <c>Every_option_a_command_can_take_a_value_for_takes_one</c> in the tests holds the commands to
/// it by reading the usage lines this build documents.
/// </remarks>
public sealed class CommandLine
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--project", "-p", "--output", "-o", "--count", "--limit", "--filter", "--name",
        "--binary", "--install-dir", "--dir",
        "--threshold", "--min-score", "--function", "--jobs", "--unit",
        "--comparison", "--port", "--signatures", "--library", "--budget",
        "--inventory", "--length", "--min-fixed", "--opcode", "--lead", "--runtime", "--object", "--procedure",
        "--min", "--encoding", "--section",
    };

    /// <summary>The options that take a value, for tests and for anything listing them.</summary>
    public static IReadOnlyCollection<string> ValueTaking => ValueOptions;

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Command { get; } = [];

    public List<string> Positional { get; } = [];

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.StartsWith('-') && arg.Length == 2 && !char.IsDigit(arg[1])))
            {
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    result._values[arg[..equals]] = arg[(equals + 1)..];
                    continue;
                }

                if (ValueOptions.Contains(arg) && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    result._values[arg] = args[++i];
                    continue;
                }

                result._flags.Add(arg);
                continue;
            }

            if (result.Command.Count == 0 && result.Positional.Count == 0)
            {
                result.Command.Add(arg);
            }
            else
            {
                result.Positional.Add(arg);
            }
        }

        return result;
    }

    /// <summary>Commands whose first word is followed by a subcommand, as in <c>inspect sections</c>.</summary>
    private static readonly HashSet<string> SubcommandCommands = new(StringComparer.Ordinal)
    {
        "inspect", "toolchain", "schema", "sigs",
    };

    /// <summary>
    /// Second word of a two-word command, for example <c>inspect sections</c>. Only the commands
    /// that actually take a subcommand consume the first positional argument; for everything else,
    /// as in <c>disasm main</c>, that argument is data.
    /// </summary>
    public string? Subcommand
        => Command.Count > 0 && SubcommandCommands.Contains(Command[0]) && Positional.Count > 0
            ? Positional[0]
            : null;

    /// <summary>Positional arguments after the command (and subcommand).</summary>
    public List<string> Arguments => Positional.Skip(Subcommand is null ? 0 : 1).ToList();

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string? ProjectPath => Value("--project") ?? Value("-p");

    public string? Output => Value("--output") ?? Value("-o");

    public int? Count(string name = "--limit")
        => Value(name) is { } value && int.TryParse(value, out int parsed) ? parsed : null;

    /// <summary>
    /// An option that names several things, written <c>--unit=crt,mingw</c>. One value in the file,
    /// several in the command, because the alternative is an option that can be repeated and a
    /// dictionary that can hold it once.
    /// </summary>
    public string[] List(string name)
        => Value(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public string JoinedCommand => Command.Count == 0 ? "help" : string.Join(' ', Command.Concat(Subcommand is null ? [] : new[] { Subcommand }));
}
