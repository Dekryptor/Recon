using System.Buffers.Binary;
using Iced.Intel;
using Recon.Images;
using Recon.Pe;

namespace Recon.Analysis;

/// <summary>Decodes bytes into <see cref="DecodedInsn"/> records, resolving branch and memory targets to RVAs.</summary>
public sealed class CodeDecoder
{
    private readonly IBinaryImage _image;
    private uint? _firstSectionRva;
    private readonly MasmFormatter _formatter;
    private readonly InstructionInfoFactory _infoFactory = new();

    /// <summary>
    /// A resolver makes the formatted text symbolic instead of numeric: the comparison passes one so
    /// that an address the rebuild is free to move is rendered as the thing it points at.
    /// </summary>
    public CodeDecoder(IBinaryImage image, ISymbolResolver? symbols = null)
    {
        _image = image;
        _formatter = symbols is null ? new MasmFormatter() : new MasmFormatter(symbols, null);
    }

    public IBinaryImage Image => _image;

    /// <summary>
    /// Whether to render each instruction to text as it is decoded. On for a display, off for an
    /// analysis: a big binary decodes millions of instructions that nobody will ever read, and the
    /// strings for them were the largest single allocation the inventory made. A display decodes the
    /// few hundred instructions it shows, on demand, with this left on.
    /// </summary>
    public bool IncludeText { get; set; } = true;

    /// <summary>
    /// Whether to record the registers each instruction reads and writes. Only the calling-convention
    /// heuristic and the register tracking that finds position-independent jump tables read those,
    /// and both decode the one function they are looking at. Every instruction of a big program
    /// paying for a string per register is seconds spent on something no pass asked for.
    /// </summary>
    public bool TrackRegisters { get; set; } = true;

    /// <summary>
    /// When set, registers are recorded only for these mnemonics. Register tracking exists for the
    /// two idioms that compute an address without loading it — <c>lea reg, [address]</c> and the
    /// <c>call</c>/<c>add</c> pair of the position-independent idiom — so a pass can ask for it on
    /// those instructions alone instead of on every instruction in the program.
    /// </summary>
    public HashSet<Mnemonic>? RegisterMnemonics { get; set; }

    private bool WantsRegisters(Mnemonic mnemonic)
        => TrackRegisters && (RegisterMnemonics is null || RegisterMnemonics.Contains(mnemonic));

    /// <summary>
    /// Whether to record every memory operand. A pass that only asks "is this a call, and where does
    /// it go" does not need them; the xref and jump-table passes do. The memory operand of a branch
    /// is always recorded, whatever this says, because that one is cheap and the import thunks depend
    /// on it.
    /// </summary>
    public bool TrackMemory { get; set; } = true;

    public ulong ImageBase => _image.ImageBase;

    /// <summary>
    /// Whether this image's instruction set is one the decoder speaks. Iced decodes x86 and x86-64;
    /// handed an image in some other instruction set it would produce x86 instructions out of those
    /// bytes, which is worse than producing nothing, because the output looks like an answer.
    /// AArch64 is decoded here rather than by Iced: its instructions are all four bytes, so a walk
    /// cannot lose its place even where it recognises nothing.
    /// </summary>
    public bool CanDecode => _image.Isa is "x86" or "x64" or "arm64";

    /// <summary>Why <see cref="CanDecode"/> is false, in words fit for a report and for a CLI.</summary>
    public string? DecodeProblem => CanDecode
        ? null
        : $"the decoder speaks x86, x86-64 and arm64, not {_image.Isa}";

    /// <summary>Raw image bytes. Set by the inventory builder before decoding.</summary>
    public byte[]? Data { get; set; }

    public List<DecodedInsn> DecodeSection(BinarySection section)
    {
        if (Data is null || !CanDecode)
        {
            return [];
        }

        return DecodeRange(Data, (int)section.RawOffset, (int)section.RawSize, section.Rva);
    }

    public List<DecodedInsn> DecodeRange(byte[] data, int fileOffset, int length, uint startRva)
    {
        var result = new List<DecodedInsn>();
        if (!CanDecode || length <= 0 || fileOffset < 0 || fileOffset >= data.Length)
        {
            return result;
        }

        length = Math.Min(length, data.Length - fileOffset);
        var bytes = data.AsSpan(fileOffset, length).ToArray();

        if (_image.Isa is "arm64")
        {
            return DecodeArm64(bytes, length, startRva);
        }

        // 32-bit and 64-bit x86 share an opcode space but not a meaning: the decoder has to be
        // told which one this image is.
        var decoder = Iced.Intel.Decoder.Create(_image.Is64 ? 64 : 32, bytes, _image.ImageBase + startRva);
        var output = new StringOutput();
        foreach (var instruction in decoder)
        {
            result.Add(Convert(instruction, output));
        }

        return result;
    }

    /// <summary>
    /// Walks an AArch64 window. Every instruction is four bytes, so the walk is a count rather than a
    /// decode: it cannot start an instruction in the middle of another one, and it cannot run off the
    /// end of a window into the next one the way a variable-length walk can. A trailing word or two
    /// that a section ends inside are left out — they are not instructions, they are the tail of one.
    /// </summary>
    private List<DecodedInsn> DecodeArm64(byte[] bytes, int length, uint startRva)
    {
        var result = new List<DecodedInsn>(length / Arm64Decoder.InstructionSize);
        ulong imageBase = _image.ImageBase;
        int whole = length - (length % Arm64Decoder.InstructionSize);

        for (int offset = 0; offset < whole; offset += Arm64Decoder.InstructionSize)
        {
            uint rva = startRva + (uint)offset;
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, Arm64Decoder.InstructionSize));
            var insn = Arm64Decoder.Decode(word, rva, imageBase + rva);

            if (insn.DirectTargetRva is { } target && !_image.ContainsRva(target))
            {
                // A branch that leaves the image points somewhere this analysis cannot follow, which
                // is not the same as pointing nowhere: the text still names it, only the graph omits it.
                insn.DirectTargetRva = null;
            }

            result.Add(insn);
        }

        return result;
    }

    /// <summary>Decodes every code section into an RVA-keyed map.</summary>
    public Dictionary<uint, DecodedInsn> DecodeAll()
    {
        var map = new Dictionary<uint, DecodedInsn>();
        if (!CanDecode)
        {
            return map;
        }

        foreach (var section in _image.Sections.Where(s => s.IsCode))
        {
            foreach (var insn in DecodeSection(section))
            {
                map[insn.Rva] = insn;
            }
        }

        return map;
    }

    /// <summary>Decodes a single instruction at an RVA, for <c>recon disasm</c>.</summary>
    public DecodedInsn? DecodeOne(uint rva)
    {
        if (Data is null)
        {
            return null;
        }

        var offset = _image.RvaToOffset(rva);
        if (offset is null)
        {
            return null;
        }

        var instructions = DecodeRange(Data, offset.Value, Math.Min(16, Data.Length - offset.Value), rva);
        return instructions.Count > 0 ? instructions[0] : null;
    }

    private DecodedInsn Convert(Instruction instruction, StringOutput output)
    {
        var insn = new DecodedInsn
        {
            Address = instruction.IP,
            Rva = instruction.IP >= _image.ImageBase ? (uint)(instruction.IP - _image.ImageBase) : 0,
            Length = instruction.Length,
            Code = instruction.Code,
            Mnemonic = instruction.Mnemonic,
            IsCall = instruction.Mnemonic == Mnemonic.Call,
            IsJump = instruction.Mnemonic == Mnemonic.Jmp || instruction.IsJccShortOrNear,
            IsConditionalJump = instruction.IsJccShortOrNear,
            IsReturn = instruction.Mnemonic is Mnemonic.Ret or Mnemonic.Retf or Mnemonic.Iretd,
            IsInt3 = instruction.Mnemonic == Mnemonic.Int3,
            IsNop = instruction.Mnemonic == Mnemonic.Nop,
            IsInvalid = instruction.IsInvalid,

            // `ret N` pops N bytes of arguments, which is how the callee of a stdcall call is read: the
            // amount the call removes from the caller's stack is in the callee's own last instruction.
            // This was never filled in, so every reader of it saw "no amount" — the p-code stack pass had
            // every call removing nothing, and the inventory's "ret N" evidence was never reported.
            RetPopBytes = instruction.Mnemonic is Mnemonic.Ret or Mnemonic.Retf
                && instruction.OpCount > 0
                && instruction.GetOpKind(0) is OpKind.Immediate16
                    ? (int)instruction.GetImmediate(0)
                    : null,
        };

        if (IncludeText)
        {
            _formatter.Format(instruction, output);
            insn.Text = output.ToStringAndReset();
        }

        if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
        {
            ulong target = instruction.NearBranchTarget;
            if (target >= _image.ImageBase && target - _image.ImageBase <= uint.MaxValue)
            {
                uint targetRva = (uint)(target - _image.ImageBase);
                if (_image.ContainsRva(targetRva))
                {
                    insn.DirectTargetRva = targetRva;
                }
            }
        }

        CollectOperands(instruction, insn);
        CollectImmediate(instruction, insn);
        return insn;
    }

    /// <summary>
    /// The upper-case name of each register, built once. Every memory operand used to call
    /// <c>Register.ToString().ToUpperInvariant()</c>, which is a fresh string for every operand of
    /// every instruction — millions of them for one program, all of them one of about two hundred
    /// words that were already built the first time.
    /// </summary>
    private static readonly string[] RegisterNames = BuildRegisterNames();

    private static string[] BuildRegisterNames()
    {
        var names = new string[256];
        foreach (var register in System.Enum.GetValues<Register>())
        {
            if ((int)register < names.Length)
            {
                names[(int)register] = register.ToString().ToUpperInvariant();
            }
        }

        return names;
    }

    private static string NameOf(Register register) => RegisterNames[(int)register];

    /// <summary>Whether the instruction has a memory operand at all, without asking the info factory.</summary>
    private static bool HasMemoryOperand(Instruction instruction, DecodedInsn insn)
    {
        // `lea` is the exception: iced reports no *used* memory for it, because it loads an address
        // rather than reading one, and the address is exactly what the jump-table analysis wants.
        if (insn.Mnemonic == Mnemonic.Lea)
        {
            return true;
        }

        for (int operand = 0; operand < instruction.OpCount; operand++)
        {
            if (instruction.GetOpKind(operand) == OpKind.Memory)
            {
                return true;
            }
        }

        return false;
    }

    private void CollectOperands(Instruction instruction, DecodedInsn insn)
    {
        // Nothing in this pass wants the operands, so the instruction-info factory does not have to
        // be asked about them. On a program of millions of instructions this is the difference
        // between a pass taking seconds and taking tens of seconds.
        bool wantsBranchMemory = (insn.IsCall || insn.IsJump) && instruction.Op0Kind == OpKind.Memory && insn.BranchMemory is null;
        if (!WantsRegisters(insn.Mnemonic) && !wantsBranchMemory && (!TrackMemory || !HasMemoryOperand(instruction, insn)))
        {
            return;
        }

        // iced's instruction-info factory reports exactly which registers and memory operands an
        // instruction uses and whether each is read or written.
        var info = _infoFactory.GetInfo(instruction);

        if (WantsRegisters(insn.Mnemonic))
        {
            foreach (var used in info.GetUsedRegisters())
            {
                string name = NameOf(used.Register);
                if (IsWrite(used.Access))
                {
                    insn.WrittenRegisters.Add(name);
                }

                if (IsRead(used.Access))
                {
                    insn.ReadRegisters.Add(name);
                }
            }
        }

        foreach (var memory in info.GetUsedMemory())
        {
            bool branchOperand = (insn.IsCall || insn.IsJump) && instruction.Op0Kind == OpKind.Memory && insn.BranchMemory is null;
            if (!TrackMemory && !branchOperand)
            {
                continue;
            }

            var reference = BuildMemoryRef(
                unchecked((ulong)memory.Displacement),
                GetMemorySize(memory.MemorySize),
                IsRead(memory.Access),
                IsWrite(memory.Access),
                memory.Base != Register.None,
                memory.Index != Register.None,
                NameOf(memory.Base),
                NameOf(memory.Index),
                memory.Scale);

            if (TrackMemory)
            {
                insn.MemoryRefs.Add(reference);
            }

            if (branchOperand)
            {
                insn.IsIndirectBranch = true;
                insn.BranchMemory = reference;
            }
        }

        // `lea reg, [address]` reads no memory, so iced reports no memory operand for it — and it is
        // the ordinary way a compiler loads an address. A jump table in a position-independent binary
        // is reached through a register loaded this way, so the address is recorded here or not at all.
        if (TrackMemory && insn.Mnemonic == Mnemonic.Lea && insn.MemoryRefs.Count == 0 && instruction.Op1Kind == OpKind.Memory)
        {
            insn.MemoryRefs.Add(BuildMemoryRef(
                instruction.IsIPRelativeMemoryOperand
                    ? instruction.IPRelativeMemoryAddress
                    : instruction.MemoryDisplacement64,
                GetMemorySize(instruction.MemorySize),
                read: false,
                write: false,
                instruction.MemoryBase != Register.None,
                instruction.MemoryIndex != Register.None,
                NameOf(instruction.MemoryBase),
                NameOf(instruction.MemoryIndex),
                instruction.MemoryIndexScale));
        }
    }

    /// <summary>Records the immediate operand, when there is one: `add $0x2d1f, %eax` is a constant the
    /// analysis can follow, and its value is in the instruction rather than in the operand list.</summary>
    private static void CollectImmediate(Instruction instruction, DecodedInsn insn)
    {
        for (int operand = 0; operand < instruction.OpCount; operand++)
        {
            switch (instruction.GetOpKind(operand))
            {
                case OpKind.Immediate8:
                    insn.Immediate = (sbyte)instruction.GetImmediate(operand);
                    return;
                case OpKind.Immediate16:
                case OpKind.Immediate8to16:
                    insn.Immediate = (short)instruction.GetImmediate(operand);
                    return;
                case OpKind.Immediate32:
                case OpKind.Immediate8to32:
                    insn.Immediate = (int)instruction.GetImmediate(operand);
                    return;
                case OpKind.Immediate64:
                case OpKind.Immediate32to64:
                case OpKind.Immediate8to64:
                    insn.Immediate = (long)instruction.GetImmediate(operand);
                    return;
            }
        }
    }

    private MemoryRef BuildMemoryRef(
        ulong displacement,
        int size,
        bool read,
        bool write,
        bool hasBase,
        bool hasIndex,
        string? baseRegister,
        string? indexRegister,
        int scale)
    {
        var reference = new MemoryRef
        {
            Address = displacement,
            Size = size,
            Write = write,
            Read = read,
            HasBase = hasBase,
            HasIndex = hasIndex,
            BaseRegister = hasBase ? baseRegister : null,
            IndexRegister = hasIndex ? indexRegister : null,
            IndexScale = hasBase || hasIndex ? Math.Max(1, scale) : 1,
        };

        // An operand has a known RVA when the file itself names the address: no base register (the
        // 32-bit default), or a displacement relative to the instruction pointer (the 64-bit one).
        //
        // `[edi + 0x403004]` is the interesting case, and MSVC emits it for every array access in a
        // loop: the displacement is the array's absolute address and the register is an index that
        // starts at zero. Reading it as an address is right, and dropping it would cost the xref to
        // `_g_table`. `[esi + 4]` is the opposite — an offset from a pointer nobody knows — and
        // resolving that invented data xrefs into the header at RVA 4. The two are told apart by
        // where the displacement lands: inside the image proper it is an address, in the header it
        // can only be a small offset.
        bool ripRelative = reference.BaseRegister is "RIP" or "EIP";
        bool stackRelative = reference.BaseRegister is "ESP" or "EBP" or "RSP" or "RBP";
        bool indexedAbsolute = hasBase && LandsInImage(displacement);
        if (!stackRelative && displacement != 0 && (!hasBase || ripRelative || indexedAbsolute))
        {
            reference.Rva = ResolveRva(displacement);
        }

        return reference;
    }

    private static bool IsWrite(OpAccess access)
        => access is OpAccess.Write or OpAccess.ReadWrite or OpAccess.CondWrite or OpAccess.ReadCondWrite;

    private static bool IsRead(OpAccess access)
        => access is OpAccess.Read or OpAccess.ReadWrite or OpAccess.CondRead or OpAccess.ReadCondWrite;

    private static int GetMemorySize(MemorySize size)
    {
        try
        {
            return size.GetSize();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// True when a displacement is itself an address inside the image, rather than a small offset
    /// from a register: everything below the first thing the image maps is header, and a displacement
    /// that lands there is an offset.
    /// </summary>
    private bool LandsInImage(ulong address)
    {
        if (address < _image.ImageBase)
        {
            return false;
        }

        ulong rva = address - _image.ImageBase;
        if (rva > uint.MaxValue)
        {
            return false;
        }

        uint start = _firstSectionRva ??= LowestSectionRva();

        return rva >= start && _image.ContainsRva((uint)rva);
    }

    /// <summary>The RVA the image starts mapping at: below it is header, not contents.</summary>
    private uint LowestSectionRva()
    {
        uint start = 0;
        bool any = false;
        foreach (var section in _image.Sections)
        {
            if (section.Rva == 0 || (section.RawSize == 0 && section.VirtualSize == 0))
            {
                continue;
            }

            start = any ? Math.Min(start, section.Rva) : section.Rva;
            any = true;
        }

        return start;
    }

    private uint? ResolveRva(ulong address)
    {
        if (address < _image.ImageBase)
        {
            return null;
        }

        ulong rva = address - _image.ImageBase;
        if (rva > uint.MaxValue)
        {
            return null;
        }

        return _image.ContainsRva((uint)rva) ? (uint)rva : null;
    }
}
