using Recon.Pe;

namespace Recon.Config;

/// <summary>
/// One entry of a unit's <c>covers</c>: what it means, whichever document it is resolved against.
/// The progress report resolves covers against a comparison (the original's side of it) and the
/// delinker resolves them against the inventory, and the two must agree — a cover that counts as
/// progress must be the same cover that decides which functions the rebuild is allowed to provide.
/// </summary>
public static class Covers
{
    /// <summary>Something a cover can name: a function, or a data symbol.</summary>
    public readonly record struct CoverCandidate(string? Name, uint Rva, uint Size)
    {
        /// <summary>Other names this thing is known by — folded aliases, for instance.</summary>
        public IReadOnlyList<string> Aliases { get; init; } = [];
    }

    /// <summary>
    /// The candidates a cover resolves to, or none. A symbol is tried as written, then against the
    /// demangled spelling of each name, then as an unambiguous substring, and only then given up on:
    /// a cover is written by a human against whatever name they saw, and the point of a cover is to
    /// be resolvable, not to be exact. A <c>*</c> in a symbol is a wildcard, and one glob may claim several functions. A range cover claims everything it overlaps.
    ///
    /// Pass everything there is: a candidate list of one would make every substring unambiguous, so
    /// callers hand over every name they know and ask which of them a cover claims.
    /// </summary>
    public static List<int> Resolve(CoverSpec cover, IReadOnlyList<CoverCandidate> candidates)
    {
        if (cover.Symbol is { Length: > 0 } symbol)
        {
            var byName = Where(candidates, candidate => NameIs(candidate.Name, symbol));
            if (byName.Count > 0)
            {
                return byName;
            }

            var byDemangled = Where(
                candidates,
                candidate => NameIs(candidate.Name, symbol, demangle: true) || candidate.Aliases.Any(alias => NameIs(alias, symbol, demangle: true)));
            if (byDemangled.Count > 0)
            {
                return byDemangled;
            }

            // `Shape::*` and `*_reader`: one cover may claim several functions, which is what the
            // star is asking for and the one thing the substring fallback below refuses to do.
            if (symbol.Contains('*'))
            {
                return Where(candidates, candidate => Glob(symbol, candidate.Name) || candidate.Aliases.Any(alias => Glob(symbol, alias)));
            }

            // A cover written against a mangled name, or the tail of one: the last resort, and only
            // when it is unambiguous — two functions matching one cover is no answer at all.
            var partial = Where(candidates, candidate => Contains(candidate.Name, symbol) || Contains(symbol, candidate.Name));
            return partial.Count == 1 ? partial : [];
        }

        if (cover.Rva is { } rva)
        {
            uint size = cover.Size ?? 0;
            uint end = rva + Math.Max(size, 1u);
            return Where(candidates, candidate =>
            {
                uint candidateEnd = candidate.Rva + Math.Max(candidate.Size, 1u);
                return candidate.Rva < end && rva < candidateEnd;
            });
        }

        return [];
    }

    private static bool NameIs(string? name, string symbol, bool demangle = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return string.Equals(demangle ? Demangler.Demangle(name).Text : name, symbol, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name against a pattern with <c>*</c> wildcards. A star at the start or the end is not
    /// pinned, so <c>*_reader</c> matches <c>log_reader</c>; a star in the middle keeps the order of
    /// the parts around it.
    /// </summary>
    private static bool Glob(string pattern, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string[] parts = pattern.Split('*');
        int at = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0)
            {
                continue;
            }

            bool pinnedToStart = index == 0 && pattern[0] != '*';
            bool pinnedToEnd = index == parts.Length - 1 && pattern[^1] != '*';
            int found = pinnedToEnd
                ? name.LastIndexOf(parts[index], StringComparison.Ordinal)
                : name.IndexOf(parts[index], at, StringComparison.Ordinal);
            if (found < at
                || (pinnedToStart && found != 0)
                || (pinnedToEnd && found + parts[index].Length != name.Length))
            {
                return false;
            }

            at = found + parts[index].Length;
        }

        return true;
    }

    /// <summary>Either way round: a cover may be the tail of a name, or a name the tail of a cover.</summary>
    private static bool Contains(string? text, string? part)
        => !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(part) && text.Contains(part, StringComparison.Ordinal);

    private static List<int> Where(IReadOnlyList<CoverCandidate> candidates, Func<CoverCandidate, bool> predicate)
    {
        var result = new List<int>();
        for (int index = 0; index < candidates.Count; index++)
        {
            if (predicate(candidates[index]))
            {
                result.Add(index);
            }
        }

        return result;
    }
}
