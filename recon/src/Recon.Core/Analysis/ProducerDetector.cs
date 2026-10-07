using Recon.DebugInfo;
using Recon.Elf;
using Recon.Images;
using Recon.Pe;
using Recon.Toolchains;

namespace Recon.Analysis;

/// <summary>One piece of raw evidence about which tools touched the binary.</summary>
public sealed class ProducerEvidence
{
    /// <summary>rich_header, comment_section, pdb_compiland, dwarf_producer, linker_version, import_dll.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string? Version { get; set; }

    public uint? Count { get; set; }

    public string? Detail { get; set; }

    /// <summary>Compilation unit the evidence belongs to, when it is unit-scoped.</summary>
    public string? Unit { get; set; }

    /// <summary>Profile ids this evidence matches, if any.</summary>
    public List<string> Matches { get; set; } = [];
}

public sealed class ToolchainSuggestion
{
    public string ProfileId { get; set; } = string.Empty;

    public string Confidence { get; set; } = "low";

    public string Scope { get; set; } = "binary";

    /// <summary>Evidence kinds that produced this suggestion.</summary>
    public List<string> Evidence { get; set; } = [];

    public string? Unit { get; set; }

    public string? Note { get; set; }
}

public sealed class ProducerReport
{
    public List<ProducerEvidence> Evidence { get; set; } = [];

    /// <summary>Compilation units read from DWARF, with their producer strings.</summary>
    public List<(string Name, string Producer)> DwarfUnits { get; set; } = [];

    public List<ToolchainSuggestion> Suggestions { get; set; } = [];

    public List<string> Problems { get; set; } = [];
}

/// <summary>
/// Collects raw evidence and matches it against the detection rules in toolchain profiles.
/// Evidence is recorded even when nothing matches: "unknown stays unknown".
/// </summary>
public static class ProducerDetector
{
    /// <summary>
    /// What a Visual Basic program's own header says about how it was compiled, matched against the
    /// profiles' <c>vb6_header</c> rules.
    ///
    /// A VB6 program is compiled either to native x86 or to p-code an interpreter runs at run time,
    /// and the two share everything a profile without this rule can see: both import MSVBVM60.DLL,
    /// both carry the Visual Studio 98 linker's 6.0 stamp, and — measured over the 42 p-code programs
    /// of the corpus — a p-code program's Rich header holds one record and it is the same <c>vb60</c>
    /// product id a native program's does. A p-code program therefore matches the <c>vb6-native</c>
    /// profile's rules while being nothing like what that profile describes: there is no compiled
    /// code in it for a padding byte or an alignment to describe, and no function a rebuild could
    /// reproduce. So the header decides: a profile that names one kind is suggested for that kind
    /// only, and the profile that names the other kind is withdrawn from every piece of evidence it
    /// matched. The other evidence is left alone — it is true, it is just not specific.
    /// </summary>
    private static void MatchVb6HeaderRules(IBinaryImage image, ToolchainRegistry registry, ProducerReport report)
    {
        if (image.Pe?.Vb6 is not { } vb)
        {
            return;
        }

        string kind = vb.IsPcode ? "pcode" : "native";
        var evidence = new ProducerEvidence
        {
            Kind = "vb_header",
            Tool = "Visual Basic",
            Detail = vb.Describe(),
        };

        foreach (var profile in ProfilesFor(image.Format, registry))
        {
            string? declared = profile.Vb6CodeKind;
            if (declared is null)
            {
                continue;
            }

            if (string.Equals(declared, kind, StringComparison.Ordinal))
            {
                if (!evidence.Matches.Contains(profile.Id))
                {
                    evidence.Matches.Add(profile.Id);
                }

                continue;
            }

            foreach (var other in report.Evidence)
            {
                other.Matches.RemoveAll(id => id == profile.Id);
            }
        }

        report.Evidence.Add(evidence);
    }

    public static ProducerReport Detect(IBinaryImage image, DebugInfoResult? debug, ToolchainRegistry registry, DwarfInfo? dwarf = null)
    {
        var report = new ProducerReport();

        // Evidence that only one format has is collected from that format's own image; everything
        // after this is shared, and the rules below match on the evidence, not on the file.
        if (image.Pe is PeImage pe)
        {
            AddRichHeaderEvidence(pe, report);
            AddLinkerVersionEvidence(pe, report);
        }

        if (image.Elf is ElfImage elf)
        {
            AddElfEvidence(elf, report);
        }

        AddCommentEvidence(image, report);
        AddImportEvidence(image, report);
        AddDebugEvidence(debug, report);
        AddDwarfEvidence(dwarf, report);
        AddSectionEvidence(image, report);

        if (image.Pe is PeImage peRules)
        {
            MatchRichHeaderRules(peRules, registry, report);
            MatchLinkerVersionRules(peRules, registry, report);
        }

        MatchCommentRules(image.Format, registry, report);
        MatchImportRules(image.Format, registry, report);
        MatchDebugRules(image.Format, debug, registry, report);
        MatchDwarfRules(image, registry, report);
        AddDwarfProblems(dwarf, report);
        MatchSectionRules(image, registry, report);

        MatchVb6HeaderRules(image, registry, report);

        report.Suggestions = Summarize(report, registry);
        return report;
    }

    /// <summary>
    /// What an ELF file says about its own making. <c>.comment</c> names the compiler, the needed
    /// libraries name the runtime, and the symbol versions date the libc it was linked against —
    /// all of it recorded as the same kinds of evidence a PE file produces, so the profiles' rules
    /// match it without being rewritten.
    /// </summary>
    private static void AddElfEvidence(ElfImage elf, ProducerReport report)
    {
        foreach (string library in elf.Needed.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "import_dll",
                Tool = "runtime",
                Detail = library,
            });
        }

        foreach (string version in elf.ImportedSymbols
            .Select(s => s.VersionedName)
            .Where(n => n.Contains('@', StringComparison.Ordinal))
            .Select(n => n[(n.IndexOf('@') + 1)..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "symbol_version",
                Tool = "runtime",
                Detail = version,
            });
        }

        if (elf.Interpreter is { Length: > 0 } interpreter)
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "interpreter",
                Tool = "runtime",
                Detail = interpreter,
            });
        }

        string kind = elf.Type switch
        {
            ElfType.SharedObject => elf.IsLibrary ? "shared object" : "position-independent executable",
            ElfType.Executable => elf.IsPositionIndependent ? "position-independent executable" : "executable",
            ElfType.Relocatable => "relocatable object",
            _ => elf.Type.ToString().ToLowerInvariant(),
        };

        report.Evidence.Add(new ProducerEvidence
        {
            Kind = "elf_type",
            Tool = "link",
            Detail = $"{kind} os_abi={elf.OsAbi}"
                     + (elf.BuildId is null ? string.Empty : $" build_id={elf.BuildId}"),
        });
    }

    // ------------------------------------------------------------ evidence

    private static void AddRichHeaderEvidence(PeImage image, ProducerReport report)
    {
        if (image.Rich is null)
        {
            return;
        }

        foreach (var entry in image.Rich.Entries)
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "rich_header",
                Tool = entry.Tool,
                Version = entry.Build == 0 ? null : entry.Build.ToString(),
                Count = entry.Count,
                Detail = $"prod_id=0x{entry.ProdId:X4} build={entry.Build}"
                    + (entry.IsLinker ? " (linker)" : string.Empty)
                    + (entry.IsLast ? " (last entry)" : string.Empty),
            });
        }

        report.Evidence.Add(new ProducerEvidence
        {
            Kind = "rich_header_summary",
            Tool = "rich",
            Detail = $"xor_key=0x{image.Rich.XorKey:X8} entries={image.Rich.Entries.Count} fingerprint={image.Rich.Fingerprint[..16]}",
        });
    }

    private static void AddCommentEvidence(IBinaryImage image, ProducerReport report)
    {
        foreach (var text in image.CommentStrings.Distinct(StringComparer.Ordinal))
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "comment_section",
                Tool = "compiler",
                Detail = text,
            });
        }
    }

    private static void AddLinkerVersionEvidence(PeImage image, ProducerReport report)
    {
        report.Evidence.Add(new ProducerEvidence
        {
            Kind = "linker_version",
            Tool = "link",
            Version = image.LinkerVersion,
            Detail = $"optional header MajorLinkerVersion.MinorLinkerVersion = {image.LinkerVersion}",
        });
    }

    private static void AddImportEvidence(IBinaryImage image, ProducerReport report)
    {
        foreach (var dll in image.Imports.Select(i => i.Module)
            .Where(module => module.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "import_dll",
                Tool = "runtime",
                Detail = dll,
            });
        }
    }

    private static void AddDebugEvidence(DebugInfoResult? debug, ProducerReport report)
    {
        if (debug is null)
        {
            return;
        }

        bool fromDwarf = string.Equals(debug.Kind, "dwarf", StringComparison.Ordinal);
        foreach (var compiland in debug.Compilands)
        {
            if (compiland.Producer is null)
            {
                continue;
            }

            // DWARF compilands already produce a dwarf_producer record per unit; adding a second
            // record for the same string would only duplicate the evidence.
            if (fromDwarf && report.Evidence.Any(e =>
                    e.Kind == "dwarf_producer"
                    && e.Detail == compiland.Producer
                    && string.Equals(e.Unit, compiland.ObjectFile, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "pdb_compiland",
                Tool = "compiler",
                Detail = compiland.Producer,
                Unit = compiland.Unit,
            });
        }
    }

    private static void AddDwarfEvidence(DwarfInfo? dwarf, ProducerReport report)
    {
        if (dwarf is null)
        {
            return;
        }

        report.DwarfUnits = dwarf.Units;
        foreach (var (name, producer) in dwarf.Units)
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "dwarf_producer",
                Tool = "compiler",
                Detail = producer,
                Unit = string.IsNullOrEmpty(name) ? null : name,
            });
        }
    }

    private static void AddDwarfProblems(DwarfInfo? dwarf, ProducerReport report)
    {
        if (dwarf is null)
        {
            return;
        }

        foreach (var problem in dwarf.Problems.Distinct(StringComparer.Ordinal))
        {
            report.Problems.Add($"dwarf: {problem}");
        }
    }

    private static void AddSectionEvidence(IBinaryImage image, ProducerReport report)
    {
        foreach (var section in image.Sections)
        {
            report.Evidence.Add(new ProducerEvidence
            {
                Kind = "section",
                Tool = "link",
                Detail = $"present: {section.Name}",
            });
        }
    }

    // ------------------------------------------------------------ rule matching

    private static void MatchRichHeaderRules(PeImage image, ToolchainRegistry registry, ProducerReport report)
    {
        if (image.Rich is null)
        {
            return;
        }

        foreach (var entry in image.Rich.Entries)
        {
            var evidence = report.Evidence.FirstOrDefault(e =>
                e.Kind == "rich_header" && e.Detail is not null && e.Detail.StartsWith($"prod_id=0x{entry.ProdId:X4} build={entry.Build}", StringComparison.Ordinal));
            if (evidence is null)
            {
                continue;
            }

            foreach (var profile in registry.All(image.Kind == PeKind.Pe32 ? "pe32" : "pe64", image.DescribeMachine()))
            {
                foreach (var rule in profile.Detect.RichHeader)
                {
                    bool idMatches = rule.Id == 0 || rule.Id == entry.ProdId;
                    bool roleMatches = rule.Role is null
                                       || (rule.Role == "linker" && entry.IsLinker)
                                       || (rule.Role == "compiler" && !entry.IsLinker);
                    if (!idMatches || !roleMatches)
                    {
                        continue;
                    }

                    if (entry.Build < rule.BuildMin || entry.Build > rule.BuildMax)
                    {
                        continue;
                    }

                    if (!evidence.Matches.Contains(profile.Id))
                    {
                        evidence.Matches.Add(profile.Id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The profiles this evidence may speak about: the ones that claim this file's format. A
    /// compiler string alone is not enough — GCC 14 on Linux is not GCC 14 for 32-bit Windows, and
    /// saying it is would be a confident answer built on the wrong half of the evidence.
    /// </summary>
    private static IEnumerable<ToolchainProfile> ProfilesFor(string format, ToolchainRegistry registry)
        => registry.All().Where(profile => profile.Targets.Count == 0
            || profile.Targets.Any(target => string.IsNullOrEmpty(target.Format)
                || string.Equals(target.Format, format, StringComparison.OrdinalIgnoreCase)));

    private static void MatchCommentRules(string format, ToolchainRegistry registry, ProducerReport report)
    {
        foreach (var evidence in report.Evidence.Where(e => e.Kind == "comment_section" && e.Detail is not null))
        {
            foreach (var profile in ProfilesFor(format, registry))
            {
                foreach (var rule in profile.Detect.CommentSection)
                {
                    if (rule.Contains.Length > 0 && evidence.Detail!.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!evidence.Matches.Contains(profile.Id))
                        {
                            evidence.Matches.Add(profile.Id);
                        }
                    }
                }
            }
        }
    }

    private static void MatchLinkerVersionRules(PeImage image, ToolchainRegistry registry, ProducerReport report)
    {
        var evidence = report.Evidence.FirstOrDefault(e => e.Kind == "linker_version");
        if (evidence is null)
        {
            return;
        }

        var actual = (image.MajorLinkerVersion, image.MinorLinkerVersion);
        foreach (var profile in registry.All())
        {
            foreach (var rule in profile.Detect.LinkerVersion)
            {
                var min = ParseVersion(rule.Min);
                var max = ParseVersion(rule.Max);
                if (Compare(actual, min) >= 0 && Compare(actual, max) <= 0)
                {
                    if (!evidence.Matches.Contains(profile.Id))
                    {
                        evidence.Matches.Add(profile.Id);
                    }
                }
            }
        }
    }

    private static void MatchImportRules(string format, ToolchainRegistry registry, ProducerReport report)
    {
        foreach (var evidence in report.Evidence.Where(e => e.Kind == "import_dll" && e.Detail is not null))
        {
            foreach (var profile in ProfilesFor(format, registry))
            {
                foreach (var rule in profile.Detect.ImportDll)
                {
                    if (rule.Contains.Length > 0 && evidence.Detail!.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!evidence.Matches.Contains(profile.Id))
                        {
                            evidence.Matches.Add(profile.Id);
                        }
                    }
                }
            }
        }
    }

    private static void MatchDebugRules(string format, DebugInfoResult? debug, ToolchainRegistry registry, ProducerReport report)
    {
        if (debug is null)
        {
            return;
        }


        foreach (var evidence in report.Evidence.Where(e => e.Kind == "pdb_compiland" && e.Detail is not null))
        {
            foreach (var profile in ProfilesFor(format, registry))
            {
                // A compiler producer string means the same thing whether it was read from a PDB
                // compiland or from a DWARF compilation unit, so both rule tables apply.
                var patterns = profile.Detect.PdbCompiland.Select(r => r.ProducerContains)
                    .Concat(profile.Detect.DwarfProducer.Select(r => r.ProducerContains));
                foreach (string pattern in patterns)
                {
                    if (pattern.Length > 0 && evidence.Detail!.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!evidence.Matches.Contains(profile.Id))
                        {
                            evidence.Matches.Add(profile.Id);
                        }
                    }
                }
            }
        }
    }

    private static void MatchDwarfRules(IBinaryImage image, ToolchainRegistry registry, ProducerReport report)
    {
        foreach (var evidence in report.Evidence.Where(e => e.Kind == "dwarf_producer" && e.Detail is not null))
        {
            foreach (var profile in ProfilesFor(image.Format, registry))
            {
                foreach (var rule in profile.Detect.DwarfProducer)
                {
                    if (rule.ProducerContains.Length > 0 && evidence.Detail!.Contains(rule.ProducerContains, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!evidence.Matches.Contains(profile.Id))
                        {
                            evidence.Matches.Add(profile.Id);
                        }
                    }
                }
            }
        }
    }

    private static void MatchSectionRules(IBinaryImage image, ToolchainRegistry registry, ProducerReport report)
    {
        var present = image.Sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var evidence in report.Evidence.Where(e => e.Kind == "section" && e.Detail is not null))
        {
            string name = evidence.Detail!.Replace("present: ", string.Empty, StringComparison.Ordinal);
            bool exists = present.Contains(name);
            foreach (var profile in ProfilesFor(image.Format, registry))
            {
                foreach (var rule in profile.Detect.Sections)
                {
                    if (!string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool matches = rule.Absent ? !exists : exists;
                    if (matches && !evidence.Matches.Contains(profile.Id))
                    {
                        evidence.Matches.Add(profile.Id);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------ summarising

    /// <summary>
    /// The newest compiler release one piece of evidence names, as a number, or 0 when the producer
    /// string does not say. Used only to order two profiles the evidence supports equally well, so a
    /// string it cannot read simply leaves the order to the tie-breaks after it.
    /// </summary>
    private static int NewestCompilerVersion(ProducerEvidence evidence)
    {
        if (evidence.Kind is not ("comment_section" or "pdb_compiland" or "dwarf_producer") || evidence.Detail is null)
        {
            // A linker's version is not a compiler's; mixing the two would compare a build number
            // against a release.
            return 0;
        }

        string detail = evidence.Detail;

        // "clang version 19.1.7" — the compiler names itself outright.
        const string versionWord = "version ";
        int at = detail.IndexOf(versionWord, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return LeadingNumber(detail[(at + versionWord.Length)..]);
        }

        // "GCC: (GNU) 14-win32" — a .comment entry: the release follows the bracketed name.
        const string gnu = "(GNU) ";
        at = detail.IndexOf(gnu, StringComparison.Ordinal);
        if (at >= 0)
        {
            return LeadingNumber(detail[(at + gnu.Length)..]);
        }

        // "GNU C17 14-win32 ..." — a DWARF producer: the standard's year comes first, the release
        // second, so the number that matters is after the second space.
        if (detail.StartsWith("GNU C", StringComparison.Ordinal))
        {
            int first = detail.IndexOf(' ');
            int second = first >= 0 ? detail.IndexOf(' ', first + 1) : -1;
            if (second > 0)
            {
                return LeadingNumber(detail[(second + 1)..]);
            }
        }

        return 0;
    }

    private static int LeadingNumber(string text)
    {
        int start = 0;
        while (start < text.Length && !char.IsAsciiDigit(text[start]))
        {
            start++;
        }

        int end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > start && int.TryParse(text[start..end], out int value) ? value : 0;
    }

    private static List<ToolchainSuggestion> Summarize(ProducerReport report, ToolchainRegistry registry)
    {
        var suggestions = new List<ToolchainSuggestion>();

        var byProfile = report.Evidence
            .Where(e => e.Matches.Count > 0)
            .SelectMany(e => e.Matches.Select(m => (ProfileId: m, Evidence: e)))
            .GroupBy(t => t.ProfileId, StringComparer.Ordinal);

        foreach (var group in byProfile)
        {
            var evidence = group.Select(t => t.Evidence).ToList();
            var kinds = evidence.Select(e => e.Kind).Distinct(StringComparer.Ordinal).ToList();

            string confidence = kinds switch
            {
                _ when kinds.Contains("pdb_compiland") || kinds.Contains("dwarf_producer") => "high",
                _ when kinds.Contains("section") && kinds.Contains("import_dll") => "medium",
                _ when kinds.Contains("comment_section") => "medium",
                _ when kinds.Contains("rich_header") => "medium",
                _ when kinds.Contains("linker_version") => "medium",
                _ when kinds.Contains("import_dll") => "low",
                _ => "low",
            };

            // A single weak signal stays weak even when several of the same kind agree.
            if (kinds.Count == 1 && kinds[0] == "import_dll")
            {
                confidence = "low";
            }

            if (kinds.Count >= 3)
            {
                confidence = confidence == "low" ? "medium" : confidence;
            }

            suggestions.Add(new ToolchainSuggestion
            {
                ProfileId = group.Key,
                Confidence = confidence,
                Scope = "binary",
                Evidence = kinds,
                Note = DescribeEvidence(evidence),
            });
        }

        // A binary that mixes objects from two compilers (an SDK runtime plus application code, or
        // two GCC versions) is normal. Report the distinct producer generations that no profile
        // claims, not every command line, so the message stays readable and actionable.
        var unmatched = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in report.Evidence.Where(e =>
                     e.Matches.Count == 0 && e.Kind is "comment_section" or "pdb_compiland" or "dwarf_producer"))
        {
            string label = $"{evidence.Kind}: {ProducerLabel(evidence.Detail!)}";
            if (seen.Add(label))
            {
                unmatched.Add(label);
            }
        }

        if (unmatched.Count > 0)
        {
            report.Problems.Add($"no profile matches these producer strings: {string.Join("; ", unmatched)}");
        }

        // Which compiler release each profile's evidence names, so that two releases of the same
        // compiler can be told apart below.
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in byProfile)
        {
            int newest = 0;
            foreach (var (_, evidenceItem) in group)
            {
                newest = Math.Max(newest, NewestCompilerVersion(evidenceItem));
            }

            versions[group.Key] = newest;
        }

        // Ties are broken by how many different kinds of evidence a profile matched, then by which
        // release of the compiler that evidence names, and only then by name: a stripped binary where
        // the GCC comment survives but no DWARF producer does should still prefer the profile that
        // matched something specific over one that matched the format, and a program that links
        // runtime objects from an older release of its own compiler should be attributed to the
        // release that compiled the program — that is the one a rebuild has to reproduce.
        return suggestions
            .OrderByDescending(s => Rank(s.Confidence))
            .ThenByDescending(s => s.Evidence.Count)
            .ThenByDescending(s => versions.GetValueOrDefault(s.ProfileId, 0))
            .ThenBy(s => s.ProfileId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reduces a producer command line to the part that identifies the compiler build.</summary>
    private static string ProducerLabel(string detail)
    {
        var words = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var label = new List<string>();
        foreach (string word in words)
        {
            if (word.StartsWith('-') || word.Contains('/') || word.Contains('\\'))
            {
                break;
            }

            label.Add(word);
            if (label.Count == 3)
            {
                break;
            }
        }

        return label.Count == 0 ? detail : string.Join(' ', label);
    }

    private static string DescribeEvidence(List<ProducerEvidence> evidence)
    {
        var version = evidence
            .Where(e => e.Version is not null && e.Tool == "link")
            .Select(e => e.Version)
            .FirstOrDefault();
        return version is null ? string.Empty : $"linker build {version}";
    }

    /// <summary>Maps each compilation unit to a profile, using compiland producers and comment strings.</summary>
    public static Dictionary<string, (string ProfileId, string Confidence, List<string> Evidence)> DetectUnits(
        DebugInfoResult? debug,
        ProducerReport report)
    {
        var result = new Dictionary<string, (string, string, List<string>)>(StringComparer.Ordinal);
        if (debug is null)
        {
            return result;
        }

        foreach (var compiland in debug.Compilands)
        {
            // A PDB names the producer of each compiland. COFF file symbols do not, but the DWARF
            // compilation unit with the same source file name does, which is the MinGW case.
            var match = compiland.Producer is null
                ? null
                : report.Evidence.FirstOrDefault(e =>
                    e.Kind == "pdb_compiland" && e.Unit == compiland.Unit && e.Matches.Count > 0);
            if (match is not null)
            {
                result[compiland.Unit] = (match.Matches[0], "high", ["pdb_compiland"]);
                continue;
            }

            // Without a PDB, the DWARF compilation unit with the same source file name identifies
            // the toolchain that compiled this object. Mixed binaries make this per-unit view matter.
            var dwarfEvidence = report.Evidence.FirstOrDefault(e =>
                e.Kind == "dwarf_producer"
                && e.Unit is not null
                && string.Equals(Path.GetFileName(e.Unit), Path.GetFileName(compiland.Unit), StringComparison.OrdinalIgnoreCase)
                && e.Matches.Count > 0);
            if (dwarfEvidence is not null)
            {
                result[compiland.Unit] = (dwarfEvidence.Matches[0], "high", ["dwarf_producer"]);
            }
        }

        return result;
    }

    private static int Rank(string confidence) => confidence switch
    {
        "high" => 3,
        "medium" => 2,
        _ => 1,
    };

    private static (int Major, int Minor) ParseVersion(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        var parts = text.Split('.');
        int major = int.TryParse(parts[0], out int m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 0;
        return (major, minor);
    }

    private static int Compare((int Major, int Minor) left, (int Major, int Minor) right)
    {
        int major = left.Major.CompareTo(right.Major);
        return major != 0 ? major : left.Minor.CompareTo(right.Minor);
    }
}
