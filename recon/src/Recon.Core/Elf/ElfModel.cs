namespace Recon.Elf;

/// <summary>ELF class: how wide an address is.</summary>
public enum ElfClass
{
    None = 0,
    Elf32 = 1,
    Elf64 = 2,
}

/// <summary>Byte order of the file.</summary>
public enum ElfData
{
    None = 0,
    LittleEndian = 1,
    BigEndian = 2,
}

/// <summary>What kind of file this is: an executable, a shared object, a relocatable object, a core.</summary>
public enum ElfType
{
    None = 0,
    Relocatable = 1,
    Executable = 2,
    SharedObject = 3,
    Core = 4,
}

public enum ElfSectionType : uint
{
    Null = 0,
    ProgBits = 1,
    SymbolTable = 2,
    StringTable = 3,
    Rela = 4,
    Hash = 5,
    Dynamic = 6,
    Note = 7,
    NoBits = 8,
    Rel = 9,
    Shlib = 10,
    DynamicSymbols = 11,
    InitArray = 14,
    FiniArray = 15,
    PreinitArray = 16,
    Group = 17,
    SymbolTableShndx = 18,
    Relr = 19,
    GnuAttributes = 0x6FFFFFF5,
    GnuHash = 0x6FFFFFF6,
    GnuLiblist = 0x6FFFFFF7,
    GnuVersionDefinition = 0x6FFFFFFD,
    GnuVersionNeed = 0x6FFFFFFE,
    GnuVersionSymbol = 0x6FFFFFFF,
}

[Flags]
public enum ElfSectionFlags : uint
{
    None = 0,
    Write = 0x1,
    Alloc = 0x2,
    Executable = 0x4,
    Merge = 0x10,
    Strings = 0x20,
    InfoLink = 0x40,
    LinkOrder = 0x80,
    Ordered = 0x400,
}

/// <summary>
/// One section header. <see cref="Address"/> is what the program sees once it is loaded, which for
/// an allocated section is the same address the analyser works in; <see cref="Offset"/> and
/// <see cref="Size"/> say where it sits in the file, and are zero for a section that occupies no
/// file space (<c>.bss</c>).
/// </summary>
public sealed class ElfSection
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public ElfSectionType Type { get; set; }

    public uint RawType { get; set; }

    public ElfSectionFlags Flags { get; set; }

    public ulong Address { get; set; }

    public ulong Offset { get; set; }

    public ulong Size { get; set; }

    public uint Link { get; set; }

    public uint Info { get; set; }

    public ulong Alignment { get; set; }

    public ulong EntrySize { get; set; }

    public bool IsAllocated => Flags.HasFlag(ElfSectionFlags.Alloc);

    public bool IsExecutable => Flags.HasFlag(ElfSectionFlags.Executable);

    public bool IsWritable => Flags.HasFlag(ElfSectionFlags.Write);

    public bool IsCode => IsExecutable && IsAllocated && Type is ElfSectionType.ProgBits;

    /// <summary>True when the section holds no bytes in the file, so its size is memory only.</summary>
    public bool OccupiesNoFileSpace => Type is ElfSectionType.NoBits;

    public ulong EndAddress => Address + Size;

    public bool ContainsAddress(ulong address) => IsAllocated && address >= Address && address < EndAddress;

    /// <summary>File offset of an address, or null when the address is not in the file at all.</summary>
    public ulong? AddressToOffset(ulong address)
    {
        if (!ContainsAddress(address) || OccupiesNoFileSpace)
        {
            return null;
        }

        ulong delta = address - Address;
        return delta < Size ? Offset + delta : null;
    }

    /// <summary>What the section holds, in the words the inventory uses: code, data, rodata, bss, debug.</summary>
    public string Kind
    {
        get
        {
            if (Name.StartsWith(".debug", StringComparison.Ordinal) || Name is ".gdb_index")
            {
                return "debug";
            }

            if (Type is ElfSectionType.NoBits)
            {
                return "bss";
            }

            if (IsCode)
            {
                return "code";
            }

            if (!IsAllocated)
            {
                return Type switch
                {
                    ElfSectionType.SymbolTable or ElfSectionType.DynamicSymbols => "symbols",
                    ElfSectionType.StringTable => "strings",
                    ElfSectionType.Rel or ElfSectionType.Relr or ElfSectionType.Rela => "relocations",
                    ElfSectionType.Dynamic => "dynamic",
                    ElfSectionType.Note => "note",
                    _ => "metadata",
                };
            }

            return IsWritable ? "data" : "rodata";
        }
    }

    public List<string> FlagNames()
    {
        var names = new List<string>();
        if (Flags.HasFlag(ElfSectionFlags.Alloc))
        {
            names.Add("alloc");
        }

        if (Flags.HasFlag(ElfSectionFlags.Executable))
        {
            names.Add("exec");
        }

        if (Flags.HasFlag(ElfSectionFlags.Write))
        {
            names.Add("write");
        }

        if (Flags.HasFlag(ElfSectionFlags.Merge))
        {
            names.Add("merge");
        }

        if (Flags.HasFlag(ElfSectionFlags.Strings))
        {
            names.Add("strings");
        }

        return names;
    }
}

/// <summary>One program header: what the loader maps, and with which permissions.</summary>
public sealed class ElfSegment
{
    public uint Type { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public ulong Offset { get; set; }

    public ulong VirtualAddress { get; set; }

    public ulong FileSize { get; set; }

    public ulong MemorySize { get; set; }

    public uint Flags { get; set; }

    public ulong Alignment { get; set; }

    public bool IsReadable => (Flags & 4) != 0;

    public bool IsWritable => (Flags & 2) != 0;

    public bool IsExecutable => (Flags & 1) != 0;

    public string PermissionString =>
        $"{(IsReadable ? "r" : "-")}{(IsWritable ? "w" : "-")}{(IsExecutable ? "x" : "-")}";
}

/// <summary>STT_*: the values are the file's, so they are spelled out rather than renumbered.</summary>
public enum ElfSymbolKind
{
    Notype = 0,
    Object = 1,
    Function = 2,
    Section = 3,
    File = 4,
    Common = 5,
    Tls = 6,
    GnuIndirect = 10,
}

public enum ElfSymbolBinding
{
    Local,
    Global,
    Weak,
    Unique,
}

public enum ElfSymbolVisibility
{
    Default,
    Internal,
    Hidden,
    Protected,
}

/// <summary>
/// One symbol table entry. <see cref="Source"/> says which table it came from, because the two
/// tables answer different questions: <c>.symtab</c> names the functions of the program, and
/// <c>.dynsym</c> names what it imports and exports.
/// </summary>
public sealed class ElfSymbol
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Name with the version appended, as <c>readelf</c> prints it: <c>printf@GLIBC_2.2.5</c>.</summary>
    public string VersionedName { get; set; } = string.Empty;

    public ulong Address { get; set; }

    public ulong Size { get; set; }

    public ElfSymbolKind Kind { get; set; }

    public ElfSymbolBinding Binding { get; set; }

    public ElfSymbolVisibility Visibility { get; set; }

    /// <summary>Section index, or one of the reserved values (0 undefined, 0xFFF1 absolute, …).</summary>
    public ushort SectionIndex { get; set; }

    public string? SectionName { get; set; }

    /// <summary><c>symtab</c> or <c>dynsym</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>True when the symbol names bytes in this file; false for an import.</summary>
    public bool IsDefined => SectionIndex != 0;

    public bool IsFunction => Kind is ElfSymbolKind.Function;

    public bool IsObject => Kind is ElfSymbolKind.Object;

    public string Display => VersionedName.Length > 0 ? VersionedName : Name;
}

/// <summary>One relocation. <see cref="TargetAddress"/> is filled in when the entry names a target.</summary>
public sealed class ElfRelocation
{
    /// <summary>Address of the value that needs fixing up.</summary>
    public ulong Address { get; set; }

    public uint Type { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public string? SymbolName { get; set; }

    /// <summary>Address of the symbol the relocation names, when it is defined in this file.</summary>
    public ulong? SymbolAddress { get; set; }

    /// <summary>Addend: carried in the entry for <c>RELA</c>, read from the instruction for <c>REL</c>.</summary>
    public long Addend { get; set; }

    /// <summary>Where the relocation points, when that can be said: symbol plus addend.</summary>
    public ulong? TargetAddress { get; set; }

    /// <summary>Width of the patched field in bytes: 8 for a pointer, 4 for a 32-bit displacement.</summary>
    public byte Width { get; set; } = 8;

    public bool IsRelativeToProgramCounter => TypeName.Contains("PC", StringComparison.Ordinal);

    /// <summary>Name of the section the relocation applies to, for example <c>.rela.dyn</c>.</summary>
    public string SectionName { get; set; } = string.Empty;

    /// <summary>True when the relocation names a symbol this file imports from a shared object.</summary>
    public bool IsImport => SymbolName is not null && !IsDefined;

    public bool IsDefined { get; set; }
}

/// <summary>A note from a <c>SHT_NOTE</c> section or a <c>PT_NOTE</c> segment.</summary>
public sealed class ElfNote
{
    public string Name { get; set; } = string.Empty;

    public uint Type { get; set; }

    public byte[] Description { get; set; } = [];

    public string SectionName { get; set; } = string.Empty;

    /// <summary>Build ids and ABI tags are the ones an analyst looks at, so they are decoded.</summary>
    public string Display =>
        Name switch
        {
            "GNU" when Type == 3 => $"build-id {Convert.ToHexStringLower(Description)}",
            "GNU" when Type == 5 => $"abi-tag {string.Join('.', Description.Take(4))}",
            "GNU" when Type == 4 => $"gold-version {Text()}",
            _ => $"{Name} type {Type} ({Description.Length} byte(s))",
        };

    private string Text()
    {
        int end = Array.IndexOf(Description, (byte)0);
        return System.Text.Encoding.ASCII.GetString(Description, 0, end < 0 ? Description.Length : end);
    }
}

/// <summary>One <c>DT_*</c> entry, kept as a pair because only some tags have a name here.</summary>
public sealed class ElfDynamicEntry
{
    public ulong Tag { get; set; }

    public string TagName { get; set; } = string.Empty;

    public ulong Value { get; set; }

    public string? Text { get; set; }
}

/// <summary>
/// A parsed ELF file. Both classes (32 and 64 bit) and both byte orders parse; the analysis
/// currently speaks x86 and x86-64, so other machines are reported rather than guessed at.
/// </summary>
public sealed class ElfImage
{
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public ElfClass Class { get; set; }

    public ElfData Data { get; set; }

    public ElfType Type { get; set; }

    public ushort Machine { get; set; }

    public byte OsAbi { get; set; }

    public byte AbiVersion { get; set; }

    /// <summary>Address the entry point is at, as an absolute address in this file's own coordinates.</summary>
    public ulong EntryPoint { get; set; }

    /// <summary>Lowest mapped address: what <c>readelf</c> calls the image base for a non-PIE file.</summary>
    public ulong ImageBase { get; set; }

    public ulong Flags { get; set; }

    public List<ElfSection> Sections { get; set; } = [];

    public List<ElfSegment> Segments { get; set; } = [];

    public List<ElfSymbol> Symbols { get; set; } = [];

    public List<ElfRelocation> Relocations { get; set; } = [];

    public List<ElfDynamicEntry> Dynamic { get; set; } = [];

    public List<ElfNote> Notes { get; set; } = [];

    /// <summary>Strings from <c>.comment</c>: on a GNU system, the compiler that produced the file.</summary>
    public List<string> CommentStrings { get; set; } = [];

    /// <summary>Path of the ELF interpreter, from <c>PT_INTERP</c>: <c>/lib64/ld-linux-x86-64.so.2</c>.</summary>
    public string? Interpreter { get; set; }

    /// <summary>
    /// The initialisation function the dynamic loader calls before everything else, from
    /// <c>DT_INIT</c>. Nothing in the file calls it and a stripped file does not name it, so this
    /// entry is the only place it is reachable from.
    /// </summary>
    public ulong? InitAddress { get; set; }

    /// <summary>The same for <c>DT_FINI</c>, which the loader calls on the way out.</summary>
    public ulong? FiniAddress { get; set; }

    /// <summary>Name of a separate debug file, from <c>.gnu_debuglink</c>, plus its CRC.</summary>
    public string? DebugLink { get; set; }

    public uint? DebugLinkCrc { get; set; }

    /// <summary>Build id, when the file has one: the fingerprint that finds its debug file.</summary>
    public string? BuildId { get; set; }

    /// <summary>Libraries named by <c>DT_NEEDED</c>.</summary>
    public List<string> Needed { get; set; } = [];

    /// <summary><c>DT_SONAME</c>: the name a shared object is known by.</summary>
    public string? SoName { get; set; }

    public bool Is64 => Class is ElfClass.Elf64;

    public bool IsSharedObject => Type is ElfType.SharedObject;

    /// <summary>
    /// A shared object rather than a program. A position-independent executable is <c>ET_DYN</c>
    /// like a library, and what tells them apart is that only the program has an interpreter: it is
    /// meant to be run, not to be loaded by something else.
    /// </summary>
    public bool IsLibrary => Type is ElfType.SharedObject && Interpreter is null;

    public bool IsRelocatable => Type is ElfType.Relocatable;

    /// <summary>True when the file is a position-independent executable: its addresses start near zero.</summary>
    public bool IsPositionIndependent => Type is ElfType.SharedObject
        || (Type is ElfType.Executable && ImageBase < 0x1000000);

    public bool IsLittleEndian => Data is ElfData.LittleEndian;

    /// <summary>Architecture in the words the project's configuration uses: <c>x86</c>, <c>x64</c>, …</summary>
    public string Architecture => Machine switch
    {
        3 => "x86",
        62 => "x64",
        40 => "arm",
        183 => "arm64",
        _ => $"0x{Machine:X}",
    };

    /// <summary>The instruction set, which for these machines is the architecture.</summary>
    public string Isa => Architecture;

    /// <summary><c>elf32</c> or <c>elf64</c>, matching the <c>format</c> a project declares.</summary>
    public string Format => Class switch
    {
        ElfClass.Elf32 => "elf32",
        ElfClass.Elf64 => "elf64",
        _ => "elf",
    };

    public IReadOnlyList<ElfSection> CodeSections => Sections.Where(s => s.IsCode).ToList();

    public IReadOnlyList<ElfSection> AllocatedSections => Sections.Where(s => s.IsAllocated).ToList();

    public ElfSection? SectionContainingAddress(ulong address) => Sections.FirstOrDefault(s => s.ContainsAddress(address));

    public ElfSection? SectionNamed(string name)
        => Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    public ulong? AddressToOffset(ulong address) => SectionContainingAddress(address)?.AddressToOffset(address);

    /// <summary>Symbols that name bytes in this file, sorted by address: what an inventory starts from.</summary>
    public IReadOnlyList<ElfSymbol> DefinedSymbols
        => Symbols.Where(s => s.IsDefined).OrderBy(s => s.Address).ToList();

    /// <summary>What the file imports: undefined dynamic symbols, in the order <c>readelf</c> prints them.</summary>
    public IReadOnlyList<ElfSymbol> ImportedSymbols
        => Symbols.Where(s => !s.IsDefined && s.Source == "dynsym" && s.Name.Length > 0).ToList();

    /// <summary>Defined symbols other files can link against.</summary>
    public IReadOnlyList<ElfSymbol> ExportedSymbols
        => Symbols.Where(s => s.IsDefined && s.Source == "dynsym"
            && s.Binding is ElfSymbolBinding.Global or ElfSymbolBinding.Weak
            && s.Visibility is ElfSymbolVisibility.Default or ElfSymbolVisibility.Protected).ToList();

    public IReadOnlyList<ElfSymbol> Functions => Symbols.Where(s => s.IsFunction && s.IsDefined).OrderBy(s => s.Address).ToList();

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}

/// <summary>What the loader found, plus everything it could not make sense of.</summary>
public sealed class ElfLoadResult
{
    public ElfImage? Image { get; set; }

    public List<string> Problems { get; set; } = [];

    public byte[] Bytes { get; set; } = [];

    public bool Ok => Image is not null && Problems.Count == 0;
}
