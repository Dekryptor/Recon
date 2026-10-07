using Recon.DebugInfo;
using Recon.Images;
using Recon.Macho;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The Mach-O loader, over a file built in memory. The corpus covers what clang and ld64.lld write;
/// this covers the two shapes no corpus binary here has — 32-bit and byte-reversed — and the fat
/// container in front of an image, because those are the parts of a header parser that a working
/// corpus never exercises.
/// </summary>
public class MachoLoaderTests
{
    private static LoadedImage Load(SyntheticMachoOptions? options = null, string name = "fixture.macho")
        => ImageLoader.LoadBytes(SyntheticMacho.Build(options), name);

    private static IBinaryImage Image(SyntheticMachoOptions? options = null)
    {
        var result = Load(options);
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    // ------------------------------------------------------------------ which file is this

    [Fact]
    public void A_macho_file_is_known_from_a_pe_or_an_elf_one()
    {
        var result = Load();

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Equal("macho64", result.Format);
        Assert.NotNull(result.Image!.Macho);
        Assert.Null(result.Image.Pe);
        Assert.Null(result.Image.Elf);
    }

    [Fact]
    public void A_32_bit_macho_file_is_read_as_one()
    {
        var image = Image(new SyntheticMachoOptions { Is64 = false });

        Assert.Equal("macho32", image.Format);
        Assert.Equal("x86", image.ArchName);
        Assert.Equal(SyntheticMacho.Base32, image.ImageBase);
    }

    /// <summary>
    /// The byte order no machine on a developer's desk has. Every address in the file is written the
    /// other way round, so a loader that reads them natively gets a different image and not an error.
    /// </summary>
    [Fact]
    public void A_byte_reversed_macho_file_reads_the_same_image()
    {
        var image = Image(new SyntheticMachoOptions { BigEndian = true });

        Assert.Equal("macho64", image.Format);
        Assert.Equal(SyntheticMacho.Base, image.ImageBase);

        var text = Assert.Single(image.Sections, s => s.Name == "__text");
        Assert.Equal(SyntheticMacho.AddOffset, text.Rva);
        Assert.Equal(0x20u, text.VirtualSize);
    }

    [Fact]
    public void A_fat_file_is_read_as_the_slice_it_holds()
    {
        var image = Image(new SyntheticMachoOptions { Fat = true });

        Assert.Equal("macho64", image.Format);
        Assert.Equal("x64", image.ArchName);

        var macho = image.Macho!;
        Assert.Equal(2, macho.FatSlices.Count);
        Assert.Contains(macho.FatSlices, slice => slice.Architecture == "ppc");
        Assert.Contains(macho.FatSlices, slice => slice.Architecture == "x64");

        // The PowerPC slice is first, so reading x86-64 means the loader chose rather than assumed.
        Assert.Equal("ppc", macho.FatSlices[0].Architecture);
    }

    /// <summary>A fat file with 32-byte entries: the same file, one word wider per slice.</summary>
    [Fact]
    public void A_fat_file_with_wide_entries_is_read_the_same_way()
    {
        var image = Image(new SyntheticMachoOptions { Fat = true, Fat64 = true });

        Assert.Equal("macho64", image.Format);
        Assert.Equal(2, image.Macho!.FatSlices.Count);
        Assert.Equal(SyntheticMacho.Base, image.ImageBase);
    }

    // ------------------------------------------------------------------ what the file says

    [Fact]
    public void The_sections_are_where_the_file_says_they_are()
    {
        var image = Image();

        var text = Assert.Single(image.Sections, s => s.Name == "__text");
        Assert.Equal(SyntheticMacho.AddOffset, text.Rva);
        Assert.True(text.IsCode);

        var stubs = Assert.Single(image.Sections, s => s.Name == "__stubs");
        Assert.Equal(SyntheticMacho.StubOffset, stubs.Rva);
        Assert.Contains("symbol_stubs", stubs.FlagNames);

        var data = Assert.Single(image.Sections, s => s.Name == "__data");
        Assert.False(data.IsCode);
    }

    /// <summary>
    /// <c>LC_MAIN</c>'s entry point is an offset into a segment, not an address: it has to be turned
    /// into one with the segment it belongs to, which is the one thing about it a reader gets wrong.
    /// </summary>
    [Fact]
    public void The_entry_point_comes_from_the_main_load_command()
    {
        var image = Image();

        Assert.Equal(SyntheticMacho.AddOffset + SyntheticMacho.EntryOffset, image.EntryPointRva);
    }

    [Fact]
    public void The_symbols_are_read_and_the_import_is_not_one_of_them()
    {
        var image = Image();

        Assert.Contains(image.Symbols, s => s.Name == "_add" && s.Rva == SyntheticMacho.AddOffset);
        Assert.Contains(image.Symbols, s => s.Name == "_main");

        var printf = Assert.Single(image.Imports, i => i.Name == "_printf");
        Assert.Equal(SyntheticMacho.PointerOffset, printf.SlotRva);
    }

    /// <summary>
    /// A stub has no <c>nlist</c> of its own: the indirect symbol table is the only thing in the file
    /// that says which import it is for, and the section's own reserved2 is what says how big one is.
    /// </summary>
    [Fact]
    public void A_stub_is_named_from_the_indirect_symbol_table()
    {
        var image = Image();
        var debug = MachoSymbols.Read(image);

        Assert.NotNull(debug);
        var stub = Assert.Single(debug.Symbols, s => s.Name == "_printf" && !s.IsData);
        Assert.Equal(SyntheticMacho.StubOffset, stub.Rva);
        Assert.Equal(6u, stub.Size);
        Assert.Equal(SymbolSource.Macho, stub.Source);
    }

    /// <summary>
    /// The analysis over a 32-bit Mach-O image, which is the one shape no corpus binary here has: the
    /// word size the loader reads differently in the segment commands, in the <c>nlist</c> entries and
    /// in the entry point, and which the rest of the tool is supposed not to notice at all.
    /// </summary>
    [Fact]
    public void A_32_bit_macho_image_is_inventoried()
    {
        var image = Image(new SyntheticMachoOptions { Is64 = false });
        var project = new Recon.Config.ProjectConfig
        {
            Project = new Recon.Config.ProjectMeta { Name = "macho32" },
            Target = new Recon.Config.TargetSpec { Format = image.Format, Arch = image.ArchName },
        };

        var document = Recon.Inventory.InventoryBuilder.Build(new Recon.Inventory.InventoryInputs
        {
            Project = project,
            Image = image,
            Bytes = image.Bytes,
            Debug = MachoSymbols.Read(image),
            Options = new Recon.Analysis.AnalysisOptions { BuildXrefs = true },
        });

        Assert.Equal("macho32", document.Binary.Format);
        Assert.Equal("x86", document.Binary.Isa);
        Assert.Equal(SyntheticMacho.Base32, document.Binary.ImageBase);
        Assert.Contains(document.Functions, f => f.Name == "_add" && f.Ranges[0].Rva == SyntheticMacho.AddOffset);
        Assert.Contains(document.Functions, f => f.Name == "_main");

        // The stub is named from the indirect symbol table, and the import from the pointer it jumps
        // through — both of which are tables of 32-bit entries in this image.
        var stub = Assert.Single(document.Functions, f => f.Name == "_printf");
        Assert.Equal("__stubs", stub.Section);
        Assert.Equal(SyntheticMacho.PointerOffset, Assert.Single(document.Imports, i => i.Name == "_printf").IatRva);
    }

    // ------------------------------------------------------------------ what the file is not

    [Fact]
    public void A_file_of_no_known_format_says_so()
    {
        var result = ImageLoader.LoadBytes([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], "mystery.bin");

        Assert.Null(result.Image);
        Assert.Contains("cannot tell what", string.Join("; ", result.Problems));
    }

    /// <summary>A file that ends after its header is data, not an exception: the loader returns what
    /// it could read and says what it could not.</summary>
    [Fact]
    public void A_truncated_macho_file_says_what_it_could_not_read()
    {
        var result = Load(new SyntheticMachoOptions { Truncated = true });

        Assert.False(result.Ok, string.Join("; ", result.Problems));
        Assert.NotEmpty(result.Problems);
    }
}
