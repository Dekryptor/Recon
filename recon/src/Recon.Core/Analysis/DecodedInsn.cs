using Iced.Intel;

namespace Recon.Analysis;

/// <summary>A memory operand that refers to a fixed address (as opposed to a stack slot).</summary>
/// <remarks>
/// A struct, not a class: an 11 MB program is 2.5 million instructions and a third of them carry a
/// memory operand, which at a class each is a hundred megabytes of object headers around four
/// fields. Everything here is a value; nothing needs identity.
/// </remarks>
public struct MemoryRef
{
    /// <summary>Absolute address as encoded, before relocation adjustment.</summary>
    public ulong Address { get; set; }

    /// <summary>RVA when the address could be resolved into the image; null for external addresses.</summary>
    public uint? Rva { get; set; }

    public int Size { get; set; }

    public bool Read { get; set; }

    public bool Write { get; set; }

    public bool HasBase { get; set; }

    public bool HasIndex { get; set; }

    public string? BaseRegister { get; set; }

    public string? IndexRegister { get; set; }

    public int IndexScale { get; set; }

    /// <summary>True when the operand touches the stack frame (base ESP/EBP): not a data xref.</summary>
    public bool IsFrameRelative => BaseRegister is "ESP" or "EBP" || BaseRegister is "RSP" or "RBP";
}

/// <summary>
/// One decoded instruction with the few facts the inventory needs. Deliberately not an iced
/// instruction: analysis code should not depend on the disassembler's object graph.
/// </summary>
/// <remarks>
/// Sized for the biggest binary anyone is likely to hand it. A Visual Basic 6 game client with an
/// 11 MB <c>.text</c> decodes to about 2.5 million of these, so the difference between this class
/// costing 200 bytes and costing 90 is the difference between the inventory finishing and the
/// process being killed. Three things buy that: the eight booleans are one flags word instead of
/// eight fields, the rendered text is only produced when someone asks to see it, and the three
/// lists — which most instructions never use — are allocated on first touch rather than for every
/// instruction decoded.
/// </remarks>
public sealed class DecodedInsn
{
    [Flags]
    private enum Flags : ushort
    {
        None = 0,
        Call = 1 << 0,
        Jump = 1 << 1,
        ConditionalJump = 1 << 2,
        Return = 1 << 3,
        Int3 = 1 << 4,
        Nop = 1 << 5,
        Privileged = 1 << 6,
        Invalid = 1 << 7,
        IndirectBranch = 1 << 8,
    }

    private Flags _flags;

    private List<MemoryRef>? _memoryRefs;
    private List<string>? _writtenRegisters;
    private List<string>? _readRegisters;

    public uint Rva { get; set; }

    public int Length { get; set; }

    public ulong Address { get; set; }

    public Code Code { get; set; }

    public Mnemonic Mnemonic { get; set; }

    /// <summary>
    /// Rendered text, for displays such as <c>recon disasm</c>. Empty for an instruction decoded
    /// during an analysis: formatting 2.5 million instruction strings that no one will read is the
    /// single largest allocation a big binary asks for, and a display decodes the few hundred
    /// instructions it shows.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    public bool IsCall
    {
        get => (_flags & Flags.Call) != 0;
        set => Set(Flags.Call, value);
    }

    /// <summary>
    /// Whether the instruction is a branch of either kind. <see cref="IsConditionalJump"/> says
    /// whether it is one that also falls through.
    /// </summary>
    public bool IsJump
    {
        get => (_flags & Flags.Jump) != 0;
        set => Set(Flags.Jump, value);
    }

    public bool IsConditionalJump
    {
        get => (_flags & Flags.ConditionalJump) != 0;
        set => Set(Flags.ConditionalJump, value);
    }

    public bool IsReturn
    {
        get => (_flags & Flags.Return) != 0;
        set => Set(Flags.Return, value);
    }

    public bool IsInt3
    {
        get => (_flags & Flags.Int3) != 0;
        set => Set(Flags.Int3, value);
    }

    public bool IsNop
    {
        get => (_flags & Flags.Nop) != 0;
        set => Set(Flags.Nop, value);
    }

    public bool IsPrivileged
    {
        get => (_flags & Flags.Privileged) != 0;
        set => Set(Flags.Privileged, value);
    }

    public bool IsInvalid
    {
        get => (_flags & Flags.Invalid) != 0;
        set => Set(Flags.Invalid, value);
    }

    /// <summary>Target RVA of a direct branch, when the target lies inside the image.</summary>
    public uint? DirectTargetRva { get; set; }

    /// <summary>True for <c>jmp reg</c>, <c>call [mem]</c> and friends.</summary>
    public bool IsIndirectBranch
    {
        get => (_flags & Flags.IndirectBranch) != 0;
        set => Set(Flags.IndirectBranch, value);
    }

    /// <summary>Memory operand of an indirect branch, which is what jump tables and IAT calls use.</summary>
    public MemoryRef? BranchMemory { get; set; }

    public bool EndsBlock => IsReturn || (IsJump && !IsConditionalJump);

    /// <summary>Every memory operand of the instruction. Allocated when first touched.</summary>
    public List<MemoryRef> MemoryRefs
    {
        get => _memoryRefs ??= [];
        set => _memoryRefs = value;
    }

    /// <summary>Whether the memory operand list exists yet, so a caller can skip touching it.</summary>
    public bool HasMemoryRefs => _memoryRefs is { Count: > 0 };

    /// <summary>Whether any register was recorded as written, so a caller can avoid allocating.</summary>
    public bool HasWrittenRegisters => _writtenRegisters is { Count: > 0 };

    /// <summary>Registers written by the instruction, by operand position (best effort).</summary>
    public List<string> WrittenRegisters
    {
        get => _writtenRegisters ??= [];
        set => _writtenRegisters = value;
    }

    public List<string> ReadRegisters
    {
        get => _readRegisters ??= [];
        set => _readRegisters = value;
    }

    /// <summary>Immediate value of <c>ret imm16</c>: the number of bytes the callee pops.</summary>
    public int? RetPopBytes { get; set; }

    /// <summary>
    /// The immediate operand of an instruction that has exactly one, as a signed value. This is what
    /// <c>add $delta, reg</c> adds, which is how a position-independent binary finishes computing the
    /// address a jump table is indexed from.
    /// </summary>
    public long? Immediate { get; set; }

    public string Display => $"{Rva:X8}  {Text}";

    private void Set(Flags flag, bool value)
    {
        if (value)
        {
            _flags |= flag;
        }
        else
        {
            _flags &= ~flag;
        }
    }

    public DecodedInsn Clone()
    {
        return new DecodedInsn
        {
            Rva = Rva,
            Length = Length,
            Address = Address,
            Code = Code,
            Mnemonic = Mnemonic,
            Text = Text,
            IsCall = IsCall,
            IsJump = IsJump,
            IsConditionalJump = IsConditionalJump,
            IsReturn = IsReturn,
            IsInt3 = IsInt3,
            IsNop = IsNop,
            IsPrivileged = IsPrivileged,
            IsInvalid = IsInvalid,
            DirectTargetRva = DirectTargetRva,
            IsIndirectBranch = IsIndirectBranch,
            BranchMemory = BranchMemory,
            MemoryRefs = [.. MemoryRefs],
            WrittenRegisters = [.. WrittenRegisters],
            ReadRegisters = [.. ReadRegisters],
            RetPopBytes = RetPopBytes,
            Immediate = Immediate,
        };
    }
}
