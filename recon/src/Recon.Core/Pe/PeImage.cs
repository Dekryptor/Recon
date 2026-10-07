using System.Security.Cryptography;
using System.Text;

namespace Recon.Pe;

public enum PeKind
{
    Pe32,
    Pe32Plus,
}

[Flags]
public enum SectionFlags
{
    None = 0,
    Code = 1 << 0,
    InitializedData = 1 << 1,
    UninitializedData = 1 << 2,
    Executable = 1 << 3,
    Readable = 1 << 4,
    Writable = 1 << 5,
    Discardable = 1 << 6,
    Shared = 1 << 7,
}

public sealed class PeSection
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Virtual size (the size in memory). Zero in object files; use <see cref="RawSize"/> there.</summary>
    public uint VirtualSize { get; set; }

    public uint Rva { get; set; }

    public uint RawSize { get; set; }

    public uint RawOffset { get; set; }

    public uint Characteristics { get; set; }

    public SectionFlags Flags { get; set; }

    public bool IsCode => Flags.HasFlag(SectionFlags.Code) || Flags.HasFlag(SectionFlags.Executable);

    public uint RvaEnd => Rva + Math.Max(VirtualSize, RawSize);

    public bool ContainsRva(uint rva) => rva >= Rva && rva < RvaEnd;

    public int? RvaToOffset(uint rva)
    {
        if (rva < Rva)
        {
            return null;
        }

        uint delta = rva - Rva;
        return delta < RawSize ? (int)(RawOffset + delta) : null;
    }

    public List<string> FlagNames()
    {
        var names = new List<string>();
        if (Flags.HasFlag(SectionFlags.Code))
        {
            names.Add("code");
        }

        if (Flags.HasFlag(SectionFlags.InitializedData))
        {
            names.Add("data");
        }

        if (Flags.HasFlag(SectionFlags.UninitializedData))
        {
            names.Add("bss");
        }

        if (Flags.HasFlag(SectionFlags.Executable))
        {
            names.Add("exec");
        }

        if (Flags.HasFlag(SectionFlags.Readable))
        {
            names.Add("read");
        }

        if (Flags.HasFlag(SectionFlags.Writable))
        {
            names.Add("write");
        }

        if (Flags.HasFlag(SectionFlags.Discardable))
        {
            names.Add("discardable");
        }

        if (Flags.HasFlag(SectionFlags.Shared))
        {
            names.Add("shared");
        }

        return names;
    }
}

public sealed class PeDataDirectory
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public uint Rva { get; set; }

    public uint Size { get; set; }

    public bool Present => Rva != 0 && Size != 0;
}

public sealed class PeImport
{
    public string Dll { get; set; } = string.Empty;

    public string? Name { get; set; }

    public ushort? Ordinal { get; set; }

    public uint IatRva { get; set; }

    /// <summary>RVA of the INT/ILT entry that pointed here, when the binary kept one.</summary>
    public uint? LookupRva { get; set; }

    public string Display => Name ?? $"#{Ordinal}";
}

public sealed class PeExport
{
    public string? Name { get; set; }

    public uint Ordinal { get; set; }

    public uint Rva { get; set; }

    /// <summary>Set when the export is a forwarder: the target is a DLL and symbol name, not an RVA.</summary>
    public string? Forwarder { get; set; }
}

public sealed class PeRelocation
{
    /// <summary>RVA of the value that needs fixing up.</summary>
    public uint Rva { get; set; }

    public ushort Type { get; set; }

    public string Kind { get; set; } = "unknown";

    /// <summary>Value currently stored at <see cref="Rva"/>, already adjusted by the image base when possible.</summary>
    public uint? TargetRva { get; set; }

    public uint RawValue { get; set; }

    public byte Width { get; set; } = 4;
}

public sealed class PeTlsCallback
{
    public uint Rva { get; set; }

    public int Index { get; set; }
}

public sealed class PeDebugEntry
{
    public uint Type { get; set; }

    public string TypeName { get; set; } = string.Empty;

    /// <summary>Path embedded in a CodeView record: where the PDB was when the binary was linked.</summary>
    public string? PdbPath { get; set; }

    public Guid? PdbGuid { get; set; }

    public uint? PdbAge { get; set; }

    public uint TimeDateStamp { get; set; }

    /// <summary>File offset of the COFF symbol table, 0 when absent.</summary>
    public uint SymbolTableOffset { get; set; }

    public uint SymbolCount { get; set; }

    public uint SizeOfData { get; set; }

    public uint RawOffset { get; set; }
}

public sealed class PeResourceType
{
    public string Name { get; set; } = string.Empty;

    public ushort Id { get; set; }

    public int Entries { get; set; }
}

/// <summary>
/// One entry of the PE exception directory — the table the linker writes into <c>.pdata</c>: the range
/// a function occupies, and where what the unwinder needs for it lives. It is the one place a PE image
/// states where a function *ends*; a symbol table states where one begins and leaves the end to the
/// next symbol or to a guess.
/// </summary>
public sealed class PeRuntimeFunction
{
    public uint BeginRva { get; set; }

    /// <summary>
    /// The end of the range, or null when the entry does not state one. A zero end is a form the
    /// 64-bit unwinder chains — the next entry's begin is this function's end — and reading it as
    /// "no end" is the conservative answer: the extent then comes from the analysis's own estimation
    /// rather than from a rule this reader would be inventing. An end at or before the begin is not a
    /// range either, and reads the same way.
    /// </summary>
    public uint? EndRva { get; set; }

    /// <summary>The size of the range, or null when <see cref="EndRva"/> is.</summary>
    public uint? Size => EndRva is { } end && end > BeginRva ? end - BeginRva : null;

    /// <summary>
    /// The 64-bit form's third word: the RVA of this function's unwind data in <c>.xdata</c>. Null on
    /// the 32-bit form, which has no third word — its entries are eight bytes and a range is all they
    /// state. Reading twelve bytes there would take the next entry's begin for a handler address,
    /// which is a mistake in the shape of a fact.
    /// </summary>
    public uint? UnwindInfoRva { get; set; }
}

/// <summary>A parsed PE image. PE32 is the first-class citizen; PE32+ parses but is not analysed yet.</summary>
public sealed class PeImage
{
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public PeKind Kind { get; set; }

    public ushort Machine { get; set; }

    public ushort Characteristics { get; set; }

    public ushort DllCharacteristics { get; set; }

    public ulong ImageBase { get; set; }

    public uint EntryPointRva { get; set; }

    public uint SizeOfImage { get; set; }

    public uint SizeOfHeaders { get; set; }

    public uint Checksum { get; set; }

    public byte MajorLinkerVersion { get; set; }

    public byte MinorLinkerVersion { get; set; }

    public uint TimeDateStamp { get; set; }

    /// <summary>File offset of the COFF symbol table, 0 when absent.</summary>
    public uint SymbolTableOffset { get; set; }

    public uint SymbolCount { get; set; }

    public byte MajorOsVersion { get; set; }

    public byte MinorOsVersion { get; set; }

    public byte MajorSubsystemVersion { get; set; }

    public byte MinorSubsystemVersion { get; set; }

    public ushort Subsystem { get; set; }

    public List<PeSection> Sections { get; set; } = [];

    public List<PeDataDirectory> DataDirectories { get; set; } = [];

    public List<PeImport> Imports { get; set; } = [];

    public List<PeExport> Exports { get; set; } = [];

    public List<PeRelocation> Relocations { get; set; } = [];

    public List<PeTlsCallback> TlsCallbacks { get; set; } = [];

    public List<PeDebugEntry> DebugEntries { get; set; } = [];

    public List<PeResourceType> ResourceTypes { get; set; } = [];

    /// <summary>
    /// The exception directory as read: every entry the table holds, in the table's own order, for any
    /// machine that has the directory at all. What the analysis makes of them (which of them are code,
    /// what to do with a missing end) is the analysis's decision, not the loader's — an entry that
    /// points outside a code section is still an entry the file states.
    /// </summary>
    public List<PeRuntimeFunction> RuntimeFunctions { get; set; } = [];

    /// <summary>Human-readable strings found in <c>.comment</c> and similar sections.</summary>
    public List<string> CommentStrings { get; set; } = [];

    public RichHeader? Rich { get; set; }

    /// <summary>
    /// The VB5! header of a Visual Basic 5/6 program, or null when this is not one. When it is
    /// present and <see cref="Vb6HeaderInfo.IsPcode"/> is true the bytes the image calls code are
    /// p-code for MSVBVM60 to interpret, not x86 — see <see cref="Vb6Header"/>.
    /// </summary>
    public Vb6HeaderInfo? Vb6 { get; set; }

    public bool IsDll => (Characteristics & 0x2000) != 0;

    public bool HasRelocations => Relocations.Count > 0;

    public string LinkerVersion => $"{MajorLinkerVersion}.{MinorLinkerVersion}";

    public IReadOnlyList<PeSection> CodeSections => Sections.Where(s => s.IsCode).ToList();

    public PeSection? SectionContainingRva(uint rva) => Sections.FirstOrDefault(s => s.ContainsRva(rva));

    public PeSection? SectionNamed(string name)
        => Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool ContainsRva(uint rva) => SectionContainingRva(rva) is not null || rva < SizeOfHeaders;

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public string DescribeMachine() => DescribeMachine(Machine);

    /// <summary>
    /// The machine code in the words the configuration uses. A COFF object carries the same numbers
    /// a PE header does, so an archive reads them with this rather than with a second copy of the
    /// table that could disagree with it.
    /// </summary>
    public static string DescribeMachine(ushort machine) => machine switch
    {
        0x014C => "x86",
        0x8664 => "x64",
        0x01C0 => "arm",
        0xAA64 => "arm64",
        _ => $"0x{machine:X4}",
    };
}

/// <summary>What the loader found, plus everything it could not make sense of.</summary>
public sealed class PeLoadResult
{
    public PeImage? Image { get; set; }

    public List<string> Problems { get; set; } = [];

    public byte[] Bytes { get; set; } = [];

    public bool Ok => Image is not null && Problems.Count == 0;
}

internal static class EncodingL
{
    public static string Latin1(byte[] bytes, int offset, int length) => Encoding.Latin1.GetString(bytes, offset, length);
}
