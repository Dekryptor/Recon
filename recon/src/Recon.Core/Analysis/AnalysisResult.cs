namespace Recon.Analysis;

public sealed class Range
{
    public uint Rva { get; set; }

    public uint Size { get; set; }

    public uint End => Rva + Size;
}

public sealed class CallingConventionInfo
{
    public string Value { get; set; } = "unknown";

    public string Confidence { get; set; } = "low";

    public List<string> Evidence { get; set; } = [];
}

public sealed class FunctionEntry
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Demangled { get; set; }

    public List<Range> Ranges { get; set; } = [];

    public string Section { get; set; } = string.Empty;

    public string Isa { get; set; } = "x86";

    public List<SymbolSourceWire> FoundBy { get; set; } = [];

    public string Confidence { get; set; } = "low";

    public CallingConventionInfo CallingConvention { get; set; } = new();

    public string? Toolchain { get; set; }

    public string? ToolchainConfidence { get; set; }

    public List<string> ToolchainEvidence { get; set; } = [];

    public List<string> Flags { get; set; } = [];

    public List<string> Aliases { get; set; } = [];

    public List<string> Unknowns { get; set; } = [];

    /// <summary>Set for stubs that jump straight into an imported function.</summary>
    public string? ImportThunk { get; set; }

    public string? Unit { get; set; }

    public uint Start => Ranges.Count > 0 ? Ranges[0].Rva : 0;

    public uint Size => (uint)Ranges.Sum(r => (long)r.Size);

    public bool Covers(uint rva) => Ranges.Any(r => rva >= r.Rva && rva < r.End);
}

/// <summary>Wire format for <see cref="DebugInfo.SymbolSource"/> so the model stays UI-free.</summary>
public sealed class SymbolSourceWire(string value)
{
    public string Value { get; } = value;

    public override string ToString() => Value;

    public static implicit operator SymbolSourceWire(string value) => new(value);

    public static implicit operator string(SymbolSourceWire wire) => wire.Value;
}

public sealed class DataEntry
{
    public uint Rva { get; set; }

    public uint Size { get; set; }

    public string? Name { get; set; }

    public string Kind { get; set; } = "data";

    public string Source { get; set; } = "section";
}

public sealed class JumpTableEntry
{
    public uint Rva { get; set; }

    public int Entries { get; set; }

    public string? Owner { get; set; }

    /// <summary><c>absolute</c> when entries are full addresses, <c>relative</c> otherwise.</summary>
    public string Kind { get; set; } = "absolute";

    /// <summary>
    /// Bytes per entry: 4 for a table of offsets (every position-independent one) or of 32-bit
    /// addresses, 8 for a table of 64-bit addresses. The size of the table follows from it, and the
    /// data range the inventory reports for the table is only right if this is.
    /// </summary>
    public int EntryWidth { get; set; } = 4;

    public List<uint> Targets { get; set; } = [];

    /// <summary>RVA of the indirect jump that uses this table.</summary>
    public uint UsedAtRva { get; set; }
}

public sealed class Xref
{
    public uint FromRva { get; set; }

    public uint ToRva { get; set; }

    /// <summary>call, jump, data_read, data_write, data_ref, import_call, import_ref, reloc.</summary>
    public string Kind { get; set; } = "data_ref";

    public bool ViaReloc { get; set; }

    public string? InFunction { get; set; }

    public string? ToFunction { get; set; }
}

public sealed class AnalysisResult
{
    public List<FunctionEntry> Functions { get; set; } = [];

    public List<DataEntry> Data { get; set; } = [];

    public List<JumpTableEntry> JumpTables { get; set; } = [];

    public List<Xref> Xrefs { get; set; } = [];

    public List<string> Problems { get; set; } = [];

    public Dictionary<string, int> Statistics { get; set; } = [];

    public int InstructionCount { get; set; }
}
