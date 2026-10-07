using System.Text;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Loader tests run against the synthetic fixture, which is byte-for-byte deterministic. The real
/// toolchain corpus is exercised separately in <see cref="CorpusTests"/>.
/// </summary>
public class PeLoaderTests
{
    private static PeImage Load(SyntheticPeOptions? options = null)
    {
        var result = PeLoader.LoadBytes(SyntheticPe.Build(options), "fixture.exe");
        Assert.NotNull(result.Image);
        return result.Image!;
    }

    [Fact]
    public void Reads_the_headers()
    {
        var image = Load();

        Assert.Equal(PeKind.Pe32, image.Kind);
        Assert.Equal(0x014C, image.Machine);
        Assert.Equal("x86", image.DescribeMachine());
        Assert.Equal(0x400000ul, image.ImageBase);
        Assert.Equal(SyntheticPe.EntryPointRva, image.EntryPointRva);
        Assert.Equal(0x6000u, image.SizeOfImage);
        Assert.Equal("9.0", image.LinkerVersion);
        Assert.False(image.IsDll);
        Assert.Equal(16, image.DataDirectories.Count);
    }

    [Fact]
    public void Sees_a_dll_bit_set_for_libraries()
    {
        byte[] bytes = SyntheticPe.Build();
        // IMAGE_FILE_DLL lives in the characteristics word at 0x106 of the COFF header.
        bytes[0x116] = 0x0F;
        bytes[0x117] = 0x21;
        var image = PeLoader.LoadBytes(bytes, "fixture.dll").Image!;

        Assert.True(image.IsDll);
    }

    [Fact]
    public void Reads_sections_with_flags_and_mapping()
    {
        var image = Load();

        Assert.Equal([".text", ".rdata", ".data", ".reloc"], image.Sections.Select(s => s.Name));
        var text = image.SectionNamed(".text")!;
        Assert.True(text.IsCode);
        Assert.Equal(0x1000u, text.Rva);
        Assert.Equal(0x300u, text.VirtualSize);

        // RVA to file offset mapping: the headers are unmapped, sections are mapped.
        Assert.Equal(0x118, PeLoader.RvaToOffset(image, 0x118));
        Assert.Equal(0x400, PeLoader.RvaToOffset(image, 0x1000));
        Assert.Null(PeLoader.RvaToOffset(image, 0x9000));
        Assert.Equal(0x2000u, PeLoader.AddressToRva(image, 0x402000));
        Assert.Null(PeLoader.AddressToRva(image, 0x1000));
    }

    [Fact]
    public void Reads_imports_with_names_and_iat_slots()
    {
        var image = Load();

        Assert.Equal(2, image.Imports.Count);
        Assert.All(image.Imports, import => Assert.Equal("KERNEL32.dll", import.Dll));
        Assert.Equal("GetTickCount", image.Imports[0].Name);
        Assert.Equal(SyntheticPe.IatRva, image.Imports[0].IatRva);
        Assert.Equal("ExitProcess", image.Imports[1].Name);
        Assert.Equal(SyntheticPe.IatRva + 4, image.Imports[1].IatRva);

        // The lookup table entry and the IAT slot are different addresses for the same import.
        Assert.NotEqual(image.Imports[0].LookupRva, image.Imports[0].IatRva);
    }

    [Fact]
    public void Reads_imports_by_ordinal()
    {
        byte[] bytes = SyntheticPe.Build();
        // Turn the first name entry into an ordinal entry: set the high bit of the ILT entry.
        const int ilt = 0x800 + 0x038;
        bytes[ilt] = 0x2A;
        bytes[ilt + 1] = 0x00;
        bytes[ilt + 2] = 0x00;
        bytes[ilt + 3] = 0x80;

        var image = PeLoader.LoadBytes(bytes, "fixture.exe").Image!;

        Assert.Null(image.Imports[0].Name);
        Assert.Equal((ushort)0x2A, image.Imports[0].Ordinal);
    }

    [Fact]
    public void Reads_exports_with_names_and_ordinals()
    {
        var image = Load();

        var export = Assert.Single(image.Exports);
        Assert.Equal("func_b", export.Name);
        Assert.Equal(1u, export.Ordinal);
        Assert.Equal(SyntheticPe.FuncBRva, export.Rva);
        Assert.Null(export.Forwarder);
    }

    [Fact]
    public void Reads_base_relocations_with_their_targets()
    {
        var image = Load();

        var codeRelocs = image.Relocations.Where(r => r.Rva is >= 0x1000 and < 0x2000).ToList();
        Assert.Equal(2, codeRelocs.Count);
        Assert.All(codeRelocs, r => Assert.Equal("HIGHLOW", r.Kind));

        // 0x1021 is the immediate of "mov eax, [0x402000]": the target is the data it points at.
        var dataRead = image.Relocations.Single(r => r.Rva == 0x1021);
        Assert.Equal(SyntheticPe.DataARva, dataRead.TargetRva);
        Assert.Equal(0x400000u + SyntheticPe.DataARva, dataRead.RawValue);

        // 0x1054 is the immediate of "push 0x402014".
        Assert.Equal(SyntheticPe.StringRva, image.Relocations.Single(r => r.Rva == 0x1054).TargetRva);

        // Three entries inside the jump table, which is data.
        var tableRelocs = image.Relocations.Where(r => r.Rva is >= 0x2008 and < 0x2014).ToList();
        Assert.Equal(3, tableRelocs.Count);
        Assert.Equal(SyntheticPe.JumpTargets, tableRelocs.Select(r => r.TargetRva!.Value));

        // One in .data: a pointer to code.
        Assert.Equal(SyntheticPe.FuncBRva, image.Relocations.Single(r => r.Rva == 0x3004).TargetRva);
    }

    [Fact]
    public void Relocations_that_are_not_plain_pointers_keep_their_raw_value()
    {
        byte[] bytes = SyntheticPe.Build();
        // Point the second entry of the jump-table block at data_b instead, whose value (0x2A) is
        // not an image address, so no target RVA can be derived from it.
        bytes[RelocFileOffset + 20] = 0x04;
        bytes[RelocFileOffset + 21] = 0x30;
        var image = PeLoader.LoadBytes(bytes, "fixture.exe").Image!;

        var reloc = image.Relocations.Single(r => r.Rva == 0x2004);
        Assert.Null(reloc.TargetRva);
        Assert.Equal(0x2Au, reloc.RawValue);
    }

    /// <summary>File offset of the .reloc section in the fixture.</summary>
    private const int RelocFileOffset = 0x1000;

    [Fact]
    public void Reads_the_rich_header_with_the_linker_last()
    {
        var image = Load();

        Assert.NotNull(image.Rich);
        var rich = image.Rich!;
        Assert.Equal(4, rich.Entries.Count);
        Assert.Equal([0x83u, 0x84u, 1u, 0x91u], rich.Entries.Select(e => e.ProdId));
        Assert.Equal([12u, 4u, 26u, 1u], rich.Entries.Select(e => e.Count));
        Assert.Equal(21022, rich.Entries[0].Build);
        Assert.Equal("cl_c_9.0", rich.Entries[0].Tool);
        Assert.Equal("cl_cpp_9.0", rich.Entries[1].Tool);
        Assert.Equal("imports", rich.Entries[2].Tool);
        Assert.True(rich.Entries[^1].IsLinker);
        Assert.False(rich.Entries[0].IsLinker);
        Assert.Equal(rich.Entries[^1], rich.LinkerEntry);
        Assert.NotEmpty(rich.Fingerprint);
    }

    [Fact]
    public void A_binary_without_a_rich_header_reports_none()
    {
        var image = Load(new SyntheticPeOptions { RichHeader = false });
        Assert.Null(image.Rich);
    }

    [Fact]
    public void Reads_the_coff_symbol_table_including_file_symbols_and_long_names()
    {
        var image = Load(new SyntheticPeOptions { FoldedAlias = true });

        Assert.NotEqual(0u, image.SymbolTableOffset);
        Assert.True(image.SymbolCount > 8);

        // Loader-level reading is covered by the analyser tests, which consume these symbols.
        Assert.Empty(image.CommentStrings);
    }

    [Fact]
    public void Reads_the_comment_section_as_producer_evidence()
    {
        byte[] bytes = SyntheticPe.Build(new SyntheticPeOptions { Comment = SyntheticPe.CommentText });
        var image = PeLoader.LoadBytes(bytes, "fixture.exe").Image!;

        Assert.Contains(image.CommentStrings, text => text.Contains("Microsoft (R) Optimized Compiler"));
    }

    [Fact]
    public void Reads_the_pdata_section_and_exception_directory()
    {
        var image = Load(new SyntheticPeOptions { Pdata = true });

        Assert.NotNull(image.SectionNamed(".pdata"));
        var exception = image.DataDirectories.Single(d => d.Name == "exception");
        Assert.True(exception.Present);
        Assert.Equal(SyntheticPe.PdataRva, exception.Rva);
    }

    [Fact]
    public void Reads_a_codeview_debug_directory_entry()
    {
        byte[] bytes = SyntheticPe.Build(new SyntheticPeOptions { PdbReference = true });
        var image = PeLoader.LoadBytes(bytes, "fixture.exe").Image!;

        var entry = Assert.Single(image.DebugEntries, d => d.Type == 2);
        Assert.Equal("codeview", entry.TypeName);
        Assert.Equal(@"C:\build\fixture.pdb", entry.PdbPath);
        Assert.Equal(7u, entry.PdbAge);
        Assert.NotNull(entry.PdbGuid);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_pe()
    {
        var result = PeLoader.LoadBytes(Encoding.ASCII.GetBytes("not a portable executable at all, but certainly long enough to pass the size check"), "x.bin");

        Assert.Null(result.Image);
        Assert.Contains(result.Problems, p => p.Contains("MZ"));
    }

    [Fact]
    public void Reports_a_truncated_section_instead_of_crashing()
    {
        byte[] bytes = SyntheticPe.Build();
        var truncated = bytes[..0x500];

        var result = PeLoader.LoadBytes(truncated, "truncated.exe");

        Assert.NotNull(result.Image);
        Assert.Contains(result.Problems, p => p.Contains("past the end of the file"));
    }

    [Fact]
    public void Refuses_a_64_bit_image_with_a_clear_message()
    {
        byte[] bytes = SyntheticPe.Build();
        // Flip the optional header magic to PE32+.
        bytes[0x118] = 0x0B;
        bytes[0x119] = 0x02;

        var result = PeLoader.LoadBytes(bytes, "x64.exe");

        Assert.NotNull(result.Image);
        Assert.Equal(PeKind.Pe32Plus, result.Image!.Kind);
    }

    [Fact]
    public void Hashes_are_stable_and_lower_case()
    {
        var image = Load();
        Assert.Equal(64, image.Sha256.Length);
        Assert.Equal(image.Sha256.ToLowerInvariant(), image.Sha256);
    }
}
