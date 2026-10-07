using System.Buffers.Binary;
using Recon.Analysis;
using Recon.Images;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// AArch64 decoding. The instruction words below are not invented: they are taken from
/// <c>sample-macho-arm64</c> in the corpus, whose functions the linker's own map names and whose
/// globals the relocation records place, so a word and the address it reaches can be checked against
/// each other rather than against this decoder's opinion of itself.
/// </summary>
public class Arm64DecoderTests
{
    private const ulong ImageBase = 0x100000000;

    // -------------------------------------------------------------------------- control flow

    [Fact]
    public void A_return_is_a_branch_to_the_link_register()
    {
        // c0035fd6 in _add: the last instruction of every function in the corpus.
        var insn = Arm64Decoder.Decode(0xD65F03C0u, rva: 0x5B4, address: ImageBase + 0x5B4);

        Assert.True(insn.IsReturn);
        Assert.Equal(4, insn.Length);
        Assert.Equal("ret", insn.Text);
    }

    [Fact]
    public void A_call_is_a_branch_that_records_where_it_goes()
    {
        // 5effff97 in _main: `bl _add`, whose target the map puts at 0x1000005B0.
        var insn = Arm64Decoder.Decode(0x97FFFF5Eu, rva: 0x838, address: ImageBase + 0x838);

        Assert.True(insn.IsCall);
        Assert.False(insn.IsJump);
        Assert.Equal(0x5B0u, insn.DirectTargetRva);
        Assert.Equal($"bl 0x{ImageBase + 0x5B0:x}", insn.Text);

        // A plain branch is the same instruction without the link.
        var branch = Arm64Decoder.Decode(0x14000002u, rva: 0x760, address: ImageBase + 0x760);
        Assert.True(branch.IsJump);
        Assert.False(branch.IsCall);
        Assert.Equal(0x768u, branch.DirectTargetRva);
    }

    [Fact]
    public void A_backward_branch_is_signed()
    {
        // 41ffff54 in _loop_sum: `b.ne` from 0x75C back to 0x744. An unsigned offset would send the
        // loop into whatever follows the section, and every loop in the program with it.
        var insn = Arm64Decoder.Decode(0x54FFFF41u, rva: 0x75C, address: ImageBase + 0x75C);

        Assert.True(insn.IsConditionalJump);
        Assert.Equal(0x744u, insn.DirectTargetRva);
        Assert.StartsWith("b.ne", insn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_through_a_register_is_indirect_and_has_no_target()
    {
        // br x8 / blr x8: where they go is decided at run time, so the honest answer is no answer.
        var jump = Arm64Decoder.Decode(0xD61F0100u, rva: 0, address: ImageBase);
        Assert.True(jump.IsJump);
        Assert.True(jump.IsIndirectBranch);
        Assert.Null(jump.DirectTargetRva);

        var call = Arm64Decoder.Decode(0xD63F0100u, rva: 0, address: ImageBase);
        Assert.True(call.IsCall);
        Assert.True(call.IsIndirectBranch);
        Assert.Null(call.DirectTargetRva);
    }

    // ----------------------------------------------------------------------------- the frame

    [Fact]
    public void A_frame_opens_with_a_pre_indexed_pair_and_closes_with_a_post_indexed_one()
    {
        // fd7b04a9 at the top of _main: stp x29, x30, [sp, #64] — the pair that saves the frame
        // pointer and the return address before the frame moves.
        var open = Arm64Decoder.Decode(0xA9047BFDu, rva: 0, address: ImageBase);
        Assert.Equal("stp x29, x30, [sp, #64]", open.Text);

        // f657c3a8 at the end of _loop_sum: ldp x22, x21, [sp], #48 — the same pair restored after
        // the stack pointer has been put back.
        var close = Arm64Decoder.Decode(0xA8C357F6u, rva: 0, address: ImageBase);
        Assert.Equal("ldp x22, x21, [sp], #48", close.Text);
    }

    // --------------------------------------------------------------------------- constants

    [Fact]
    public void A_constant_that_will_not_fit_in_an_instruction_is_built_in_pieces()
    {
        // 41008052 in _main, before `bl _add`: movz w1, #0x2 — the second argument.
        Assert.Equal("movz w1, #0x2", Arm64Decoder.Decode(0x52800041u, rva: 0, address: ImageBase).Text);

        // 00008012 in _dispatch: movn w0, #0x0 — the default case's -1, written as a bitwise not.
        Assert.Equal("movn w0, #0x0", Arm64Decoder.Decode(0x12800000u, rva: 0, address: ImageBase).Text);
    }

    [Fact]
    public void A_bit_pattern_is_reconstructed_from_immr_and_imms()
    {
        // a80a0012 in _loop_sum: and w8, w21, #0x7 — `i & 7`. The immediate is stored as a run of
        // ones and a rotation, not as a seven; decoding it wrong would print a number that is not
        // in the program.
        Assert.Equal("and w8, w21, #0x7", Arm64Decoder.Decode(0x12000AA8u, rva: 0, address: ImageBase).Text);

        // The same instruction with immr set: four ones rotated right by four places, which is
        // 0xf0000000 and not 0x000000f0. Every immediate in the corpus sits at immr == 0, where a
        // rotation and its mirror agree, so this case is the one the corpus cannot check.
        Assert.Equal("and w0, w0, #0xf0000000", Arm64Decoder.Decode(0x12040C00u, rva: 0, address: ImageBase).Text);
    }

    [Fact]
    public void A_multiply_add_with_the_zero_register_is_a_multiply()
    {
        // 1451151b in _loop_sum: madd w20, w8, w21, w20 — sum += i * g_table[i & 7] in one
        // instruction, whose addend is the accumulator rather than zero.
        Assert.Equal("madd w20, w8, w21, w20", Arm64Decoder.Decode(0x1B155114u, rva: 0, address: ImageBase).Text);

        // The same instruction with xzr as its addend is what `mul` assembles to.
        Assert.Equal("mul w0, w1, w2", Arm64Decoder.Decode(0x1B027C20u, rva: 0, address: ImageBase).Text);
    }

    // ---------------------------------------------------------------------------- addressing

    [Fact]
    public void An_address_is_reached_as_a_page_and_an_offset_within_it()
    {
        // 48000090 at the end of _loop_sum: adrp x8, 0x100008000, the page _g_counter lives on; the
        // `str` after it supplies the offset inside that page.
        var page = Arm64Decoder.Decode(0x90000048u, rva: 0x768, address: ImageBase + 0x768);
        Assert.Equal($"adrp x8, 0x{ImageBase + 0x8000:x}", page.Text);

        // b6c60310 earlier in the same function: adr x22, 0x100008010 — _g_table, close enough for
        // one instruction to name it outright.
        var near = Arm64Decoder.Decode(0x1003C6B6u, rva: 0x73C, address: ImageBase + 0x73C);
        Assert.Equal($"adr x22, 0x{ImageBase + 0x8010:x}", near.Text);
    }

    [Fact]
    public void An_indexed_read_is_a_register_offset_scaled_by_the_element_size()
    {
        // c85a68b8 in _loop_sum: ldr w8, [x22, w8, uxtw #4] — g_table[i & 7], where the index is a
        // 32-bit value extended to 64 bits and scaled by four.
        Assert.Equal("ldr w8, [x22, w8, uxtw #4]", Arm64Decoder.Decode(0xB8685AC8u, rva: 0, address: ImageBase).Text);
    }

    // ------------------------------------------------------------------------------ the rest

    [Fact]
    public void A_word_it_does_not_know_is_counted_and_sized_but_not_named()
    {
        // The all-zero word is not a legal AArch64 instruction. It is still four bytes of a real
        // instruction stream, so it is sized and counted; what it is not is called invalid, which
        // would be a claim about the program rather than about what this decoder knows.
        var insn = Arm64Decoder.Decode(0x00000000u, rva: 0x40, address: ImageBase + 0x40);

        Assert.Equal(4, insn.Length);
        Assert.False(insn.IsInvalid);
        Assert.Equal(".inst 0x00000000", insn.Text);
    }

    [Fact]
    public void Every_instruction_is_four_bytes_so_a_walk_cannot_lose_its_place()
    {
        // Ten bytes: two instructions and a tail that is not one. On x86 that tail would be an
        // instruction the buffer ended inside, and the next window would start in the middle of it.
        var bytes = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xD65F03C0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0xD65F03C0);

        var image = ArmImage(bytes);
        var decoder = new CodeDecoder(image) { Data = image.Bytes };
        var section = Assert.Single(image.Sections);
        var instructions = decoder.DecodeSection(section);

        Assert.Equal(2, instructions.Count);
        Assert.Equal(0x1000u, instructions[0].Rva);
        Assert.Equal(0x1004u, instructions[1].Rva);
    }

    /// <summary>
    /// The whole point of decoding is the graph: which addresses are code, and what reaches what.
    /// Every call in this binary goes to an address the linker's own map says a function starts at, so
    /// a decoder that misplaces one bit of an offset lands in the middle of nowhere and is caught here.
    /// </summary>
    [Fact]
    public void Every_call_in_the_arm64_corpus_reaches_a_function_the_map_names()
    {
        if (!TestPaths.MachoCorpusExists("sample-macho-arm64"))
        {
            return;
        }

        var result = ImageLoader.Load(TestPaths.MachoCorpus("sample-macho-arm64"));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        var image = result.Image!;

        var map = Recon.DebugInfo.MapFileReader.Read(TestPaths.MachoCorpus("sample-macho-arm64.map"), image);
        var starts = map.Symbols
            .Where(s => !s.IsData)
            .Select(s => s.Rva)
            .ToHashSet();

        var decoder = new CodeDecoder(image) { Data = image.Bytes, IncludeText = false };
        var calls = image.Sections
            .Where(s => s.IsCode)
            .SelectMany(s => decoder.DecodeSection(s))
            .Where(i => i.IsCall && i.DirectTargetRva is not null)
            .ToList();

        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            Assert.True(
                starts.Contains(call.DirectTargetRva!.Value),
                $"a call at 0x{call.Rva:x} reaches 0x{call.DirectTargetRva.Value:x}, which the map does not name as a function");
        }
    }

    private static IBinaryImage ArmImage(byte[] code)
    {
        // The synthetic PE32 the other analysis tests use, with its machine set to AArch64: the
        // decoder is chosen by the image's instruction set, not by the container it came in.
        var image = new byte[0x400 + code.Length];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0x40);

        int pe = 0x40;
        "PE\0\0"u8.CopyTo(image.AsSpan(pe, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 4), 0xAA64);  // AArch64
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 20), 0xF0);   // PE32+ optional header
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 22), 0x0102);

        int optional = pe + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x20B);  // PE32+
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optional + 24), ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 32), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 108), 16);

        int section = optional + 0xF0;
        ".text\0\0"u8.CopyTo(image.AsSpan(section, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), (uint)code.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), (uint)code.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 36), 0x60000020);

        code.CopyTo(image, 0x400);

        var loaded = ImageLoader.LoadBytes(image, "arm64-fixture.exe");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Problems));
        return loaded.Image!;
    }
}
