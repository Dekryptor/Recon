using Recon.Images;

namespace Recon.DebugInfo;

/// <summary>
/// Turns an ELF symbol table into debug symbols: <c>.symtab</c> for a file that still has one, and
/// <c>.dynsym</c> for what it imports and exports. This is the ELF equivalent of the COFF table on
/// the PE side, and it is read as debug information for the same reason — the analysis asks "what
/// is named here", not "what does this format's loader do".
/// </summary>
public static class ElfSymbols
{
    public static DebugInfoResult? Read(IBinaryImage image)
    {
        if (image.Elf is null)
        {
            return null;
        }

        var result = new DebugInfoResult
        {
            Kind = "elf",
            FilePath = image.Path,
        };

        // STT_FILE symbols name the translation unit every symbol after them belongs to, which is
        // how the inventory attributes a function to a source file without any side file.
        string? unit = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in image.Symbols)
        {
            if (!symbol.IsDefined)
            {
                continue;
            }

            if (symbol.IsFile)
            {
                unit = DwarfSymbols.UnitName(symbol.Name);
                if (unit is not null && !seen.Contains(unit))
                {
                    seen.Add(unit);
                    result.Compilands.Add(new CompilandInfo { Unit = unit, ObjectFile = symbol.Name });
                }

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
                // A zero size is "the symbol table does not know", not "this is zero bytes long".
                Size = symbol.Size > 0 ? symbol.Size : null,
                Source = SymbolSource.Elf,
                IsData = symbol.IsObject,
                Unit = unit,
            });
        }

        return result.Symbols.Count == 0 ? null : result;
    }
}
