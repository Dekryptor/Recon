using Recon.Elf;
using Recon.Macho;
using Recon.Pe;

namespace Recon.Images;

/// <summary>
/// An ELF image as the analysis sees it. The translation is mostly one of address: ELF talks in
/// absolute addresses, PE in RVAs, and everything downstream of the loader speaks RVA — so the
/// image base comes off every address here, once, instead of being subtracted in twenty places.
/// </summary>
public sealed class ElfBinaryImage : IBinaryImage
{
    private readonly ElfImage _elf;
    private List<BinarySection>? _sections;
    private List<BinarySymbol>? _symbols;
    private List<BinaryRelocation>? _relocations;
    private List<BinaryImport>? _imports;
    private List<BinaryExport>? _exports;

    public ElfBinaryImage(ElfImage elf, byte[] bytes)
    {
        _elf = elf;
        Bytes = bytes;
    }

    public ElfImage Elf => _elf;

    public MachoImage? Macho => null;

    public PeImage? Pe => null;

    public byte[] Bytes { get; }

    public string Path => _elf.Path;

    public string Sha256 => _elf.Sha256;

    public string Format => _elf.Format;

    public string ArchName => _elf.Architecture;

    public string Isa => _elf.Isa;

    public bool Is64 => _elf.Is64;

    public bool IsSharedObject => _elf.IsLibrary;

    public bool IsPositionIndependent => _elf.IsPositionIndependent;

    public ulong ImageBase => _elf.ImageBase;

    public uint EntryPointRva => _elf.EntryPoint >= _elf.ImageBase
        ? (uint)(_elf.EntryPoint - _elf.ImageBase)
        : (uint)_elf.EntryPoint;

    public uint SizeOfImage
    {
        get
        {
            ulong end = _elf.ImageBase + 1;
            foreach (var section in _elf.Sections.Where(s => s.IsAllocated))
            {
                end = Math.Max(end, section.EndAddress);
            }

            foreach (var segment in _elf.Segments.Where(s => s.Type == 1))
            {
                end = Math.Max(end, segment.VirtualAddress + segment.MemorySize);
            }

            return (uint)(end - _elf.ImageBase);
        }
    }

    /// <summary>ELF has no subsystem field; the operating system is in the ABI note instead.</summary>
    public ushort Subsystem => 0;

    /// <summary>ELF carries no linker version stamp — the <c>.comment</c> section is the closest thing.</summary>
    public string LinkerVersion => string.Empty;

    public uint Timestamp => 0;

    public IReadOnlyList<string> CommentStrings => _elf.CommentStrings;

    // Section 0 is the reserved null entry every ELF file has; it holds nothing, so it is not a
    // section of the image.
    public IReadOnlyList<BinarySection> Sections => _sections ??= [.. _elf.Sections
        .Where(section => section.Type is not ElfSectionType.Null)
        .Select(ConvertSection)];

    public IReadOnlyList<BinarySymbol> Symbols => _symbols ??= [.. _elf.Symbols
        .Where(s => s.Name.Length > 0)
        .Select(s => new BinarySymbol
        {
            Name = s.Name,
            Version = ExtractVersion(s.VersionedName, s.Name),
            Rva = ToRva(s.Address),
            Size = (uint)Math.Min(s.Size, uint.MaxValue),
            IsFunction = s.IsFunction,
            IsObject = s.IsObject,
            IsFile = s.Kind is ElfSymbolKind.File,
            IsSection = s.Kind is ElfSymbolKind.Section,
            Source = s.Source,
            IsDefined = s.IsDefined,
        })];

    public IReadOnlyList<BinaryRelocation> Relocations => _relocations ??= [.. _elf.Relocations.Select(r => new BinaryRelocation
    {
        Rva = ToRva(r.Address),
        Kind = r.TypeName,
        Type = r.Type,
        // A target that is not in this image is not an RVA here, so it is dropped rather than
        // wrapped around into an address that happens to be inside the file.
        TargetRva = r.TargetAddress is ulong target && target >= _elf.ImageBase && target - _elf.ImageBase <= uint.MaxValue
            ? (uint)(target - _elf.ImageBase)
            : null,
        Width = r.Width,
        SymbolName = r.SymbolName,
        IsImport = r.IsImport,
        SectionName = r.SectionName,
    })];

    public IReadOnlyList<BinaryImport> Imports => _imports ??= BuildImports();

    public IReadOnlyList<BinaryExport> Exports => _exports ??= [.. _elf.ExportedSymbols.Select((symbol, index) => new BinaryExport
    {
        Name = symbol.Name,
        Rva = ToRva(symbol.Address),
        // ELF exports have no ordinals; the index in .dynsym is the closest thing and is not one.
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

    public BinarySection? SectionNamed(string name) => Convert(_elf.SectionNamed(name));

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
        => address >= _elf.ImageBase && address - _elf.ImageBase <= uint.MaxValue
            ? (uint)(address - _elf.ImageBase)
            : 0;

    private List<BinaryImport> BuildImports()
    {
        // An ELF import is an undefined dynamic symbol. The file says which libraries it needs but
        // not which one each symbol comes from, so the module is left empty rather than guessed.
        var slotBySymbol = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var relocation in _elf.Relocations)
        {
            if (relocation.SymbolName is null || relocation.IsDefined)
            {
                continue;
            }

            if (relocation.TypeName.EndsWith("JUMP_SLOT", StringComparison.Ordinal)
                || relocation.TypeName.EndsWith("GLOB_DAT", StringComparison.Ordinal))
            {
                slotBySymbol.TryAdd(relocation.SymbolName, ToRva(relocation.Address));
            }
        }

        return [.. _elf.ImportedSymbols.Select(symbol => new BinaryImport
        {
            Module = string.Empty,
            Name = symbol.Name,
            Version = ExtractVersion(symbol.VersionedName, symbol.Name),
            SlotRva = slotBySymbol.GetValueOrDefault(symbol.Display),
        })];
    }

    private static string? ExtractVersion(string versioned, string name)
        => versioned.Length > name.Length + 1 && versioned.StartsWith(name, StringComparison.Ordinal)
            ? versioned[(name.Length + 1)..]
            : null;

    private BinarySection ConvertSection(ElfSection section) => new()
    {
        Name = section.Name,
        // A section that is not loaded — debug info, the symbol tables — has no address to speak
        // of, so it keeps only the file range its bytes are read from.
        Rva = section.IsAllocated ? ToRva(section.Address) : 0,
        VirtualSize = section.IsAllocated ? (uint)Math.Min(section.Size, uint.MaxValue) : 0,
        RawSize = section.OccupiesNoFileSpace ? 0 : (uint)Math.Min(section.Size, uint.MaxValue),
        RawOffset = section.OccupiesNoFileSpace ? 0 : (uint)Math.Min(section.Offset, uint.MaxValue),
        Characteristics = (ulong)section.Flags,
        IsAllocated = section.IsAllocated,
        IsCode = section.IsCode,
        IsExecutable = section.IsExecutable,
        IsWritable = section.IsWritable,
        IsReadable = section.IsAllocated,
        IsUninitialized = section.OccupiesNoFileSpace,
        Kind = KindOf(section),
        FlagNames = section.FlagNames(),
    };

    private BinarySection? Convert(ElfSection? section) => section is null ? null : ConvertSection(section);

    private static string KindOf(ElfSection section) => section.Kind switch
    {
        "debug" => SectionKind.Debug,
        "bss" => SectionKind.Bss,
        "code" => SectionKind.Code,
        "data" => SectionKind.Data,
        "rodata" => SectionKind.ReadOnly,
        _ => SectionKind.Metadata,
    };
}
