using Recon.Elf;
using Recon.Macho;
using Recon.Pe;

namespace Recon.Images;

/// <summary>What a section holds, in the words the inventory and the viewer use.</summary>
public static class SectionKind
{
    public const string Code = "code";
    public const string Data = "data";
    public const string ReadOnly = "read_only";
    public const string Bss = "bss";
    public const string Debug = "debug";
    public const string Metadata = "metadata";
}

/// <summary>
/// One section of any executable format: the same fields PE and ELF both have, which is all the
/// analysis ever needed. A format's own section record keeps everything else (PE characteristics,
/// ELF section types) behind <see cref="IBinaryImage.Pe"/> and <see cref="IBinaryImage.Elf"/>.
/// </summary>
public sealed class BinarySection
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Address the section is loaded at, relative to the image base.</summary>
    public uint Rva { get; set; }

    /// <summary>Size in memory, which for a BSS section is larger than what the file holds.</summary>
    public uint VirtualSize { get; set; }

    public uint RawSize { get; set; }

    public uint RawOffset { get; set; }

    /// <summary>The format's own flag word: PE characteristics, ELF <c>sh_flags</c>.</summary>
    public ulong Characteristics { get; set; }

    public bool IsCode { get; set; }

    public bool IsExecutable { get; set; }

    public bool IsWritable { get; set; }

    public bool IsReadable { get; set; }

    /// <summary>True when the section occupies memory but no bytes in the file.</summary>
    public bool IsUninitialized { get; set; }

    /// <summary>
    /// True when the section is part of the loaded image. Sections that are not — debug info, the
    /// symbol tables — still have bytes in the file and are still read, but an address never
    /// resolves into them.
    /// </summary>
    public bool IsAllocated { get; set; } = true;

    public string Kind { get; set; } = SectionKind.Metadata;

    public uint RvaEnd => Rva + Math.Max(VirtualSize, RawSize);

    public bool ContainsRva(uint rva) => IsAllocated && rva >= Rva && rva < RvaEnd;

    /// <summary>File offset of an address, or null when that address is not in the file.</summary>
    public int? RvaToOffset(uint rva)
    {
        if (!ContainsRva(rva) || IsUninitialized)
        {
            return null;
        }

        uint delta = rva - Rva;
        return delta < RawSize ? (int)(RawOffset + delta) : null;
    }

    public List<string> FlagNames { get; set; } = [];
}

/// <summary>
/// A function's range as a table in the file itself states it: PE's exception directory, the
/// <c>.pdata</c> section, which every 64-bit image has one entry per function in and a 32-bit image
/// has one per function with a handler in. Nothing else in a binary states where a function *ends* —
/// a symbol table says where one begins — so a reader that has this table does not have to guess the
/// extent. Which table said it is not this type's business: the analysis records that under the
/// name in <see cref="DebugInfo.SymbolSourceNames"/>.
/// </summary>
public sealed class BinaryFunctionRange
{
    public uint BeginRva { get; set; }

    /// <summary>The end, or null when the table does not state one (see <see cref="Pe.PeRuntimeFunction.EndRva"/>).</summary>
    public uint? EndRva { get; set; }

    public uint? Size => EndRva is { } end && end > BeginRva ? end - BeginRva : null;
}

/// <summary>
/// One symbol any format can describe: ELF's <c>.symtab</c> and <c>.dynsym</c> entries, and — for
/// PE — the ones the COFF table turns into debug info instead, since a PE image has no symbol table
/// of its own in the sense this type means.
/// </summary>
public sealed class BinarySymbol
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The version a dynamic symbol asks for, as in <c>printf@GLIBC_2.2.5</c>.</summary>
    public string? Version { get; set; }

    public uint Rva { get; set; }

    public uint Size { get; set; }

    public bool IsFunction { get; set; }

    public bool IsObject { get; set; }

    public bool IsFile { get; set; }

    public bool IsSection { get; set; }

    /// <summary>Which table or reader it came from: <c>symtab</c>, <c>dynsym</c>, <c>coff</c>, …</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>False for an import: a symbol that names no bytes in this file.</summary>
    public bool IsDefined { get; set; }

    /// <summary>Which library an import comes from, when the format says (a PE import always does).</summary>
    public string? Module { get; set; }

    public string Display => Version is null ? Name : $"{Name}@{Version}";
}

/// <summary>One relocation, reduced to what the analysis uses: where it is, and what it points at.</summary>
public sealed class BinaryRelocation
{
    public uint Rva { get; set; }

    /// <summary>The format's own name: <c>HIGHLOW</c>, <c>R_X86_64_RELATIVE</c>, …</summary>
    public string Kind { get; set; } = string.Empty;

    public uint Type { get; set; }

    /// <summary>The address the relocation points at, when the format lets us say.</summary>
    public uint? TargetRva { get; set; }

    /// <summary>The value stored at <see cref="Rva"/>, already adjusted by the image base where possible.</summary>
    public uint RawValue { get; set; }

    public byte Width { get; set; } = 4;

    public string? SymbolName { get; set; }

    /// <summary>True when the relocation names a symbol this file imports.</summary>
    public bool IsImport { get; set; }

    /// <summary>Name of the section the relocation applies to, for example <c>.rela.dyn</c>.</summary>
    public string SectionName { get; set; } = string.Empty;
}

/// <summary>Something the file imports: a PE import descriptor, or an undefined ELF dynamic symbol.</summary>
public sealed class BinaryImport
{
    /// <summary>The DLL (PE) or, for ELF, the library the symbol is believed to come from.</summary>
    public string Module { get; set; } = string.Empty;

    public string? Name { get; set; }

    public ushort? Ordinal { get; set; }

    /// <summary>The version an ELF import asks for (<c>GLIBC_2.2.5</c>); PE imports have none.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Address of the slot that holds the imported address: the IAT entry for PE, the GOT or PLT
    /// slot named by the relocation for ELF. A call to an import goes through this.
    /// </summary>
    public uint SlotRva { get; set; }

    public bool IsOrdinal => Name is null;

    public string Display => Name ?? $"#{Ordinal}";

    public string Qualified => Module.Length == 0 ? Display : $"{Module}!{Display}";
}

/// <summary>Something the file exports: a PE export table entry, or a defined ELF dynamic symbol.</summary>
public sealed class BinaryExport
{
    public string? Name { get; set; }

    public uint Rva { get; set; }

    public uint Ordinal { get; set; }

    /// <summary>Set when the export points at another module instead of at this file's code.</summary>
    public string? Forwarder { get; set; }

    public string Display => Name ?? $"ordinal_{Ordinal}";
}

/// <summary>
/// The image as the analysis sees it: PE and ELF side by side. Every consumer downstream of the
/// loader — inventory, decoding, xrefs, producer detection, the compare engine, the reports — takes
/// this and nothing format-shaped, which is what makes the second format a matter of writing a
/// loader rather than of rewriting the tool.
/// </summary>
public interface IBinaryImage
{
    string Path { get; }

    string Sha256 { get; }

    /// <summary><c>pe32</c>, <c>pe64</c>, <c>elf32</c>, <c>elf64</c>: what <c>project.toml</c> calls <c>format</c>.</summary>
    string Format { get; }

    /// <summary>The machine, in the words the configuration uses: <c>x86</c>, <c>x64</c>, <c>arm</c>, <c>arm64</c>.</summary>
    string ArchName { get; }

    /// <summary>The instruction set, which for every machine so far is the architecture.</summary>
    string Isa { get; }

    bool Is64 { get; }

    /// <summary>A DLL for PE, a shared object for ELF, a PIE for either.</summary>
    bool IsSharedObject { get; }

    bool IsPositionIndependent { get; }

    ulong ImageBase { get; }

    uint EntryPointRva { get; }

    /// <summary>Size of the image in memory: <c>SizeOfImage</c> for PE, the highest mapped address for ELF.</summary>
    uint SizeOfImage { get; }

    /// <summary>PE subsystem; 0 for a format that has no such field.</summary>
    ushort Subsystem { get; }

    /// <summary>The linker's own version stamp, when the format carries one (PE does, ELF does not).</summary>
    string LinkerVersion { get; }

    uint Timestamp { get; }

    IReadOnlyList<BinarySection> Sections { get; }

    IReadOnlyList<BinarySymbol> Symbols { get; }

    IReadOnlyList<BinaryRelocation> Relocations { get; }

    IReadOnlyList<BinaryImport> Imports { get; }

    IReadOnlyList<BinaryExport> Exports { get; }

    IReadOnlyList<uint> TlsCallbackRvas { get; }

    /// <summary>
    /// Function extents a table in the file states, which today is the PE exception directory and
    /// nothing else. Empty for a format with no such table — an empty list, not an absent one, so a
    /// caller never has to ask which format it is holding.
    /// </summary>
    IReadOnlyList<BinaryFunctionRange> FunctionRanges { get; }

    /// <summary>Human-readable strings from <c>.comment</c> and its equivalents: who built the file.</summary>
    IReadOnlyList<string> CommentStrings { get; }

    byte[] Bytes { get; }

    bool ContainsRva(uint rva);

    BinarySection? SectionContainingRva(uint rva);

    BinarySection? SectionNamed(string name);

    int? RvaToOffset(uint rva);

    /// <summary>The PE image, for the parts of the tool that are PE-only (delink, relink, resources).</summary>
    PeImage? Pe { get; }

    /// <summary>The ELF image, for anything that wants the file's own structures.</summary>
    ElfImage? Elf { get; }

    /// <summary>The Mach-O image, likewise.</summary>
    MachoImage? Macho { get; }
}

/// <summary>What a loader found, plus everything it could not make sense of.</summary>
public sealed class LoadedImage
{
    public IBinaryImage? Image { get; set; }

    public List<string> Problems { get; set; } = [];

    public byte[] Bytes { get; set; } = [];

    /// <summary>The format the loader recognised, even when it then failed: what an error message wants.</summary>
    public string Format { get; set; } = string.Empty;

    public bool Ok => Image is not null && Problems.Count == 0;
}
