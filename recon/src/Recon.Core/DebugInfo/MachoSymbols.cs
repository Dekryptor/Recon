using Recon.Images;

namespace Recon.DebugInfo;

/// <summary>
/// Turns a Mach-O symbol table into debug symbols. It is the same idea as <see cref="ElfSymbols"/>:
/// the file's own table is the description, and it is the only one a Mach-O image built without
/// debug information has. What it cannot say is how big a symbol is — <c>nlist</c> carries no size —
/// so every size is left unknown rather than being taken from the next symbol's address.
/// </summary>
public static class MachoSymbols
{
    public static DebugInfoResult? Read(IBinaryImage image)
    {
        if (image.Macho is null)
        {
            return null;
        }

        var result = new DebugInfoResult
        {
            Kind = "macho",
            FilePath = image.Path,
        };

        foreach (var symbol in image.Symbols)
        {
            if (!symbol.IsDefined || symbol.IsFile || symbol.IsSection)
            {
                continue;
            }

            if (!symbol.IsFunction && !symbol.IsObject)
            {
                continue;
            }

            result.Symbols.Add(new DebugSymbol
            {
                Name = symbol.Name,
                Rva = symbol.Rva,
                Size = null,
                Source = SymbolSource.Macho,
                IsData = symbol.IsObject,
            });

            // No compilands: which translation unit a symbol came from is not in this table. The
            // N_STAB entries that would say are debug information of their own, and an object file
            // is the only place they survive the linker.
        }

        AddStubs(image, result);
        return result.Symbols.Count == 0 ? null : result;
    }

    /// <summary>
    /// Names the stubs. A <c>__stubs</c> entry has no <c>nlist</c> of its own — most builds leave the
    /// section unnamed in the symbol table — so the indirect symbol table is the only thing in the
    /// file that says which import each stub is for. Reading it costs nothing and needs no decoding,
    /// which is the point: in an image whose instructions the decoder does not speak, these are the
    /// only named code addresses there are.
    /// </summary>
    private static void AddStubs(IBinaryImage image, DebugInfoResult result)
    {
        var macho = image.Macho!;
        foreach (var section in macho.Sections)
        {
            if (!section.IsSymbolStubs || section.Size == 0)
            {
                continue;
            }

            // The size of one stub is in the section's own reserved2 field; a section that leaves it
            // at zero is one this tool has to guess for, and six bytes is an x86-64 indirect jump.
            uint stubSize = section.Reserved2 != 0 ? section.Reserved2 : (uint)(macho.Is64 ? 6 : 5);
            if (stubSize == 0)
            {
                continue;
            }

            uint count = (uint)(section.Size / stubSize);
            for (uint index = 0; index < count; index++)
            {
                uint indirect = section.Reserved1 + index;
                if (indirect >= macho.IndirectSymbols.Count)
                {
                    break;
                }

                uint entry = macho.IndirectSymbols[(int)indirect];
                if ((entry & 0x80000000) != 0)
                {
                    continue; // INDIRECT_SYMBOL_LOCAL: a stub that stays inside the image
                }

                uint symbolIndex = entry & 0x3FFFFFFF;
                if (symbolIndex >= macho.Symbols.Count)
                {
                    continue;
                }

                ulong address = section.Address + ((ulong)index * stubSize);
                if (address < image.ImageBase)
                {
                    continue;
                }

                ulong offset = address - image.ImageBase;
                if (offset > uint.MaxValue)
                {
                    continue;
                }

                uint rva = (uint)offset;

                result.Symbols.Add(new DebugSymbol
                {
                    Name = macho.Symbols[(int)symbolIndex].Name,
                    Rva = rva,
                    // The stub's size is known: the section is a table of equal-sized entries.
                    Size = stubSize,
                    Source = SymbolSource.Macho,
                    IsData = false,
                });
            }
        }
    }
}
