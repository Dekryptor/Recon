using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Recon.Build;
using Recon.Config;
using Recon.Images;
using Recon.Pe;
using Recon.Project;
using Recon.Toolchains;
using Recon.Verify;

namespace Recon;

public sealed class DoctorCheck
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; set; }
}

public sealed class DoctorReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("environment")]
    public Dictionary<string, string> Environment { get; set; } = [];

    [JsonPropertyName("checks")]
    public List<DoctorCheck> Checks { get; set; } = [];
}

/// <summary>
/// Diagnoses a broken setup: missing inputs, mismatched hashes, unreadable binaries, absent debug
/// info, a PDB that belongs to another build, toolchains that are not where local.toml says.
/// </summary>
public static class Doctor
{
    public static DoctorReport Diagnose(ProjectContext? context, string toolVersion)
    {
        var report = new DoctorReport();
        report.Environment["os"] = RuntimeInformation.OSDescription;
        report.Environment["arch"] = RuntimeInformation.OSArchitecture.ToString();
        report.Environment["runtime"] = RuntimeInformation.FrameworkDescription;
        report.Environment["tool_version"] = toolVersion;
        report.Environment["cwd"] = Directory.GetCurrentDirectory();

        void Check(string name, string status, string detail, string? hint = null)
            => report.Checks.Add(new DoctorCheck { Name = name, Status = status, Detail = detail, Hint = hint });

        if (context is null)
        {
            Check("project", "fail", "no project.toml found", "run `recon init` or pass --project <dir>");
            report.Ok = false;
            return report;
        }

        Check("project", "ok", context.Project.FilePath);
        report.Environment["project_root"] = context.RootDirectory;

        Check(
            "local.toml",
            context.Local.IsPresent ? "ok" : "warn",
            context.Local.IsPresent ? context.Local.FilePath : "not present; inputs are looked up under ./inputs",
            context.Local.IsPresent ? null : "run `recon init` to generate one, or create it by hand");

        Check(
            "toolchain profiles",
            context.Registry.KnownIds.Any() ? "ok" : "warn",
            context.Registry.KnownIds.Any()
                ? string.Join(", ", context.Registry.KnownIds)
                : "no profiles found in paths.profiles",
            context.Registry.KnownIds.Any() ? null : "add profile files under the directories listed in paths.profiles");

        CheckProfileFreshness(context, Check);

        foreach (var diagnostic in context.Diagnostics.Items)
        {
            Check(
                "diagnostic",
                diagnostic.Severity == Config.DiagnosticSeverity.Error ? "fail" : "warn",
                diagnostic.ToString());
        }

        var verify = Verifier.Verify(context);
        report.Ok = report.Ok && verify.Ok;

        foreach (var input in verify.Inputs)
        {
            if (!input.Exists)
            {
                Check($"input {input.Id}", "fail", $"missing: {input.Path}", "place the file there or fix inputs.dir in local.toml");
                continue;
            }

            Check(
                $"input {input.Id}",
                input.Ok ? "ok" : "fail",
                input.Ok ? $"SHA-256 matches ({input.File})" : $"SHA-256 mismatch: expected {input.Expected[..12]}..., found {input.Actual?[..12]}...",
                input.Ok ? null : "the input is a different build than this project describes");
        }

        // The build's own tools do not depend on the binary being readable, and a missing input is
        // exactly the situation in which a user most needs to know whether the compiler is there, so
        // they are checked before anything that needs the image.
        CheckBuildTools(context, Check, report);

        var image = context.LoadImage();
        if (image.Image is null)
        {
            Check("binary", "fail", string.Join("; ", image.Problems));
            report.Ok = false;
            return report;
        }

        var loaded = image.Image;
        Check("binary", "ok", $"{loaded.Format} {loaded.ArchName}, image base 0x{loaded.ImageBase:X}, entry 0x{loaded.EntryPointRva:X}");
        if (loaded.Is64)
        {
            Check(
                "binary width",
                "warn",
                "64-bit: the prologue hints, the jump tables and the data references are read for both "
                + "widths, but a stripped 64-bit binary still misses the blocks that nothing but a "
                + "conditional jump arrives at — the cold half of a function, say — because a jump "
                + "target on its own is a label inside a function as often as it is a boundary");
        }

        if (loaded.Relocations.Count == 0)
        {
            Check("relocations", "warn", "no relocations: the image cannot be rebased, which also means fewer xrefs to recover");
        }

        var debug = context.LoadDebugInfo(loaded);
        if (debug is null)
        {
            Check("debug info", "warn", "none found (checked the project, then the binary's directory)", "debug info raises confidence and gives function sizes");
        }
        else
        {
            string detail = $"{debug.Kind}: {debug.Symbols.Count} symbols ({debug.FunctionCount} functions), {debug.Compilands.Count} compilands";
            Check("debug info", debug.Problems.Count == 0 ? "ok" : "warn", detail);
            foreach (var problem in debug.Problems)
            {
                Check("debug info problem", "warn", problem);
            }

            var codeView = loaded.Pe?.DebugEntries.FirstOrDefault(d => d.Type == 2 && d.PdbGuid is not null);
            if (debug.Kind == "pdb" && codeView?.PdbGuid is not null && debug.Guid is not null && debug.Guid != codeView.PdbGuid)
            {
                Check("debug info identity", "fail", "the PDB GUID does not match the binary's CodeView record; this PDB belongs to another build");
                report.Ok = false;
            }
        }

        foreach (var check in verify.Toolchains)
        {
            // Where the tools are is not the question; whether they can be run is. A toolchain with
            // no install root — every Unix one — is fine as long as its compiler and linker resolve.
            string where = check.RootExists ? $"at {check.Root}" : "with no install root (the tools come from PATH)";

            string? toolProblem = check.CcProblem ?? check.LinkProblem;
            if (toolProblem is not null)
            {
                Check($"toolchain {check.Id}", "fail", $"{check.Id} {where}: {toolProblem}");
                report.Ok = false;
                continue;
            }

            if (check.FilesMissing.Count > 0 || check.FilesMismatched.Count > 0)
            {
                Check(
                    $"toolchain {check.Id}",
                    "fail",
                    $"{check.Id} {where}: {check.FilesMissing.Count} missing, {check.FilesMismatched.Count} hashed differently");
                report.Ok = false;
                continue;
            }

            Check(
                $"toolchain {check.Id}",
                "ok",
                check.FilesChecked > 0
                    ? $"{check.Id} at {check.Root}: {check.FilesChecked} files verified"
                    : $"{check.Id} {where}: cc {check.Cc}, link {check.Link}");
        }

        return report;
    }

    /// <summary>
    /// The tools the build would actually run, per toolchain the project's units ask for. A profile
    /// whose compiler is not installed is not an error in the profile: it is a machine fact, and
    /// <c>doctor</c> is where that belongs.
    /// </summary>
    private static void CheckBuildTools(
        ProjectContext context,
        Action<string, string, string, string?> check,
        DoctorReport report)
    {
        // Doctor asks about the toolchains before it has loaded the image, so it loads it here when
        // it needs one: the context caches it, and a project that names its toolchain never pays.
        IBinaryImage? loaded = null;
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var unit in context.Project.Units)
        {
            string id = context.Project.ResolveToolchain(unit);
            if (id.Length == 0)
            {
                loaded ??= context.LoadImage().Image;
                // Not a warning: the build asks the binary who built it, and only says so when it
                // cannot tell. Naming one is how you override a guess, not how you enable a build.
                var guess = loaded is null
                    ? null
                    : ToolchainSelector.Choose(
                        context.Registry,
                        null,
                        loaded,
                        context.LoadDebugInfo(loaded),
                        context.Dwarf,
                        concreteOnly: true);

                check(
                    $"build {unit.Name}",
                    guess is null ? "warn" : "ok",
                    guess is null
                        ? $"unit \"{unit.Name}\" names no toolchain, and nothing in the binary says which one built it"
                        : $"unit \"{unit.Name}\" names no toolchain: the build will use \"{guess.Id}\", which this binary's evidence points at",
                    guess is null ? "set unit.toolchain or target.default_toolchain" : "set unit.toolchain to override the guess");
                continue;
            }

            if (!used.TryGetValue(id, out var units))
            {
                used[id] = units = [];
            }

            units.Add(unit.Name);
        }

        foreach (var (id, units) in used.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var profile = context.Registry.GetResolved(id);
            if (profile is null)
            {
                // A broken reference is already a configuration error, reported when the project loads.
                continue;
            }

            var install = context.Local.Toolchains.TryGetValue(id, out var local) ? local : null;
            var use = ToolResolver.Resolve(profile, install, context.Project.RootDirectory);
            string detail = $"{units.Count} unit(s): {string.Join(", ", units)}";

            if (profile.Compile is null)
            {
                check($"build {id}", "warn", $"{detail}: the profile has no [compile] section", "add [compile] to the profile, or choose a toolchain that has one");
                continue;
            }

            if (use.CcProblem is not null)
            {
                check($"build {id}", "fail", $"{detail}: {use.CcProblem}", "set root, cc or link for this toolchain in local.toml");
                report.Ok = false;
                continue;
            }

            string linker = profile.Link is null
                ? "no [link] section, so the units cannot be linked"
                : use.LinkProblem ?? $"link {use.Link}";
            check(
                $"build {id}",
                profile.Link is null || use.LinkProblem is not null ? "warn" : "ok",
                $"{detail}: cc {use.Cc}, {linker}",
                profile.Link is null || use.LinkProblem is not null ? "add [link] to the profile, or point link at the linker in local.toml" : null);
        }
    }

    /// <summary>
    /// A project that copied the shipped profiles in — what <c>recon init</c> does — keeps that
    /// snapshot: a profile added to the tool since is invisible to it, and a copy edited since is
    /// invisible to everyone else. Neither is an error, and neither is visible anywhere else: the
    /// project's own files are the whole set, so the only symptom is a detection that does not fire.
    /// This compares them and says what differs.
    /// </summary>
    private static void CheckProfileFreshness(ProjectContext context, Action<string, string, string, string?> check)
    {
        var projectCopies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string directory in context.Project.Paths.Profiles)
        {
            string full = Path.GetFullPath(Path.Combine(context.Project.RootDirectory, directory));
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(full, "*.toml", SearchOption.AllDirectories))
            {
                projectCopies[Path.GetFileName(file)] = file;
            }
        }

        // A project with no profile files of its own is using the shipped set, which is always
        // current; there is nothing to compare and nothing to say.
        if (projectCopies.Count == 0)
        {
            return;
        }

        var missing = new List<string>();
        var stale = new List<string>();
        foreach (var (fileName, content) in BuiltInProfiles.All())
        {
            if (!projectCopies.TryGetValue(fileName, out string? copy))
            {
                missing.Add(fileName);
                continue;
            }

            if (!string.Equals(File.ReadAllText(copy), content, StringComparison.Ordinal))
            {
                stale.Add(fileName);
            }
        }

        if (missing.Count == 0 && stale.Count == 0)
        {
            return;
        }

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"{missing.Count} shipped profile{(missing.Count == 1 ? "" : "s")} this project does not have ({string.Join(", ", missing)})");
        }

        if (stale.Count > 0)
        {
            parts.Add(stale.Count == 1
                ? $"1 copy that differs from the shipped one ({stale[0]})"
                : $"{stale.Count} copies that differ from the shipped one ({string.Join(", ", stale)})");
        }

        check(
            "toolchain profiles",
            "warn",
            string.Join("; ", parts),
            "run `recon toolchain builtins` to add the missing files; to refresh a copy that differs, delete it first");
    }

}
