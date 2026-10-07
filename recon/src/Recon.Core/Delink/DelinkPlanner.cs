using System.Text;
using Recon.Config;
using Recon.Inventory;
using Recon.Pe;

namespace Recon.Delink;

/// <summary>
/// Cuts an image into pieces a linker can put back at the same addresses.
///
/// The pieces have to tile each section exactly: no overlaps, no gaps, because a byte that belongs
/// to no piece is a byte the relinked image quietly loses, and two pieces claiming the same byte
/// cannot both be right. Functions claim first, then named data, then jump tables, and whatever is
/// left becomes filler — still emitted, because an image without its padding is not the same image.
/// </summary>
public static class DelinkPlanner
{
    /// <summary>Relocation kinds that are a plain absolute address in the image.</summary>
    private static readonly string[] EmittableKinds = ["HIGHLOW", "DIR64", "ABSOLUTE"];

    public static DelinkDocument Plan(
        PeImage image,
        InventoryDocument inventory,
        ProjectConfig project,
        string toolVersion,
        string command)
    {
        var document = new DelinkDocument
        {
            Generator = new GeneratorInfo { Tool = "recon", Version = toolVersion, Command = command },
            Binary = new DelinkBinary
            {
                Path = image.Path,
                ImageBase = image.ImageBase,
                EntryRva = image.EntryPointRva,
                Format = image.Kind == PeKind.Pe32 ? "pe32" : "pe64",
                Arch = image.DescribeMachine(),
                IsDll = image.IsDll,
            },
        };

        var symbols = new List<DelinkSymbolInfo>();
        var pieces = new List<DelinkPiece>();

        // Every name in the plan is defined once in the relinked image, because an image cannot
        // define the same name twice: whichever address comes first keeps it.
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var functions = inventory.Functions
            .Where(f => f.Ranges.Count > 0)
            .OrderBy(f => f.Ranges[0].Rva)
            .ToList();

        // Every function the image has, as a cover sees it. A cover that resolves by an unambiguous
        // substring needs the others to know whether its substring *was* unambiguous, so the whole
        // list is built once and every cover is resolved against all of it: resolved against one
        // function at a time, every substring is unambiguous, which is how `symbol = "add"` used to
        // claim `__multadd_D2A`.
        var coverCandidates = functions
            .SelectMany(f => f.Ranges.Select(r => new Covers.CoverCandidate(f.Name, r.Rva, r.Size) { Aliases = f.Aliases }))
            .ToList();

        string? tailOwner = null;
        foreach (var section in image.Sections.OrderBy(s => s.Rva))
        {
            var info = new DelinkSectionInfo
            {
                Name = section.Name,
                Rva = section.Rva,
                VirtualSize = section.VirtualSize,
                RawSize = section.RawSize,
                Emitted = true,
            };

            // What the section has in memory beyond the bytes written back for it. Everything the
            // plan writes is file bytes; the rest of the virtual extent is zeroes the loader maps and
            // the file does not hold - for an MSVC image, the uninitialised data `link.exe` merged
            // into `.data`. One section can carry it; a second is not something this tool can tell a
            // script-less linker, so it is reported rather than guessed at.
            info.TailBytes = section.VirtualSize > ContentExtent(section)
                ? section.VirtualSize - ContentExtent(section)
                : 0;
            if (info.TailBytes > 0)
            {
                if (tailOwner is null)
                {
                    tailOwner = section.Name;
                }
                else
                {
                    document.Problems.Add(
                        $"{section.Name} has {info.TailBytes} byte(s) of zero-filled tail as well as {tailOwner}: " +
                        "only one section can carry it, because a script-less linker is told about it by " +
                        "its name and the name can only mean one section");
                }
            }

            info.Source = AsmWriter.SourceFileName(section.Name);
            info.Output = AsmWriter.SectionName(section.Name);
            info.Input = AsmWriter.InputSectionName(info.Output);
            info.Flags = SectionFlags(section);
            if (!string.Equals(info.Output, section.Name, StringComparison.Ordinal))
            {
                info.Note = $"emitted as {info.Output}: \"{section.Name}\" is not a name an assembler accepts";
            }

            if (string.Equals(section.Name, ".reloc", StringComparison.OrdinalIgnoreCase))
            {
                // The base relocation table is written back byte for byte: its entries are page
                // RVAs and offsets, so they survive unchanged as long as the layout does.
                info.Note = "re-emitted as it was; the linker is told not to write one of its own";
            }
            var claims = Claims(section, inventory, functions, document.Problems);
            var sectionPieces = Tile(section, claims, document.Problems);
            info.Pieces = sectionPieces.Count;
            AttachLabels(sectionPieces, Labels(section, inventory, document.Problems), usedNames, document.Problems);

            foreach (var piece in sectionPieces)
            {
                DecideProvider(piece, functions, coverCandidates, project, document.Problems);
                NamePiece(piece, symbols, usedNames, document.Problems);
                AddFixups(piece, inventory.Relocations, symbols, document.Problems);
                pieces.Add(piece);
            }

            document.Sections.Add(info);
        }

        document.Pieces = pieces;
        document.Symbols = symbols;

        // The entry point is an address in the original, and the linker wants a name for it.
        symbols.Add(new DelinkSymbolInfo { Name = document.EntrySymbol, Rva = image.EntryPointRva, Kind = "entry", Synthetic = true });

        document.Counts = new DelinkCounts
        {
            Sections = document.Sections.Count,
            Pieces = pieces.Count,
            Functions = pieces.Count(p => p.Kind == PieceKinds.Function),
            Data = pieces.Count(p => p.Kind == PieceKinds.Data),
            JumpTables = pieces.Count(p => p.Kind == PieceKinds.JumpTable),
            Fillers = pieces.Count(p => p.Kind == PieceKinds.Filler),
            Original = pieces.Count(p => p.Provider == Providers.Original),
            Rebuilt = pieces.Count(p => p.Provider == Providers.Rebuilt),
            Bytes = pieces.Sum(p => (long)p.Size),
            Fixups = pieces.Sum(p => p.Fixups.Count(f => f.Emitted)),
            FixupsNotEmitted = pieces.Sum(p => p.Fixups.Count(f => !f.Emitted)),
            Labels = pieces.Sum(p => p.Labels.Count),
        };

        if (document.Counts.FixupsNotEmitted > 0)
        {
            document.Problems.Add(
                $"{document.Counts.FixupsNotEmitted} relocation(s) are not plain absolute addresses and are emitted as literal bytes: " +
                "the relinked image is only correct at the original's image base");
        }

        if (document.Counts.Rebuilt > 0)
        {
            document.Notes.Add(
                $"{document.Counts.Rebuilt} piece(s) are claimed by a unit with provider = \"rebuilt\": " +
                "`recon link` takes them from that unit's own build and reports what it placed");
        }

        return document;
    }

    // ------------------------------------------------------------------ claims and tiling

    private sealed record Claim(uint Rva, uint Size, string Kind, string? Name, string? Source);

    /// <summary>
    /// What claims a byte range inside this section, in priority order. A jump table inside a
    /// function belongs to the function — splitting a body in two would emit two symbols for one
    /// thing — so a later claim that overlaps an earlier one is dropped and reported.
    /// </summary>
    private static List<Claim> Claims(
        PeSection section,
        InventoryDocument inventory,
        List<FunctionInfo> functions,
        List<string> problems)
    {
        uint start = section.Rva;
        uint end = section.Rva + ContentExtent(section);
        var claims = new List<Claim>();

        foreach (var function in functions)
        {
            foreach (var range in function.Ranges)
            {
                if (range.Rva >= start && range.Rva < end && range.Size > 0)
                {
                    claims.Add(new Claim(range.Rva, Math.Min(range.Size, end - range.Rva), PieceKinds.Function, function.Name, function.Section));
                }
            }
        }

        // "section" rows are the inventory's per-section fallbacks, not symbols the image exports:
        // the bytes they describe are emitted either way, as this symbol or as filler.
        foreach (var data in inventory.Data.Where(d => d.Size > 0 && !string.IsNullOrEmpty(d.Name) && d.Source != "section").OrderBy(d => d.Rva))
        {
            if (data.Rva >= start && data.Rva < end)
            {
                claims.Add(new Claim(data.Rva, Math.Min(data.Size, end - data.Rva), PieceKinds.Data, data.Name, data.Source));
            }
        }

        foreach (var table in inventory.JumpTables.Where(t => t.Entries > 0).OrderBy(t => t.Rva))
        {
            if (table.Rva >= start && table.Rva < end)
            {
                var size = Math.Min((uint)table.Entries * 4, end - table.Rva);
                claims.Add(new Claim(table.Rva, size, PieceKinds.JumpTable, table.Owner, null));
            }
        }

        claims.Sort((a, b) => a.Rva != b.Rva ? a.Rva.CompareTo(b.Rva) : b.Size.CompareTo(a.Size));

        // Keep the earliest claim on each byte; a jump table inside a function loses to the function.
        var kept = new List<Claim>();
        foreach (var claim in claims)
        {
            bool overlaps = kept.Any(existing => claim.Rva < existing.Rva + existing.Size && existing.Rva < claim.Rva + claim.Size);
            if (overlaps)
            {
                problems.Add(
                    $"0x{claim.Rva:X}+0x{claim.Size:X} ({claim.Kind}{(claim.Name is null ? string.Empty : $" {claim.Name}")}) " +
                    "overlaps a piece already claimed and was dropped");
                continue;
            }

            kept.Add(claim);
        }

        return kept;
    }

    /// <summary>Walks the section from its first byte to its last, filling the gaps with filler.</summary>
    private static List<DelinkPiece> Tile(PeSection section, List<Claim> claims, List<string> problems)
    {
        var pieces = new List<DelinkPiece>();
        uint cursor = section.Rva;
        uint end = section.Rva + ContentExtent(section);

        foreach (var claim in claims)
        {
            if (claim.Rva > cursor)
            {
                pieces.Add(Filler(section.Name, cursor, claim.Rva - cursor, "between claimed pieces"));
            }

            pieces.Add(new DelinkPiece
            {
                Id = Id(claim.Kind, claim.Rva),
                Kind = claim.Kind,
                Name = claim.Name,
                Rva = claim.Rva,
                Size = claim.Size,
                Section = section.Name,
            });
            cursor = claim.Rva + claim.Size;
        }

        if (cursor < end)
        {
            pieces.Add(Filler(section.Name, cursor, end - cursor, "to the end of the section"));
        }

        // Nothing is appended past `end`: a section whose virtual size exceeds its bytes on disk
        // (.bss, typically) ends in zeroes that are already part of the range tiled above, and the
        // writer emits them as .space because no file byte describes them.

        return pieces;
    }

    /// <summary>
    /// A name the image can be relinked with. Two symbols can share a name in a symbol table — a
    /// static one in one object, an external one in another — but a relinked image cannot define one
    /// name at two addresses, so the second is given the address as part of its name and the plan
    /// says so.
    /// </summary>
    private static string UniqueName(HashSet<string> usedNames, string name, uint rva, List<string> problems)
    {
        if (usedNames.Add(name))
        {
            return name;
        }

        string unique = $"{name}_at_{rva:x}";
        while (!usedNames.Add(unique))
        {
            unique += "_";
        }

        problems.Add($"the name \"{name}\" is at more than one address; the one at 0x{rva:X} is emitted as {unique}");
        return unique;
    }

    /// <summary>
    /// The names in a section that claim no bytes: everything the symbol table knows about an address
    /// without knowing how much is there. Section fallbacks are not names.
    /// </summary>
    private static List<(uint Rva, string Name, string Kind)> Labels(
        PeSection section,
        InventoryDocument inventory,
        List<string> problems)
    {
        uint start = section.Rva;
        uint end = section.Rva + ContentExtent(section);
        var labels = new List<(uint, string, string)>();

        foreach (var data in inventory.Data.Where(d => d.Size == 0 && !string.IsNullOrEmpty(d.Name) && d.Source != "section"))
        {
            if (data.Rva >= start && data.Rva <= end)
            {
                labels.Add((data.Rva, data.Name!, "data"));
            }
        }

        return labels;
    }

    /// <summary>
    /// Puts each label in the piece that covers its address. A label is written where it belongs, not
    /// at the start of the piece that happens to contain it: a name at the wrong address is worse than
    /// no name, because a reference to it would resolve to the wrong place and still look right.
    /// </summary>
    private static void AttachLabels(
        List<DelinkPiece> pieces,
        List<(uint Rva, string Name, string Kind)> labels,
        HashSet<string> usedNames,
        List<string> problems)
    {
        if (pieces.Count == 0)
        {
            return;
        }

        foreach (var (rva, symbol, kind) in labels.OrderBy(l => l.Rva))
        {
            var piece = pieces.FirstOrDefault(p => p.Rva <= rva && rva < p.Rva + p.Size);
            if (piece is null && rva == pieces[^1].Rva + pieces[^1].Size)
            {
                // A name at the very end of the section: after the last piece's bytes, at the address.
                piece = pieces[^1];
            }

            if (piece is null)
            {
                continue;
            }

            string name = symbol;
            if (string.Equals(name, piece.Symbol, StringComparison.Ordinal) || piece.Labels.Any(l => l.Name == name))
            {
                continue;
            }

            piece.Labels.Add(new DelinkLabel { Rva = rva, Name = UniqueName(usedNames, name, rva, problems), Kind = kind });
        }
    }

    /// <summary>
    /// How many bytes of a section the plan writes back: the virtual extent, because that is the
    /// part the loader maps. A file rounds it up to the file alignment, and the rounding is zeroes.
    /// </summary>
    private static uint ContentExtent(PeSection section)
        => section.VirtualSize == 0 || section.RawSize == 0
            ? Math.Max(section.VirtualSize, section.RawSize)
            : Math.Min(section.VirtualSize, section.RawSize);

    /// <summary>The GNU as flags that give a section the characteristics the original's had.</summary>
    private static string SectionFlags(PeSection section)
    {
        const uint UninitializedData = 0x0000_0080;
        const uint Code = 0x0000_0020;
        const uint Execute = 0x2000_0000;
        const uint Write = 0x8000_0000;

        // "b" is a section with no bytes in the file: the assembler refuses to store anything in it,
        // which is exactly right, because there is nothing to store.
        if ((section.Characteristics & UninitializedData) != 0)
        {
            return "b";
        }

        var flags = new StringBuilder();
        if ((section.Characteristics & (Code | Execute)) != 0)
        {
            flags.Append('x');
        }

        flags.Append((section.Characteristics & Write) != 0 ? 'w' : 'r');
        return flags.ToString();
    }

    private static DelinkPiece Filler(string section, uint rva, uint size, string why)
        => new()
        {
            Id = Id(PieceKinds.Filler, rva),
            Kind = PieceKinds.Filler,
            Rva = rva,
            Size = size,
            Section = section,
            Notes = { why },
        };

    private static string Id(string kind, uint rva) => $"{kind}_{rva:x}";

    // ------------------------------------------------------------------ provider and names

    /// <summary>
    /// Who provides a piece. A unit's covers claim functions, and a unit that says
    /// <c>provider = "rebuilt"</c> is asking for its own build to take their place; everything else
    /// keeps the original's bytes, which is what makes a half-reconstructed image linkable at all.
    ///
    /// A cover is resolved against every range the image has, never against the piece alone: a cover
    /// that resolves by an unambiguous substring has to see what else its substring could have meant.
    /// The piece is claimed when any of the ranges the cover matched starts where it does.
    /// </summary>
    private static void DecideProvider(
        DelinkPiece piece,
        List<FunctionInfo> functions,
        List<Covers.CoverCandidate> candidates,
        ProjectConfig project,
        List<string> problems)
    {
        if (piece.Kind != PieceKinds.Function)
        {
            return;
        }

        if (!functions.Any(f => f.Ranges.Any(r => r.Rva == piece.Rva)))
        {
            return;
        }

        foreach (var unit in project.Units)
        {
            foreach (var cover in unit.Covers)
            {
                if (!Covers.Resolve(cover, candidates).Any(index => candidates[index].Rva == piece.Rva))
                {
                    continue;
                }

                piece.Unit = unit.Name;
                piece.Provider = string.Equals(unit.Provider, Providers.Rebuilt, StringComparison.Ordinal)
                    ? Providers.Rebuilt
                    : Providers.Original;

                if (piece.Provider == Providers.Rebuilt && unit.Status is "library" or "skip")
                {
                    problems.Add(
                        $"unit[{unit.Name}] claims 0x{piece.Rva:X} as rebuilt but its status is \"{unit.Status}\"; " +
                        "the original's bytes are kept");
                    piece.Provider = Providers.Original;
                }

                return;
            }
        }
    }

    private static void NamePiece(
        DelinkPiece piece,
        List<DelinkSymbolInfo> symbols,
        HashSet<string> usedNames,
        List<string> problems)
    {
        if (piece.Kind is not (PieceKinds.Function or PieceKinds.Data) || string.IsNullOrEmpty(piece.Name))
        {
            return;
        }

        // The name is kept as the original spelled it: only the assembler needs it written a
        // particular way, and that is the writer's business, not the plan's.
        string symbol = UniqueName(usedNames, piece.Name, piece.Rva, problems);
        piece.Symbol = symbol;
        piece.SymbolRenamed = !AsmWriter.IsPlainSymbol(symbol);

        if (symbols.All(existing => existing.Rva != piece.Rva))
        {
            symbols.Add(new DelinkSymbolInfo
            {
                Name = symbol,
                Rva = piece.Rva,
                Kind = piece.Kind == PieceKinds.Function ? "function" : "data",
            });
        }
    }

    // ------------------------------------------------------------------ fixups

    /// <summary>
    /// The absolute addresses inside a piece. Each one becomes a reference to the symbol at the
    /// address it points at — or, when nothing is named at that address, to a synthetic symbol that
    /// is the address. Either way the linker writes the value, not us.
    /// </summary>
    private static void AddFixups(
        DelinkPiece piece,
        List<RelocationInfo> relocations,
        List<DelinkSymbolInfo> symbols,
        List<string> problems)
    {
        uint end = piece.Rva + piece.Size;
        foreach (var relocation in relocations.Where(r => r.Rva >= piece.Rva && r.Rva + r.Width <= end))
        {
            bool emittable = relocation.Width == 4 && EmittableKinds.Contains(relocation.Kind, StringComparer.OrdinalIgnoreCase);
            var fixup = new DelinkFixup
            {
                Rva = relocation.Rva,
                Width = relocation.Width,
                Kind = relocation.Kind,
                RawValue = relocation.RawValue,
                TargetRva = relocation.TargetRva,
                Emitted = emittable,
            };

            if (emittable && relocation.TargetRva is { } target)
            {
                var named = symbols.FirstOrDefault(s => s.Rva == target);
                if (named is not null)
                {
                    fixup.TargetSymbol = named.Name;
                }
                else
                {
                    // Nothing is named at this address, so the plan names it: a reference needs a
                    // symbol, and an address with no name is still a place in the image.
                    string synthetic = AsmWriter.AbsoluteSymbol(target);
                    if (symbols.All(existing => existing.Name != synthetic))
                    {
                        symbols.Add(new DelinkSymbolInfo { Name = synthetic, Rva = target, Kind = "absolute", Synthetic = true });
                    }

                    fixup.TargetSymbol = synthetic;
                }
            }

            piece.Fixups.Add(fixup);
        }
    }
}
