namespace Recon.Analysis;

/// <summary>
/// Decodes one AArch64 instruction word.
/// </summary>
/// <remarks>
/// <para>
/// AArch64 is the opposite of x86 in the one way that matters here: every instruction is four bytes,
/// so where an instruction starts is arithmetic rather than decoding. Nothing can be stepped into by
/// mistake, no window boundary can cut one in half, and a walk that recognises nothing still knows
/// exactly where the next instruction is. That is what makes a partial decoder safe where a partial
/// x86 decoder would not be: an unrecognised word costs a mnemonic, never alignment.
/// </para>
/// <para>
/// So this decodes what the analysis is built on — every form of branch, the PC-relative addressing
/// a position-independent binary uses for every global it touches, the stack pairs that open and
/// close a frame, and the arithmetic that builds a constant — and reports anything else as
/// <c>.inst 0x…</c>, the way a disassembler reports a word it has no entry for. It never guesses. An
/// instruction it does not decode is counted, sized and skipped, which is a different thing from an
/// instruction it decodes wrongly.
/// </para>
/// </remarks>
public static class Arm64Decoder
{
    /// <summary>Every AArch64 instruction is this many bytes; there is no other length.</summary>
    public const int InstructionSize = 4;

    public static DecodedInsn Decode(uint word, uint rva, ulong address)
    {
        var insn = new DecodedInsn
        {
            Address = address,
            Rva = rva,
            Length = InstructionSize,
        };

        if (TryBranch(word, address, insn)
            || TryPcRelative(word, address, insn)
            || TrySystem(word, insn)
            || TryDataProcessingImmediate(word, insn)
            || TryLoadStore(word, insn)
            || TryDataProcessingRegister(word, insn))
        {
            return insn;
        }

        Unknown(word, insn);
        return insn;
    }

    // ---------------------------------------------------------------------------- branches

    /// <summary>B, BL, B.cond, CBZ/CBNZ, TBZ/TBNZ, and the register forms BR, BLR and RET.</summary>
    private static bool TryBranch(uint word, ulong address, DecodedInsn insn)
    {
        // b / bl: a 26-bit word offset, signed, relative to this instruction.
        if ((word & 0x7C000000) == 0x14000000)
        {
            bool link = (word & 0x80000000) != 0;
            long offset = SignExtend(word & 0x03FFFFFF, 26) << 2;
            insn.IsCall = link;
            insn.IsJump = !link;
            SetTarget(insn, address, offset);
            insn.Text = (link ? "bl " : "b ") + Hex((ulong)((long)address + offset));
            return true;
        }

        // b.cond: the condition is the last four bits; bit 4 is reserved and always zero.
        if ((word & 0xFF000000) == 0x54000000 && (word & 0x10) == 0)
        {
            long offset = SignExtend((word >> 5) & 0x7FFFF, 19) << 2;
            insn.IsJump = true;
            insn.IsConditionalJump = true;
            SetTarget(insn, address, offset);
            insn.Text = $"b.{Condition(word & 0xF)} {Hex((ulong)((long)address + offset))}";
            return true;
        }

        // cbz / cbnz: compare a register against zero and branch. Bit 24 says which comparison.
        if ((word & 0x7E000000) == 0x34000000)
        {
            bool wide = (word & 0x80000000) != 0;
            bool nonzero = (word & 0x01000000) != 0;
            long offset = SignExtend((word >> 5) & 0x7FFFF, 19) << 2;
            insn.IsJump = true;
            insn.IsConditionalJump = true;
            SetTarget(insn, address, offset);
            insn.Text = $"cb{(nonzero ? "nz" : "z")} {Register((int)(word & 0x1F), wide)}, "
                        + Hex((ulong)((long)address + offset));
            return true;
        }

        // tbz / tbnz: test one bit and branch. The bit number is b5:b40, split across the word.
        if ((word & 0x7E000000) == 0x36000000)
        {
            bool nonzero = (word & 0x80000000) != 0;
            int bit = (int)(((word >> 26) & 0x20) | ((word >> 19) & 0x1F));
            long offset = SignExtend((word >> 5) & 0x3FFF, 14) << 2;
            insn.IsJump = true;
            insn.IsConditionalJump = true;
            SetTarget(insn, address, offset);
            insn.Text = $"tb{(nonzero ? "nz" : "z")} {Register((int)(word & 0x1F), bit >= 32)}, #{bit}, "
                        + Hex((ulong)((long)address + offset));
            return true;
        }

        // br / blr / ret: an unconditional branch to a register. They share one encoding and differ
        // only in the four-bit opcode, which is why `ret` is a branch and not an instruction of its
        // own kind.
        if ((word & 0xFE1F0000) == 0xD61F0000 && (word & 0x0000FC00) == 0)
        {
            int rn = (int)((word >> 5) & 0x1F);
            switch ((word >> 21) & 0xF)
            {
                case 0b0000:
                    insn.IsJump = true;
                    insn.IsIndirectBranch = true;
                    insn.Text = $"br {Register(rn, wide: true)}";
                    return true;
                case 0b0001:
                    insn.IsCall = true;
                    insn.IsIndirectBranch = true;
                    insn.Text = $"blr {Register(rn, wide: true)}";
                    return true;
                case 0b0010:
                    insn.IsReturn = true;
                    insn.Text = rn == 30 ? "ret" : $"ret {Register(rn, wide: true)}";
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records where a branch goes, as an RVA rather than only in the text, so that the analysis reads
    /// the target the decoder computed instead of parsing its own output.
    /// </summary>
    private static void SetTarget(DecodedInsn insn, ulong address, long offset)
    {
        ulong target = (ulong)((long)address + offset);
        if (target >= address - insn.Rva)
        {
            insn.DirectTargetRva = (uint)(target - (address - insn.Rva));
        }
    }

    // ------------------------------------------------------------------------ PC-relative

    /// <summary>
    /// ADR and ADRP: how AArch64 reaches an address without a load. ADRP yields the 4 KiB page the
    /// symbol lives on — the instruction after it adds the offset inside that page — so reporting the
    /// page is what the instruction says, and naming a symbol would be a guess at the next one.
    /// </summary>
    private static bool TryPcRelative(uint word, ulong address, DecodedInsn insn)
    {
        if ((word & 0x1F000000) != 0x10000000)
        {
            return false;
        }

        bool page = (word & 0x80000000) != 0;
        long offset = SignExtend(((word >> 5) & 0x7FFFF) << 2 | ((word >> 29) & 0x3), 21);
        int rd = (int)(word & 0x1F);

        if (page)
        {
            ulong pageBase = (address & ~0xFFFUL) + (ulong)(offset << 12);
            insn.Text = $"adrp {Register(rd, wide: true)}, 0x{pageBase:x}";
        }
        else
        {
            insn.Text = $"adr {Register(rd, wide: true)}, {Hex((ulong)((long)address + offset))}";
        }

        return true;
    }

    // ----------------------------------------------------------------------------- system

    private static bool TrySystem(uint word, DecodedInsn insn)
    {
        if (word == 0xD503201F)
        {
            insn.IsNop = true;
            insn.Text = "nop";
            return true;
        }

        // svc / brk: the two a program asks for deliberately.
        if ((word & 0xFC000000) == 0xD4000000)
        {
            uint immediate = (word >> 5) & 0xFFFF;
            uint kind = (word >> 21) & 0x7;
            if (kind == 0 && (word & 0x1F) == 1)
            {
                insn.Text = $"svc #0x{immediate:x}";
                return true;
            }

            if (kind == 1 && (word & 0x1F) == 0)
            {
                insn.Text = $"brk #0x{immediate:x}";
                return true;
            }
        }

        // The hint group: scheduling advice that changes no state. nop is the one that matters, and
        // naming the rest is honest where decoding them as something else would not be.
        if ((word & 0xFFFFFC1F) == 0xD503201F)
        {
            string? name = ((word >> 5) & 0x7F) switch
            {
                1 => "yield",
                2 => "wfe",
                3 => "wfi",
                4 => "sev",
                5 => "sevl",
                _ => null,
            };

            if (name is not null)
            {
                insn.Text = name;
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------- data processing, immediate

    private static bool TryDataProcessingImmediate(uint word, DecodedInsn insn)
    {
        if ((word & 0x1C000000) != 0x10000000)
        {
            return false;
        }

        bool wide = (word & 0x80000000) != 0;
        uint op = (word >> 23) & 0x7;

        // add / sub (immediate), with the cmp/cmn alias whose destination is the zero register.
        if (op == 0b010)
        {
            bool subtract = (word & 0x40000000) != 0;
            bool setFlags = (word & 0x20000000) != 0;
            uint imm12 = (word >> 10) & 0xFFF;

            // sh, bit 22, selects imm12 as it stands or shifted into the high half of the register.
            ulong immValue = ((word >> 22) & 0x1) != 0 ? imm12 << 12 : imm12;

            int rn = (int)((word >> 5) & 0x1F);
            int rd = (int)(word & 0x1F);
            string operand = Register(rn, wide, sp: true);
            string destination = Register(rd, wide, sp: true);

            if (setFlags && rd == 31)
            {
                insn.Text = $"{(subtract ? "cmp" : "cmn")} {operand}, #0x{immValue:x}";
            }
            else if (!subtract && !setFlags && immValue == 0 && (rn == 31 || rd == 31))
            {
                insn.Text = $"mov {destination}, {operand}";
            }
            else
            {
                insn.Text = $"{(subtract ? "sub" : "add")}{(setFlags ? "s" : string.Empty)} "
                            + $"{destination}, {operand}, #0x{immValue:x}";
            }

            return true;
        }

        // move wide: movn / movz / movk, the three that build a constant one 16-bit piece at a time.
        if (op == 0b101)
        {
            string name = ((word >> 29) & 0x3) switch
            {
                0b00 => "movn",
                0b10 => "movz",
                0b11 => "movk",
                _ => string.Empty,
            };

            if (name.Length == 0)
            {
                return false;
            }

            uint hw = (word >> 21) & 0x3;
            uint imm16 = (word >> 5) & 0xFFFF;
            int rd = (int)(word & 0x1F);
            string shifted = hw == 0 ? string.Empty : $", lsl #{hw * 16}";
            insn.Text = $"{name} {Register(rd, wide)}, #0x{imm16:x}{shifted}";
            return true;
        }

        // logical immediate: and / orr / eor / ands, and `orr Rd, xzr, #imm`, which is how a constant
        // that is not a movz shape gets into a register.
        if (op == 0b100)
        {
            uint opc = (word >> 29) & 0x3;
            int rn = (int)((word >> 5) & 0x1F);
            int rd = (int)(word & 0x1F);
            if (!TryBitmaskImmediate(word, wide, out ulong immediate))
            {
                return false;
            }

            if (opc == 0b01 && rn == 31)
            {
                insn.Text = $"mov {Register(rd, wide)}, #0x{immediate:x}";
                return true;
            }

            string name = opc switch { 0b00 => "and", 0b01 => "orr", 0b10 => "eor", _ => "ands" };
            insn.Text = $"{name} {Register(rd, wide)}, {Register(rn, wide)}, #0x{immediate:x}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// The immediate of a logical instruction is not stored as a value: N, immr and imms describe a
    /// run of ones rotated within an element, and the element size comes from N and imms together.
    /// Only the two shapes that are unambiguous are decoded — a 64-bit pattern (N set) and a 32-bit
    /// plain one — because a wrong reconstruction would print a number the program does not contain.
    /// Anything else is left to <c>.inst</c>.
    /// </summary>
    private static bool TryBitmaskImmediate(uint word, bool wide, out ulong immediate)
    {
        immediate = 0;
        bool n = (word & 0x00400000) != 0;
        uint immr = (word >> 16) & 0x3F;
        uint imms = (word >> 10) & 0x3F;

        if (n)
        {
            // N set: the element is the whole 64-bit register, imms counts the ones, immr rotates
            // them right — bit i moves to bit i - immr, which is a shift down, not up.
            ulong ones = imms == 63 ? ulong.MaxValue : (1UL << (int)(imms + 1)) - 1;
            int rotate = (int)(immr & 63);
            immediate = rotate == 0 ? ones : (ones >> rotate) | (ones << (64 - rotate));
            return true;
        }

        if (imms < 32 && immr < 32)
        {
            // N clear with a 32-bit element: the pattern is replicated to fill a 64-bit register.
            uint ones = (1u << (int)(imms + 1)) - 1;
            int rotate = (int)(immr & 31);
            uint pattern = rotate == 0 ? ones : (ones >> rotate) | (ones << (32 - rotate));
            immediate = wide ? ((ulong)pattern << 32) | pattern : pattern;
            return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------- load/store

    private static bool TryLoadStore(uint word, DecodedInsn insn)
    {
        // Load/store pair: the instructions that open and close a frame, and one reason a function's
        // start can be recognised on a binary with no symbols at all.
        if ((word & 0x3A000000) == 0x28000000)
        {
            uint opc = (word >> 30) & 0x3;
            bool wide = opc == 0b10 || opc == 0b11;
            int scale = opc == 0b10 ? 8 : 4;
            long immediate = SignExtend((word >> 15) & 0x7F, 7) * scale;
            int rt2 = (int)((word >> 10) & 0x1F);
            int rn = (int)((word >> 5) & 0x1F);
            int rt = (int)(word & 0x1F);
            bool load = (word & 0x00400000) != 0;
            uint mode = (word >> 23) & 0x7;

            string name = load ? "ldp" : "stp";
            string address = mode switch
            {
                0b001 => $"[{Register(rn, wide: true, sp: true)}], #{immediate}",
                0b011 => $"[{Register(rn, wide: true, sp: true)}, #{immediate}]!",
                0b010 => $"[{Register(rn, wide: true, sp: true)}, #{immediate}]",
                _ => $"[{Register(rn, wide: true, sp: true)}]",
            };

            insn.Text = $"{name} {Register(rt, wide)}, {Register(rt2, wide)}, {address}";
            return true;
        }

        // Load literal: a constant or address held in a literal pool beside the code.
        if ((word & 0x3B000000) == 0x18000000)
        {
            uint opc = (word >> 30) & 0x3;
            if (opc > 0b01)
            {
                return false;
            }

            long offset = SignExtend((word >> 5) & 0x7FFFF, 19) << 2;
            int rt = (int)(word & 0x1F);
            insn.Text = $"ldr {Register(rt, opc == 0b01)}, {Hex((ulong)((long)insn.Address + offset))}";
            return true;
        }

        // Load/store with a register offset: an index into an array, scaled and optionally extended.
        if ((word & 0x3B200C00) == 0x38200800)
        {
            uint size = (word >> 30) & 0x3;
            uint opc = (word >> 22) & 0x3;
            if (opc > 0b01)
            {
                // opc 10 and 11 are the sign-extending loads and the prefetch hint: real
                // instructions, but rendering one wrongly is worse than not rendering it.
                return false;
            }

            int rm = (int)((word >> 16) & 0x1F);
            uint option = (word >> 13) & 0x7;
            int rn = (int)((word >> 5) & 0x1F);
            int rt = (int)(word & 0x1F);
            string amount = (word & 0x1000) != 0 ? $" #{1 << (int)size}" : string.Empty;
            string index = size == 0b11
                ? $"{Register(rm, wide: true)}{ExtendText(option, 0)}{amount}"
                : $"{Register(rm, wide: false)}{ExtendText(option, 0)}{amount}";
            insn.Text = $"{(opc == 0b01 ? "ldr" : "str")} {Register(rt, size == 0b11)}, "
                        + $"[{Register(rn, wide: true, sp: true)}, {index.TrimEnd()}]";
            return true;
        }

        // Load/store with an unsigned immediate offset: the ordinary `ldr x0, [x1, #8]`.
        if ((word & 0x3B000000) == 0x39000000)
        {
            uint size = (word >> 30) & 0x3;
            uint opc = (word >> 22) & 0x3;
            if (opc > 0b01)
            {
                return false;
            }

            uint immediate = (word >> 10) & 0xFFF;
            int rn = (int)((word >> 5) & 0x1F);
            int rt = (int)(word & 0x1F);
            uint scale = 1u << (int)size;
            string offset = immediate == 0 ? string.Empty : $", #{immediate * scale}";
            string register = size == 0b11 ? Register(rt, wide: true) : Register(rt, wide: false);
            insn.Text = $"{(opc == 0b01 ? "ldr" : "str")} {register}, "
                        + $"[{Register(rn, wide: true, sp: true)}{offset}]";
            return true;
        }

        return false;
    }

    // -------------------------------------------------------- data processing, register

    private static bool TryDataProcessingRegister(uint word, DecodedInsn insn)
    {
        bool wide = (word & 0x80000000) != 0;

        // Logical, shifted register: and / orr / eor / ands, and `orr Rd, xzr, Rm`, which is `mov`.
        if ((word & 0x1F000000) == 0x0A000000)
        {
            uint opc = (word >> 29) & 0x3;
            uint shift = (word >> 22) & 0x3;
            uint amount = (word >> 10) & 0x3F;
            int rm = (int)((word >> 16) & 0x1F);
            int rn = (int)((word >> 5) & 0x1F);
            int rd = (int)(word & 0x1F);

            if (opc == 0b01 && rn == 31 && shift == 0 && amount == 0)
            {
                insn.Text = $"mov {Register(rd, wide)}, {Register(rm, wide)}";
                return true;
            }

            string name = opc switch { 0b00 => "and", 0b01 => "orr", 0b10 => "eor", _ => "ands" };
            insn.Text = $"{name} {Register(rd, wide)}, {Register(rn, wide)}, {Register(rm, wide)}{ShiftText(shift, amount)}";
            return true;
        }

        // Add/subtract, shifted or extended register. The cmp/cmn alias is printed only when the
        // instruction carries no shift, because that is when the alias and the instruction say the
        // same thing; otherwise `subs xzr, …` is what it assembles to anyway.
        if ((word & 0x1F000000) == 0x0B000000)
        {
            bool subtract = (word & 0x40000000) != 0;
            bool setFlags = (word & 0x20000000) != 0;
            bool extended = (word & 0x00200000) != 0;
            int rm = (int)((word >> 16) & 0x1F);
            int rn = (int)((word >> 5) & 0x1F);
            int rd = (int)(word & 0x1F);
            uint shift = (word >> 22) & 0x3;
            uint amount = (word >> 10) & 0x3F;
            uint option = (word >> 13) & 0x7;
            uint extendAmount = (word >> 10) & 0x7;

            string operand = extended
                ? $"{Register(rm, wide)}{ExtendText(option, extendAmount)}"
                : $"{Register(rm, wide)}{ShiftText(shift, amount)}";

            bool isAlias = setFlags && rd == 31
                           && (extended ? extendAmount == 0 && option is 0b010 or 0b011 : shift == 0 && amount == 0);

            if (isAlias)
            {
                insn.Text = $"{(subtract ? "cmp" : "cmn")} {Register(rn, wide)}, {operand}";
            }
            else
            {
                insn.Text = $"{(subtract ? "sub" : "add")}{(setFlags ? "s" : string.Empty)} "
                            + $"{Register(rd, wide, sp: true)}, {Register(rn, wide, sp: true)}, {operand}";
            }

            return true;
        }

        // Multiply-add: madd and msub, which `mul` and `mneg` are the aliases of — the same
        // instruction with the zero register as the value being added.
        if ((word & 0x7FE00000) == 0x1B000000)
        {
            bool subtract = (word & 0x00008000) != 0;
            int rm = (int)((word >> 16) & 0x1F);
            int ra = (int)((word >> 10) & 0x1F);
            int rn = (int)((word >> 5) & 0x1F);
            int rd = (int)(word & 0x1F);

            if (ra == 31)
            {
                insn.Text = $"{(subtract ? "mneg" : "mul")} {Register(rd, wide)}, {Register(rn, wide)}, {Register(rm, wide)}";
            }
            else
            {
                insn.Text = $"{(subtract ? "msub" : "madd")} {Register(rd, wide)}, {Register(rn, wide)}, "
                            + $"{Register(rm, wide)}, {Register(ra, wide)}";
            }

            return true;
        }

        return false;
    }

    private static string ShiftText(uint shift, uint amount)
        => shift switch
        {
            0b01 => $", lsl #{amount}",
            0b10 => $", lsr #{amount}",
            0b11 => $", asr #{amount}",
            _ => string.Empty,
        };

    private static string ExtendText(uint option, uint amount)
    {
        string name = option switch
        {
            0b000 => "uxtb",
            0b001 => "uxth",
            0b010 => "uxtw",
            0b011 => "uxtx",
            0b100 => "sxtb",
            0b101 => "sxth",
            0b110 => "sxtw",
            _ => "sxtx",
        };

        return amount == 0 ? $", {name}" : $", {name} #{amount}";
    }

    // ------------------------------------------------------------------------------ unknown

    /// <summary>
    /// What an unrecognised word becomes: counted, sized, and shown as the word it is. Not
    /// <c>(bad)</c> — it is four bytes of a real instruction stream, and calling it invalid would be a
    /// claim about the program rather than about what this decoder knows.
    /// </summary>
    private static void Unknown(uint word, DecodedInsn insn) => insn.Text = $".inst 0x{word:x8}";

    // ------------------------------------------------------------------------------- helpers

    private static string Condition(uint condition) => condition switch
    {
        0 => "eq",
        1 => "ne",
        2 => "hs",
        3 => "lo",
        4 => "mi",
        5 => "pl",
        6 => "vs",
        7 => "vc",
        8 => "hi",
        9 => "ls",
        10 => "ge",
        11 => "lt",
        12 => "gt",
        13 => "le",
        14 => "al",
        _ => "nv",
    };

    /// <summary>
    /// The name of a register. Register 31 is the stack pointer in the encodings that address memory
    /// and the zero register everywhere else, which is why the caller says which one it is reading.
    /// </summary>
    private static string Register(int number, bool wide, bool sp = false)
    {
        if (number == 31)
        {
            return sp ? (wide ? "sp" : "wsp") : (wide ? "xzr" : "wzr");
        }

        return (wide ? "x" : "w") + number;
    }

    private static string Hex(ulong value) => $"0x{value:x}";

    private static long SignExtend(uint value, int bits)
    {
        // Shift the sign bit up into the top of a long and back down arithmetically. The obvious
        // `(value ^ sign) - sign` is unsigned subtraction: for a negative offset it wraps to a huge
        // positive number, and a branch target came out four gigabytes above where it points.
        int shift = 64 - bits;
        return ((long)value << shift) >> shift;
    }
}
