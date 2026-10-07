using System.Text;
using Recon.Pe;

namespace Recon.Delink;

/// <summary>
/// Writes a section of the original as assembly: the bytes as bytes, every absolute address as a
/// reference, and every piece at the offset it belongs at. The result is deliberately dumb — it is
/// the original, transcribed — because dumb is what makes the relinked image linkable when only
/// some of it has been reconstructed.
/// </summary>
public static class AsmWriter
{
    public const string Prefix = "recon_abs_";

    private const int BytesPerLine = 16;

    private const int SpaceThreshold = 16;

    /// <summary>
    /// The name a section is emitted under. PE section names are not always assembler section names:
    /// a long one is stored as <c>/4</c> with the real name in the string table, and <c>/</c> starts
    /// a comment in GNU as. The original name stays in the plan; this is what the assembler sees.
    /// </summary>
    public static string SectionName(string sectionName)
    {
        var text = new StringBuilder(sectionName.Length + 1);
        foreach (char c in sectionName)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_');
        }

        if (text.Length == 0)
        {
            return "recon_section";
        }

        return char.IsAsciiDigit(text[0]) ? "_" + text : text.ToString();
    }

    /// <summary>
    /// The assembler section a section's bytes are written into, which is the output name — the
    /// assembler sets a section's alignment and flags from its name, and a section it recognises is
    /// one it gets right: a <c>.debug_*</c> section is byte-aligned (a debug section is not padded to
    /// a four-byte boundary, and padding it changes the image's section sizes), <c>.bss</c> takes no
    /// contents, <c>.text</c> is code. The exception is a name the assembler can write on its own,
    /// which is prefixed so that the two are never the same section.
    /// </summary>
    public static string InputSectionName(string? output)
    {
        string name = output ?? "section";
        return AssemblerOwn.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? ReconPrefix + name
            : name;
    }

    /// <summary>The prefix that keeps our bytes out of a section the assembler writes for itself.</summary>
    public const string ReconPrefix = ".recon";

    /// <summary>
    /// Section names an assembler can produce without being asked: a <c>.debug_line</c> for the file
    /// it is assembling, for instance. Ours are written under a prefixed name, and the assembler's
    /// own copy is discarded — the linker drops an input section it cannot place into the output
    /// section of the same name, which would put the assembler's bytes in front of ours.
    /// </summary>
    public static readonly string[] AssemblerOwn =
    [
        ".debug_line",
        ".line",
        ".debug",
        ".comment",
        ".drectve",
        ".stab",
        ".stabstr",
    ];

    /// <summary>Sections the assembler writes for itself, as patterns for a linker script.</summary>
    public static IEnumerable<string> AssemblerOwnPatterns()
    {
        foreach (string name in AssemblerOwn)
        {
            yield return name;
        }

        yield return ".note*";
        yield return ".debug$*";
    }

    /// <summary>
    /// The flags to write for a section, which is nothing when they only restate what the assembler
    /// assumes: it creates .text, .data and .bss before it reads a line of ours, and restating their
    /// attributes is a warning it then ignores. The flags it assumes are the ones asked for here.
    /// </summary>
    public static string SectionFlagsFor(string inputName, string flags) => inputName switch
    {
        ".text" when flags == "xr" => string.Empty,
        ".data" when flags == "w" => string.Empty,
        ".bss" when flags == "b" => string.Empty,
        _ => flags,
    };

    /// <summary>The assembly file a section is written to.</summary>
    public static string SourceFileName(string sectionName)
    {
        var text = new StringBuilder();
        foreach (char c in sectionName.TrimStart('.'))
        {
            text.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return (text.Length == 0 ? "section" : text.ToString()) + ".s";
    }

    /// <summary>
    /// A name the assembler will accept. Names that had to be reshaped are marked as such in the
    /// plan, because a symbol that is not the name in the original is a name someone will look for
    /// and not find.
    /// </summary>
    public static string SanitizeSymbol(string name)
    {
        var text = new StringBuilder(name.Length + 1);
        foreach (char c in name)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '$' or '@' ? c : '_');
        }

        if (text.Length == 0)
        {
            return "_recon_unnamed";
        }

        return char.IsAsciiDigit(text[0]) ? "_" + text : text.ToString();
    }

    /// <summary>True when an assembler reads the name as a symbol without being told how.</summary>
    public static bool IsPlainSymbol(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '$' or '@'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// How a name is spelled in assembly. A decorated C++ name is not a symbol an assembler reads on
    /// its own, but it is a name, and it is written in quotes rather than reshaped: a name the
    /// relinked image spells differently is a name the original does not have.
    /// </summary>
    public static string SymbolText(string name)
    {
        if (IsPlainSymbol(name))
        {
            return name;
        }

        // Quoting takes almost anything; a name that quotes cannot carry has to be reshaped.
        if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is '"' or '\\'))
        {
            return SanitizeSymbol(name);
        }

        return "\"" + name + "\"";
    }

    /// <summary>The invented name for an address nothing has a name for.</summary>
    public static string AbsoluteSymbol(uint rva) => Prefix + rva.ToString("x8");

    /// <summary>One assembly file per emitted section, in the order the linker will see them.</summary>
    public static List<(string FileName, string Text)> Emit(
        PeImage image,
        byte[] bytes,
        DelinkDocument plan,
        IReadOnlyDictionary<string, RebuiltCode>? rebuilt = null,
        bool positionalNames = false)
    {
        var files = new List<(string, string)>();
        foreach (var section in plan.Sections.Where(s => s.Emitted))
        {
            files.Add((section.Source ?? SourceFileName(section.Name), EmitSection(image, bytes, plan, section.Name, rebuilt, positionalNames)));
        }

        return files;
    }

    /// <summary>
    /// The name a section is emitted under when a script-less linker is doing the placing.
    ///
    /// That linker has no option for an address: a section lands where its alignment and its *name*
    /// put it. A linker script says "this section starts here"; the only thing lld-link and link.exe
    /// can be told the same thing with is the order of their input sections, which for names they do
    /// not know is the order of the names. So the pieces are named after their position in the image
    /// - `.recon00`, `.recon01`, and so on, in RVA order - and the original's names are written into
    /// the section table afterwards, once each section has landed where the plan says it belongs.
    /// Measured on a real MSVC x64 DLL: named this way, lld-link lays six sections out in exactly the
    /// order the plan has them; named the original's way, it swaps `.rsrc` and `.reloc`, because an
    /// input section called `.reloc` goes into lld-link's built-in slot for one.
    /// </summary>
    public static string PositionalName(int index) => $".recon{index:D2}";

    /// <summary>
    /// The positional name of every section, in the order they are laid out, with the original's name
    /// beside it. The two callers are the emitter and the step that restores the names after the link.
    /// </summary>
    public static List<(uint Rva, string Positional, string Original)> PositionalNames(DelinkDocument plan)
    {
        var names = new List<(uint, string, string)>();
        int index = 0;
        foreach (var section in plan.Sections.Where(s => s.Emitted).OrderBy(s => s.Rva))
        {
            names.Add((section.Rva, PositionalName(index++), section.Name));
        }

        return names;
    }

    public static string EmitSection(
        PeImage image,
        byte[] bytes,
        DelinkDocument plan,
        string sectionName,
        IReadOnlyDictionary<string, RebuiltCode>? rebuilt = null,
        bool positionalNames = false)
    {
        var body = new StringBuilder();
        var sectionInfo = plan.Sections.First(s => s.Name == sectionName);
        body.Append("# generated by recon delink ").Append(DelinkSchema.Version).Append(" - do not edit\n");
        body.Append("# section ").Append(sectionName)
            .Append(" of ").Append(Path.GetFileName(image.Path)).Append('\n');
        string input = positionalNames
            ? PositionalNames(plan).First(p => p.Original == sectionName).Positional
            : sectionInfo.Input ?? InputSectionName(sectionInfo.Output ?? SectionName(sectionName));
        body.Append("\t.section ").Append(input);
        string flags = SectionFlagsFor(input, sectionInfo.Flags);
        if (flags.Length > 0)
        {
            body.Append(", \"").Append(flags).Append('"');
        }

        body.Append('\n');

        // The zero-filled tail of the section, in a block of its own, before the pieces: an
        // uninitialised section has no place in the file, so a linker that is handed one extends the
        // section it belongs to without writing bytes - which is the whole point, because the bytes
        // are not in the original's file either. Whichever linker reads this, the *name* is what makes
        // it join the right section: lld-link and link.exe fold `.bss` into `.data`, and a linker
        // script is told to, because neither of them was given the original's virtual size.
        void EmitTail()
        {
            if (sectionInfo.TailBytes <= 0)
            {
                return;
            }

            body.Append("\n\t# the section's zero-filled tail, ").Append(sectionInfo.TailBytes)
                .Append(" byte(s) that are in memory and not in the file\n");
            // `.bss`, never the section's own name: the linker has to be able to tell the tail from the
            // section's bytes. Told apart, lld-link folds this into the section it follows and lets the
            // size grow without writing a byte of it - which is the whole point, because the bytes are
            // not in the original's file either. Under the section's own name the space would count as
            // content: measured, lld-link then writes the tail out and rounds the raw size up to the
            // file alignment, 0x1a00 instead of the original's 0xa00, and every raw offset after it.
            body.Append("\t.section .bss\n");
            body.Append("\t.space 0x").Append(sectionInfo.TailBytes.ToString("X")).Append(", 0\n\n");
            body.Append("\t.section ").Append(input);
            string again = SectionFlagsFor(input, sectionInfo.Flags);
            if (again.Length > 0)
            {
                body.Append(", \"").Append(again).Append('"');
            }

            body.Append('\n');
        }

        EmitTail();

        uint sectionRva = sectionInfo.Rva;
        foreach (var piece in plan.Pieces.Where(p => p.Section == sectionName).OrderBy(p => p.Rva))
        {
            body.Append('\n');
            body.Append("\t# ").Append(piece.Kind)
                .Append(piece.Name is null ? string.Empty : " " + piece.Name)
                .Append(" at 0x").Append(piece.Rva.ToString("X"))
                .Append(", ").Append(piece.Size).Append(" byte(s)");
            if (piece.Unit is not null)
            {
                body.Append(", unit ").Append(piece.Unit).Append(" (").Append(piece.Provider).Append(')');
            }

            RebuiltCode? code = null;
            if (rebuilt is not null && rebuilt.TryGetValue(piece.Id, out var found))
            {
                code = found;
                body.Append(", from ").Append(code.Source);
                body.Append(", ").Append(code.Bytes.Length).Append(" byte(s), ")
                    .Append(code.References.Count).Append(" reference(s)");
                if (code.OriginalBytesKept > 0)
                {
                    body.Append(", ").Append(code.OriginalBytesKept).Append(" byte(s) of the original after it");
                }
            }

            body.Append('\n');

            // A guard, not a filler: if the plan tiles the section correctly this is a no-op, and if
            // it does not, the assembler stops instead of quietly producing a shifted image.
            uint offset = piece.Rva - sectionRva;
            body.Append("\t.org 0x").Append(offset.ToString("X")).Append('\n');

            if (piece.Symbol is { Length: > 0 } symbol)
            {
                EmitSymbol(body, symbol, piece.Kind == PieceKinds.Function ? "function" : "object");
            }

            EmitBytes(body, image, bytes, piece, code);
        }

        body.Append('\n');
        body.Append("\t.end\n");
        return body.ToString();
    }

    /// <summary>
    /// One reference of a rebuilt function, as an assembly expression.
    ///
    /// An address is the name of what it points at plus the addend the compiler left in the four
    /// bytes. A displacement is the same thing relative to the field's own address, which is what a
    /// COFF <c>rel32</c> means and what the assembler will fold into a constant whenever the target is
    /// in this same output section.
    /// </summary>
    private static string Reference(RebuiltReference reference)
    {
        var text = new StringBuilder();
        text.Append(SymbolText(reference.Symbol));
        if (reference.Addend != 0)
        {
            text.Append(" + ").Append(reference.Addend);
        }

        if (reference.Kind == "rel32")
        {
            text.Append(" - . - 4");
        }

        return text.ToString();
    }

    /// <summary>
    /// A name in the relinked image: global, so the linker keeps it, and typed, so the image's symbol
    /// table says what it names. COFF spells a typed symbol <c>.def</c>/<c>.endef</c> — the ELF
    /// <c>.type</c> is a no-op here — and without it every name in the relinked image looks like data,
    /// which is a difference that is not in the bytes.
    /// </summary>
    private static void EmitSymbol(StringBuilder body, string symbol, string kind)
    {
        string text = SymbolText(symbol);
        body.Append("\t.globl ").Append(text).Append('\n');
        body.Append("\t.def ").Append(text).Append("; .scl 2; .type ").Append(kind == "function" ? "32" : "0").Append("; .endef\n");
        body.Append(text).Append(":\n");
    }

    /// <summary>
    /// A piece's bytes, with every address written as the name of what it points at.
    ///
    /// When the piece is filled by a unit's own build, the bytes are the compiler's and so are the
    /// addresses — but the addresses are the *unit's*, and the unit is not this image. What is written
    /// for those four bytes is therefore a reference to the name the plan defines, exactly as the
    /// original's own addresses are: `<c>.long bump</c>` for an address, and
    /// <c>.long g_counter + 8 - . - 4</c> for a displacement, both of which the linker resolves with
    /// its own arithmetic. A compiler's address and this image's address for the same name are the
    /// same address by the time the linker is done, and the middle step — which unit thought the
    /// variable was where — never has to be guessed at.
    /// </summary>
    private static void EmitBytes(
        StringBuilder body,
        PeImage image,
        byte[] bytes,
        DelinkPiece piece,
        RebuiltCode? rebuilt = null)
    {
        var fixups = piece.Fixups
            .Where(f => f.Emitted && f.TargetSymbol is not null)
            .ToDictionary(f => f.Rva, f => f.TargetSymbol!);

        // Labels sorted by address, so the walk below can emit each one when it reaches it.
        var labels = piece.Labels.OrderBy(l => l.Rva).ToList();
        int next = 0;

        void EmitLabelsAt(uint rva)
        {
            while (next < labels.Count && labels[next].Rva <= rva)
            {
                FlushRun();
                FlushZeros();
                EmitSymbol(body, labels[next].Name, labels[next].Kind == "function" ? "function" : "object");
                next++;
            }
        }

        uint rva = piece.Rva;
        uint end = piece.Rva + piece.Size;
        var run = new List<byte>(BytesPerLine);
        int zeros = 0;

        // A rebuild's own references, by the address they sit at, and the address the rebuild stops
        // at: past it the piece is the original again, because a shorter function leaves the bytes
        // that were there — the plan's own fixups and labels still apply to them.
        Dictionary<uint, RebuiltReference> references = [];
        uint rebuiltEnd = rva;
        if (rebuilt is not null)
        {
            rebuiltEnd = (uint)(piece.Rva + rebuilt.Bytes.Length);
            foreach (var reference in rebuilt.References)
            {
                references[piece.Rva + reference.At] = reference;
            }
        }

        void FlushRun()
        {
            if (run.Count == 0)
            {
                return;
            }

            body.Append("\t.byte ");
            body.Append(string.Join(", ", run.Select(b => "0x" + b.ToString("x2"))));
            body.Append('\n');
            run.Clear();
        }

        void FlushZeros()
        {
            if (zeros == 0)
            {
                return;
            }

            body.Append("\t.space ").Append(zeros).Append(", 0\n");
            zeros = 0;
        }

        while (rva < end)
        {
            EmitLabelsAt(rva);

            if (rebuilt is not null && rva < rebuiltEnd)
            {
                // The compiler's byte, or the reference it wrote where an address was.
                if (references.TryGetValue(rva, out var reference))
                {
                    FlushRun();
                    FlushZeros();
                    body.Append("\t.long ").Append(Reference(reference)).Append('\n');
                    rva += 4;
                    continue;
                }

                FlushZeros();
                byte emitted = rebuilt.Bytes[(int)(rva - piece.Rva)];
                if (emitted == 0)
                {
                    FlushRun();
                    zeros += 1;
                }
                else
                {
                    run.Add(emitted);
                    if (run.Count == BytesPerLine)
                    {
                        FlushRun();
                    }
                }

                rva += 1;
                continue;
            }

            if (fixups.TryGetValue(rva, out var target))
            {
                FlushRun();
                FlushZeros();
                body.Append("\t.long ").Append(SymbolText(target)).Append('\n');
                rva += 4;
                continue;
            }

            int? offset = image.SectionContainingRva(rva)?.RvaToOffset(rva);
            if (offset is null || offset.Value >= bytes.Length)
            {
                // Past the bytes on disk but inside the section: the image has it as zeroes.
                FlushRun();
                zeros += 1;
                rva += 1;
                continue;
            }

            byte value = bytes[offset.Value];
            if (value == 0)
            {
                FlushRun();
                zeros += 1;
            }
            else
            {
                FlushZeros();
                run.Add(value);
                if (run.Count == BytesPerLine)
                {
                    FlushRun();
                }
            }

            rva += 1;
        }

        FlushRun();
        FlushZeros();

        // A name at the end of the piece: the address the last byte left behind.
        EmitLabelsAt(end);
    }
}
