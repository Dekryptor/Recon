using System.Text.Json.Serialization;
using Recon.Build;
using Recon.Config;
using Recon.Pe;
using Recon.Project;
using Recon.Toolchains;

namespace Recon.Verify;

public sealed class InputCheck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("expected_sha256")]
    public string Expected { get; set; } = string.Empty;

    [JsonPropertyName("actual_sha256")]
    public string? Actual { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class ToolchainCheck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("root_exists")]
    public bool RootExists { get; set; }

    /// <summary>Compiler the profile resolves to on this machine, whether from an install or from PATH.</summary>
    [JsonPropertyName("cc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cc { get; set; }

    /// <summary>Linker the profile resolves to on this machine.</summary>
    [JsonPropertyName("link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Link { get; set; }

    /// <summary>Why the compiler cannot be run, or null when it can.</summary>
    [JsonPropertyName("cc_problem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CcProblem { get; set; }

    /// <summary>Why the linker cannot be run, or null when it can.</summary>
    [JsonPropertyName("link_problem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkProblem { get; set; }

    [JsonPropertyName("files_checked")]
    public int FilesChecked { get; set; }

    [JsonPropertyName("files_missing")]
    public List<string> FilesMissing { get; set; } = [];

    [JsonPropertyName("files_mismatched")]
    public List<string> FilesMismatched { get; set; } = [];

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}

/// <summary>One unit's build inputs: the source it compiles and the toolchain that compiles it.</summary>
public sealed class UnitCheck
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("toolchain")]
    public string Toolchain { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

public sealed class VerifyReport
{
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("inputs")]
    public List<InputCheck> Inputs { get; set; } = [];

    [JsonPropertyName("debug_matches_binary")]
    public bool? DebugMatchesBinary { get; set; }

    [JsonPropertyName("toolchains")]
    public List<ToolchainCheck> Toolchains { get; set; } = [];

    /// <summary>The units' own inputs: what M3's build compiles, checked the same way as the binary inputs.</summary>
    [JsonPropertyName("units")]
    public List<UnitCheck> Units { get; set; } = [];

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];
}

/// <summary>
/// Checks that the files a project names are the files it means: existence and SHA-256, plus debug
/// info identity and toolchain installs. Never touches the network and never writes anything.
/// </summary>
public static class Verifier
{
    public static VerifyReport Verify(ProjectContext context, bool includeToolchains = true)
    {
        var report = new VerifyReport
        {
            Project = context.Project.Project.Name,
            Ok = true,
        };

        foreach (var input in context.Project.Inputs)
        {
            string path = context.ResolveInputPath(input);
            var check = new InputCheck
            {
                Id = input.Id,
                Role = input.Role,
                File = input.File,
                Path = path,
                Expected = input.Sha256,
            };

            if (!File.Exists(path))
            {
                check.Exists = false;
                check.Message = "file not found";
                check.Ok = false;
                report.Ok = false;
                report.Inputs.Add(check);
                continue;
            }

            check.Exists = true;
            check.Actual = PeImage.HashFile(path);
            check.Ok = string.Equals(check.Actual, check.Expected, StringComparison.OrdinalIgnoreCase);
            if (!check.Ok)
            {
                check.Message = "SHA-256 mismatch: the file is not the one this project was built against";
                report.Ok = false;
            }

            report.Inputs.Add(check);
        }

        // The binary's inputs are above; these are the build's. A unit whose source is missing is as
        // much a broken project as a missing original, and it is cheaper to say so here than halfway
        // through a build.
        //
        // A unit with no toolchain named is not broken: `recon build` asks the binary who made it
        // and uses that profile, which is the normal state of a project that has just been pointed
        // at a file. So verify asks the same question before calling anything wrong — otherwise it
        // reports FAILED on a project that builds, and anyone reading `ok` chases nothing.
        var detected = new Lazy<ToolchainProfile?>(() =>
        {
            var loaded = context.LoadImage();
            if (loaded.Image is null)
            {
                return null;
            }

            return ToolchainSelector.Choose(
                context.Registry, null, loaded.Image, context.LoadDebugInfo(loaded.Image), context.Dwarf, concreteOnly: true);
        });

        foreach (var unit in context.Project.Units)
        {
            string source = Path.GetFullPath(Path.Combine(context.Project.RootDirectory, unit.Source));
            string toolchainId = context.Project.ResolveToolchain(unit);
            var check = new UnitCheck
            {
                Name = unit.Name,
                Source = unit.Source,
                Toolchain = toolchainId,
                Exists = File.Exists(source),
                Ok = true,
            };

            var profile = toolchainId.Length == 0 ? null : context.Registry.GetResolved(toolchainId);
            if (profile is null)
            {
                profile = detected.Value;
                if (profile is not null)
                {
                    check.Toolchain = profile.Id;
                    check.Message = $"no toolchain named; recon build uses \"{profile.Id}\" - the profile this binary itself points at (set unit.toolchain to override)";
                }
            }

            if (!check.Exists)
            {
                check.Ok = false;
                check.Message = $"source not found: {unit.Source}";
            }
            else if (profile is null)
            {
                check.Ok = false;
                check.Message = toolchainId.Length == 0
                    ? "no toolchain named, and nothing in the binary names one either"
                    : $"unknown profile \"{toolchainId}\"";
            }
            else if (profile.Compile is null)
            {
                check.Ok = false;
                check.Message = $"profile \"{profile.Id}\" has no [compile] section";
            }

            if (!check.Ok)
            {
                report.Ok = false;
            }

            report.Units.Add(check);
        }

        var image = context.LoadImage();
        if (image.Image is null)
        {
            report.Ok = false;
            report.Messages.AddRange(image.Problems);
            return report;
        }

        var debug = context.LoadDebugInfo(image.Image);
        if (debug is not null)
        {
            var codeView = image.Image.Pe?.DebugEntries.FirstOrDefault(d => d.Type == 2 && d.PdbGuid is not null);
            if (debug.Kind == "pdb" && codeView?.PdbGuid is not null && debug.Guid is not null)
            {
                report.DebugMatchesBinary = debug.Guid == codeView.PdbGuid
                                            && (codeView.PdbAge is null || debug.Age is null || codeView.PdbAge == debug.Age);
                if (report.DebugMatchesBinary == false)
                {
                    report.Ok = false;
                    report.Messages.Add("debug info does not match the binary (GUID/age differ): the PDB belongs to another build");
                }
            }

            foreach (var problem in debug.Problems)
            {
                report.Messages.Add($"{debug.Kind}: {problem}");
            }
        }

        if (includeToolchains)
        {
            foreach (var (id, install) in context.Local.Toolchains.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var profile = context.Registry.GetResolved(id);
                var use = profile is null ? null : ToolResolver.Resolve(profile, install, context.Project.RootDirectory);
                var check = new ToolchainCheck
                {
                    Id = id,
                    Root = install.Root,
                    Cc = use?.Cc,
                    Link = use?.Link,
                    CcProblem = use?.CcProblem,
                    LinkProblem = use?.LinkProblem,
                };

                check.RootExists = !string.IsNullOrEmpty(install.Root) && Directory.Exists(install.Root);

                // A toolchain is usable when its compiler and linker can be found. An install root is
                // only required by a profile that declares one: a Unix toolchain has no install
                // directory at all and its compiler is on PATH, so demanding a root here would call
                // every Linux toolchain broken.
                if (profile is null)
                {
                    check.Ok = false;
                    report.Messages.Add($"toolchain {id}: unknown profile (known: {string.Join(", ", context.Registry.KnownIds)})");
                    report.Toolchains.Add(check);
                    continue;
                }

                string? problem = use!.CcProblem ?? use.LinkProblem;
                if (problem is not null)
                {
                    check.Ok = false;
                    report.Messages.Add($"toolchain {id}: {problem}");
                    report.Toolchains.Add(check);
                    continue;
                }

                bool declaresInstall = profile!.Install is { Files.Count: > 0 };
                if (declaresInstall && !check.RootExists)
                {
                    check.Ok = false;
                    report.Messages.Add($"toolchain {id}: install root {install.Root} does not exist");
                    report.Toolchains.Add(check);
                    continue;
                }

                if (!declaresInstall)
                {
                    check.Ok = true;
                    check.FilesChecked = 0;
                    report.Messages.Add(check.RootExists
                        ? $"toolchain {id}: the profile lists no install.files, so the install cannot be verified by hash"
                        : $"toolchain {id}: no install root, so nothing to verify by hash; {use.Cc} and {use.Link} were found on PATH");
                    report.Toolchains.Add(check);
                    continue;
                }

                foreach (var file in profile!.Install!.Files)
                {
                    string path = Path.Combine(install.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    check.FilesChecked++;
                    if (!File.Exists(path))
                    {
                        check.FilesMissing.Add(file.Path);
                        check.Ok = false;
                        continue;
                    }

                    string actual = PeImage.HashFile(path);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        check.FilesMismatched.Add(file.Path);
                        check.Ok = false;
                    }
                }

                if (!check.Ok)
                {
                    report.Ok = false;
                }

                report.Toolchains.Add(check);
            }
        }

        return report;
    }
}
