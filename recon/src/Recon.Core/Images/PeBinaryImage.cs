using Recon.Elf;
using Recon.Macho;
using Recon.Pe;

namespace Recon.Images;

/// <summary>
/// A PE image as the analysis sees it. Nothing here is new information: every field is the PE
/// loader's own, translated into the vocabulary the inventory, the decoder and the compare engine
/// share with the ELF side.
/// </summary>
public sealed class PeBinaryImage : IBinaryImage
{
    private readonly PeImage _pe;
    private List<BinarySection>? _sections;
    private List<BinaryRelocation>? _relocations;
    private List<BinaryImport>? _imports;
    private List<BinaryExport>? _exports;
    private List<uint>? _tls;
    private List<BinaryFunctionRange>? _functionRanges;

    public PeBinaryImage(PeImage pe, byte[] bytes)
    {
        _pe = pe;
        Bytes = bytes;
    }

    public PeImage Pe => _pe;

    public MachoImage? Macho => null;

    public ElfImage? Elf => null;

    public byte[] Bytes { get; }

    public string Path => _pe.Path;

    public string Sha256 => _pe.Sha256;

    public string Format => _pe.Kind is PeKind.Pe32 ? "pe32" : "pe64";

    public string ArchName => _pe.DescribeMachine();

    // A p-code VB6 program is an x86 image whose code is not x86: the bytes in the code region are
    // tokens for MSVBVM60's interpreter. It keeps its architecture — the PE header, the imports and
    // the sections are all still i386 — but its instruction set is one no machine decoder speaks,
    // and saying so here is what makes the decoder refuse instead of inventing instructions.
    public string Isa => _pe.Vb6?.IsPcode == true ? "vb6-pcode" : ArchName;

    public bool Is64 => _pe.Kind is PeKind.Pe32Plus;

    public bool IsSharedObject => _pe.IsDll;

    /// <summary>IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE: the image can be moved, like an ELF PIE.</summary>
    public bool IsPositionIndependent => (_pe.DllCharacteristics & 0x0040) != 0;

    public ulong ImageBase => _pe.ImageBase;

    public uint EntryPointRva => _pe.EntryPointRva;

    public uint SizeOfImage => _pe.SizeOfImage;

    public ushort Subsystem => _pe.Subsystem;

    public string LinkerVersion => _pe.LinkerVersion;

    public uint Timestamp => _pe.TimeDateStamp;

    public IReadOnlyList<string> CommentStrings => _pe.CommentStrings;

    public IReadOnlyList<BinarySection> Sections => _sections ??= [.. _pe.Sections.Select(ConvertSection)];

    /// <summary>
    /// A PE image has no symbol table of its own in the sense the analysis means: what it knows
    /// about names comes from the COFF table, the PDB or DWARF, all of which are read as debug
    /// information instead. So this is empty, and not by accident.
    /// </summary>
    public IReadOnlyList<BinarySymbol> Symbols => [];

    public IReadOnlyList<BinaryRelocation> Relocations => _relocations ??= [.. _pe.Relocations.Select(r => new BinaryRelocation
    {
        Rva = r.Rva,
        Kind = r.Kind,
        Type = r.Type,
        TargetRva = r.TargetRva,
        RawValue = r.RawValue,
        Width = r.Width,
    })];

    public IReadOnlyList<BinaryImport> Imports => _imports ??= [.. _pe.Imports.Select(i => new BinaryImport
    {
        Module = i.Dll,
        Name = i.Name,
        Ordinal = i.Ordinal,
        SlotRva = i.IatRva,
    })];

    public IReadOnlyList<BinaryExport> Exports => _exports ??= [.. _pe.Exports.Select(e => new BinaryExport
    {
        Name = e.Name,
        Rva = e.Rva,
        Ordinal = e.Ordinal,
        Forwarder = e.Forwarder,
    })];

    public IReadOnlyList<uint> TlsCallbackRvas => _tls ??= [.. _pe.TlsCallbacks.Select(c => c.Rva)];

    /// <summary>The exception directory, one range per entry, in the table's own order.</summary>
    public IReadOnlyList<BinaryFunctionRange> FunctionRanges => _functionRanges ??= [.. _pe.RuntimeFunctions.Select(f => new BinaryFunctionRange
    {
        BeginRva = f.BeginRva,
        EndRva = f.EndRva,
    })];

    public bool ContainsRva(uint rva) => _pe.ContainsRva(rva);

    public BinarySection? SectionContainingRva(uint rva) => Convert(_pe.SectionContainingRva(rva));

    public BinarySection? SectionNamed(string name) => Convert(_pe.SectionNamed(name));

    public int? RvaToOffset(uint rva) => PeLoader.RvaToOffset(_pe, rva);

    private BinarySection ConvertSection(PeSection section) => new()
    {
        Name = section.Name,
        Rva = section.Rva,
        VirtualSize = section.VirtualSize,
        RawSize = section.RawSize,
        RawOffset = section.RawOffset,
        Characteristics = section.Characteristics,
        IsCode = section.IsCode,
        IsExecutable = section.Flags.HasFlag(SectionFlags.Executable) || section.IsCode,
        IsWritable = section.Flags.HasFlag(SectionFlags.Writable),
        IsReadable = true,
        IsUninitialized = section.Flags.HasFlag(SectionFlags.UninitializedData),
        Kind = KindOf(section),
        FlagNames = section.FlagNames(),
    };

    /// <summary>Null for "no such section", which is what a lookup by address or by name returns.</summary>
    private BinarySection? Convert(PeSection? section) => section is null ? null : ConvertSection(section);

    private static string KindOf(PeSection section)
    {
        if (section.Name.StartsWith(".debug", StringComparison.Ordinal))
        {
            return SectionKind.Debug;
        }

        if (section.Flags.HasFlag(SectionFlags.UninitializedData))
        {
            return SectionKind.Bss;
        }

        if (section.IsCode)
        {
            return SectionKind.Code;
        }

        return section.Flags.HasFlag(SectionFlags.Writable) ? SectionKind.Data : SectionKind.ReadOnly;
    }
}
