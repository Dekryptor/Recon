using System.Globalization;
using System.Text;

namespace Recon.Toml;

/// <summary>
/// A small, strict TOML reader. It exists because the tool has to explain configuration errors in
/// terms of the file the user wrote: every value keeps the line it came from, duplicate keys are
/// errors rather than silent overwrites, and unknown keys are reported by the binder, not ignored.
/// Supported: tables, arrays of tables, dotted keys, inline tables, arrays, all four string forms,
/// integers in every base, floats, booleans and date-times (kept as text).
/// </summary>
public static class TomlParser
{
    public static TomlDocument ParseFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new TomlParseException(path, 0, 0, $"cannot read file: {ex.Message}");
        }

        return Parse(text, path);
    }

    public static TomlDocument Parse(string text, string fileName)
    {
        var parser = new Parser(text, fileName);
        return new TomlDocument(fileName, parser.ParseDocument());
    }

    private sealed class Parser(string text, string fileName)
    {
        private readonly string _text = text;
        private readonly string _file = fileName;
        private int _position;
        private int _line = 1;
        private int _lineStart;

        private bool AtEnd => _position >= _text.Length;

        private char Current => _position < _text.Length ? _text[_position] : '\0';

        private char Peek(int offset = 1) => _position + offset < _text.Length ? _text[_position + offset] : '\0';

        private int Column => _position - _lineStart + 1;

        public TomlTable ParseDocument()
        {
            var root = new TomlTable { Line = 1, Column = 1, Status = TableStatus.Explicit };
            TomlTable current = root;

            while (true)
            {
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    break;
                }

                if (Current == '[')
                {
                    current = ParseTableHeader(root);
                }
                else
                {
                    ParseKeyValue(current);
                }

                ExpectEndOfStatement();
            }

            return root;
        }

        // ---------------------------------------------------------------- statements

        private TomlTable ParseTableHeader(TomlTable root)
        {
            int line = _line;
            Advance(); // '['
            bool array = Current == '[';
            if (array)
            {
                Advance();
            }

            var path = ParseKeyPath(insideHeader: true);
            SkipHorizontalWhitespace();
            Expect(']');
            if (array)
            {
                Expect(']');
            }

            TomlTable parent = root;
            for (int i = 0; i < path.Count - 1; i++)
            {
                parent = DescendForHeader(parent, path[i], line);
            }

            string name = path[^1];

            if (array)
            {
                TomlArray target;
                if (parent.TryGet(name, out TomlValue? existing))
                {
                    if (existing is TomlArray { IsArrayOfTables: true } array2)
                    {
                        target = array2;
                    }
                    else
                    {
                        throw Error(line, $"cannot redefine {Describe(path)} as an array of tables");
                    }
                }
                else
                {
                    target = new TomlArray { Line = line, Column = Column, IsArrayOfTables = true };
                    parent.Set(name, target, line);
                }

                var element = new TomlTable { Line = line, Column = Column, Status = TableStatus.ArrayElement };
                target.Items.Add(element);
                return element;
            }

            if (parent.TryGet(name, out TomlValue? found))
            {
                switch (found)
                {
                    case TomlTable table when table.Status is TableStatus.ImplicitPath:
                        table.Status = TableStatus.Explicit;
                        return table;
                    case TomlTable table when table.Status is TableStatus.Explicit:
                        throw Error(line, $"table {Describe(path)} is already defined at line {table.Line}");
                    case TomlTable table:
                        throw Error(line, $"cannot define table {Describe(path)}: it is already a {(table.Status is TableStatus.Inline ? "inline table" : "dotted key value")} (line {table.Line})");
                    default:
                        throw Error(line, $"cannot define table {Describe(path)}: the key already holds a {found.TypeName} (line {found.Line})");
                }
            }

            var created = new TomlTable { Line = line, Column = Column, Status = TableStatus.Explicit };
            parent.Set(name, created, line);
            return created;
        }

        private TomlTable DescendForHeader(TomlTable parent, string name, int line)
        {
            if (!parent.TryGet(name, out TomlValue? child))
            {
                var implicitTable = new TomlTable { Line = line, Column = Column, Status = TableStatus.ImplicitPath };
                parent.Set(name, implicitTable, line);
                return implicitTable;
            }

            return child switch
            {
                TomlTable table when table.Status is not TableStatus.Inline => table,
                TomlArray { IsArrayOfTables: true, Items.Count: > 0 } array
                    => (TomlTable)array.Items[^1],
                _ => throw Error(line, $"cannot extend {name}: it holds a {child.TypeName} (line {child.Line})"),
            };
        }

        private void ParseKeyValue(TomlTable table)
        {
            int line = _line;
            var path = ParseKeyPath(insideHeader: false);
            SkipHorizontalWhitespace();
            Expect('=');
            SkipHorizontalWhitespace();
            TomlValue value = ParseValue();

            TomlTable target = table;
            for (int i = 0; i < path.Count - 1; i++)
            {
                string name = path[i];
                if (!target.TryGet(name, out TomlValue? child))
                {
                    var dotted = new TomlTable { Line = line, Column = Column, Status = TableStatus.DottedKey };
                    target.Set(name, dotted, line);
                    target = dotted;
                    continue;
                }

                target = child switch
                {
                    TomlTable { Status: TableStatus.DottedKey or TableStatus.ImplicitPath or TableStatus.Explicit } t => t,
                    TomlTable { Status: TableStatus.ArrayElement } t => t,
                    TomlTable { Status: TableStatus.Inline } t
                        => throw Error(line, $"cannot extend inline table {name} (line {t.Line})"),
                    _ => throw Error(line, $"cannot use {name} as a table: it is a {child.TypeName} (line {child.Line})"),
                };
            }

            string key = path[^1];
            if (target.TryGet(key, out TomlValue? existing))
            {
                throw Error(line, $"duplicate key {key}: already defined at line {existing.Line}");
            }

            target.Set(key, value, line);
        }

        private List<string> ParseKeyPath(bool insideHeader)
        {
            var parts = new List<string>();
            while (true)
            {
                SkipHorizontalWhitespace();
                parts.Add(ParseKey());
                SkipHorizontalWhitespace();
                if (Current == '.')
                {
                    Advance();
                    if (insideHeader && (Current == ']' || AtEnd))
                    {
                        throw Error(_line, "expected a key name after '.'");
                    }

                    continue;
                }

                return parts;
            }
        }

        private string ParseKey()
        {
            if (Current is '"' or '\'')
            {
                return ParseString().ToString();
            }

            int start = _position;
            while (!AtEnd && (char.IsLetterOrDigit(Current) || Current is '_' or '-'))
            {
                Advance();
            }

            if (_position == start)
            {
                throw Error(_line, $"expected a key name but found '{Current}'");
            }

            return _text[start.._position];
        }

        private void ExpectEndOfStatement()
        {
            SkipHorizontalWhitespace();
            if (Current == '#')
            {
                SkipComment();
            }

            if (AtEnd)
            {
                return;
            }

            if (Current is '\r' or '\n')
            {
                Advance();
                return;
            }

            throw Error(_line, $"unexpected '{Current}' after the value; one statement per line");
        }

        // ---------------------------------------------------------------- values

        private TomlValue ParseValue()
        {
            int line = _line;
            int column = Column;
            char c = Current;

            if (c == '"' || c == '\'')
            {
                return ParseString();
            }

            if (c == '[')
            {
                return ParseArray();
            }

            if (c == '{')
            {
                return ParseInlineTable();
            }

            // Everything else is a bare token: booleans, numbers, dates.
            int start = _position;
            while (!AtEnd && (char.IsLetterOrDigit(Current) || Current is '+' or '-' or '_' or '.' or ':' or 'e' or 'E'))
            {
                // Stop at whitespace or a comment; 'e'/'E' are part of numbers but also of words,
                // and words are validated below.
                if (Current is ' ' or '\t' or '#' or '\n' or '\r')
                {
                    break;
                }

                Advance();
            }

            string token = _text[start.._position];
            if (token.Length == 0)
            {
                throw Error(line, $"expected a value but found '{c}'");
            }

            if (token is "true" or "false")
            {
                return new TomlBoolean { Value = token == "true", Line = line, Column = column };
            }

            if (token is "inf" or "+inf")
            {
                return new TomlFloat { Value = double.PositiveInfinity, Line = line, Column = column };
            }

            if (token == "-inf")
            {
                return new TomlFloat { Value = double.NegativeInfinity, Line = line, Column = column };
            }

            if (token is "nan" or "+nan" or "-nan")
            {
                return new TomlFloat { Value = double.NaN, Line = line, Column = column };
            }

            if (LooksLikeDateTime(token))
            {
                return new TomlDateTime { Raw = token, Line = line, Column = column };
            }

            string cleaned = token.Replace("_", string.Empty);
            bool negative = cleaned.StartsWith('-');

            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || cleaned.StartsWith("0o", StringComparison.OrdinalIgnoreCase)
                || cleaned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                string digits = cleaned[2..];
                if (digits.Length == 0)
                {
                    throw Error(line, $"malformed integer '{token}'");
                }

                try
                {
                    long value = cleaned[1] switch
                    {
                        'x' or 'X' => Convert.ToInt64(digits, 16),
                        'o' or 'O' => Convert.ToInt64(digits, 8),
                        _ => Convert.ToInt64(digits, 2),
                    };
                    return new TomlInteger { Value = value, Line = line, Column = column };
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw Error(line, $"malformed integer '{token}'");
                }
            }

            if (cleaned.Contains('.') || cleaned.Contains('e') || cleaned.Contains('E'))
            {
                if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                {
                    return new TomlFloat { Value = f, Line = line, Column = column };
                }

                throw Error(line, $"malformed float '{token}'");
            }

            if (long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            {
                return new TomlInteger { Value = integer, Line = line, Column = column };
            }

            _ = negative;
            throw Error(line, $"unrecognised value '{token}'");
        }

        private static bool LooksLikeDateTime(string token)
        {
            // 1979-05-27, 07:32:00, 1979-05-27T07:32:00Z and friends.
            if (token.Length < 5)
            {
                return false;
            }

            if (token.Contains(':') && token.Contains('-'))
            {
                return true;
            }

            if (token.Length == 10 && token[4] == '-' && token[7] == '-')
            {
                return true;
            }

            return token.Length == 8 && token[2] == ':' && token[5] == ':';
        }

        private TomlArray ParseArray()
        {
            var array = new TomlArray { Line = _line, Column = Column };
            Advance(); // '['

            while (true)
            {
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    throw Error(array.Line, "array is not closed");
                }

                if (Current == ']')
                {
                    Advance();
                    return array;
                }

                array.Items.Add(ParseValue());
                SkipWhitespaceAndComments();

                if (Current == ',')
                {
                    Advance();
                    continue;
                }

                if (Current == ']')
                {
                    Advance();
                    return array;
                }

                throw Error(_line, $"expected ',' or ']' in array but found '{Current}'");
            }
        }

        private TomlTable ParseInlineTable()
        {
            var table = new TomlTable { Line = _line, Column = Column, Status = TableStatus.Inline };
            Advance(); // '{'

            while (true)
            {
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    throw Error(table.Line, "inline table is not closed");
                }

                if (Current == '}')
                {
                    Advance();
                    return table;
                }

                ParseKeyValue(table);
                SkipWhitespaceAndComments();

                if (Current == ',')
                {
                    Advance();
                    continue;
                }

                if (Current == '}')
                {
                    Advance();
                    return table;
                }

                throw Error(_line, $"expected ',' or '}}' in inline table but found '{Current}'");
            }
        }

        private TomlString ParseString()
        {
            int line = _line;
            int column = Column;
            char quote = Current;
            bool literal = quote == '\'';

            if (Peek() == quote && Peek(2) == quote)
            {
                return ParseMultilineString(quote, literal, line, column);
            }

            Advance(); // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error(line, "string is not closed");
                }

                char c = Current;
                if (c == quote)
                {
                    Advance();
                    return new TomlString { Value = builder.ToString(), Line = line, Column = column, IsLiteral = literal };
                }

                if (c is '\n' or '\r')
                {
                    throw Error(line, "newline in a single-line string");
                }

                if (!literal && c == '\\')
                {
                    Advance();
                    builder.Append(ParseEscape());
                    continue;
                }

                builder.Append(c);
                Advance();
            }
        }

        private TomlString ParseMultilineString(char quote, bool literal, int line, int column)
        {
            Advance();
            Advance();
            Advance();

            // A newline immediately after the opening delimiter is trimmed.
            if (Current == '\r')
            {
                Advance();
            }

            if (Current == '\n')
            {
                Advance();
            }

            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error(line, "multi-line string is not closed");
                }

                if (Current == quote && Peek() == quote && Peek(2) == quote)
                {
                    AppendUpToFiveQuotes(builder, quote);
                    return new TomlString
                    {
                        Value = builder.ToString(),
                        Line = line,
                        Column = column,
                        IsLiteral = literal,
                        IsMultiline = true,
                    };
                }

                char c = Current;
                if (!literal && c == '\\')
                {
                    // A backslash at the end of a line swallows the following whitespace.
                    int save = _position;
                    Advance();
                    if (Current is '\n' or '\r' || OnlyWhitespaceToEndOfLine())
                    {
                        SkipWhitespaceAndNewlines();
                        continue;
                    }

                    _position = save;
                    Advance();
                    builder.Append(ParseEscape());
                    continue;
                }

                builder.Append(c);
                Advance();
            }
        }

        private bool OnlyWhitespaceToEndOfLine()
        {
            int i = _position;
            while (i < _text.Length && _text[i] is ' ' or '\t')
            {
                i++;
            }

            return i < _text.Length && _text[i] is '\n' or '\r';
        }

        private void AppendUpToFiveQuotes(StringBuilder builder, char quote)
        {
            // The closing delimiter is three quotes; one or two quotes before it are content.
            int count = 0;
            while (Current == quote && count < 5)
            {
                count++;
                Advance();
            }

            builder.Append(quote, Math.Max(0, count - 3));
        }

        private string ParseEscape()
        {
            char c = Current;
            switch (c)
            {
                case 'b': Advance(); return "\b";
                case 't': Advance(); return "\t";
                case 'n': Advance(); return "\n";
                case 'f': Advance(); return "\f";
                case 'r': Advance(); return "\r";
                case '"': Advance(); return "\"";
                case '\\': Advance(); return "\\";
                case 'u': return ParseUnicodeEscape(4);
                case 'U': return ParseUnicodeEscape(8);
                default:
                    throw Error(_line, $"unknown escape sequence '\\{c}'");
            }
        }

        private string ParseUnicodeEscape(int digits)
        {
            Advance(); // 'u' or 'U'
            if (_position + digits > _text.Length)
            {
                throw Error(_line, "truncated unicode escape");
            }

            string hex = _text.Substring(_position, digits);
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
            {
                throw Error(_line, $"malformed unicode escape '\\u{hex}'");
            }

            for (int i = 0; i < digits; i++)
            {
                Advance();
            }

            return char.ConvertFromUtf32(code);
        }

        // ---------------------------------------------------------------- helpers

        private void SkipWhitespaceAndComments()
        {
            while (!AtEnd)
            {
                char c = Current;
                if (c is ' ' or '\t')
                {
                    Advance();
                }
                else if (c == '#')
                {
                    SkipComment();
                }
                else if (c is '\r' or '\n')
                {
                    Advance();
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipWhitespaceAndNewlines()
        {
            while (!AtEnd && Current is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
            }
        }

        private void SkipHorizontalWhitespace()
        {
            while (!AtEnd && Current is ' ' or '\t')
            {
                Advance();
            }
        }

        private void SkipComment()
        {
            while (!AtEnd && Current is not '\n')
            {
                Advance();
            }
        }

        private void Expect(char c)
        {
            if (Current != c)
            {
                throw Error(_line, $"expected '{c}' but found '{(AtEnd ? "end of file" : Current.ToString())}'");
            }

            Advance();
        }

        private void Advance()
        {
            if (AtEnd)
            {
                return;
            }

            if (_text[_position] == '\n')
            {
                _line++;
                _lineStart = _position + 1;
            }

            _position++;
        }

        private TomlParseException Error(int line, string message) => new(_file, line, Column, message);

        private static string Describe(List<string> path) => string.Join('.', path);
    }
}
