namespace Recon.DebugInfo;

/// <summary>Where a symbol came from. Recorded per symbol so confidence can be explained.</summary>
public enum SymbolSource
{
    Pdb,
    Dwarf,
    Coff,
    Elf,
    Macho,
    Map,
    Export,
    EntryPoint,
    TlsCallback,
    RuntimeCallback,
    CallTarget,
    Prologue,

    /// <summary>
    /// The PE exception directory — the `.pdata` section: the compiler's own statement of where each
    /// function begins and ends. It is evidence about existence and extent, never about names (an
    /// entry has no name field), which is why it is a source and not a symbol table.
    /// </summary>
    ExceptionDirectory,
    JumpTableTarget,

    /// <summary>
    /// A procedure the program's own table names: a Visual Basic 6 p-code method. Not a symbol and not a
    /// guess — the descriptor states where the stream starts and how long it is, which is why a function
    /// found this way is high confidence.
    /// </summary>
    PcodeProcedure,

    ImportThunk,
    Config,
    Signature,
}

public static class SymbolSourceNames
{
    public static string ToWire(SymbolSource source) => source switch
    {
        SymbolSource.Pdb => "pdb",
        SymbolSource.Dwarf => "dwarf",
        SymbolSource.Coff => "coff",
        SymbolSource.Elf => "elf",
        SymbolSource.Macho => "macho",
        SymbolSource.Map => "map",
        SymbolSource.Export => "export",
        SymbolSource.EntryPoint => "entry_point",
        SymbolSource.TlsCallback => "tls_callback",
        SymbolSource.RuntimeCallback => "runtime_callback",
        SymbolSource.CallTarget => "call_target",
        SymbolSource.Prologue => "prologue",
        SymbolSource.ExceptionDirectory => "pdata",
        SymbolSource.JumpTableTarget => "jump_table_target",
        SymbolSource.PcodeProcedure => "pcode_procedure",
        SymbolSource.ImportThunk => "import_thunk",
        SymbolSource.Config => "config",
        SymbolSource.Signature => "signature",
        _ => "unknown",
    };
}

public sealed class DebugSymbol
{
    public string Name { get; set; } = string.Empty;

    public uint Rva { get; set; }

    /// <summary>Code size when the source knows it (PDB procedure symbols do).</summary>
    public uint? Size { get; set; }

    public SymbolSource Source { get; set; }

    /// <summary>Set for data symbols so they do not enter the function inventory.</summary>
    public bool IsData { get; set; }

    /// <summary>Compilation unit or object file this symbol came from, when known.</summary>
    public string? Unit { get; set; }

    public override string ToString() => $"{Rva:X8} {Name}";
}

/// <summary>One object file / compilation unit that went into the image.</summary>
public sealed class CompilandInfo
{
    public string Unit { get; set; } = string.Empty;

    public string? ObjectFile { get; set; }

    /// <summary>Producer string, for example <c>Microsoft (R) Optimized Compiler x86</c>.</summary>
    public string? Producer { get; set; }

    public int? ModuleIndex { get; set; }

    public string? Toolchain { get; set; }
}

public sealed class DebugInfoResult
{
    /// <summary>
    /// The source that was asked first: <c>pdb</c>, <c>dwarf</c>, <c>coff</c>, <c>map</c>, <c>elf</c>
    /// or <c>none</c>. It is not the whole story — see <see cref="Sources"/>.
    /// </summary>
    public string Kind { get; set; } = "none";

    /// <summary>
    /// Every source that contributed a symbol, computed from the symbols themselves. A Linux binary
    /// usually has two: DWARF names the functions the compiler described, and the ELF symbol table
    /// names the runtime stubs it did not. Saying only "dwarf" would credit DWARF with names it never
    /// had, which matters when the question is where a name came from.
    /// </summary>
    public List<string> Sources { get; set; } = [];

    public string? FilePath { get; set; }

    public List<DebugSymbol> Symbols { get; set; } = [];

    public List<CompilandInfo> Compilands { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    /// <summary>GUID from the PDB info stream, when reading a PDB.</summary>
    public Guid? Guid { get; set; }

    public uint? Age { get; set; }

    public int FunctionCount => Symbols.Count(s => !s.IsData);

    /// <summary>Recomputes <see cref="Sources"/> from the symbols, and returns this result.</summary>
    public DebugInfoResult WithSources()
    {
        Sources = [.. Symbols
            .Select(s => SymbolSourceNames.ToWire(s.Source))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];

        return this;
    }

    public int DataCount => Symbols.Count(s => s.IsData);
}
