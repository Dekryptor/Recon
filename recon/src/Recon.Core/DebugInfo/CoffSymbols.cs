using System.Buffers.Binary;
using System.Text;
using Recon.Pe;

namespace Recon.DebugInfo;

/// <summary>
/// Reads the COFF symbol table of a PE image. MinGW and MSVC linkers both write one unless the image
/// was stripped with <c>--strip-all</c>; it carries function names, section-relative addresses and,
/// through file symbols, which object file each symbol came from. Sizes are usually absent (the
/// auxiliary records in linked images keep only zeros), so sizes stay estimated and are marked so.
/// </summary>
public static class CoffSymbols
{
    private const int SymbolSize = 18;
    private const byte ClassExternal = 2;
    private const byte ClassStatic = 3;
    private const byte ClassFile = 103;
    private const ushort TypeFunction = 0x20;

    public static DebugInfoResult Read(PeImage image, byte[] bytes)
    {
        var result = new DebugInfoResult { Kind = "coff", FilePath = image.Path };
        var units = new List<CompilandInfo>();
        var seenUnits = new HashSet<string>(StringComparer.Ordinal);
        if (image.SymbolTableOffset == 0 || image.SymbolCount == 0)
        {
            result.Problems.Add("the image has no COFF symbol table (it was stripped)");
            return result;
        }

        int symbolTable = (int)image.SymbolTableOffset;
        int count = (int)image.SymbolCount;
        if (symbolTable + (count * SymbolSize) + 4 > bytes.Length)
        {
            result.Problems.Add("COFF symbol table is outside the file");
            return result;
        }

        int stringTable = symbolTable + (count * SymbolSize);
        uint stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(stringTable));

        string? currentUnit = null;
        for (int i = 0; i < count;)
        {
            int offset = symbolTable + (i * SymbolSize);
            string name = ResolveName(bytes, offset, stringTable, stringTableSize);
            int value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 8));
            short section = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 12));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 14));
            byte storageClass = bytes[offset + 16];
            byte auxCount = bytes[offset + 17];

            if (storageClass == ClassFile && auxCount > 0)
            {
                // File symbols name the object file the following symbols came from.
                int auxOffset = offset + SymbolSize;
                int length = Math.Min(auxCount * SymbolSize, bytes.Length - auxOffset);
                int end = auxOffset;
                int limit = auxOffset + length;
                while (end < limit && bytes[end] != 0)
                {
                    end++;
                }

                currentUnit = Encoding.ASCII.GetString(bytes, auxOffset, Math.Max(0, end - auxOffset));
                if (currentUnit.Length == 0)
                {
                    currentUnit = null;
                }
                else if (seenUnits.Add(currentUnit))
                {
                    units.Add(new CompilandInfo { Unit = currentUnit, ObjectFile = currentUnit });
                }

                i += 1 + auxCount;
                continue;
            }

            bool isSectionSymbol = name.StartsWith('.') && (type & TypeFunction) == 0;
            if (!isSectionSymbol && section > 0 && section <= image.Sections.Count && name.Length > 0 && value >= 0)
            {
                var peSection = image.Sections[section - 1];

                // A COFF symbol's value is an offset inside its section, never an RVA: the section
                // base has to be added. Symbols in .text without a function type are labels, jump
                // tables and other data living in the code section - recording them as data keeps
                // the disassembler from inventing functions around them.
                uint rva = peSection.Rva + (uint)value;
                bool isFunction = (type & TypeFunction) != 0 && storageClass is ClassExternal or ClassStatic;

                result.Symbols.Add(new DebugSymbol
                {
                    Name = NormalizeName(name),
                    Rva = rva,
                    Source = SymbolSource.Coff,
                    IsData = !isFunction,
                    Unit = currentUnit,
                });
            }

            i += 1 + auxCount;
        }

        result.Compilands = units;
        return result;
    }

    /// <summary>MinGW decorates C names with a leading underscore; the plan's ids should not care.</summary>
    private static string NormalizeName(string name)
        => name.StartsWith('_') && !name.StartsWith("__", StringComparison.Ordinal) ? name[1..] : name;

    private static string ResolveName(byte[] bytes, int offset, int stringTable, uint stringTableSize)
    {
        // Short names are inline; long ones are an offset into the string table.
        uint zeroes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        if (zeroes == 0)
        {
            uint stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (stringTableSize <= 4 || stringOffset < 4 || stringOffset >= stringTableSize || stringTable + stringOffset >= bytes.Length)
            {
                return string.Empty;
            }

            int start = stringTable + (int)stringOffset;
            int end = start;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, start, end - start);
        }

        var inline = bytes.AsSpan(offset, 8);
        int terminator = inline.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator >= 0 ? inline[..terminator] : inline);
    }
}
