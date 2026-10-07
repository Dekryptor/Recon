using Recon.Images;

namespace Recon.DebugInfo;

/// <summary>
/// Turns the subprograms of a DWARF unit into debug symbols. GCC and Clang on Windows (MinGW and
/// clang-cl with <c>-gdwarf</c>) write DWARF into the image itself, so this is the highest-quality
/// symbol source for a MinGW target: names, exact addresses and exact sizes, with no side files.
/// </summary>
public static class DwarfSymbols
{
    public static DebugInfoResult Read(IBinaryImage image, DwarfInfo dwarf)
    {
        var result = new DebugInfoResult
        {
            Kind = "dwarf",
            FilePath = image.Path,
        };

        result.Problems.AddRange(dwarf.Problems);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        int splitFunctions = 0;
        foreach (var function in dwarf.Functions)
        {
            if (function.IsDeclaration)
            {
                // A DIE that only declares: the headers of a C++ binary are full of them, because
                // every inline function and template they mention gets a declaration DIE.
                declared.Add(function.Name);
                continue;
            }

            if (function.Rva is null)
            {
                if (function.HasRanges)
                {
                    // The address is in DW_AT_ranges, which this reader does not decode. A compiler
                    // does this to a function it split into a hot and a cold part, and the symbol
                    // table names the parts, so skipping it here loses nothing.
                    splitFunctions++;
                }
                else
                {
                    declared.Add(function.Name);
                }

                continue;
            }

            result.Symbols.Add(new DebugSymbol
            {
                Name = function.Name,
                Rva = function.Rva.Value,
                // When both low_pc and high_pc are present the span between them is the whole function
                // and DW_AT_ranges only refines where its parts are, so the size stands.
                Size = function.Size,
                Source = SymbolSource.Dwarf,
                IsData = false,
                Unit = UnitName(function.Unit),
            });
        }

        if (declared.Count > 0)
        {
            result.Problems.Add($"{declared.Count} DWARF subprogram(s) are declarations without a body (a C++ binary has many: its headers declare what they use)");
        }

        if (splitFunctions > 0)
        {
            result.Problems.Add($"{splitFunctions} DWARF subprogram(s) have no DW_AT_low_pc, so their addresses come from the symbol table instead");
        }

        foreach (var (name, producer) in dwarf.Units)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            result.Compilands.Add(new CompilandInfo
            {
                Unit = UnitName(name)!,
                ObjectFile = name,
                Producer = string.IsNullOrEmpty(producer) ? null : producer,
            });
        }

        return result;
    }

    /// <summary>
    /// Reduces a DWARF file name to the form linkers put in symbol tables: <c>crtexe.c</c> rather
    /// than <c>./mingw-w64-crt/crt/crtexe.c</c>. Both sides are compared by this name, so unit
    /// attribution works whether the symbols came from DWARF, a PDB or a COFF file symbol.
    /// </summary>
    public static string? UnitName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        string trimmed = name.Replace('\\', '/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
