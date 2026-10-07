using Recon.Analysis;
using Recon.Images;
using Recon.Inventory;

namespace Recon.Toolchains;

/// <summary>
/// Decides which profile one image is analysed with.
///
/// Three sources, in order: the project names a toolchain (<c>[target] default_toolchain</c>) and that
/// is an instruction to follow, so it wins; otherwise the evidence in the file itself is asked — the
/// producer detector runs before the disassembler and costs one pass over the debug information and
/// the headers, which is cheap enough to do up front; and otherwise the first profile that can
/// describe this format is used, so that padding bytes, alignment and pointer size still come from
/// somewhere rather than from a hard-coded default.
///
/// Asking the evidence before the analysis matters: the profile supplies the prologue hints and the
/// padding bytes the analyser uses on a stripped binary, where they are most of what it has to go on.
/// </summary>
public static class ToolchainSelector
{
    /// <summary>Picks the profile for one image. Returns null when the registry has nothing at all.</summary>
    /// <param name="concreteOnly">
    /// Skip profiles that are abstract. Analysing with a base profile is fine — it still carries the
    /// alignment and padding rules — but compiling with one is not, so the build asks for a profile
    /// that can actually be run.
    /// </param>
    public static ToolchainProfile? Choose(
        ToolchainRegistry registry,
        string? configuredId,
        IBinaryImage image,
        DebugInfo.DebugInfoResult? debug = null,
        DebugInfo.DwarfInfo? dwarf = null,
        bool concreteOnly = false)
    {
        if (!string.IsNullOrEmpty(configuredId) && registry.GetResolved(configuredId) is { } named)
        {
            return named;
        }

        var producers = ProducerDetector.Detect(image, debug, registry, dwarf);
        foreach (var suggestion in producers.Suggestions)
        {
            if (registry.GetResolved(suggestion.ProfileId) is { } detected)
            {
                return detected;
            }
        }

        return registry.All(image.Format, image.ArchName).FirstOrDefault(p => !concreteOnly || !p.IsAbstract);
    }
}
