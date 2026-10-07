using System.Globalization;
using Recon.Images;
using Recon.Pe;

namespace Recon.DebugInfo;

/// <summary>
/// Reads linker map files. Two formats share the name: the MSVC <c>link /MAP</c> listing and the
/// GNU ld map (<c>-Wl,-Map=...</c>). Debug info beats a map file, but a map beats guessing.
/// </summary>
public static class MapFileReader
{
    public static DebugInfoResult Read(string path, IBinaryImage image)
    {
        var result = new DebugInfoResult { Kind = "map", FilePath = path };
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Problems.Add($"cannot read map file {path}: {ex.Message}");
            return result;
        }

        // Three formats share the name. ld64's is the one an Apple-targeted link produces, and it
        // is the only one that says how big each symbol is.
        bool isLd64 = lines.Any(l => l.StartsWith("# Symbols:", StringComparison.Ordinal));
        bool isMsvc = lines.Any(l => l.Contains("Publics by Value", StringComparison.OrdinalIgnoreCase));
        if (isLd64)
        {
            ReadLd64(lines, image, result);
        }
        else if (isMsvc)
        {
            ReadMsvc(lines, image, result);
        }
        else
        {
            ReadGnu(lines, image, result);
        }

        if (result.Symbols.Count == 0)
        {
            result.Problems.Add("no symbols were found in the map file; the format may have changed");
        }

        return result;
    }

    // ------------------------------------------------------------- GNU ld

    private static void ReadGnu(string[] lines, IBinaryImage image, DebugInfoResult result)
    {
        // Section lines look like " .text          0x00401000      0x158"; symbol lines are
        // "                0x00401000                main" and belong to the section above them.
        var sectionVmas = new Dictionary<string, (ulong Vma, ulong Size)>(StringComparer.Ordinal);
        var symbols = new List<(ulong Vma, string Name, string Section, string? ObjectFile)>();
        string currentSection = string.Empty;
        string? currentObject = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || line.StartsWith("LOAD", StringComparison.Ordinal))
            {
                continue;
            }

            string trimmed = line.Trim();
            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // " .text 0x00401000 0x158" optionally followed by object file names.
            if (tokens.Length >= 2 && !tokens[0].StartsWith("0x", StringComparison.Ordinal)
                && tokens[0].StartsWith('.') && TryParseHex(tokens[1], out ulong vma))
            {
                ulong size = tokens.Length >= 3 && TryParseHex(tokens[2], out ulong s) ? s : 0;
                if (!sectionVmas.ContainsKey(tokens[0]))
                {
                    sectionVmas[tokens[0]] = (vma, size);
                }

                currentSection = tokens[0];
                continue;
            }

            // " .text 0x... 0x... ctest.o" style contribution lines name the object file.
            if (tokens.Length >= 4 && tokens[0].StartsWith('.') && TryParseHex(tokens[1], out _))
            {
                currentObject = tokens[^1];
                continue;
            }

            // Symbol lines: exactly two tokens, an address and a plain name.
            if (tokens.Length == 2 && TryParseHex(tokens[0], out ulong address) && IsPlainSymbol(tokens[1]))
            {
                symbols.Add((address, tokens[1], currentSection, currentObject));
            }
        }

        ulong delta = ComputeImageBaseDelta(sectionVmas, image, result);
        var seen = new Dictionary<(string Name, uint Rva), DebugSymbol>();

        foreach (var (vma, name, section, objectFile) in symbols)
        {
            if (vma < delta)
            {
                continue;
            }

            uint rva = (uint)(vma - delta);
            if (rva == 0 && vma == 0)
            {
                continue;
            }

            // Duplicates happen when a symbol is listed in several contributions.
            if (seen.ContainsKey((name, rva)))
            {
                continue;
            }

            var symbol = new DebugSymbol
            {
                Name = name,
                Rva = rva,
                Source = SymbolSource.Map,
                Unit = objectFile,
                IsData = image.SectionContainingRva(rva)?.IsCode != true,
            };
            seen[(name, rva)] = symbol;
            result.Symbols.Add(symbol);
        }
    }

    private static bool IsPlainSymbol(string token)
    {
        if (token.Length == 0 || token.Contains('=') || token.Contains('*') || token.StartsWith("0x", StringComparison.Ordinal))
        {
            return false;
        }

        if (token.Contains("PROVIDE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '.' or '$' or '?' or '@' or '<' or '>' or '-' or ':' or '~'))
            {
                return false;
            }
        }

        return char.IsLetter(token[0]) || token[0] is '_' or '.' or '?';
    }

    private static bool TryParseHex(string text, out ulong value)
        => ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length >= 4);

    /// <summary>
    /// Map files print absolute addresses. Matching section names and sizes against the PE gives the
    /// image base the link used, which is what converts them back to RVAs.
    /// </summary>
    private static ulong ComputeImageBaseDelta(Dictionary<string, (ulong Vma, ulong Size)> sections, IBinaryImage image, DebugInfoResult result)
    {
        var votes = new Dictionary<ulong, int>();
        foreach (var section in image.Sections)
        {
            if (!sections.TryGetValue(section.Name, out var entry))
            {
                continue;
            }

            if (entry.Vma < section.Rva)
            {
                continue;
            }

            ulong delta = entry.Vma - section.Rva;
            if (delta % 0x1000 != 0 && delta != 0)
            {
                continue;
            }

            votes[delta] = votes.GetValueOrDefault(delta) + 1;
        }

        if (votes.Count == 0)
        {
            result.Problems.Add($"could not derive the link-time image base from the map file; assuming 0x{image.ImageBase:X}");
            return image.ImageBase;
        }

        var best = votes.OrderByDescending(v => v.Value).First();
        if (best.Key != image.ImageBase)
        {
            result.Problems.Add($"map file was linked at image base 0x{best.Key:X}, the image now uses 0x{image.ImageBase:X}");
        }

        return best.Key;
    }

    // ------------------------------------------------------------- ld64 / ld64.lld

    /// <summary>
    /// ld64's map: sections as <c>0xADDR\t0xSIZE\t__SEGMENT\t__SECTION</c> and, under
    /// <c># Symbols:</c>, one <c>0xADDR\t0xSIZE\t[ file] name</c> line per symbol. It is what a
    /// stripped Mach-O image has to be read against, because there is no symbol table left in it.
    /// </summary>
    private static void ReadLd64(string[] lines, IBinaryImage image, DebugInfoResult result)
    {
        var sectionVmas = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var symbols = new List<(ulong Address, string Name)>();
        bool inSymbols = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("# Sections:", StringComparison.Ordinal))
            {
                inSymbols = false;
                continue;
            }

            if (line.StartsWith("# Symbols:", StringComparison.Ordinal))
            {
                inSymbols = true;
                continue;
            }

            string[] parts = line.Split('\t');
            if (inSymbols)
            {
                if (parts.Length < 3 || !TryParseHex(parts[0], out ulong address))
                {
                    continue;
                }

                // The last column is "[ file index] name" — the two are separated by a space, not
                // a tab, so the index has to come off before the name is a name.
                string name = parts[^1].Trim();
                if (name.StartsWith('[') && name.IndexOf(']') is int close and > 0)
                {
                    name = name[(close + 1)..].Trim();
                }

                if (IsPlainSymbol(name))
                {
                    symbols.Add((address, name));
                }

                continue;
            }

            if (parts.Length >= 4 && TryParseHex(parts[0], out ulong sectionAddress))
            {
                // The last column is the section's own name, which is what the inventory calls it.
                sectionVmas.TryAdd(parts[^1].Trim(), sectionAddress);
            }
        }

        ulong delta = ComputeImageBaseDelta(
            sectionVmas.ToDictionary(entry => entry.Key, entry => (entry.Value, (ulong)0), StringComparer.Ordinal),
            image,
            result);

        var seen = new HashSet<(string, uint)>();
        foreach (var (address, name) in symbols)
        {
            if (address < delta)
            {
                continue;
            }

            uint rva = (uint)(address - delta);
            if (rva == 0 || !seen.Add((name, rva)))
            {
                continue;
            }

            result.Symbols.Add(new DebugSymbol
            {
                Name = name,
                Rva = rva,
                // The size is there for the taking, and it is the linker's padded size rather than
                // the symbol's own: _add is 9 bytes of instructions in a 16-byte slot. Taking it
                // would make every function look bigger than it is and the next one overlap it.
                Size = null,
                Source = SymbolSource.Map,
                IsData = image.SectionContainingRva(rva)?.IsCode != true,
            });
        }
    }

    // ------------------------------------------------------------- MSVC link /MAP

    private static void ReadMsvc(string[] lines, IBinaryImage image, DebugInfoResult result)
    {
        var sectionNamesByIndex = new Dictionary<int, string>();
        var inPublics = false;

        foreach (var raw in lines)
        {
            string line = raw;

            // "0001:00000000 00000123H .text                   CODE"
            if (!inPublics && line.Length > 20 && TryParseSegmentOffset(line, out int segment, out uint offset, out uint length, out string sectionName))
            {
                sectionNamesByIndex[segment] = sectionName;
                _ = offset;
                _ = length;
                continue;
            }

            if (line.Contains("Publics by Value", StringComparison.OrdinalIgnoreCase))
            {
                inPublics = true;
                continue;
            }

            if (!inPublics)
            {
                continue;
            }

            // " 0001:00000000       ?Foo@@YAXXZ                00401000     foo.obj"
            var trimmed = line.TrimStart();
            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            int addressIndex = -1;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.Length == 8 && TryParseHexPlain(token, out ulong value) && value >= image.ImageBase - 0x100000 && value <= image.ImageBase + image.SizeOfImage + 0x100000)
                {
                    addressIndex = i;
                    break;
                }
            }

            if (addressIndex < 1 || addressIndex == 0)
            {
                continue;
            }

            string name = tokens[addressIndex - 1];
            if (name.Length == 0)
            {
                continue;
            }

            ulong address = 0;
            TryParseHexPlain(tokens[addressIndex], out address);
            if (address < image.ImageBase)
            {
                continue;
            }

            uint rva = (uint)(address - image.ImageBase);
            var symbol = new DebugSymbol
            {
                Name = name,
                Rva = rva,
                Source = SymbolSource.Map,
                Unit = tokens.Length > addressIndex + 1 ? tokens[^1] : null,
                IsData = image.SectionContainingRva(rva)?.IsCode != true,
            };
            result.Symbols.Add(symbol);
        }
    }

    private static bool TryParseHexPlain(string text, out ulong value)
        => ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static bool TryParseSegmentOffset(string line, out int segment, out uint offset, out uint length, out string sectionName)
    {
        segment = 0;
        offset = 0;
        length = 0;
        sectionName = string.Empty;

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return false;
        }

        var parts = tokens[0].Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out segment))
        {
            return false;
        }

        if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
        {
            return false;
        }

        string lengthToken = tokens[1].TrimEnd('H', 'h');
        if (!uint.TryParse(lengthToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out length))
        {
            return false;
        }

        sectionName = tokens[2];
        return true;
    }
}
