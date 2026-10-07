namespace Recon.Permute;

/// <summary>
/// Produces candidate sources: the same program written a different way, so that rebuilding it and
/// comparing the bytes can say which one the original was.
///
/// The rule this file lives by is that a variant must be *provably* equivalent to the original. A
/// permuter whose variants sometimes mean something else is worse than no permuter, because the one
/// it finds may match the bytes and still be the wrong source — so every edit below is one whose
/// equivalence follows from the shape of the code it is applied to, and anything the generator is not
/// sure about is simply not emitted. Working on C text rather than on an AST is the price of having
/// no C front end in the tool; it is paid in coverage, never in soundness.
/// </summary>
public static partial class VariantGenerator
{
    /// <summary>Precedence-ordered binary operators, lowest first. Used only to find operand spans.</summary>
    private static readonly string[] Operators =
    [
        "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=",
        "||", "&&",
        "|", "^", "&",
        "==", "!=",
        "<", ">", "<=", ">=",
        "<<", ">>",
        "+", "-",
        "*", "/", "%",
        "++", "--",
    ];

    /// <summary>The operators whose two operands may change places without changing the value.</summary>
    private static readonly string[] Commutative = ["+", "*", "==", "!=", "&", "|", "^"];

    /// <summary>
    /// Statements that begin with one of these are never swapped with their neighbour: moving a
    /// <c>return</c> or a loop header is not the same program. A declaration begins with a type
    /// keyword too, which is why this is a shorter list than the keywords.
    /// </summary>
    private static readonly HashSet<string> ControlFlow =
    [
        "return", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "goto",
    ];

    private static readonly HashSet<string> Keywords =
    [
        "if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue",
        "goto", "sizeof", "int", "char", "short", "long", "unsigned", "signed", "float", "double",
        "void", "const", "static", "volatile", "register", "struct", "union", "enum", "typedef",
        "extern", "inline", "restrict", "bool", "true", "false", "NULL", "nullptr",
    ];

    /// <summary>A statement as the scanner found it: a span of the source and the line it starts on.</summary>
    private sealed record Stmt(int Start, int End, int Line, bool Compound);

    /// <summary>
    /// Every edit the generator can make to this source, in a fixed order — by position, then by kind
    /// — so that two runs on the same file produce the same list in the same order. A search that
    /// cannot be reproduced cannot be checked.
    /// </summary>
    /// <param name="source">The unit's source text.</param>
    /// <param name="limit">How many variants to return at most; zero means all of them.</param>
    public static List<SourceVariant> Generate(string source, int limit = 0)
    {
        var variants = new List<SourceVariant>();
        var statements = Scan(source);

        for (int i = 0; i + 1 < statements.Count; i++)
        {
            AddStatementSwap(source, statements[i], statements[i + 1], variants);
            AddDeclarationSwap(source, statements[i], statements[i + 1], variants);
        }

        foreach (var stmt in statements)
        {
            AddOperandSwaps(source, stmt, variants);
            AddIncrementForms(source, stmt, variants);
        }

        AddBranchInversions(source, variants);

        if (limit > 0 && variants.Count > limit)
        {
            variants.RemoveRange(limit, variants.Count - limit);
        }

        return variants;
    }

    // ------------------------------------------------------------------- statement level

    /// <summary>
    /// Swap two consecutive statements. Only when neither can observe the other: the identifiers they
    /// touch are disjoint, and neither calls anything — a call could reach the same global state or
    /// the same object through a pointer, and swapping those is not something this file can prove safe.
    /// </summary>
    private static void AddStatementSwap(string source, Stmt first, Stmt second, List<SourceVariant> variants)
    {
        if (first.Compound || second.Compound)
        {
            return;
        }

        string a = source[first.Start..first.End];
        string b = source[second.Start..second.End];
        if (a.Contains('(') || b.Contains('('))
        {
            return;
        }

        if (StartsWithControlFlow(a) || StartsWithControlFlow(b))
        {
            return;
        }

        if (!Disjoint(Identifiers(a), Identifiers(b)))
        {
            return;
        }

        string text = source[..first.Start] + b + Gap(source, first.End, second.Start) + a + source[second.End..];
        variants.Add(new SourceVariant
        {
            Id = $"swap-stmt-{first.Line}-{second.Line}",
            Kind = "statement-swap",
            Line = first.Line,
            Description = $"swap the statements on lines {first.Line} and {second.Line}",
            Text = text,
        });
    }

    /// <summary>
    /// Swap two consecutive declarations of the same type with no initialisers. They cannot depend on
    /// each other, and what this really explores is stack layout: the compiler may well have laid the
    /// original's locals out in another order.
    /// </summary>
    private static void AddDeclarationSwap(string source, Stmt first, Stmt second, List<SourceVariant> variants)
    {
        if (first.Compound || second.Compound)
        {
            return;
        }

        if (!TryDeclaration(source, first, out string typeA, out string nameA)
            || !TryDeclaration(source, second, out string typeB, out string nameB))
        {
            return;
        }

        if (!string.Equals(typeA, typeB, StringComparison.Ordinal))
        {
            return;
        }

        string a = source[first.Start..first.End];
        string b = source[second.Start..second.End];
        string text = source[..first.Start] + b + Gap(source, first.End, second.Start) + a + source[second.End..];
        variants.Add(new SourceVariant
        {
            Id = $"swap-decl-{first.Line}-{second.Line}",
            Kind = "declaration-swap",
            Line = first.Line,
            Description = $"declare {nameB} before {nameA}",
            Text = text,
        });
    }

    /// <summary>
    /// Swap the two operands of a commutative operator. The operands are the spans between adjacent
    /// operators at the same nesting depth, so precedence inside each span is untouched: in
    /// <c>a + b * c</c> the operands of <c>+</c> are <c>a</c> and <c>b * c</c>, and the result is
    /// <c>b * c + a</c>, which means the same thing.
    /// </summary>
    private static void AddOperandSwaps(string source, Stmt stmt, List<SourceVariant> variants)
    {
        if (stmt.Compound)
        {
            return;
        }

        string text = source[stmt.Start..stmt.End];
        var operators = TopLevelOperators(text);

        // Everything up to the first '=' is the declarator, not an expression: in 'int *p = &a;' the
        // '*' is how the pointer is declared and the '&' is unary, and neither is an operator with
        // two operands to swap.
        int expressionStart = ReturnPrefixLength(text);
        if (expressionStart < 0)
        {
            expressionStart = 0;
        }

        var assignment = operators.FirstOrDefault(op => op.Symbol == "=");
        if (assignment.Symbol is not null && assignment.Index + 1 > expressionStart)
        {
            expressionStart = assignment.Index + 1;
        }

        operators = operators.Where(op => op.Index >= expressionStart).ToList();
        for (int i = 0; i < operators.Count; i++)
        {
            var (index, symbol) = operators[i];
            if (!Commutative.Contains(symbol))
            {
                continue;
            }

            // An operand reaches up to the next operator that binds at least as loosely, so a
            // tighter one stays inside it: the right operand of '+' in 'a + b * c' is 'b * c', not
            // 'b'. Cutting at the next operator regardless of precedence turned that expression into
            // 'b + a* c', which does not mean the same thing at all.
            int precedence = Precedence(symbol);

            int leftStart = expressionStart;
            for (int j = i - 1; j >= 0; j--)
            {
                if (Precedence(operators[j].Symbol) <= precedence)
                {
                    leftStart = operators[j].Index + operators[j].Symbol.Length;
                    break;
                }
            }

            int rightEnd = ExpressionEnd(text);
            for (int j = i + 1; j < operators.Count; j++)
            {
                if (Precedence(operators[j].Symbol) <= precedence)
                {
                    rightEnd = operators[j].Index;
                    break;
                }
            }

            if (leftStart < 0 || rightEnd <= index + symbol.Length)
            {
                continue;
            }

            string left = text[leftStart..index].Trim();
            string right = text[(index + symbol.Length)..rightEnd].Trim();
            if (left.Length == 0 || right.Length == 0 || left == right)
            {
                continue;
            }

            // The span boundaries sit right against the neighbouring tokens, so the space around the
            // operator is rebuilt rather than inherited: 'a +' + 'c' would otherwise read as 'a +c'.
            string replacement = text[..leftStart].TrimEnd() + " " + right + " " + symbol + " " + left + text[rightEnd..];
            int line = stmt.Line + text[..index].Count(c => c == '\n');
            variants.Add(new SourceVariant
            {
                Id = $"swap-ops-{line}-{leftStart}",
                Kind = "operand-swap",
                Line = line,
                Description = $"swap the operands of '{symbol}' on line {line}: {left} {symbol} {right}",
                Text = source[..stmt.Start] + replacement + source[stmt.End..],
            });
        }
    }

    /// <summary><c>i++;</c>, <c>++i;</c> and <c>i += 1;</c> are the same statement written three ways.</summary>
    private static void AddIncrementForms(string source, Stmt stmt, List<SourceVariant> variants)
    {
        if (stmt.Compound)
        {
            return;
        }

        string text = source[stmt.Start..stmt.End];
        string body = text.TrimEnd();
        if (!body.EndsWith(';'))
        {
            return;
        }

        body = body[..^1].Trim();
        string indent = text[..(text.Length - text.TrimStart().Length)];
        string? rewritten = null;
        if (body.EndsWith("++", StringComparison.Ordinal) && body.Length > 2 && IsIdentifier(body[..^2]))
        {
            rewritten = "++" + body[..^2];
        }
        else if (body.StartsWith("++", StringComparison.Ordinal) && body.Length > 2 && IsIdentifier(body[2..]))
        {
            rewritten = body[2..] + " += 1";
        }
        else if (body.EndsWith(" += 1", StringComparison.Ordinal) && body.Length > 5 && IsIdentifier(body[..^5]))
        {
            rewritten = body[..^5] + "++";
        }

        if (rewritten is null)
        {
            return;
        }

        variants.Add(new SourceVariant
        {
            Id = $"rewrite-incr-{stmt.Line}",
            Kind = "increment-form",
            Line = stmt.Line,
            Description = $"write '{body}' as '{rewritten}' on line {stmt.Line}",
            Text = source[..stmt.Start] + indent + rewritten + ";" + source[stmt.End..],
        });
    }

    /// <summary>
    /// Invert an <c>if</c>/<c>else</c>: negate the condition and swap the two branches. The condition
    /// is still evaluated once and in the same place, so this cannot change what the program does.
    /// </summary>
    private static void AddBranchInversions(string source, List<SourceVariant> variants)
    {
        for (int i = 0; i + 1 < source.Length; i++)
        {
            if (source[i] != 'i' || source[i + 1] != 'f' || !AtTokenStart(source, i))
            {
                continue;
            }

            int head = SkipSpace(source, i + 2);
            if (head >= source.Length || source[head] != '(')
            {
                continue;
            }

            int close = Match(source, head);
            if (close < 0)
            {
                continue;
            }

            string condition = source[(head + 1)..close];
            if (condition.TrimStart().StartsWith("!(", StringComparison.Ordinal))
            {
                continue;
            }
            int thenStart = SkipSpace(source, close + 1);
            if (thenStart >= source.Length || source[thenStart] != '{')
            {
                continue;
            }

            int thenEnd = Match(source, thenStart);
            if (thenEnd < 0)
            {
                continue;
            }

            int elseAt = SkipSpace(source, thenEnd + 1);
            if (elseAt + 4 >= source.Length || !string.Equals(source[elseAt..(elseAt + 4)], "else", StringComparison.Ordinal))
            {
                continue;
            }

            int elseBlock = SkipSpace(source, elseAt + 4);
            if (elseBlock >= source.Length || source[elseBlock] != '{')
            {
                continue;
            }

            int elseEnd = Match(source, elseBlock);
            if (elseEnd < 0)
            {
                continue;
            }

            string then = source[(thenStart + 1)..thenEnd];
            string otherwise = source[(elseBlock + 1)..elseEnd];
            int line = source[..i].Count(c => c == '\n') + 1;

            // Doubling the parentheses keeps the negated condition unambiguous whatever was in it.
            string text = source[..i]
                + $"if (!({condition})) {{{otherwise}}} else {{{then}}}"
                + source[(elseEnd + 1)..];

            variants.Add(new SourceVariant
            {
                Id = $"invert-if-{line}",
                Kind = "branch-inversion",
                Line = line,
                Description = $"invert the if/else on line {line}",
                Text = text,
            });
        }
    }

    // ----------------------------------------------------------------------------- scanning

    /// <summary>Splits the source into statements, skipping comments, strings and preprocessor lines.</summary>
    private static List<Stmt> Scan(string source)
    {
        var statements = new List<Stmt>();
        int depth = 0;
        int paren = 0;
        int start = -1;
        int line = 1;
        bool compound = false;
        bool lineStart = true;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '\n')
            {
                line++;
                lineStart = true;
                continue;
            }

            if (c is ' ' or '\t' or '\r')
            {
                continue;
            }

            if (c == '#' && lineStart)
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                line++;
                lineStart = true;
                continue;
            }

            lineStart = false;

            if (c == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }

                    line++;
                    lineStart = true;
                    continue;
                }

                if (source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        if (source[i] == '\n')
                        {
                            line++;
                        }

                        i++;
                    }

                    i++;
                    continue;
                }
            }

            if (c is '"' or '\'')
            {
                char quote = c;
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                continue;
            }

            if (c == '(')
            {
                paren++;
                start = start < 0 ? -1 : start;
                continue;
            }

            if (c == ')')
            {
                paren = Math.Max(0, paren - 1);
                continue;
            }

            if (c == '{')
            {
                depth++;

                // A brace only makes a statement compound when one is open: the brace that opens a
                // function body belongs to no statement, and calling everything after it compound
                // silently disables the whole generator.
                if (start >= 0)
                {
                    compound = true;
                }

                start = -1;
                continue;
            }

            if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
                compound = false;
                start = -1;
                continue;
            }

            if (c == ';' && paren == 0)
            {
                if (start >= 0)
                {
                    statements.Add(new Stmt(start, i + 1, line - source[start..(i + 1)].Count(ch => ch == '\n'), compound));
                }

                start = -1;
                compound = false;
                continue;
            }

            if (start < 0)
            {
                start = i;

                // Compound is a property of the statement being accumulated, so it starts false with
                // it: a brace seen before this one belongs to whatever came before, not to this.
                compound = false;
            }
        }

        return statements;
    }

    /// <summary>The binary operators in one statement that are not inside parentheses.</summary>
    private static List<(int Index, string Symbol)> TopLevelOperators(string text)
    {
        var found = new List<(int, string)>();
        int paren = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '"' or '\'')
            {
                char quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                continue;
            }

            if (c == '(')
            {
                paren++;
                continue;
            }

            if (c == ')')
            {
                paren = Math.Max(0, paren - 1);
                continue;
            }

            if (paren > 0)
            {
                continue;
            }

            // Longest first: '==' is not '=', '++' is not '+' and '+=' is neither. Reading the short
            // one left a stray '=' behind the comparison and turned 'i++' into 'i + + i'.
            string? symbol = LongestOperatorAt(text, i);
            if (symbol is null)
            {
                continue;
            }

            // A '+' or '&' with nothing value-shaped to its left is unary, not an operator between two
            // operands, so it is not a boundary either: '-a + b' keeps '-a' as the left operand.
            if (PrecededByValue(text, i))
            {
                found.Add((i, symbol));
            }

            i += symbol.Length - 1;
        }

        return found;
    }

    /// <summary>The longest operator starting here, so two-character operators are read as one token.</summary>
    private static string? LongestOperatorAt(string text, int index)
    {
        string? best = null;
        foreach (string op in Operators)
        {
            if (string.CompareOrdinal(text, index, op, 0, op.Length) == 0 && (best is null || op.Length > best.Length))
            {
                best = op;
            }
        }

        return best;
    }

    private static bool PrecededByValue(string text, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or ')' or ']');
    }

    /// <summary>
    /// How tightly an operator binds, as its position in <see cref="Operators"/>: the list is ordered
    /// loosest first, so a smaller number is a looser operator. Only the ordering is used.
    /// </summary>
    private static int Precedence(string symbol) => Array.IndexOf(Operators, symbol);

    /// <summary>Where the returned expression starts, so 'return a + b' is not rewritten as 'b + return a'.</summary>
    private static int ReturnPrefixLength(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (string.CompareOrdinal(text, i, "return", 0, 6) != 0 || i + 6 >= text.Length || !char.IsWhiteSpace(text[i + 6]))
        {
            return -1;
        }

        int after = i + 6;
        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            after++;
        }

        return after;
    }

    private static int ExpressionEnd(string text)
    {
        int i = text.Length - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || text[i] == ';'))
        {
            i--;
        }

        return i + 1;
    }

    private static bool TryDeclaration(string source, Stmt stmt, out string type, out string name)
    {
        type = string.Empty;
        name = string.Empty;
        string text = source[stmt.Start..stmt.End].Trim();
        if (!text.EndsWith(';') || text.Contains('=') || text.Contains('(') || text.Contains('[') || text.Contains(','))
        {
            return false;
        }

        string body = text[..^1].Trim();
        int split = body.LastIndexOf(' ');
        if (split <= 0)
        {
            return false;
        }

        type = body[..split].Trim();
        name = body[(split + 1)..].Trim();
        return IsIdentifier(name)
            && type.Length > 0
            && !ControlFlow.Contains(type)
            && type.All(c => char.IsLetterOrDigit(c) || c is '_' or ' ' or '*');
    }

    private static bool StartsWithControlFlow(string statement)
    {
        string text = statement.Trim();
        foreach (string keyword in ControlFlow)
        {
            if (text.StartsWith(keyword, StringComparison.Ordinal)
                && (text.Length == keyword.Length || !(char.IsLetterOrDigit(text[keyword.Length]) || text[keyword.Length] == '_')))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Identifiers(string text)
        => IdentifiersRegex().Matches(text).Select(m => m.Value).Where(id => !Keywords.Contains(id)).ToList();

    private static bool Disjoint(List<string> left, List<string> right)
        => !left.Any(id => right.Contains(id, StringComparer.Ordinal));

    private static string Gap(string source, int end, int start) => source[end..start];

    private static bool IsIdentifier(string text)
        => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool AtTokenStart(string source, int index)
        => index == 0 || !(char.IsLetterOrDigit(source[index - 1]) || source[index - 1] == '_');

    private static int SkipSpace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>The index of the closer matching the bracket at <paramref name="index"/>.</summary>
    private static int Match(string source, int index)
    {
        char open = source[index];
        char close = open switch { '(' => ')', '{' => '}', _ => '\0' };
        if (close == '\0')
        {
            return -1;
        }

        int depth = 0;
        for (int i = index; i < source.Length; i++)
        {
            char c = source[i];
            if (c is '"' or '\'')
            {
                char quote = c;
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial System.Text.RegularExpressions.Regex IdentifiersRegex();
}
