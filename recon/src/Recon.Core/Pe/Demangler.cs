using System.Text;

namespace Recon.Pe;

public enum SymbolKind
{
    Unknown,
    Function,
    Data,
}

public sealed class DemangleResult
{
    /// <summary>Fully qualified name, or null when the mangling uses a construct we do not handle.</summary>
    public string? Text { get; set; }

    public SymbolKind Kind { get; set; } = SymbolKind.Unknown;

    /// <summary>Calling convention implied by the mangling, if any.</summary>
    public string? CallingConvention { get; set; }

    public bool IsMemberFunction { get; set; }

    /// <summary>Which scheme the symbol uses: <c>msvc</c>, <c>itanium</c>, <c>c</c>.</summary>
    public string Scheme { get; set; } = "c";

    /// <summary>Set when a name was produced by a partial decoder: do not present it as exact.</summary>
    public bool Partial { get; set; }
}

/// <summary>
/// Best-effort symbol demangling for the two schemes this tool meets first. Names that use
/// constructs we do not decode keep <c>Text = null</c> rather than a wrong guess.
/// </summary>
public static class Demangler
{
    public static DemangleResult Demangle(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return new DemangleResult { Scheme = "c" };
        }

        if (symbol.StartsWith('?'))
        {
            var result = DemangleMsvc(symbol);
            result.Scheme = "msvc";
            return result;
        }

        if (symbol.StartsWith("_Z", StringComparison.Ordinal) || symbol.StartsWith("__Z", StringComparison.Ordinal))
        {
            var result = DemangleItanium(symbol);
            result.Scheme = "itanium";
            return result;
        }

        // C decoration: _name@N is __stdcall, @name@N is __fastcall, plain _name is __cdecl.
        if (symbol.StartsWith('@') && TryParseDecorated(symbol, '@', out string fastName, out int fastBytes))
        {
            return new DemangleResult
            {
                Text = fastName,
                Kind = SymbolKind.Function,
                CallingConvention = "fastcall",
                Scheme = "c",
            };
        }

        if (symbol.StartsWith('_') && TryParseDecorated(symbol[1..], '@', out string stdName, out _))
        {
            return new DemangleResult
            {
                Text = stdName,
                Kind = SymbolKind.Function,
                CallingConvention = "stdcall",
                Scheme = "c",
            };
        }

        // MinGW writes stdcall as _name@N; after stripping the underscore it is name@N.
        if (!symbol.StartsWith('_') && symbol.Contains('@') && TryParseDecorated(symbol, '@', out string decorated, out _))
        {
            return new DemangleResult
            {
                Text = decorated,
                Kind = SymbolKind.Function,
                CallingConvention = "stdcall",
                Scheme = "c",
            };
        }

        string trimmed = symbol.StartsWith('_') ? symbol[1..] : symbol;
        return new DemangleResult
        {
            Text = trimmed,
            Kind = LooksLikeData(symbol) ? SymbolKind.Data : SymbolKind.Function,
            CallingConvention = "cdecl",
            Scheme = "c",
        };
    }

    private static bool TryParseDecorated(string text, char separator, out string name, out int stackBytes)
    {
        name = text;
        stackBytes = 0;
        int at = text.LastIndexOf(separator);
        if (at <= 0)
        {
            return false;
        }

        if (!int.TryParse(text[(at + 1)..], out stackBytes))
        {
            return false;
        }

        // The decoration prefix is a calling-convention marker, not part of the name: the fastcall
        // form is @name@8 and the stdcall form is _name@8.
        name = text[..at].TrimStart('@', '_');
        return name.Length > 0;
    }

    private static bool LooksLikeData(string symbol)
        => symbol.StartsWith("__real@", StringComparison.Ordinal)
           || symbol.Contains("@@3", StringComparison.Ordinal)
           || symbol.StartsWith("_?", StringComparison.Ordinal);

    // ---------------------------------------------------------------- MSVC

    /// <summary>
    /// Decodes the common shapes of MSVC mangling: <c>?name@Class@@YA...</c>,
    /// <c>?name@Class@@QAE...</c>, plus argument types. Returns <c>Partial</c> when it had to stop.
    /// </summary>
    public static DemangleResult DemangleMsvc(string symbol)
    {
        var result = new DemangleResult { Scheme = "msvc" };

        if (symbol.StartsWith("??_", StringComparison.Ordinal) || (symbol.Length > 2 && symbol[1] == '?' && char.IsAsciiDigit(symbol[2])))
        {
            if (TryDemangleMsvcSpecial(symbol, result))
            {
                return result;
            }
        }

        var body = symbol.AsSpan(1);
        int separator = body.IndexOf("@@");
        if (separator < 0)
        {
            return result;
        }

        var nameParts = body[..separator].ToString().Split('@', StringSplitOptions.RemoveEmptyEntries);
        if (nameParts.Length == 0)
        {
            return result;
        }

        string name = nameParts[0];
        var scope = nameParts.Skip(1).Reverse().ToList();
        string rest = symbol[(1 + separator + 2)..];

        // A trailing "3" style marker means the symbol is data, not a function.
        if (rest.Length > 0 && char.IsDigit(rest[0]))
        {
            result.Kind = SymbolKind.Data;
            result.Text = string.Join("::", scope.Append(name));
            return result;
        }

        var parser = new MsvcParser(result, name, scope, rest);
        parser.Parse();
        return result;
    }

    private sealed class MsvcParser(DemangleResult result, string name, List<string> scope, string rest)
    {
        private readonly DemangleResult _result = result;
        private readonly string _name = name;
        private readonly List<string> _scope = scope;
        private readonly string _rest = rest;

        public void Parse()
        {
            _result.Kind = SymbolKind.Function;
            int index = 0;
            if (!ParseFunctionPrefix(_rest, ref index))
            {
                return;
            }

            if (index < _rest.Length && _rest[index] == '@')
            {
                // Constructors carry no return type; their argument list starts right here.
                index++;
            }

            // The return type comes before the argument list. It is parsed to advance past it; the
            // inventory shows signatures without return types, which is what a reader wants.
            if (ParseType(_rest, ref index) is null)
            {
                _result.Partial = true;
                _result.Text = string.Join("::", _scope.Append(_name));
                return;
            }

            var args = new List<string>();
            while (index < _rest.Length && _rest[index] != 'Z' && _rest[index] != '@')
            {
                string? type = ParseType(_rest, ref index);
                if (type is null)
                {
                    _result.Partial = true;
                    break;
                }

                args.Add(type);
            }

            if (args.Count == 1 && args[0] == "void")
            {
                args.Clear();
            }

            _result.Text = $"{string.Join("::", _scope.Append(_name))}({string.Join(", ", args)})";
        }

        /// <summary>
        /// Parses only what a compiler-generated symbol needs: the calling convention and the
        /// argument list that follows it, leaving the name to the caller. Returns null when the tail
        /// cannot be read.
        /// </summary>
        public List<string>? ParseArguments()
        {
            int index = 0;
            if (!ParseFunctionPrefix(_rest, ref index))
            {
                return null;
            }

            // Constructors and destructors have no return type: after the member group comes an
            // argument list introduced by '@', while a deleting destructor has a return type first.
            bool argumentsOnly = index < _rest.Length && _rest[index] == '@';
            if (argumentsOnly)
            {
                index++;
            }
            else if (ParseType(_rest, ref index) is null)
            {
                return null;
            }

            var args = new List<string>();
            while (index < _rest.Length && _rest[index] != 'Z' && _rest[index] != '@')
            {
                string? type = ParseType(_rest, ref index);
                if (type is null)
                {
                    _result.Partial = true;
                    break;
                }

                args.Add(type);
            }

            if (args.Count == 1 && args[0] == "void")
            {
                args.Clear();
            }

            return args;
        }

        /// <summary>
        /// The letters between <c>@@</c> and the return type say how the function is called.
        /// Member functions use three - access or virtual, cv-qualifier, calling convention - while
        /// static members use two, and a free function uses <c>Y</c> plus the convention.
        /// </summary>
        private bool ParseFunctionPrefix(string rest, ref int index)
        {
            if (rest.Length == 0)
            {
                return false;
            }

            if (rest[0] == 'Y')
            {
                if (rest.Length < 2)
                {
                    return false;
                }

                _result.CallingConvention = CallType(rest[1]);
                index = 2;
                return true;
            }

            char access = rest[0];
            if (!"ABIJQSEUMVW".Contains(access, StringComparison.Ordinal))
            {
                return false;
            }

            _result.IsMemberFunction = true;
            bool isStatic = access is 'B' or 'J' or 'S';
            int i = 1;
            if (!isStatic)
            {
                // cv-qualifier: A none, B const, C volatile, D const volatile.
                if (i >= rest.Length || !"ABCD".Contains(rest[i], StringComparison.Ordinal))
                {
                    return false;
                }

                i++;
            }

            if (i < rest.Length && CallType(rest[i]) is not null)
            {
                _result.CallingConvention = CallType(rest[i]);
                i++;
            }
            else if (!isStatic)
            {
                return false; // a non-static member always names its convention
            }

            index = i;
            return true;
        }

        private static string? CallType(char c) => c switch
        {
            'A' => "cdecl",
            'C' => "pascal",
            'E' => "thiscall",
            'G' => "stdcall",
            'I' => "fastcall",
            'M' => "clrcall",
            'Q' => "vectorcall",
            _ => null,
        };

        private string? ParseType(string rest, ref int index)
        {
            if (index >= rest.Length)
            {
                return null;
            }

            char c = rest[index];
            switch (c)
            {
                case 'X':
                    index++;
                    return "void";
                case 'D':
                    index++;
                    return "char";
                case 'C':
                    index++;
                    return "signed char";
                case 'E':
                    index++;
                    return "unsigned char";
                case 'F':
                    index++;
                    return "short";
                case 'G':
                    index++;
                    return "unsigned short";
                case 'H':
                    index++;
                    return "int";
                case 'I':
                    index++;
                    return "unsigned int";
                case 'J':
                    index++;
                    return "long";
                case 'K':
                    index++;
                    return "unsigned long";
                case 'M':
                    index++;
                    return "float";
                case 'N':
                    index++;
                    return "double";
                case 'O':
                    index++;
                    return "long double";
                case '_':
                {
                    // Extended built-in types are two letters: _J __int64, _N bool, _W wchar_t.
                    if (index + 1 >= rest.Length)
                    {
                        return null;
                    }

                    char extended = rest[index + 1];
                    index += 2;
                    return extended switch
                    {
                        'J' => "__int64",
                        'K' => "unsigned __int64",
                        'N' => "bool",
                        'W' => "wchar_t",
                        'Q' => "char16_t",
                        'U' => "char32_t",
                        'S' => "char8_t",
                        _ => null,
                    };
                }
                case 'P':
                case 'Q':
                case 'R':
                case 'S':
                case 'A':
                case 'B':
                {
                    index++;

                    // The code is followed by a cv-qualifier letter: A none, B const, C volatile,
                    // D const volatile. Not consuming it decodes int* as int&*.
                    string qualifier = string.Empty;
                    if (index < rest.Length && "ABCD".Contains(rest[index], StringComparison.Ordinal))
                    {
                        qualifier = rest[index] switch
                        {
                            'B' => " const",
                            'C' => " volatile",
                            'D' => " const volatile",
                            _ => string.Empty,
                        };
                        index++;
                    }

                    string? inner = ParseType(rest, ref index);
                    if (inner is null)
                    {
                        return null;
                    }

                    string suffix = c switch
                    {
                        'P' => " *",
                        'Q' => " *const",
                        'R' => " &",
                        'S' => " &const",
                        _ => " &",
                    };
                    return inner + qualifier + suffix;
                }

                case 'U':
                case 'V':
                case 'T':
                case 'W':
                {
                    // A named type written without the leading '?': PAUS@@ is S*, AAUS@@ is S&.
                    int end = rest.IndexOf("@@", index, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        return null;
                    }

                    string body = rest[(index + 1)..end];
                    index = end + 2;
                    if (body.Length > 0 && body.All(char.IsAsciiDigit))
                    {
                        // A back-reference to a type named earlier in the same symbol. Resolving it
                        // needs the numbered type table, so it is shown as unresolved and flagged.
                        _result.Partial = true;
                        return $"<type {body}>";
                    }

                    var parts = body.Split('@', StringSplitOptions.RemoveEmptyEntries).Reverse().ToList();
                    string named = string.Join("::", parts);
                    return c switch
                    {
                        'U' => $"struct {named}",
                        'T' => $"union {named}",
                        'W' => $"enum {named}",
                        _ => named,
                    };
                }

                case '?':
                {
                    // A class, struct, enum or union type: ?A<name>@@ or ?AV<name>@@
                    if (index + 1 < rest.Length && "AVUTWE".Contains(rest[index + 1]))
                    {
                        char kind = rest[index + 1];
                        int start = index + 2;
                        int end = rest.IndexOf("@@", start, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            return null;
                        }

                        var parts = rest[start..end].Split('@', StringSplitOptions.RemoveEmptyEntries).Reverse().ToList();
                        index = end + 2;
                        string name = string.Join("::", parts);
                        return kind switch
                        {
                            'V' => name,
                            'U' => $"struct {name}",
                            'T' => $"union {name}",
                            'W' => $"enum {name}",
                            _ => name,
                        };
                    }

                    return null;
                }

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Names the compiler gives to things that are not ordinary functions: constructors,
    /// destructors, vtables and RTTI records. Every MSVC binary has them, so they get a readable
    /// label instead of a failed parse. Expected labels were checked against llvm-undname.
    /// </summary>
    private static bool TryDemangleMsvcSpecial(string symbol, DemangleResult result)
    {
        if (symbol.Length > 2 && symbol[1] == '?' && symbol[2] is '0' or '1')
        {
            // ??0Circle@@QAE@H@Z is a constructor, ??1Circle@@QAE@XZ a destructor. The class chain
            // before @@ is the name, and the letters after it are the usual member-function group.
            string tail = symbol[3..];
            int separator = tail.IndexOf("@@", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            string chain = LastScope(tail, chained: true);
            if (chain.Length == 0)
            {
                return false;
            }

            string className = chain.Split("::")[^1];
            string name = symbol[2] == '0' ? className : "~" + className;
            var arguments = new MsvcParser(result, className, [], tail[(separator + 2)..]).ParseArguments();
            if (arguments is null)
            {
                result.Partial = true;
                arguments = [];
            }

            result.Kind = SymbolKind.Function;
            result.IsMemberFunction = true;
            result.Text = $"{chain}::{name}({string.Join(", ", arguments)})";
            return true;
        }

        var labels = new (string Prefix, string Label, SymbolKind Kind)[]
        {
            ("??_7", "`vftable'", SymbolKind.Data),
            ("??_8", "`vbtable'", SymbolKind.Data),
            ("??_R0", "`RTTI Type Descriptor'", SymbolKind.Data),
            ("??_R1", "`RTTI Base Class Descriptor'", SymbolKind.Data),
            ("??_R2", "`RTTI Base Class Array'", SymbolKind.Data),
            ("??_R3", "`RTTI Class Hierarchy Descriptor'", SymbolKind.Data),
            ("??_R4", "`RTTI Complete Object Locator'", SymbolKind.Data),
            ("??_G", "`scalar deleting destructor'", SymbolKind.Function),
            ("??_E", "`vector deleting destructor'", SymbolKind.Function),
        };

        foreach (var (prefix, label, kind) in labels)
        {
            if (!symbol.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            bool chained = prefix is "??_7" or "??_8" or "??_G" or "??_E";
            string tail = symbol[prefix.Length..];
            string scopeName = LastScope(tail, chained);
            result.Kind = kind;

            if (prefix is "??_G" or "??_E")
            {
                // A deleting destructor is a member function like any other: its calling convention
                // and its argument list are encoded after the class name, and leaving them out is
                // how such a function ends up described as a cdecl free function.
                int separator = tail.IndexOf("@@", StringComparison.Ordinal);
                var args = separator >= 0
                    ? new MsvcParser(result, scopeName, [], tail[(separator + 2)..]).ParseArguments() ?? []
                    : [];
                result.Text = scopeName.Length > 0
                    ? $"{scopeName}::{label}({string.Join(", ", args)})"
                    : $"::{label}({string.Join(", ", args)})";
                return true;
            }

            result.Text = scopeName.Length > 0 ? $"{scopeName}::{label}" : $"::{label}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pulls the class name out of the tail of a vtable or RTTI symbol. RTTI tails carry flag and
    /// number fields before the name, so only the last field is taken there; a vtable tail is the
    /// name alone and keeps its namespace chain.
    /// </summary>
    private static string LastScope(string tail, bool chained)
    {
        int separator = tail.IndexOf("@@", StringComparison.Ordinal);
        string section = separator >= 0 ? tail[..separator] : tail;
        if (section.StartsWith('?'))
        {
            // A type-encoded name such as ?AUC@@: drop the marker, keep the name.
            section = section.Length > 2 && section[1] == 'A' && "UVWT".Contains(section[2], StringComparison.Ordinal)
                ? section[3..]
                : section[1..];
        }

        var parts = section.Split('@', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 0 && p[0] != '?')
            .Reverse()
            .ToList();
        if (parts.Count > 1 && !chained)
        {
            parts = [parts[0]];
        }

        return parts.Count > 0 ? string.Join("::", parts) : string.Empty;
    }

    // ---------------------------------------------------------------- Itanium

    /// <summary>
    /// Decodes the common shapes of Itanium mangling (<c>_ZN3Foo3barEi</c>). Nested names,
    /// cv-qualified member functions, simple types, pointers/references and templates are handled.
    /// </summary>
    public static DemangleResult DemangleItanium(string symbol)
    {
        var result = new DemangleResult { Scheme = "itanium", Kind = SymbolKind.Data };
        var text = symbol.StartsWith("__Z", StringComparison.Ordinal) ? symbol[3..] : symbol[2..];
        var parser = new ItaniumParser(text);
        string? name = parser.ParseName();
        if (name is null)
        {
            result.Partial = true;
            return result;
        }

        result.Text = name;
        result.Kind = SymbolKind.Function;
        if (parser.IsMemberFunction)
        {
            // On 32-bit targets a member function receives 'this' implicitly.
            result.IsMemberFunction = true;
            result.CallingConvention = "thiscall";
        }

        var args = parser.ParseParameterList();
        if (args is not null)
        {
            result.Text = $"{name}({string.Join(", ", args)})";
        }
        else
        {
            result.Partial = true;
        }

        if (parser.HasConstQualifier)
        {
            result.Text = result.Text?.Replace(")(", ") const(") ?? result.Text;
        }

        return result;
    }

    private sealed class ItaniumParser(string text)
    {
        private readonly string _text = text;
        private int _index;

        public bool IsMemberFunction { get; private set; }

        public bool HasConstQualifier { get; private set; }

        public string? ParseName()
        {
            var parts = new List<string>();
            var substitutions = new List<string>();
            bool isMember = false;
            if (!ParseNested(ref parts, substitutions, ref isMember, out string? first))
            {
                return null;
            }

            if (first is null)
            {
                return null;
            }

            IsMemberFunction = isMember;
            parts.Insert(0, first);
            HasConstQualifier = _index < _text.Length && _text[_index] == 'K';
            if (HasConstQualifier)
            {
                _index++;
            }

            return string.Join("::", parts);
        }

        private bool ParseNested(ref List<string> parts, List<string> substitutions, ref bool isMember, out string? simple)
        {
            simple = null;
            if (_index >= _text.Length)
            {
                return false;
            }

            char c = _text[_index];
            if (c == 'N')
            {
                _index++;
                bool first = true;
                while (_index < _text.Length && _text[_index] != 'E')
                {
                    char next = _text[_index];
                    if (next == 'C' || next == 'D')
                    {
                        // Constructors (C1/C2/C3) and destructors (D0/D1/D2).
                        _index++;
                        if (_index < _text.Length && char.IsDigit(_text[_index]))
                        {
                            _index++;
                        }

                        parts.Add(first ? "???" : parts[^1] + "::~" + parts[^1]);
                        continue;
                    }

                    string? part = ParseUnqualified(substitutions);
                    if (part is null)
                    {
                        return false;
                    }

                    parts.Add(part);
                    if (!first)
                    {
                        isMember = true;
                    }

                    first = false;
                }

                if (_index < _text.Length && _text[_index] == 'E')
                {
                    _index++;
                }

                simple = parts[0];
                parts.RemoveAt(0);
                return true;
            }

            simple = ParseUnqualified(substitutions);
            return simple is not null;
        }

        private string? ParseUnqualified(List<string> substitutions)
        {
            if (_index >= _text.Length)
            {
                return null;
            }

            char c = _text[_index];
            if (c == 'S')
            {
                // Substitutions: S_ = first, S0_ = later, St = std, Sa = std::allocator.
                if (_index + 1 < _text.Length && _text[_index + 1] == 't')
                {
                    _index += 2;
                    return "std";
                }

                if (_index + 1 < _text.Length && _text[_index + 1] == 'a')
                {
                    _index += 2;
                    return "std::allocator";
                }

                int start = _index + 1;
                int end = _text.IndexOf('_', start);
                if (end < 0)
                {
                    return null;
                }

                string digits = _text[start..end];
                _index = end + 1;
                if (digits.Length == 0)
                {
                    return substitutions.Count > 0 ? substitutions[0] : null;
                }

                return int.TryParse(digits, out int index) && index < substitutions.Count ? substitutions[index] : null;
            }

            if (c == 'L')
            {
                // Internal linkage name: L<name>
                _index++;
                return ParseUnqualified(substitutions);
            }

            if (char.IsDigit(c))
            {
                int length = 0;
                while (_index < _text.Length && char.IsDigit(_text[_index]))
                {
                    length = (length * 10) + (_text[_index] - '0');
                    _index++;
                }

                if (_index + length > _text.Length)
                {
                    return null;
                }

                string name = _text.Substring(_index, length);
                _index += length;
                if (_index < _text.Length && _text[_index] == 'I')
                {
                    string? template = ParseTemplateArguments(substitutions);
                    if (template is not null)
                    {
                        name += template;
                    }
                }

                substitutions.Add(name);
                return name;
            }

            return null;
        }

        private string? ParseTemplateArguments(List<string> substitutions)
        {
            if (_index >= _text.Length || _text[_index] != 'I')
            {
                return null;
            }

            _index++;
            var args = new List<string>();
            while (_index < _text.Length && _text[_index] != 'E')
            {
                string? arg = ParseTypeName(substitutions);
                if (arg is null)
                {
                    return null;
                }

                args.Add(arg);
            }

            if (_index >= _text.Length)
            {
                return null;
            }

            _index++; // 'E'
            return "<" + string.Join(", ", args) + ">";
        }

        private string? ParseTypeName(List<string> substitutions)
        {
            if (_index >= _text.Length)
            {
                return null;
            }

            char c = _text[_index];
            switch (c)
            {
                case 'v':
                    _index++;
                    return "void";
                case 'b':
                    _index++;
                    return "bool";
                case 'c':
                    _index++;
                    return "char";
                case 'a':
                    _index++;
                    return "signed char";
                case 'h':
                    _index++;
                    return "unsigned char";
                case 's':
                    _index++;
                    return "short";
                case 't':
                    _index++;
                    return "unsigned short";
                case 'i':
                    _index++;
                    return "int";
                case 'j':
                    _index++;
                    return "unsigned int";
                case 'l':
                    _index++;
                    return "long";
                case 'm':
                    _index++;
                    return "unsigned long";
                case 'x':
                    _index++;
                    return "long long";
                case 'y':
                    _index++;
                    return "unsigned long long";
                case 'f':
                    _index++;
                    return "float";
                case 'd':
                    _index++;
                    return "double";
                case 'e':
                    _index++;
                    return "long double";
                case 'w':
                    _index++;
                    return "wchar_t";
                case 'P':
                    _index++;
                    return Wrap(ParseTypeName(substitutions), "*");
                case 'R':
                    _index++;
                    return Wrap(ParseTypeName(substitutions), "&");
                case 'O':
                    _index++;
                    return Wrap(ParseTypeName(substitutions), "&&");
                case 'K':
                    _index++;
                    return Wrap(ParseTypeName(substitutions), " const");
                case 'V':
                    _index++;
                    return Wrap(ParseTypeName(substitutions), " volatile");
                default:
                    return ParseUnqualified(substitutions);
            }
        }

        private static string? Wrap(string? inner, string suffix) => inner is null ? null : inner + suffix;

        public List<string>? ParseParameterList()
        {
            var args = new List<string>();
            var substitutions = new List<string>();
            while (_index < _text.Length)
            {
                char c = _text[_index];
                if (c == 'E' || c == 'K' || c == 'V')
                {
                    _index++;
                    continue;
                }

                string? type = ParseTypeName(substitutions);
                if (type is null)
                {
                    return args.Count == 0 ? [] : args;
                }

                args.Add(type);
            }

            if (args.Count == 1 && args[0] == "void")
            {
                args.Clear();
            }

            return args;
        }
    }
}
