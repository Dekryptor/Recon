using Recon.Toml;

namespace Recon.Config;

/// <summary>Where one toolchain install lives on this machine.</summary>
public sealed class LocalToolchain
{
    /// <summary>Install root. May be absolute: <c>local.toml</c> is the one file allowed to hold absolute paths.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Run the tools through Wine instead of natively.</summary>
    public bool Wine { get; set; }

    /// <summary>
    /// Compiler to use instead of the profile's <c>compile.exe</c>. Absolute, or relative to
    /// <see cref="Root"/>. This is how a Linux host points a MinGW profile at its cross compiler
    /// without a second profile for the same GCC version.
    /// </summary>
    public string? Cc { get; set; }

    /// <summary>Linker to use instead of the profile's <c>link.exe</c>. Same rules as <see cref="Cc"/>.</summary>
    public string? Link { get; set; }

    public Dictionary<string, string> Env { get; set; } = [];
}

/// <summary>
/// Machine-specific paths, written by the user and never committed. Keeping them here is what
/// makes "a project repository contains only your own source and config" true by construction.
/// </summary>
public sealed class LocalConfig
{
    public int SchemaVersion { get; set; }

    /// <summary>Directory that holds the input files named in <c>project.toml</c>.</summary>
    public string InputsDir { get; set; } = "inputs";

    public Dictionary<string, LocalToolchain> Toolchains { get; set; } = new(StringComparer.Ordinal);

    public string FilePath { get; set; } = string.Empty;

    public string RootDirectory { get; set; } = string.Empty;

    public bool IsPresent => !string.IsNullOrEmpty(FilePath);

    public string InputDirectory
        => Path.IsPathRooted(InputsDir) ? InputsDir : Path.GetFullPath(Path.Combine(RootDirectory, InputsDir));

    public static LocalConfig Empty(string projectRoot)
        => new() { RootDirectory = projectRoot };

    public static LocalConfig Load(string path, Diagnostics diagnostics)
    {
        // local.toml is optional: without it, defaults apply and inputs live next to the project.
        if (!File.Exists(path))
        {
            return Empty(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        }

        var document = TomlLoader.LoadDocument(path, diagnostics);
        return Load(document, path, diagnostics);
    }

    public static LocalConfig Load(TomlDocument document, string path, Diagnostics diagnostics)
    {
        var root = TableScope.Root(document, diagnostics);
        var config = new LocalConfig
        {
            FilePath = Path.GetFullPath(path),
            RootDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
        };

        config.SchemaVersion = (int)root.RequireInteger("schema_version");
        SchemaVersions.CheckSchemaVersion(config.SchemaVersion, path, root.Line, "schema_version", diagnostics);

        var inputs = root.Child("inputs");
        if (inputs.Present)
        {
            config.InputsDir = inputs.String("dir", config.InputsDir) ?? config.InputsDir;
            inputs.RejectUnknownKeys();
        }

        var toolchains = root.Child("toolchain");
        foreach (var name in toolchains.ChildNames())
        {
            // [toolchain.<id>]: the id is the table key itself, so it is read by name, not by field.
            var table = toolchains.Child(name);
            var identifier = name;
            var install = new LocalToolchain
            {
                Root = table.String("root", string.Empty) ?? string.Empty,
                Cc = table.String("cc"),
                Link = table.String("link"),
            };
            install.Wine = table.Bool("wine", false) ?? false;
            var env = table.Child("env");
            if (env.Present)
            {
                foreach (var (key, value) in env.Pairs())
                {
                    if (value is TomlString text)
                    {
                        install.Env[key] = text.Value;
                    }
                    else
                    {
                        diagnostics.Error(path, value.Line, $"{table.Path}.env.{key}", $"expected a string, found {value.TypeName}");
                    }
                }

                env.RejectUnknownKeys();
            }

            table.RejectUnknownKeys();

            if (string.IsNullOrEmpty(identifier))
            {
                diagnostics.Error(path, table.Line, "toolchain", "expected [toolchain.<id>] with a non-empty id");
                continue;
            }

            // `root` used to be required, because every profile names its tools relative to an
            // install. That is not true of a Unix toolchain, which has no install directory at all:
            // gcc is on PATH, or `cc` names it outright. So an entry may be as small as an empty
            // `[toolchain.gcc-14-elf64]`, which means "this profile's tools, found on PATH". The
            // schema still rejects keys that are not root, wine, env, cc or link, so a typo is
            // reported rather than silently ignored.

            config.Toolchains[identifier] = install;
        }

        toolchains.RejectUnknownKeys();
        root.RejectUnknownKeys();
        return config;
    }

}
