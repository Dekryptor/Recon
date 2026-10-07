using System.Globalization;

namespace Recon.Toml;

public enum TomlKind
{
    Table,
    Array,
    String,
    Integer,
    Float,
    Boolean,
    DateTime,
}

/// <summary>How a table came into existence; TOML's redefinition rules depend on it.</summary>
public enum TableStatus
{
    /// <summary>Created as part of a header path: <c>[a.b]</c> makes <c>a</c> implicit.</summary>
    ImplicitPath,

    /// <summary>Created by a dotted key: <c>a.b = 1</c>.</summary>
    DottedKey,

    /// <summary>Declared with a <c>[table]</c> header.</summary>
    Explicit,

    /// <summary>Written as <c>{ a = 1 }</c>: cannot be extended afterwards.</summary>
    Inline,

    /// <summary>An element of a <c>[[array]]</c>.</summary>
    ArrayElement,
}

public abstract class TomlValue
{
    public abstract TomlKind Kind { get; }

    public abstract string TypeName { get; }

    /// <summary>1-based line the value starts on.</summary>
    public int Line { get; init; }

    /// <summary>1-based column the value starts at.</summary>
    public int Column { get; init; }
}

public sealed class TomlTable : TomlValue
{
    private readonly Dictionary<string, TomlValue> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lines = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public override TomlKind Kind => TomlKind.Table;

    public override string TypeName => "table";

    public TableStatus Status { get; set; } = TableStatus.ImplicitPath;

    public IReadOnlyDictionary<string, TomlValue> Entries => _entries;

    /// <summary>Keys in the order they appear in the file, which keeps reports stable.</summary>
    public IReadOnlyList<string> KeyOrder => _order;

    public bool IsEmpty => _entries.Count == 0;

    public bool TryGet(string key, out TomlValue value)
    {
        if (_entries.TryGetValue(key, out TomlValue? found))
        {
            value = found;
            return true;
        }

        value = null!;
        return false;
    }

    public TomlValue? Get(string key) => _entries.TryGetValue(key, out TomlValue? found) ? found : null;

    public bool Has(string key) => _entries.ContainsKey(key);

    /// <summary>Line the key was written on; falls back to the table's own line.</summary>
    public int KeyLine(string key) => _lines.TryGetValue(key, out int line) ? line : Line;

    public void Set(string key, TomlValue value, int line)
    {
        if (_entries.ContainsKey(key))
        {
            throw new TomlParseException(string.Empty, line, value.Column, $"duplicate key {key}");
        }

        _entries[key] = value;
        _lines[key] = line;
        _order.Add(key);
    }

    internal void Replace(string key, TomlValue value) => _entries[key] = value;

    /// <summary>Keys in file order, which keeps reports and diffs stable.</summary>
    public IEnumerable<KeyValuePair<string, TomlValue>> Pairs()
        => _order.Select(k => new KeyValuePair<string, TomlValue>(k, _entries[k]));

    public TomlTable? GetTable(string key) => Get(key) as TomlTable;

    public TomlValue? this[string key] => Get(key);

    public override string ToString() => $"table({_entries.Count} keys)";
}

public sealed class TomlArray : TomlValue
{
    public override TomlKind Kind => TomlKind.Array;

    public override string TypeName => "array";

    public List<TomlValue> Items { get; } = [];

    public bool IsArrayOfTables { get; init; }

    public int Count => Items.Count;

    public override string ToString() => $"array({Items.Count})";
}

public sealed class TomlString : TomlValue
{
    public required string Value { get; init; }

    public bool IsLiteral { get; init; }

    public bool IsMultiline { get; init; }

    public override TomlKind Kind => TomlKind.String;

    public override string TypeName => "string";

    public override string ToString() => Value;
}

public sealed class TomlInteger : TomlValue
{
    public required long Value { get; init; }

    public override TomlKind Kind => TomlKind.Integer;

    public override string TypeName => "integer";

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class TomlFloat : TomlValue
{
    public required double Value { get; init; }

    public override TomlKind Kind => TomlKind.Float;

    public override string TypeName => "float";

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class TomlBoolean : TomlValue
{
    public required bool Value { get; init; }

    public override TomlKind Kind => TomlKind.Boolean;

    public override string TypeName => "boolean";

    public override string ToString() => Value ? "true" : "false";
}

public sealed class TomlDateTime : TomlValue
{
    public required string Raw { get; init; }

    public override TomlKind Kind => TomlKind.DateTime;

    public override string TypeName => "date-time";

    public override string ToString() => Raw;
}

public sealed class TomlDocument
{
    public TomlDocument(string file, TomlTable root)
    {
        File = file;
        Root = root;
    }

    public string File { get; }

    /// <summary>Alias kept for callers that think in paths rather than files.</summary>
    public string Path => File;

    public TomlTable Root { get; }
}

/// <summary>A TOML syntax error, with the position that makes it fixable.</summary>
public sealed class TomlParseException : Exception
{
    public TomlParseException(string file, int line, int column, string message)
        : base(string.IsNullOrEmpty(file)
            ? $"line {line}: {message}"
            : $"{file}:{line}:{column}: {message}")
    {
        File = file;
        Line = line;
        Column = column;
        Detail = message;
    }

    public string File { get; }

    public int Line { get; }

    public int Column { get; }

    public string Detail { get; }
}
