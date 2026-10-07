using System.Text;

namespace Recon.Build;

/// <summary>
/// Reads the Make-syntax dependency files GCC writes (<c>-MMD -MF x.d</c>). They are the only record
/// of which headers a unit actually used, which is what makes the cache correct when a header changes
/// instead of conservative.
/// </summary>
public static class DepFile
{
    /// <summary>Dependencies of every rule in the file, in the order they appear, without duplicates.</summary>
    public static List<string> Parse(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rule in Rules(JoinContinuations(text)))
        {
            int colon = IndexOfUnescaped(rule, ':');
            if (colon < 0)
            {
                continue;
            }

            // Everything before the first colon is the target (or targets); a rule with several
            // targets repeats the same dependency list, which the deduplication below folds away.
            foreach (string token in Tokens(rule[(colon + 1)..]))
            {
                if (seen.Add(token))
                {
                    result.Add(token);
                }
            }
        }

        return result;
    }

    /// <summary>Line continuations are a backslash-newline pair, which makes one logical line.</summary>
    private static string JoinContinuations(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
            {
                i += text[i + 1] == '\r' ? 2 : 1;
                builder.Append(' ');
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    /// <summary>Non-empty, non-comment lines; each may still hold several rules separated by newlines.</summary>
    private static IEnumerable<string> Rules(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim('\r', ' ', '\t');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            yield return trimmed;
        }
    }

    /// <summary>Splits on whitespace, honouring backslash escapes: <c>a\ b</c> is one token.</summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '$')
            {
                current.Append('$');
                i++;
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static int IndexOfUnescaped(string text, char needle)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == needle)
            {
                return i;
            }
        }

        return -1;
    }
}
