using Recon.Config;
using Recon.Toml;

namespace Recon.Toolchains;

/// <summary>
/// Loads every profile found in the project's profile directories and resolves <c>extends</c> chains.
/// Resolution rules: single inheritance, depth limit 4, tables merge key by key, arrays replace.
/// </summary>
public sealed class ToolchainRegistry
{
    public const int MaxInheritanceDepth = 4;

    private readonly Dictionary<string, ToolchainProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolchainProfile> _resolved = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _diagnostics = [];

    /// <summary>An empty registry: loading a project must not require any profiles to exist.</summary>
    public static ToolchainRegistry Empty() => new();

    public IReadOnlyDictionary<string, ToolchainProfile> Raw => _profiles;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Loads every <c>*.toml</c> in the given directories. Later directories do not override earlier ids.</summary>
    public static ToolchainRegistry Load(IEnumerable<string> directories, Diagnostics diagnostics)
    {
        var registry = new ToolchainRegistry();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.toml", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var profile = ToolchainProfile.Load(file, diagnostics);
                if (string.IsNullOrEmpty(profile.Id))
                {
                    continue;
                }

                if (registry._profiles.ContainsKey(profile.Id))
                {
                    diagnostics.Error(file, 1, "id", $"duplicate profile id \"{profile.Id}\"");
                    continue;
                }

                registry._profiles[profile.Id] = profile;
            }
        }

        // A project that has not been given a toolchains directory still gets the profiles that ship
        // with the tool: `paths.profiles` adds to them, it does not replace them. Only when the
        // project's own directories yielded nothing, though — a project that copied the shipped
        // profiles in (which is what `recon init` does) must not be told they are duplicates.
        if (registry._profiles.Count == 0)
        {
            foreach (var (fileName, content) in BuiltInProfiles.All())
            {
                var document = TomlParser.Parse(content, fileName);
                var profile = ToolchainProfile.Load(document, fileName, diagnostics);
                if (string.IsNullOrEmpty(profile.Id))
                {
                    continue;
                }

                registry._profiles[profile.Id] = profile;
            }
        }

        registry.ResolveAll(diagnostics);
        return registry;
    }

    public ToolchainProfile? Get(string id) => _profiles.TryGetValue(id, out var profile) ? profile : null;

    public ToolchainProfile? GetResolved(string id)
    {
        if (_resolved.TryGetValue(id, out var resolved))
        {
            return resolved;
        }

        if (!_profiles.TryGetValue(id, out var profile))
        {
            return null;
        }

        var chain = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var result = Resolve(profile, chain, visiting, depth: 0);
        if (result is not null)
        {
            _resolved[id] = result;
        }

        return result;
    }

    public IEnumerable<ToolchainProfile> All(string? format = null, string? arch = null)
    {
        foreach (var id in _profiles.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var resolved = GetResolved(id);
            if (resolved is null || resolved.IsAbstract)
            {
                continue;
            }

            if (format is not null && arch is not null && !resolved.AppliesTo(format, arch))
            {
                continue;
            }

            yield return resolved;
        }
    }

    public IEnumerable<string> KnownIds => _profiles.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>Verifies that a unit's toolchain reference exists and is usable.</summary>
    public void ValidateReference(string? id, string file, int line, string keyPath, Diagnostics diagnostics)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        if (!_profiles.TryGetValue(id, out var profile))
        {
            diagnostics.Error(file, line, keyPath, $"unknown profile \"{id}\" (known: {string.Join(", ", KnownIds)})");
            return;
        }

        if (profile.IsAbstract)
        {
            diagnostics.Error(file, line, keyPath, $"profile \"{id}\" is abstract and cannot be used as a toolchain");
        }
    }

    private void ResolveAll(Diagnostics diagnostics)
    {
        foreach (var id in _profiles.Keys.ToList())
        {
            var resolved = GetResolved(id);
            if (resolved is null || resolved.IsAbstract)
            {
                continue;
            }

            if (resolved.Targets.Count == 0)
            {
                Error(resolved, "targets", "the resolved profile has no [[targets]] entry, not even from its parents");
            }
        }

        foreach (var diagnostic in _diagnostics)
        {
            diagnostics.Add(diagnostic);
        }
    }

    private ToolchainProfile? Resolve(ToolchainProfile profile, List<string> chain, HashSet<string> visiting, int depth)
    {
        if (depth > MaxInheritanceDepth)
        {
            Error(profile, "extends", $"inheritance chain is deeper than {MaxInheritanceDepth}");
            return null;
        }

        if (!visiting.Add(profile.Id))
        {
            Error(profile, "extends", "inheritance cycle detected");
            return null;
        }

        if (profile.Extends is null)
        {
            var clone = profile.Clone();
            clone.InheritanceChain = [profile.Id];
            return clone;
        }

        if (!_profiles.TryGetValue(profile.Extends, out var parent))
        {
            Error(profile, "extends", $"unknown parent profile \"{profile.Extends}\"");
            return null;
        }

        var resolvedParent = Resolve(parent, chain, visiting, depth + 1);
        if (resolvedParent is null)
        {
            return null;
        }

        var merged = Merge(resolvedParent, profile);
        chain.Add(profile.Id);
        return merged;
    }

    private static ToolchainProfile Merge(ToolchainProfile parent, ToolchainProfile child)
    {
        var result = parent.Clone();
        result.Id = child.Id;
        result.DisplayName = child.DisplayName;
        result.Family = child.Family;
        result.Extends = child.Extends;
        result.IsAbstract = child.IsAbstract;
        result.FilePath = child.FilePath;
        result.InheritanceChain = [.. parent.InheritanceChain, child.Id];

        // Only keys the child set itself override the parent; everything else is inherited.
        child.ApplyExplicitOverridesTo(result);
        return result;
    }

    private void Error(ToolchainProfile profile, string key, string message)
        => _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, profile.FilePath, 1, key, $"{profile.Id}: {message}"));
}
