using Recon.Archive;

namespace Recon.Delink;

/// <summary>
/// One reference inside a rebuilt function, resolved to a name the relinked image defines.
///
/// The plan describes the original's absolute addresses as references to symbols, and a rebuild has
/// the same problem the original did: the compiler wrote an address that only exists in this unit's
/// own object. What the compiler wrote is read here and turned into the same kind of reference the
/// rest of the image uses — a name, not a number — so the linker does the arithmetic and the rebuilt
/// function lands on the original's data and the original's functions rather than on its own.
/// </summary>
public sealed class RebuiltReference
{
    /// <summary>Offset within the function, which is where the four bytes were.</summary>
    public uint At { get; set; }

    /// <summary><c>dir32</c> (an address in four bytes) or <c>rel32</c> (a displacement from the field).</summary>
    public string Kind { get; set; } = "dir32";

    /// <summary>The addend the object's bytes carried at that offset.</summary>
    public int Addend { get; set; }

    /// <summary>The name in the relinked image the reference now points at.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>How the target was worked out, in the reader's own words.</summary>
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>A piece's bytes as the unit's own build produced them, ready to be placed at the address the plan fixed.</summary>
public sealed class RebuiltCode
{
    public string PieceId { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string ObjectPath { get; set; } = string.Empty;

    /// <summary>The function's bytes, exactly as the compiler emitted them.</summary>
    public byte[] Bytes { get; set; } = [];

    public List<RebuiltReference> References { get; set; } = [];

    /// <summary>How many bytes of the original are kept after the rebuild, when the rebuild is shorter.</summary>
    public int OriginalBytesKept { get; set; }
}

/// <summary>
/// Fills the pieces a unit claims with its own build.
///
/// This is the loop M5 exists for: the original is cut into pieces, a unit's source is compiled, and
/// the pieces that unit is responsible for are replaced by what the compiler produced — while every
/// other byte of the image stays where it was. For each piece that means reading the unit's object,
/// taking the function the piece is named after out of it, and turning the addresses inside it into
/// references to the names the image defines, so the linker does the arithmetic.
///
/// What it takes to do that honestly is a chain of refusals rather than a leap. A piece keeps the
/// original's bytes, with the reason reported, when:
///
/// * the unit's object is not its current build — the one failure that would produce a *plausible*
///   image, so it is refused first and by name;
/// * the object defines no function with that name, decorated or not, or holds only padding where one
///   should be, or the function is in a section the object does not have;
/// * the rebuild is longer than the space the original used, because the plan fixes every address;
/// * a name sits inside the rebuilt range, because a rebuild moves it and where it went is the
///   compiler's business;
/// * a reference inside it is not a plain address or a displacement, names something the relinked
///   image does not have, or points at an address neither the object nor the plan has an answer for;
/// * a reference inside it was resolved by the assembler rather than the linker — a call between two
///   functions of the same object, four finished bytes with no relocation naming them — and the
///   object's layout disagrees with the image's about something those bytes point at.
///
/// That is the behaviour the milestone is built on: a half-reconstructed image links, and it says which
/// half is which.
/// </summary>
public static class RebuiltPieces
{
    /// <summary>
    /// The pieces a rebuild can fill, by piece id. Anything claimed but not filled is in
    /// <paramref name="problems"/> with the reason.
    /// </summary>
    public static Dictionary<string, RebuiltCode> Collect(
        Project.ProjectContext context,
        DelinkDocument plan,
        List<string> problems)
    {
        var rebuilt = new Dictionary<string, RebuiltCode>(StringComparer.Ordinal);
        if (!plan.Pieces.Any(p => p.Provider == Providers.Rebuilt))
        {
            return rebuilt;
        }

        var names = Names(plan);
        var (units, refused) = PlannedUnits(context);
        foreach (var (name, reason) in refused.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            problems.Add($"unit[{name}] is claimed as rebuilt but its object is not its current build ({reason}); " +
                $"run `recon build --unit={name}` before linking, or the image would carry code this unit no longer says");
        }

        var objects = new Dictionary<string, CoffObject?>(StringComparer.Ordinal);

        foreach (var piece in plan.Pieces.Where(p => p.Provider == Providers.Rebuilt).OrderBy(p => p.Rva))
        {
            string name = piece.Name ?? piece.Symbol ?? string.Empty;
            if (piece.Unit is not { Length: > 0 } unitName)
            {
                problems.Add($"piece 0x{piece.Rva:X} is rebuilt but no unit claims it");
                continue;
            }

            if (!units.TryGetValue(unitName, out var unit))
            {
                continue;   // Either not a unit of this project, or refused above with the reason.
            }

            if (!objects.TryGetValue(unit.ObjectPath, out var object_))
            {
                object_ = File.Exists(unit.ObjectPath)
                    ? CoffObject.Load(File.ReadAllBytes(unit.ObjectPath), unit.ObjectPath)
                    : null;
                objects[unit.ObjectPath] = object_;
                if (object_ is not null)
                {
                    foreach (string problem in object_.Problems)
                    {
                        problems.Add(problem);
                    }
                }
            }

            if (object_ is null)
            {
                problems.Add($"unit[{unitName}] claims 0x{piece.Rva:X} as rebuilt but {Path.GetFileName(unit.ObjectPath)} is not there; " +
                    $"run `recon build --unit={unitName}`");
                continue;
            }

            if (name.Length == 0)
            {
                problems.Add($"piece 0x{piece.Rva:X} has no name, so a unit's build cannot be matched to it");
                continue;
            }

            var symbol = object_.Function(name);
            if (symbol is null || !symbol.IsDefined)
            {
                problems.Add($"unit[{unitName}] claims 0x{piece.Rva:X} ({name}) as rebuilt but {Path.GetFileName(unit.ObjectPath)} " +
                    $"does not define a function with that name; the original's bytes are kept");
                continue;
            }

            var section = object_.Section(symbol.Section);
            if (section is null)
            {
                problems.Add($"unit[{unitName}]: {name} is defined in section {symbol.Section} of {Path.GetFileName(unit.ObjectPath)}, " +
                    "which the object does not have; the original's bytes are kept");
                continue;
            }

            var code = Take(object_, section, symbol, piece, unitName, name, names, problems);
            if (code is not null)
            {
                code.Source = unit.Source;
                code.ObjectPath = unit.ObjectPath;
                rebuilt[piece.Id] = code;
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// The function's bytes as the object holds them, and what its references mean. A rebuild that
    /// cannot be placed produces a problem and no code, which leaves the piece with the original's
    /// bytes.
    /// </summary>
    private static RebuiltCode? Take(
        CoffObject object_,
        CoffObjectSection section,
        CoffObjectSymbol symbol,
        DelinkPiece piece,
        string unitName,
        string name,
        ImageNames names,
        List<string> problems)
    {
        string file = Path.GetFileName(object_.Path);
        if (symbol.Value >= section.Bytes.Length)
        {
            problems.Add($"unit[{unitName}]: {name} is at offset 0x{symbol.Value:X} of {section.Name}, past the {section.Bytes.Length} bytes " +
                $"the object holds for it; the original's bytes are kept");
            return null;
        }

        // Where the function ends inside the object: the next name in the same section, or the
        // section's end. What a compiler emits after a function is its padding, so the padding is
        // trimmed before the size is compared with the piece — a rebuild is not too big because the
        // compiler aligned the function after it.
        uint end = (uint)section.Bytes.Length;
        foreach (var other in object_.Symbols)
        {
            if (other.Section == symbol.Section && other.Value > symbol.Value && other.Value < end && !other.IsSection)
            {
                end = other.Value;
            }
        }

        int available = (int)(end - symbol.Value);
        int size = Trim(section.Bytes, (int)symbol.Value, available);
        if (size <= 0)
        {
            problems.Add($"unit[{unitName}]: {name} has no instructions in {file} (the object holds {available} byte(s) of padding " +
                "where the function should be); the original's bytes are kept");
            return null;
        }

        if (size > piece.Size)
        {
            problems.Add($"unit[{unitName}]: the rebuild of {name} is {size} byte(s) and the original's is {piece.Size}; " +
                "the plan fixes every address, so a longer function cannot be placed and the original's bytes are kept");
            return null;
        }

        // A name inside the rebuilt range would move with the layout, and the layout is the compiler's
        // now: nothing can be said about where it went, so nothing is emitted for the piece.
        if (piece.Labels.Any(l => l.Rva > piece.Rva && l.Rva < piece.Rva + size))
        {
            problems.Add($"unit[{unitName}]: {name} has a name at 0x{piece.Labels.First(l => l.Rva > piece.Rva && l.Rva < piece.Rva + size).Rva:X} " +
                "inside it, which a rebuild moves; the original's bytes are kept");
            return null;
        }

        byte[] codeBytes = section.Bytes[(int)symbol.Value..((int)symbol.Value + size)];
        if (UnfixableReference(object_, section, symbol, piece, codeBytes, names) is { } unfixable)
        {
            problems.Add($"unit[{unitName}]: {unfixable}; the original's bytes are kept");
            return null;
        }

        var code = new RebuiltCode
        {
            PieceId = piece.Id,
            Unit = unitName,
            Source = object_.Path,
            ObjectPath = object_.Path,
            Bytes = codeBytes,
            OriginalBytesKept = (int)piece.Size - size,
        };

        foreach (var relocation in section.Relocations)
        {
            if (relocation.At < symbol.Value || relocation.At >= symbol.Value + size)
            {
                continue;
            }

            if (relocation.Kind is not ("dir32" or "rel32"))
            {
                problems.Add($"unit[{unitName}]: {name}+0x{relocation.At - symbol.Value:X} is a {relocation.Kind} reference " +
                    $"({file}), which this reader does not place; the original's bytes are kept");
                return null;
            }

            var target = object_.SymbolAt(relocation.SymbolIndex);
            if (target is null)
            {
                problems.Add($"unit[{unitName}]: {name}+0x{relocation.At - symbol.Value:X} refers to symbol record " +
                    $"{relocation.SymbolIndex}, which {file} does not have; the original's bytes are kept");
                return null;
            }


            uint at = relocation.At - symbol.Value;
            int addend = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                section.Bytes.AsSpan((int)relocation.At, 4));

            var targetName = ResolveTarget(object_, target, addend, names, piece, piece.Rva + at, out string evidence);
            if (targetName is null)
            {
                problems.Add($"unit[{unitName}]: {name}+0x{at:X} refers to {Describe(target, addend)}, which the relinked image does " +
                    $"not define ({evidence}); the original's bytes are kept");
                return null;
            }

            code.References.Add(new RebuiltReference
            {
                At = at,
                Kind = relocation.Kind,
                Addend = targetName.Value.Addend,
                Symbol = targetName.Value.Symbol,
                Evidence = evidence,
            });
        }

        return code;
    }

    /// <summary>
    /// A reference the relink cannot fix, or null.
    ///
    /// A reference between two things in the same object section is resolved by the *assembler*: the
    /// four bytes hold a finished distance and no relocation names it, so nothing in the object says
    /// what it points at. Placing those bytes is right only when the object and the image agree about
    /// where the unit's names live — which they do exactly when the unit is the image's own object,
    /// the case the whole loop is for. This checks that agreement, and then, for every name the
    /// object places somewhere the image does not, looks for a field in the rebuilt bytes that is a
    /// reference to it: a call or jump written as a distance from itself, or an address written out
    /// in full. Either one is a finished address baked into bytes the plan cannot re-fix, so the
    /// piece keeps the original's bytes instead.
    /// </summary>
    private static string? UnfixableReference(
        CoffObject object_,
        CoffObjectSection section,
        CoffObjectSymbol symbol,
        DelinkPiece piece,
        byte[] bytes,
        ImageNames names)
    {
        long origin = (long)piece.Rva - symbol.Value;
        foreach (var other in object_.Symbols)
        {
            if (other.IsSection || !other.IsDefined || other.Section != symbol.Section || other.Value == symbol.Value)
            {
                continue;
            }

            string? plain = other.PlainName;
            if (plain is not { Length: > 0 } || !names.ByName.TryGetValue(plain, out var known))
            {
                continue;
            }

            long predicted = origin + other.Value;
            if (predicted == known.Rva)
            {
                continue;   // The object and the image agree about this one; its bytes travel with it.
            }

            // Misplaced, which only matters if the rebuild refers to it. A reference is exact, so
            // testing for one does not mistake an immediate for an address: a value that is the
            // symbol's offset in the object, or a distance ending on it, is a reference and nothing
            // else. The rebuild is a function, so a handful of fields each way.
            for (int at = 0; at + 4 <= bytes.Length; at++)
            {
                int field = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4));
                long here = symbol.Value + at + 4;      // the field's end, in the object's coordinates
                bool distance = field != 0 && here + field == other.Value;
                bool address = field != 0 && field == (long)other.Value;
                if (!distance && !address)
                {
                    continue;
                }

                return $"the object puts {other.Name} at +0x{other.Value:X} of {section.Name}, which would be " +
                    $"0x{predicted:X} in the image, where {known.Name} is at 0x{known.Rva:X}; {symbol.PlainName}+0x{at:X} " +
                    $"refers to it {(distance ? "as a distance" : "as an address")} the assembler already resolved, " +
                    "which the relink cannot fix";
            }
        }

        return null;
    }

    /// <summary>
    /// The name in the relinked image a reference points at, or null when there is none.
    ///
    /// The object's own spelling is the compiler's business: a call to <c>_bump</c> is a call to the
    /// image's <c>bump</c>, and <c>.bss + 0x3C</c> is the image's <c>g_counter</c> if the object has a
    /// name at exactly that offset. Everything is resolved through the name, which is what makes a
    /// rebuilt function reference the original's data rather than its own: this unit's <c>.bss</c> is
    /// 0x3c bytes away from where the original keeps the same variable.
    /// </summary>
    private static RebuiltTarget? ResolveTarget(
        CoffObject object_,
        CoffObjectSymbol target,
        int addend,
        ImageNames names,
        DelinkPiece piece,
        uint slot,
        out string evidence)
    {
        evidence = string.Empty;
        string? plain = target.PlainName;

        if (target.IsSection)
        {
            // A reference to `.data + N`: a section symbol carries no name of its own, so the name is
            // whatever the object defines at that exact offset in that section.
            var named = object_.Symbols.FirstOrDefault(s =>
                !s.IsSection && s.IsDefined && s.Section == target.Section && s.Value == (uint)addend);
            if (named is not null)
            {
                plain = named.PlainName;
                evidence = $"{target.Name} + 0x{addend:X} is {named.Name}";

                // The name is the address the offset was counting to, so the offset has been spent:
                // the expression is the name, not the name plus the distance that found it. A compiler
                // writes a reference to another function or variable as `.text + 0x80` — the section
                // and how far in — and the object's own name for it sits at exactly 0x80.
                addend = 0;
            }
            else
            {
                // Nothing in the object names that offset — a switch table, a string literal — so the
                // plan's own answer for that address is the only one there is, and it has one wherever
                // the original itself held an address in that slot.
                var inSlot = InSlot(piece, slot, out string why);
                evidence = why;
                return inSlot;
            }
        }
        else
        {
            evidence = target.IsDefined ? $"object symbol {target.Name}" : $"undefined symbol {target.Name}";
        }

        if (plain is null || plain.Length == 0)
        {
            return null;
        }

        if (names.ByName.TryGetValue(plain, out var inImage))
        {
            return new RebuiltTarget(inImage.Name, addend, evidence);
        }

        return null;
    }

    /// <summary>
    /// What the plan itself answers for the address at this slot, when the original had one there.
    ///
    /// The plan wrote a fixup for every address the original image held — the base relocation table
    /// says where they all are, and the plan resolved each one to a name or to an address of its own.
    /// A rebuild that is the original's code has an address in the same slot, because the compiler put
    /// it where the compiler put it the first time: the object's relocation is at that offset, and the
    /// plan's fixup is at that address. So the question "what is this address" was already answered
    /// for this address, and the answer is the plan's. Only an address of the same shape is taken:
    /// an absolute field is not answered by a relative fixup, whatever the offsets say.
    /// </summary>
    private static RebuiltTarget? InSlot(DelinkPiece piece, uint slot, out string evidence)
    {
        evidence = $"0x{slot:X}";
        var fixup = piece.Fixups.FirstOrDefault(f => f.Rva == slot && f.Emitted && f.TargetSymbol is { Length: > 0 });
        if (fixup is null)
        {
            evidence += " has no fixup of the plan's own";
            return null;
        }

        evidence += $" is where the original had {fixup.Kind} to {fixup.TargetSymbol}, which is what the plan wrote there";
        return new RebuiltTarget(fixup.TargetSymbol!, 0, evidence);
    }

    private static string Describe(CoffObjectSymbol target, int addend)
        => target.IsSection ? $"{target.Name} + 0x{addend:X}" : target.Name;

    /// <summary>
    /// The encodings a compiler pads a function with for the next one's alignment. These are the ones
    /// the i386 assemblers emit — the multi-byte no-ops, and the <c>lea</c> of a register into itself
    /// that GCC used before it had a long enough no-op to work with, each optionally behind the <c>cs</c>
    /// prefix it uses to pad by odd amounts. A build's padding is not part of the function, and the
    /// compiler is free to use a different amount of it than the original did.
    /// </summary>
    private static readonly byte[][] Padding =
    [
        [0x90],
        [0xCC],
        [0x66, 0x90],
        [0x66, 0x66, 0x90],
        [0x0F, 0x1F, 0x00],
        [0x0F, 0x1F, 0x40, 0x00],
        [0x0F, 0x1F, 0x44, 0x00, 0x00],
        [0x0F, 0x1F, 0x80, 0x00, 0x00, 0x00, 0x00],
        [0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x8D, 0x76, 0x00],
        [0x8D, 0x74, 0x26, 0x00],
        [0x8D, 0xB6, 0x00, 0x00, 0x00, 0x00],
        [0x8D, 0xB4, 0x26, 0x00, 0x00, 0x00, 0x00],
        [0x2E, 0x8D, 0x76, 0x00],
        [0x2E, 0x8D, 0x74, 0x26, 0x00],
        [0x2E, 0x8D, 0xB4, 0x26, 0x00, 0x00, 0x00, 0x00],
        [0x2E, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x66, 0x2E, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00],
    ];

    /// <summary>
    /// How many bytes of a function are its code: what it starts with, with the alignment padding
    /// after it removed. Only encodings that exist to be padding are trimmed — a <c>0x00</c> can be a
    /// real instruction, and a size that is silently a byte or two short is a function that is
    /// silently truncated.
    /// </summary>
    private static int Trim(byte[] bytes, int start, int available)
    {
        int end = start + available;
        bool trimmed = true;
        while (trimmed && end > start)
        {
            trimmed = false;

            // The longest match wins: the short no-ops are the tails of the long ones, and taking
            // the short one first leaves the prefix byte of the long one behind as a byte of "code".
            byte[]? longest = null;
            foreach (byte[] padding in Padding)
            {
                if ((longest is null || padding.Length > longest.Length)
                    && end - padding.Length >= start
                    && bytes.AsSpan(end - padding.Length, padding.Length).SequenceEqual(padding))
                {
                    longest = padding;
                }
            }

            if (longest is not null)
            {
                end -= longest.Length;
                trimmed = true;
            }
        }

        // A prefix byte on its own is not an instruction, so one left dangling is padding too —
        // the tail of a no-op whose body was the byte before it.
        if (end > start && end < start + available && bytes[end - 1] is 0x2E or 0x66)
        {
            end--;
        }

        return end - start;
    }

    /// <summary>
    /// Every name the relinked image defines, by the name and by the name with its C decoration
    /// removed — the second key is what lets an object's <c>_bump</c> find the plan's <c>bump</c>.
    /// A name defined twice keeps the first, and the plan's own rule is that the earlier address wins.
    /// </summary>
    private static ImageNames Names(DelinkDocument plan)
    {
        var names = new ImageNames();
        foreach (var piece in plan.Pieces.OrderBy(p => p.Rva))
        {
            names.Add(piece.Symbol, piece.Rva);
            foreach (var label in piece.Labels)
            {
                names.Add(label.Name, label.Rva);
            }
        }

        foreach (var symbol in plan.Symbols)
        {
            names.Add(symbol.Name, symbol.Rva);
        }

        return names;
    }

    /// <summary>A name the relinked image defines: how to spell it, and where it ends up.</summary>
    private readonly record struct PlanSymbol(string Name, uint Rva);

    /// <summary>
    /// A reference a rebuilt function makes, as an expression the assembler can write: the name, and
    /// how far past it the reference points.
    /// </summary>
    private readonly record struct RebuiltTarget(string Symbol, int Addend, string Evidence);

    /// <summary>
    /// The names the relinked image defines: how to spell each one, and where it ends up — both under
    /// the name and under the name with its C decoration removed, which is how an object's <c>_bump</c>
    /// finds the plan's <c>bump</c>. The spelling is the plan's own, because that is the one the
    /// assembly being written can refer to.
    /// </summary>
    private sealed class ImageNames
    {
        public Dictionary<string, PlanSymbol> ByName { get; } = new(StringComparer.Ordinal);

        public void Add(string? name, uint rva)
        {
            if (name is not { Length: > 0 })
            {
                return;
            }

            var symbol = new PlanSymbol(name, rva);
            ByName.TryAdd(name, symbol);
            string plain = CoffObject.PlainSymbolName(name);
            if (plain != name)
            {
                ByName.TryAdd(plain, symbol);
            }
        }
    }

    /// <summary>
    /// The project's rebuildable units, split into those whose objects are their current build and
    /// those that are not, with the reason.
    ///
    /// The build system already answers this question — a unit is current when its cache key, which
    /// covers the source bytes, the flags, the toolchain and the headers the compiler reported last
    /// time, is the one the manifest recorded and the object is still there. Reusing that answer is
    /// the point: a second opinion about staleness written here would be a second definition of it,
    /// and linking a stale object is the one mistake that produces an image that looks reconstructed
    /// and is not.
    /// </summary>
    private static (Dictionary<string, Build.PlannedUnit> Current, Dictionary<string, string> Refused) PlannedUnits(
        Project.ProjectContext context)
    {
        var current = new Dictionary<string, Build.PlannedUnit>(StringComparer.Ordinal);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);

        var manifest = Build.BuildManifestStore.Load(
            Path.Combine(context.Project.RootDirectory, context.Project.Paths.Build, "build.json"));
        var plan = Build.BuildPlanner.Plan(context, new Build.BuildOptions(), manifest);

        foreach (var unit in plan.Units)
        {
            if (unit.IsOriginal)
            {
                continue;
            }

            if (unit.Cached)
            {
                current[unit.Name] = unit;
                continue;
            }

            refused[unit.Name] = !File.Exists(unit.ObjectPath)
                ? $"there is no object at {unit.ObjectPath}"
                : manifest is null
                    ? "it has never been built"
                    : unit.CacheReason.Length > 0 ? unit.CacheReason : "what it is built from has changed";
        }

        return (current, refused);
    }
}
