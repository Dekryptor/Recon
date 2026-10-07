using Recon.Elf;
using Recon.Macho;
using Recon.Pe;

namespace Recon.Images;

/// <summary>
/// A Mach-O image as the analysis sees it. The translation is the same one <see cref="ElfBinaryImage"/>
/// makes — addresses in, RVAs out — plus two things the format does not have: a symbol carries no
/// size, and an import has no slot to name, because a Mach-O call goes through a stub rather than
/// through a table of addresses. Both are left unknown rather than invented.
/// </summary>
public sealed class MachoBinaryImage : IBinaryImage
{
    private readonly MachoImage _macho;
    private List<BinarySection>? _sections;
    private List<BinarySymbol>? _symbols;
    private List<BinaryRelocation>? _relocations;
    private List<BinaryImport>? _imports;
    private List<BinaryExport>? _exports;

    public MachoBinaryImage(MachoImage macho, byte[] bytes)
    {
        _macho = macho;
        Bytes = bytes;
    }

    public MachoImage Macho => _macho;

    public PeImage? Pe => null;

    public ElfImage? Elf => null;

    public byte[] Bytes { get; }

    public string Path => _macho.Path;

    public string Sha256 => _macho.Sha256;

    public string Format => _macho.Format;

    public string ArchName => _macho.Architecture;

    public string Isa => _macho.Architecture;

    public bool Is64 => _macho.Is64;

    public bool IsSharedObject => _macho.IsLibrary;

    public bool IsPositionIndependent => _macho.IsPositionIndependent;

    public ulong ImageBase => _macho.ImageBase;

    public uint EntryPointRva => _macho.EntryPointAddress >= _macho.ImageBase
        ? (uint)Math.Min(_macho.EntryPointAddress - _macho.ImageBase, uint.MaxValue)
        : (uint)_macho.EntryPointAddress;

    public uint SizeOfImage => (uint)Math.Min(_macho.SizeOfImage, uint.MaxValue);

    /// <summary>Mach-O has no subsystem field; the platform is in <c>LC_BUILD_VERSION</c> instead.</summary>
    public ushort Subsystem => 0;

    /// <summary>No linker version stamp either — <c>LC_BUILD_VERSION</c> names the SDK, not the linker.</summary>
    public string LinkerVersion => string.Empty;

    public uint Timestamp => 0;

    /// <summary>Mach-O has no <c>.comment</c>; the closest thing is the build version, in the image.</summary>
    public IReadOnlyList<string> CommentStrings => [];

    public IReadOnlyList<BinarySection> Sections => _sections ??= [.. _macho.Sections.Select(ConvertSection)];

    public IReadOnlyList<BinarySymbol> Symbols => _symbols ??= [.. _macho.Symbols
        .Where(s => s.Name.Length > 0 && !s.IsStab)
        .Select(s => new BinarySymbol
        {
            Name = s.Name,
            Rva = ToRva(s.Value),
            // A Mach-O symbol carries no size: only the section it is in, and its address. Whatever
            // the boundaries end up being, they are not read from here.
            Size = 0,
            IsFunction = s.IsFunction,
            IsObject = s.IsData,
            IsFile = false,
            IsSection = false,
            Source = "symtab",
            IsDefined = s.IsDefined,
        })];

    public IReadOnlyList<BinaryRelocation> Relocations => _relocations ??= [.. _macho.Relocations.Select(r => new BinaryRelocation
    {
        Rva = ToRva(r.Address),
        Kind = r.TypeName,
        Type = r.Type,
        TargetRva = r.TargetAddress is ulong target && target >= _macho.ImageBase && target - _macho.ImageBase <= uint.MaxValue
            ? (uint)(target - _macho.ImageBase)
            : null,
        Width = r.Width,
        SymbolName = r.SymbolName,
        IsImport = r.IsImport,
        SectionName = r.SectionName ?? string.Empty,
    })];

    /// <summary>
    /// An import is an undefined external symbol, and Mach-O is the one format that says which
    /// library each one comes from — the two-level namespace keeps the ordinal in the symbol's own
    /// descriptor. What it has no equivalent of is the slot: a call goes through a stub in
    /// <c>__stubs</c>, not through an address in a table.
    /// </summary>
    public IReadOnlyList<BinaryImport> Imports => _imports ??= BuildImports();

    /// <summary>
    /// Where each import's address is kept. A Mach-O import has no IAT: a call goes to a stub in
    /// <c>__stubs</c>, and the stub jumps through a pointer in <c>__la_symbol_ptr</c> or
    /// <c>__got</c>. Those pointer tables are the slots, and the indirect symbol table is what says
    /// which slot belongs to which import. Naming them is what lets the existing thunk detection
    /// call a stub <c>thunk__printf</c> instead of a six-byte function with no name.
    /// </summary>
    private List<BinaryImport> BuildImports()
    {
        var slotByName = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint pointerSize = _macho.Is64 ? 8u : 4u;

        foreach (var section in _macho.Sections)
        {
            // 0x6 is non-lazy symbol pointers, 0x7 lazy ones: the GOT and the lazy pointer table.
            if (section.SectionType is not (0x6 or 0x7))
            {
                continue;
            }

            uint count = (uint)(section.Size / pointerSize);
            for (uint index = 0; index < count; index++)
            {
                uint indirect = section.Reserved1 + index;
                if (indirect >= _macho.IndirectSymbols.Count)
                {
                    continue;
                }

                uint entry = _macho.IndirectSymbols[(int)indirect];
                if ((entry & 0x80000000) != 0)
                {
                    continue; // INDIRECT_SYMBOL_LOCAL: a pointer to this image, not an import
                }

                uint symbolIndex = entry & 0x3FFFFFFF;
                if (symbolIndex < _macho.Symbols.Count && _macho.Symbols[(int)symbolIndex] is { IsUndefined: true } symbol)
                {
                    slotByName.TryAdd(symbol.Name, ToRva(section.Address + (index * pointerSize)));
                }
            }
        }

        return [.. _macho.ImportedSymbols.Select(symbol => new BinaryImport
        {
            Module = symbol.LibraryName ?? string.Empty,
            Name = symbol.Name,
            // No slot means 0, which is what ELF says when it has none: RVA 0 is the header, and no
            // import lives there.
            SlotRva = slotByName.GetValueOrDefault(symbol.Name),
        })];
    }

    public IReadOnlyList<BinaryExport> Exports => _exports ??= [.. _macho.ExportedSymbols.Select((symbol, index) => new BinaryExport
    {
        Name = symbol.Name,
        Rva = ToRva(symbol.Value),
        Ordinal = (uint)index,
        Forwarder = null,
    })];

    public IReadOnlyList<uint> TlsCallbackRvas => [];

    /// <summary>
    /// Empty: this format has no exception directory. ELF states frames in `.eh_frame` and DWARF
    /// states extents in `.debug_info`, and both are read as debug information rather than here — an
    /// empty list is this adapter saying "no table in this shape", not "no function extents known".
    /// </summary>
    public IReadOnlyList<BinaryFunctionRange> FunctionRanges => [];

    public bool ContainsRva(uint rva) => SectionContainingRva(rva) is not null;

    public BinarySection? SectionContainingRva(uint rva)
        => Sections.FirstOrDefault(s => s.IsAllocated && s.ContainsRva(rva));

    public BinarySection? SectionNamed(string name) => Convert(_macho.SectionNamed(name));

    public int? RvaToOffset(uint rva)
    {
        if (SectionContainingRva(rva) is not BinarySection section)
        {
            return null;
        }

        uint delta = rva - section.Rva;
        return delta < section.RawSize ? (int)(section.RawOffset + delta) : null;
    }

    private uint ToRva(ulong address)
        => address >= _macho.ImageBase && address - _macho.ImageBase <= uint.MaxValue
            ? (uint)(address - _macho.ImageBase)
            : 0;

    private BinarySection ConvertSection(MachoSection section) => new()
    {
        Name = section.Name,
        Rva = ToRva(section.Address),
        VirtualSize = (uint)Math.Min(section.Size, uint.MaxValue),
        RawSize = section.OccupiesNoFileSpace ? 0 : (uint)Math.Min(section.Size, uint.MaxValue),
        RawOffset = section.OccupiesNoFileSpace ? 0 : section.Offset,
        Characteristics = section.Flags,
        IsAllocated = true,
        IsCode = section.IsCode,
        IsExecutable = (section.Flags & 0x80000000) != 0 || section.IsCode,
        IsWritable = IsWritable(section),
        IsReadable = true,
        IsUninitialized = section.OccupiesNoFileSpace,
        Kind = KindOf(section),
        // "code" is the word the other two formats use for the same thing (PE says it literally,
        // ELF says "execinstr"): a report that lists section flags should not need to know which
        // file it came from to see where the code is.
        FlagNames = [.. section.FlagNames(), .. CodeFlag(section)],
    };

    private BinarySection? Convert(MachoSection? section) => section is null ? null : ConvertSection(section);

    private static List<string> CodeFlag(MachoSection section) => section.IsCode ? ["code"] : [];

    private static bool IsWritable(MachoSection section)
        => section.SegmentName is "__DATA" or "__DATA_CONST" or "__DATA_DIRTY" && section.Kind is SectionKind.Data;

    private static string KindOf(MachoSection section) => section.Kind switch
    {
        MachoSectionKind.Debug => SectionKind.Debug,
        MachoSectionKind.Bss => SectionKind.Bss,
        MachoSectionKind.Code => SectionKind.Code,
        MachoSectionKind.Data => SectionKind.Data,
        MachoSectionKind.ReadOnly => SectionKind.ReadOnly,
        _ => SectionKind.Metadata,
    };
}
