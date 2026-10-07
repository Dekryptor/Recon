using Recon.Toml;

namespace Recon.Config;

/// <summary>
/// A typed view over one TOML table that reports errors with key paths and line numbers and
/// keeps track of which keys were read, so unknown keys can be rejected (strict parsing).
/// </summary>
public sealed class TableScope
{
    private readonly TomlTable? _table;
    private readonly Diagnostics _diagnostics;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly List<(string Key, TableScope Scope)> _children = [];

    private TableScope(string file, string path, TomlTable? table, Diagnostics diagnostics, bool present)
    {
        File = file;
        Path = path;
        _table = table;
        _diagnostics = diagnostics;
        Present = present;
    }

    public string File { get; }

    /// <summary>Dotted key path, for example <c>unit[0].flags</c>. Empty at the document root.</summary>
    public string Path { get; }

    /// <summary>False when the table was optional and absent: reads then return defaults silently.</summary>
    public bool Present { get; }

    public int Line => _table?.Line ?? 0;

    public static TableScope Root(TomlDocument document, Diagnostics diagnostics)
        => new(document.File, string.Empty, document.Root, diagnostics, present: true);

    public TableScope Child(string key, bool required = false)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var value = _table?.Get(key);
        switch (value)
        {
            case null:
                if (required)
                {
                    _diagnostics.Error(File, Line, path, "required key is missing");
                }

                return new TableScope(File, path, null, _diagnostics, present: false);

            case TomlTable table:
                var scope = new TableScope(File, path, table, _diagnostics, present: true);
                _children.Add((key, scope));
                return scope;

            default:
                _diagnostics.Error(File, value.Line, path, $"expected a table, found {value.TypeName}");
                return new TableScope(File, path, null, _diagnostics, present: false);
        }
    }

    /// <summary>Reads either a single table or an array of tables at <paramref name="key"/>.</summary>
    public List<TableScope> Tables(string key)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var result = new List<TableScope>();
        var value = _table?.Get(key);
        switch (value)
        {
            case null:
                return result;

            case TomlArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array.Items[i] is TomlTable table)
                    {
                        var scope = new TableScope(File, $"{path}[{i}]", table, _diagnostics, present: true);
                        _children.Add(($"{key}[{i}]", scope));
                        result.Add(scope);
                    }
                    else
                    {
                        _diagnostics.Error(File, array.Items[i].Line, $"{path}[{i}]", $"expected a table, found {array.Items[i].TypeName}");
                    }
                }

                return result;

            case TomlTable single:
            {
                var scope = new TableScope(File, path, single, _diagnostics, present: true);
                _children.Add((key, scope));
                result.Add(scope);
                return result;
            }

            default:
                _diagnostics.Error(File, value.Line, path, $"expected a table or array of tables, found {value.TypeName}");
                return result;
        }
    }

    public bool Has(string key) => _table?.Has(key) == true;

    /// <summary>
    /// Names of the sub-tables of this table, in file order. Needed for tables keyed by name, such
    /// as <c>[toolchain.msvc-2008]</c>, where the key is data rather than a known field.
    /// </summary>
    public IEnumerable<string> ChildNames()
        => _table is null
            ? []
            : _table.Pairs().Where(pair => pair.Value is TomlTable).Select(pair => pair.Key);

    public string? String(string key, string? fallback = null)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var value = _table?.Get(key);
        switch (value)
        {
            case null:
                return fallback;
            case TomlString s:
                return s.Value;
            default:
                _diagnostics.Error(File, value.Line, path, $"expected a string, found {value.TypeName}");
                return fallback;
        }
    }

    public string RequireString(string key)
    {
        bool present = Has(key);
        string? value = String(key);
        if (!present && Present)
        {
            _diagnostics.Error(File, Line, Join(Path, key), "required key is missing");
        }
        else if (present && string.IsNullOrWhiteSpace(value))
        {
            _diagnostics.Error(File, Line, Join(Path, key), "must not be empty");
        }

        return value ?? string.Empty;
    }

    public bool? Bool(string key, bool? fallback = null)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var value = _table?.Get(key);
        return value switch
        {
            null => fallback,
            TomlBoolean b => b.Value,
            _ => ReportOrDefault(key, value, "a boolean", fallback),
        };
    }

    /// <summary>A number that may legitimately be fractional: thresholds and ratios.</summary>
    public double? Double(string key, double? fallback = null)
    {
        _read.Add(key);
        var value = _table?.Get(key);
        return value switch
        {
            null => fallback,
            TomlFloat f => f.Value,
            TomlInteger i => i.Value,
            _ => ReportOrDefault(key, value, "a number", fallback),
        };
    }

    public long? Integer(string key, long? fallback = null)
    {
        _read.Add(key);
        var value = _table?.Get(key);
        return value switch
        {
            null => fallback,
            TomlInteger i => i.Value,
            _ => ReportOrDefault(key, value, "an integer", fallback),
        };
    }

    public long RequireInteger(string key, long fallback = 0)
    {
        bool present = Has(key);
        long? value = Integer(key);
        if (!present && Present)
        {
            _diagnostics.Error(File, Line, Join(Path, key), "required key is missing");
        }

        return value ?? fallback;
    }

    /// <summary>Integer that must fit an unsigned 32-bit value: RVAs, sizes and ids.</summary>
    public uint? UInt32(string key, uint? fallback = null)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var value = _table?.Get(key);
        switch (value)
        {
            case null:
                return fallback;
            case TomlInteger i when i.Value is >= 0 and <= uint.MaxValue:
                return (uint)i.Value;
            case TomlInteger i:
                _diagnostics.Error(File, i.Line, path, $"value {i.Value} is out of range for a 32-bit unsigned number");
                return fallback;
            default:
                return ReportOrDefault(key, value, "an integer", fallback);
        }
    }

    public List<string> StringArray(string key)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var result = new List<string>();
        var value = _table?.Get(key);
        switch (value)
        {
            case null:
                return result;
            case TomlArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array.Items[i] is TomlString s)
                    {
                        result.Add(s.Value);
                    }
                    else
                    {
                        _diagnostics.Error(File, array.Items[i].Line, $"{path}[{i}]", $"expected a string, found {array.Items[i].TypeName}");
                    }
                }

                return result;
            case TomlString single:
                return [single.Value];
            default:
                Report(key, value, "an array of strings");
                return result;
        }
    }

    public List<string> RequireStringArray(string key)
    {
        if (!Has(key) && Present)
        {
            _diagnostics.Error(File, Line, Join(Path, key), "required key is missing");
        }

        return StringArray(key);
    }

    public List<uint> UInt32Array(string key)
    {
        _read.Add(key);
        string path = Join(Path, key);
        var result = new List<uint>();
        var value = _table?.Get(key);
        if (value is null)
        {
            return result;
        }

        if (value is not TomlArray array)
        {
            Report(key, value, "an array of integers");
            return result;
        }

        for (int i = 0; i < array.Count; i++)
        {
            if (array.Items[i] is TomlInteger integer && integer.Value is >= 0 and <= uint.MaxValue)
            {
                result.Add((uint)integer.Value);
            }
            else
            {
                _diagnostics.Error(File, array.Items[i].Line, $"{path}[{i}]", "expected a 32-bit unsigned integer");
            }
        }

        return result;
    }

    /// <summary>Reads a string that must be one of a fixed set of values.</summary>
    public string? Enum(string key, IReadOnlyCollection<string> allowed, string? fallback = null)
    {
        string path = Join(Path, key);
        var value = _table?.Get(key);
        string? text = String(key, fallback);
        if (value is null)
        {
            return fallback;
        }

        if (text is not null && !allowed.Contains(text, StringComparer.Ordinal))
        {
            _diagnostics.Error(File, value.Line, path, $"\"{text}\" is not one of: {string.Join(", ", allowed.OrderBy(a => a, StringComparer.Ordinal))}");
            return fallback;
        }

        return text;
    }

    /// <summary>Every key/value pair of this table. Marks all of them as read.</summary>
    public IEnumerable<KeyValuePair<string, TomlValue>> Pairs()
    {
        if (_table is null)
        {
            yield break;
        }

        foreach (var pair in _table.Pairs())
        {
            _read.Add(pair.Key);
            yield return pair;
        }
    }

    /// <summary>Records a key as read without interpreting it (used for keys we accept but ignore).</summary>
    public void Ignore(params string[] keys)
    {
        foreach (var key in keys)
        {
            _read.Add(key);
        }
    }

    private T? ReportOrDefault<T>(string key, TomlValue value, string expected, T? fallback)
    {
        Report(key, value, expected);
        return fallback;
    }

    private void Report(string key, TomlValue value, string expected)
        => _diagnostics.Error(File, value.Line, Join(Path, key), $"expected {expected}, found {value.TypeName}");

    /// <summary>Reports every key in this table that was never read. <c>x-</c> prefixed keys are allowed.</summary>
    public void RejectUnknownKeys()
    {
        if (_table is null)
        {
            return;
        }

        // Nested scopes read their own keys; the parent only had to hand out the table.
        _read.UnionWith(_children.Select(c => c.Key));

        foreach (var key in _table.KeyOrder)
        {
            if (_read.Contains(key) || key.StartsWith("x-", StringComparison.Ordinal))
            {
                continue;
            }

            _diagnostics.Error(File, _table.KeyLine(key), Join(Path, key), "unknown key");
        }
    }

    private static string Join(string prefix, string key) => string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
}
