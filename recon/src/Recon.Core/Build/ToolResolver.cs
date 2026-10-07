using System.Security.Cryptography;
using System.Text;
using Recon.Config;
using Recon.Toolchains;

namespace Recon.Build;

/// <summary>
/// Turns a profile into the commands this machine would run. A profile names its tools relative to
/// an install root (<c>bin/gcc.exe</c>); <c>local.toml</c> says where that root is, and may override
/// the two executables outright, which is how a cross compiler in <c>/usr/bin</c> is used without
/// inventing a second profile for the same GCC version.
/// </summary>
public static class ToolResolver
{
    /// <summary>Resolves the compiler and linker of one profile against one machine.</summary>
    public static BuildToolchainUse Resolve(ToolchainProfile profile, LocalToolchain? install, string projectRoot)
    {
        var use = new BuildToolchainUse
        {
            Id = profile.Id,
            Family = profile.Family,
            Root = string.IsNullOrEmpty(install?.Root) ? null : install!.Root,
            Wine = install?.Wine ?? false,
            Fingerprint = Fingerprint(profile),
            ObjectExtension = string.Equals(profile.Family, "msvc", StringComparison.OrdinalIgnoreCase) ? ".obj" : ".o",
        };

        if (install is not null)
        {
            foreach (var (key, value) in install.Env)
            {
                use.Env[key] = value;
            }
        }

        foreach (var (key, value) in profile.Compile?.Env ?? [])
        {
            // The profile writes its environment in terms of the install, so {root} is substituted
            // here and a machine-local value of the same name still wins.
            use.Env.TryAdd(key, Substitute(value, use.Root ?? projectRoot));
        }

        string? cc = profile.Compile?.Exe;
        string? link = profile.Link?.Exe;

        if (profile.Compile is null)
        {
            use.CcProblem = $"profile \"{profile.Id}\" has no [compile] section: it cannot build anything";
        }
        else
        {
            use.Cc = Locate(install?.Cc ?? cc, install, projectRoot);
            use.CcProblem = Check(use.Cc, "compiler", profile.Id);
        }

        if (profile.Link is null)
        {
            use.LinkProblem = $"profile \"{profile.Id}\" has no [link] section: it cannot link an image";
        }
        else
        {
            use.Link = Locate(install?.Link ?? link, install, projectRoot);
            use.LinkProblem = Check(use.Link, "linker", profile.Id);
        }

        return use;
    }

    /// <summary>
    /// Resolves an executable name: absolute stays as it is, relative is joined to the install root,
    /// and a bare name with no install root (or none that holds it) is looked up on PATH. That last
    /// case is the normal one on a Unix box, where gcc, clang and ld live in
    /// <c>/usr/bin</c> and there is no toolchain directory to point at.
    /// </summary>
    private static string Locate(string? exe, LocalToolchain? install, string projectRoot)
    {
        if (string.IsNullOrEmpty(exe))
        {
            return string.Empty;
        }

        string path = Substitute(exe, install?.Root ?? projectRoot);
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        string root = install?.Root ?? projectRoot;
        string joined = Path.GetFullPath(Path.Combine(root, path));

        bool bareName = !path.Contains('/') && !path.Contains('\\');
        if (bareName && !File.Exists(joined))
        {
            string? onPath = FindOnPath(path);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        return joined;
    }

    /// <summary>Finds an executable on PATH, or returns null. Returns null for a name that is not found
    /// rather than a guess, so the caller can report the path it expected.</summary>
    private static string? FindOnPath(string name)
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// A missing tool is not fatal to planning: the plan, the Ninja file and the manifest all stay
    /// useful, and the message appears when the command is about to run (or in <c>toolchain check</c>).
    /// </summary>
    private static string? Check(string path, string what, string profileId)
    {
        if (string.IsNullOrEmpty(path))
        {
            return $"toolchain \"{profileId}\" does not name a {what} in its profile";
        }

        return File.Exists(path)
            ? null
            : $"{what} of toolchain \"{profileId}\" not found: {path} (set root, cc or link for it in local.toml)";
    }

    /// <summary>{root} and {id} substitutions used by profile tool paths and environment values.</summary>
    public static string Substitute(string text, string root)
        => text.Replace("{root}", root, StringComparison.Ordinal).Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>
    /// Hash of everything in the profile that changes what a compiler produces. The install's own
    /// files are fingerprinted separately by the planner, so upgrading a compiler invalidates objects.
    /// </summary>
    public static string Fingerprint(ToolchainProfile profile)
    {
        var text = new StringBuilder();
        text.Append("recon-toolchain-v1\n");
        text.Append("id=").Append(profile.Id).Append('\n');
        text.Append("family=").Append(profile.Family).Append('\n');
        foreach (var target in profile.Targets)
        {
            text.Append("target=").Append(target.Format).Append('/').Append(target.Arch).Append('\n');
        }

        if (profile.Compile is { } compile)
        {
            text.Append("cc=").Append(compile.Exe).Append('\n');
            text.Append("cc.flags=").Append(string.Join(' ', compile.DefaultFlags)).Append('\n');
            text.Append("cc.include=").Append(compile.IncludeFlag).Append('\n');
            text.Append("cc.define=").Append(compile.DefineFlag).Append('\n');
            text.Append("cc.output=").Append(compile.OutputFlag).Append('\n');
            text.Append("cc.depfile=").Append(compile.DepfileFlag).Append('\n');
            foreach (var (key, value) in compile.Env.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.Append("cc.env.").Append(key).Append('=').Append(value).Append('\n');
            }
        }

        if (profile.Link is { } link)
        {
            text.Append("link=").Append(link.Exe).Append('\n');
            text.Append("link.flags=").Append(string.Join(' ', link.DefaultFlags)).Append('\n');
            text.Append("link.output=").Append(link.OutputFlag).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Identity of an installed tool: its path, size and modification time. Hashing the binary itself
    /// on every build would cost more than the build's own bookkeeping, and an upgrade moves both.
    /// </summary>
    public static string ToolIdentity(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return "missing:" + path;
            }

            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return "unreadable:" + path;
        }
    }
}
