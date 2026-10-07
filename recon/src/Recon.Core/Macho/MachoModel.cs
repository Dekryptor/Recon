namespace Recon.Macho;

/// <summary>What the header says this file is. The two that matter for an inventory are the object
/// file a compiler hands out and the image the linker hands back.</summary>
public enum MachoFileType : uint
{
    Object = 1,
    Execute = 2,
    Core = 4,
    Dylib = 6,
    Dylinker = 7,
    Bundle = 8,
    DylibStub = 9,
    Dsym = 10,
    KextBundle = 11,
}

/// <summary>Header flags worth naming: the rest are linker bookkeeping.</summary>
[Flags]
public enum MachoFlags : uint
{
    None = 0,
    NoUndefinedReferences = 0x1,
    /// <summary>The image is position independent: every address in it is slid at load time.</summary>
    Pie = 0x200000,
    AllowStackExecution = 0x20000,
    TwoLevelNamespace = 0x80,
}

/// <summary>What a section holds, in the words the inventory uses.</summary>
public static class MachoSectionKind
{
    public const string Code = "code";
    public const string Data = "data";
    public const string ReadOnly = "rodata";
    public const string Bss = "bss";
    public const string Debug = "debug";
    public const string Metadata = "metadata";
}

/// <summary>One <c>LC_SEGMENT</c>/<c>LC_SEGMENT_64</c>: a range of the image and the sections in it.</summary>
public sealed class MachoSegment
{
    public string Name { get; set; } = string.Empty;

    public ulong Address { get; set; }

    public ulong Size { get; set; }

    public ulong FileOffset { get; set; }

    public ulong FileSize { get; set; }

    public int MaxProtection { get; set; }

    public int InitialProtection { get; set; }

    public uint Flags { get; set; }

    public List<MachoSection> Sections { get; set; } = [];

    public ulong EndAddress => Address + Size;

    /// <summary><c>7</c> for <c>rwx</c>, the way the file spells it, for a report that shows it.</summary>
    public string Protection => ProtectionString(InitialProtection);

    public static string ProtectionString(int protection)
        => new string(
        [
            (protection & 4) != 0 ? 'r' : '-',
            (protection & 2) != 0 ? 'w' : '-',
            (protection & 1) != 0 ? 'x' : '-',
        ]);
}

/// <summary>
/// One section. A Mach-O section is named by its segment and itself — <c>__TEXT,__text</c> — because
/// the name alone is not unique: <c>__DATA,__data</c> and <c>__DATA_CONST,__data</c> are different
/// sections with the same last name.
/// </summary>
public sealed class MachoSection
{
    public string Name { get; set; } = string.Empty;

    public string SegmentName { get; set; } = string.Empty;

    public ulong Address { get; set; }

    public ulong Size { get; set; }

    public uint Offset { get; set; }

    public uint Align { get; set; }

    public uint RelocationOffset { get; set; }

    public uint RelocationCount { get; set; }

    public uint Flags { get; set; }

    /// <summary>What the loader calls the section's own payload: a stub table's entry size, mostly.</summary>
    public uint Reserved1 { get; set; }

    public uint Reserved2 { get; set; }

    public string FullName => $"{SegmentName},{Name}";

    /// <summary>The low byte: regular, zerofill, symbol stubs, literal pointers, …</summary>
    public uint SectionType => Flags & 0xFF;

    public bool IsZeroFill => SectionType is 0x1 or 0xC or 0x12;

    public bool IsSymbolStubs => SectionType == 0x8;

    public bool IsDebug => (Flags & 0x02000000) != 0 || SegmentName == "__DWARF";

    public bool IsCode => Kind is MachoSectionKind.Code;

    public bool OccupiesNoFileSpace => IsZeroFill;

    public ulong EndAddress => Address + Size;

    public string Kind => MachoSectionKinds.KindOf(SegmentName, Name, Flags);

    public IReadOnlyList<string> FlagNames() => MachoSectionKinds.FlagNames(Flags);
}

/// <summary>One entry of the symbol table (<c>nlist</c> / <c>nlist_64</c>).</summary>
public sealed class MachoSymbol
{
    public string Name { get; set; } = string.Empty;

    public ulong Value { get; set; }

    public byte Type { get; set; }

    /// <summary>1-based index into the sections; 0 (<c>NO_SECT</c>) when the section says nothing.</summary>
    public byte SectionIndex { get; set; }

    public short Desc { get; set; }

    public string? SectionName { get; set; }

    public bool IsStab => (Type & 0xE0) != 0;

    public bool IsExternal => (Type & 0x01) != 0;

    public bool IsUndefined => (Type & 0x0E) == 0x00 && !IsStab;

    public bool IsAbsolute => (Type & 0x0E) == 0x02;

    public bool IsDefinedInSection => (Type & 0x0E) == 0x0E;

    public bool IsIndirect => (Type & 0x0E) == 0x0A;

    public bool IsWeak => (Desc & 0x0040) != 0 || (Desc & 0x0080) != 0;

    public bool IsFunction { get; set; }

    public bool IsData { get; set; }

    /// <summary>Which dylib an undefined symbol is looked up in: 1-based index into the load commands.</summary>
    public ushort LibraryOrdinal => (ushort)(((ushort)Desc >> 8) & 0xFF);

    public string? LibraryName { get; set; }

    public bool IsDefined => IsDefinedInSection || IsAbsolute;

    public string Kind => IsStab ? "stab" : IsUndefined ? "undefined" : IsAbsolute ? "absolute" : "section";
}

/// <summary>One relocation entry. Final images have none — the linker resolves them — so this is an
/// object file's business, and it is where a symbol's target is still visible.</summary>
public sealed class MachoRelocation
{
    public ulong Address { get; set; }

    public uint SymbolIndex { get; set; }

    public string? SymbolName { get; set; }

    public uint Type { get; set; }

    public bool IsPcRelative { get; set; }

    /// <summary>0 = 1 byte, 1 = 2, 2 = 4, 3 = 8.</summary>
    public byte Length { get; set; }

    public bool IsExternal { get; set; }

    public int Value { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public string? SectionName { get; set; }

    public ulong? TargetAddress { get; set; }

    public bool IsImport { get; set; }

    public byte Width => Length switch
    {
        0 => 1,
        1 => 2,
        3 => 8,
        _ => 4,
    };
}

/// <summary>One dylib the image loads, and how: weak, re-exported, lazily, upwards.</summary>
public sealed class MachoDylib
{
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = "load";

    public uint Timestamp { get; set; }

    public uint CurrentVersion { get; set; }

    public uint CompatibilityVersion { get; set; }

    /// <summary>True for <c>LC_ID_DYLIB</c>: the name <em>this</em> image is installed as.</summary>
    public bool IsId { get; set; }

    public string Version => $"{(CurrentVersion >> 16) & 0xFFFF}.{(CurrentVersion >> 8) & 0xFF}.{CurrentVersion & 0xFF}";
}

/// <summary>One slice of a fat (universal) file, which is what the container looks like before any
/// Mach-O header: several images, one per architecture, in one file.</summary>
public sealed class MachoFatSlice
{
    public uint CpuType { get; set; }

    public uint CpuSubType { get; set; }

    public ulong Offset { get; set; }

    public ulong Size { get; set; }

    public uint Align { get; set; }

    public string Architecture => MachoCpu.Architecture(CpuType);
}

/// <summary>A Mach-O image: the reference repository's format, and the only one of the three whose
/// sections are named by their segment as well as by themselves.</summary>
public sealed class MachoImage
{
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public bool Is64 { get; set; }

    public bool BigEndian { get; set; }

    public uint CpuType { get; set; }

    public uint CpuSubType { get; set; }

    public MachoFileType FileType { get; set; }

    public MachoFlags Flags { get; set; }

    public uint LoadCommandCount { get; set; }

    public List<MachoSegment> Segments { get; set; } = [];

    public List<MachoSection> Sections { get; set; } = [];

    public List<MachoSymbol> Symbols { get; set; } = [];

    public List<MachoRelocation> Relocations { get; set; } = [];

    public List<MachoDylib> Dylibs { get; set; } = [];

    public List<MachoFatSlice> FatSlices { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    /// <summary>From <c>LC_UUID</c>: what a dSYM is matched to its binary by.</summary>
    public string? Uuid { get; set; }

    /// <summary><c>LC_SOURCE_VERSION</c>: the tools' own version stamp, packed as 24.10.10.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>From <c>LC_BUILD_VERSION</c> or the older <c>LC_VERSION_MIN_*</c>: which OS this was built for.</summary>
    public string? Platform { get; set; }

    public string? MinOs { get; set; }

    public string? Sdk { get; set; }

    public string? InstallName { get; set; }

    public bool HasCodeSignature { get; set; }

    public bool IsEncrypted { get; set; }

    /// <summary>
    /// <c>LC_FUNCTION_STARTS</c>: where the linker said each function begins. It is the reason a
    /// stripped Mach-O image can still be inventoried, and it is not read yet — see the note in
    /// <c>docs/m6-status.md</c>.
    /// </summary>
    public uint FunctionStartsOffset { get; set; }

    public uint FunctionStartsSize { get; set; }

    /// <summary><c>LC_DYSYMTAB</c>'s indirect symbol table: how a stub names the import it jumps to.</summary>
    public uint IndirectSymbolOffset { get; set; }

    public uint IndirectSymbolCount { get; set; }

    /// <summary>
    /// The indirect symbol table, read: one symbol index per entry of every stub and pointer table,
    /// which is the only thing that says which import a stub jumps through.
    /// </summary>
    public List<uint> IndirectSymbols { get; set; } = [];

    /// <summary>Lowest address the image is mapped at: what an RVA is measured from.</summary>
    public ulong ImageBase { get; set; }

    public ulong EntryPointAddress { get; set; }

    public ulong SizeOfImage { get; set; }

    /// <summary>The machine, in the words the project's configuration uses.</summary>
    public string Architecture => MachoCpu.Architecture(CpuType);

    public string Format => Is64 ? "macho64" : "macho32";

    public bool IsLibrary => FileType is MachoFileType.Dylib or MachoFileType.Bundle or MachoFileType.DylibStub;

    public bool IsObjectFile => FileType is MachoFileType.Object;

    public bool IsPositionIndependent => (Flags & MachoFlags.Pie) != 0;

    /// <summary>What a report shows as the file's type, in the format's own words.</summary>
    public string TypeName => FileType switch
    {
        MachoFileType.Object => "object",
        MachoFileType.Execute => "executable",
        MachoFileType.Dylib => "dylib",
        MachoFileType.Bundle => "bundle",
        MachoFileType.DylibStub => "dylib stub",
        MachoFileType.Dsym => "dSYM",
        MachoFileType.Dylinker => "dynamic linker",
        _ => $"filetype {(uint)FileType}",
    };

    public IReadOnlyList<MachoSymbol> DefinedSymbols
        => Symbols.Where(s => s.IsDefined && s.Name.Length > 0).OrderBy(s => s.Value).ToList();

    public IReadOnlyList<MachoSymbol> ImportedSymbols
        => Symbols.Where(s => s.IsUndefined && s.IsExternal && s.Name.Length > 0).ToList();

    public IReadOnlyList<MachoSymbol> ExportedSymbols
        => Symbols.Where(s => s.IsDefined && s.IsExternal && !s.IsStab).ToList();

    public IReadOnlyList<MachoSection> CodeSections
        => [.. Sections.Where(s => s.IsCode)];

    public MachoSection? SectionNamed(string name)
    {
        string wanted = Bare(name);
        return Sections.FirstOrDefault(s => Bare(s.Name) == wanted || Bare(s.FullName) == wanted);
    }

    /// <summary>
    /// A section name without the punctuation its file format wraps it in. Every format names its
    /// sections differently — <c>.debug_info</c>, <c>__debug_info</c> — and the question being asked
    /// ("where is the DWARF?") is the same for all of them, so the answer should not depend on
    /// remembering which file is open.
    /// </summary>
    private static string Bare(string name) => name.TrimStart('.', '_');

    /// <summary>The section a symbol lands in, which is what says whether it is code or data.</summary>
    public MachoSection? SectionOf(MachoSymbol symbol)
        => symbol.SectionIndex > 0 && symbol.SectionIndex <= Sections.Count ? Sections[symbol.SectionIndex - 1] : null;
}

/// <summary>CPU types, which in Mach-O carry the word size in bit 24 rather than in their own field.</summary>
public static class MachoCpu
{
    public const uint X86 = 7;
    public const uint X86_64 = 0x01000007;
    public const uint Arm = 12;
    public const uint Arm64 = 0x0100000C;
    public const uint Arm64_32 = 0x0200000C;
    public const uint PowerPc = 18;
    public const uint PowerPc64 = 0x01000012;

    public static string Architecture(uint cpuType) => cpuType switch
    {
        X86 => "x86",
        X86_64 => "x64",
        Arm => "arm",
        Arm64 => "arm64",
        Arm64_32 => "arm",
        PowerPc => "ppc",
        PowerPc64 => "ppc64",
        _ => $"cpu 0x{cpuType:X}",
    };

    /// <summary>True when the CPU's own instructions are 64-bit, which for ARM64_32 they are not.</summary>
    public static bool Is64Bit(uint cpuType) => cpuType is X86_64 or Arm64 or PowerPc64;
}

/// <summary>What a section holds, worked out from its segment, its name and its flags: Mach-O says
/// "regular" about almost everything, so the name is what is left to go on.</summary>
public static class MachoSectionKinds
{
    public static string KindOf(string segment, string name, uint flags)
    {
        uint type = flags & 0xFF;
        bool pureInstructions = (flags & 0x80000000) != 0;

        if (segment == "__DWARF" || name == "__debug_info" || (flags & 0x02000000) != 0)
        {
            return MachoSectionKind.Debug;
        }

        if (type is 0x1 or 0xC or 0x12)
        {
            return MachoSectionKind.Bss;
        }

        if (pureInstructions
            || type == 0x8
            || name is "__text" or "__stubs" or "__stub_helper" or "__textcoal_nt" or "__text_startup")
        {
            return MachoSectionKind.Code;
        }

        if (segment == "__TEXT"
            || name is "__const" or "__cstring" or "__cfstring" or "__objc_methname" or "__objc_classname"
                or "__unwind_info" or "__eh_frame" or "__oslogstring" or "__swift5_types")
        {
            return MachoSectionKind.ReadOnly;
        }

        if (segment is "__DATA" or "__DATA_CONST" or "__DATA_DIRTY" or "__OBJC" or "__LAUNCH_CONST")
        {
            return MachoSectionKind.Data;
        }

        return MachoSectionKind.Metadata;
    }

    public static List<string> FlagNames(uint flags)
    {
        var names = new List<string>();
        Add(names, flags, 0x80000000, "pure_instructions");
        Add(names, flags, 0x40000000, "no_toc");
        Add(names, flags, 0x20000000, "strip_static_syms");
        Add(names, flags, 0x10000000, "no_dead_strip");
        Add(names, flags, 0x08000000, "live_support");
        Add(names, flags, 0x04000000, "self_modifying_code");
        Add(names, flags, 0x02000000, "debug");
        Add(names, flags, 0x00000400, "some_instructions");
        Add(names, flags, 0x00000200, "ext_reloc");
        Add(names, flags, 0x00000100, "loc_reloc");
        names.Add(TypeName(flags & 0xFF));
        return names;
    }

    public static string TypeName(uint type) => type switch
    {
        0x00 => "regular",
        0x01 => "zerofill",
        0x02 => "cstring_literals",
        0x03 => "4byte_literals",
        0x04 => "8byte_literals",
        0x05 => "literal_pointers",
        0x06 => "non_lazy_symbol_pointers",
        0x07 => "lazy_symbol_pointers",
        0x08 => "symbol_stubs",
        0x09 => "mod_init_func_pointers",
        0x0A => "mod_term_func_pointers",
        0x0B => "coalesced",
        0x0C => "gb_zerofill",
        0x0D => "interposing",
        0x0E => "16byte_literals",
        0x0F => "dtrace_dof",
        0x10 => "lazy_dylib_symbol_pointers",
        0x11 => "thread_local_regular",
        0x12 => "thread_local_zerofill",
        0x13 => "thread_local_variables",
        0x14 => "thread_local_variable_pointers",
        0x15 => "thread_local_init_function_pointers",
        0x16 => "init_func_offsets",
        _ => $"type 0x{type:X2}",
    };

    private static void Add(List<string> names, uint flags, uint mask, string name)
    {
        if ((flags & mask) != 0)
        {
            names.Add(name);
        }
    }
}

/// <summary>What a loader found, in the file's own structures, plus everything it could not read.</summary>
public sealed class MachoLoadResult
{
    public MachoImage? Image { get; set; }

    public List<string> Problems { get; set; } = [];

    public byte[] Bytes { get; set; } = [];

    public bool Ok => Image is not null && Problems.Count == 0;
}
