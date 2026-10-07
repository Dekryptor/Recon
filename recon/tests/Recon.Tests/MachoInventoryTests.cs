using Recon.Compare;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Inventory;
using Recon.Macho;
using Recon.Pe;
using Recon.Toolchains;
using Recon.Project;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M6 — the analysis over Mach-O. The corpus these run against is cross-compiled with clang and
/// linked with ld64.lld by <c>tools/build-macho-corpus.sh</c>; without those the script builds
/// nothing and every test here skips, which is why each one begins by asking whether the file is
/// there. What is checked is the part that is easy to get wrong and invisible when it is: which
/// address an import is reached through, where a compiler puts a switch table, and what a stripped
/// image can still be named from.
/// </summary>
[Collection("cli")]
public class MachoInventoryTests
{
    private const string Binary = "sample-macho64";
    private const string Debug = "sample-macho64-debug";
    private const string Stripped = "sample-macho64-stripped";
    private const string Arm = "sample-macho-arm64";
    private const string Library = "libsample-macho64.dylib";

    private static bool There(string name) => TestPaths.MachoCorpusExists(name);

    private static IBinaryImage Load(string name)
    {
        var result = ImageLoader.Load(TestPaths.MachoCorpus(name));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    private static InventoryDocument Inventory(IBinaryImage image, DebugInfoResult? debug = null)
    {
        var project = new ProjectConfig
        {
            Project = new ProjectMeta { Name = "macho" },
            Target = new TargetSpec { Format = image.Format, Arch = image.ArchName },
        };

        return InventoryBuilder.Build(new InventoryInputs
        {
            Project = project,
            Image = image,
            Bytes = image.Bytes,
            Debug = debug ?? MachoSymbols.Read(image),
            Options = new Recon.Analysis.AnalysisOptions { BuildXrefs = true },
        });
    }

    // ------------------------------------------------------------------ the image

    /// <summary>
    /// A Mach-O image is described the same way a PE one is: what it is, where it loads, where it
    /// starts. The 64-bit Apple base of 0x100000000 is the whole point — read it as 32 bits and
    /// every address in the file is wrong.
    /// </summary>
    [Fact]
    public void A_macho_image_is_loaded_and_described()
    {
        if (!There(Binary))
        {
            return;
        }

        var image = Load(Binary);

        Assert.Equal("macho64", image.Format);
        Assert.Equal("x64", image.ArchName);
        Assert.Equal(0x1_0000_0000u, image.ImageBase);
        Assert.NotNull(image.Macho);
        Assert.Null(image.Pe);
        Assert.Null(image.Elf);

        var text = Assert.Single(image.Sections, s => s.Name == "__text");
        Assert.True(text.IsCode);
        Assert.Contains("code", text.FlagNames);
    }

    /// <summary>
    /// <c>__PAGEZERO</c> is a 4 GB hole with no protection and no bytes, kept so a null pointer
    /// cannot be read. Taking it as a segment puts the image base at 0 and every RVA off by that hole.
    /// </summary>
    [Fact]
    public void The_page_zero_segment_is_not_part_of_the_image()
    {
        if (!There(Binary))
        {
            return;
        }

        var image = Load(Binary);

        Assert.DoesNotContain(image.Sections, s => s.Name == "__PAGEZERO");
        Assert.All(image.Sections, section => Assert.NotEqual(0u, section.Rva));
        Assert.Equal(0x5F0u, Assert.Single(image.Sections, s => s.Name == "__text").Rva);
    }

    /// <summary>
    /// A stub is a stub because the file says the section holds stubs, not because something calls
    /// it: a compiler is free to tail-jump to an import. The section type, exposed as a flag, is
    /// what the analysis goes on.
    /// </summary>
    [Fact]
    public void The_stub_section_says_that_it_holds_stubs()
    {
        if (!There(Binary))
        {
            return;
        }

        var image = Load(Binary);

        var stubs = Assert.Single(image.Sections, s => s.Name == "__stubs");
        Assert.Contains("symbol_stubs", stubs.FlagNames);
    }

    // ------------------------------------------------------------------ imports

    /// <summary>
    /// An import has no IAT in a Mach-O image: calls land on a stub, and the stub jumps through a
    /// pointer in <c>__la_symbol_ptr</c> or <c>__got</c>. The indirect symbol table is what says
    /// which pointer belongs to which import, and getting its offset wrong is how every import ends
    /// up with no slot at all — which then costs the comparison every reference to one.
    /// </summary>
    [Fact]
    public void An_import_slot_is_the_pointer_the_stub_jumps_through()
    {
        if (!There(Binary))
        {
            return;
        }

        var image = Load(Binary);

        var printf = Assert.Single(image.Imports, i => i.Name == "_printf");
        Assert.NotEqual(0u, printf.SlotRva);

        var lazy = Assert.Single(image.Sections, s => s.Name == "__la_symbol_ptr");
        Assert.True(
            printf.SlotRva >= lazy.Rva && printf.SlotRva < lazy.Rva + lazy.VirtualSize,
            $"_printf's slot 0x{printf.SlotRva:X} is not inside __la_symbol_ptr (0x{lazy.Rva:X})");
    }

    /// <summary>A stub that jumps through an import's slot is a thunk, and is named after the import.</summary>
    [Fact]
    public void A_stub_is_named_after_the_import_it_forwards_to()
    {
        if (!There(Binary))
        {
            return;
        }

        var image = Load(Binary);
        var document = Inventory(image);

        var thunk = Assert.Single(document.Functions, f => f.ImportThunk is not null && f.ImportThunk.EndsWith("_printf", StringComparison.Ordinal));
        Assert.Equal("__stubs", thunk.Section);
        Assert.Contains("import_thunk", thunk.FoundBy);
    }

    // ------------------------------------------------------------------ the switch

    /// <summary>
    /// Where a switch table lives is the toolchain's choice. clang on x86-64 Mach-O keeps it in
    /// <c>__text</c>, between the arms of the switch — inside the function, which is what a PE or
    /// ELF build puts in <c>.rdata</c> outside it. The table is still found, and its targets are
    /// still inside the function that reads them.
    /// </summary>
    [Fact]
    public void A_switch_table_sits_inside_the_function_that_reads_it()
    {
        if (!There(Binary))
        {
            return;
        }

        var document = Inventory(Load(Binary));

        var table = Assert.Single(document.JumpTables);
        Assert.Equal(11, table.Entries);
        Assert.Equal("relative", table.Kind);

        var dispatch = Assert.Single(document.Functions, f => f.Name == "_dispatch");
        Assert.Equal(dispatch.Id, table.Owner);

        uint start = dispatch.Ranges[0].Rva;
        uint end = start + Math.Max(dispatch.Ranges[0].Size, 1);
        Assert.All(table.Targets, target => Assert.True(target >= start && target < end, $"target 0x{target:X} is outside _dispatch"));
    }

    /// <summary>The table's bytes are data, not instructions: the inventory says so and decodes none of them.</summary>
    [Fact]
    public void The_bytes_of_a_switch_table_are_data_and_not_code()
    {
        if (!There(Binary))
        {
            return;
        }

        var document = Inventory(Load(Binary));
        var table = Assert.Single(document.JumpTables);

        var entry = Assert.Single(document.Data, d => d.Rva == table.Rva);
        Assert.Equal("jump_table", entry.Kind);
    }

    // ------------------------------------------------------------------ a stripped image

    /// <summary>
    /// A stripped Mach-O image still has a symbol table of sorts — the linker's own synthesized
    /// symbol stays in it — and one name in it is no reason to ignore the map file beside the
    /// binary, which names every function. This is the case where the tool either names thirteen
    /// functions or none of them.
    /// </summary>
    [Fact]
    public void A_stripped_image_is_named_by_the_map_file_beside_it()
    {
        if (!There(Stripped))
        {
            return;
        }

        using var workspace = new TempDir("recon-macho-map");
        var image = Load(Stripped);
        string binaryPath = TestPaths.MachoCorpus(Stripped);

        // The map is a side file, so it has to sit next to the binary the way a build leaves it.
        workspace.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "macho"

            [target]
            format = "{image.Format}"
            arch = "{image.ArchName}"

            [[input]]
            id = "main"
            role = "original"
            file = "{Stripped}"
            sha256 = "{PeImage.HashFile(binaryPath)}"

            [paths]
            profiles = ["{TestPaths.RepositoryRoot}/src/Recon.Core/Toolchains/profiles"]
            """);
        workspace.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.MachoCorpusDirectory}"
            """);

        var context = ProjectContext.Load(workspace.PathOf("project.toml"), new Diagnostics());
        var debug = context.LoadDebugInfo(image);

        Assert.NotNull(debug);
        Assert.Equal("map", debug.Kind);
        Assert.Contains(debug.Symbols, s => s.Name == "_main" && s.Rva == 0x850);
        Assert.Contains(debug.Symbols, s => s.Name == "_add" && s.Rva == 0x5F0);

        var document = Inventory(image, debug);
        Assert.Contains(document.Functions, f => f.Name == "_main");
        Assert.Contains(document.Functions, f => f.Name == "_fib");
    }

    /// <summary>
    /// ld64's map is not GNU ld's: the address, size and name are in one tab-separated line, and the
    /// object-file index that ld64 prints before the name is separated from it by a space.
    /// </summary>
    [Fact]
    public void The_ld64_map_format_is_read()
    {
        if (!There(Binary))
        {
            return;
        }

        var image = Load(Binary);
        var result = MapFileReader.Read(TestPaths.MachoCorpus(Binary + ".map"), image);

        Assert.Equal("map", result.Kind);
        Assert.Empty(result.Problems);

        var main = Assert.Single(result.Symbols, s => s.Name == "_main");
        Assert.Equal(0x850u, main.Rva);
        Assert.False(main.IsData);

        // The size is printed, and is the linker's padded size rather than the symbol's own: taking
        // it would make every function look bigger than it is and the next one overlap it.
        Assert.Null(main.Size);
    }

    // ------------------------------------------------------------------ the symbol table

    /// <summary>
    /// An nlist carries no size, only an address. Saying so is better than guessing: a function with
    /// no size is a function whose end the analysis still has to find.
    /// </summary>
    [Fact]
    public void The_symbol_table_names_functions_and_data_and_claims_no_size_for_them()
    {
        if (!There(Library))
        {
            return;
        }

        var image = Load(Library);
        var debug = MachoSymbols.Read(image)?.WithSources();

        Assert.NotNull(debug);
        Assert.Equal("macho", debug.Kind);
        Assert.Contains("macho", debug.Sources);

        var add = Assert.Single(debug.Symbols, s => s.Name == "_add");
        Assert.Null(add.Size);
        Assert.False(add.IsData);
        Assert.Equal(SymbolSource.Macho, add.Source);

        // A dylib exports what it defines, which is how the library's own functions are named.
        Assert.Contains(debug.Symbols, s => s.Name == "_fib");
    }

    // ------------------------------------------------------------------ another machine

    /// <summary>
    /// An arm64 image is decoded, and the thing that makes that safe is that it does not have to be
    /// decoded well: every AArch64 instruction is four bytes, so a walk can tell where the next one
    /// starts without knowing what this one does. The functions are named from the map, and the code
    /// that fills them is read as ARM rather than refused.
    /// </summary>
    [Fact]
    public void An_arm64_image_is_loaded_named_and_decoded()
    {
        if (!There(Arm))
        {
            return;
        }

        var image = Load(Arm);
        Assert.Equal("arm64", image.ArchName);

        var document = Inventory(image, MapFileReader.Read(TestPaths.MachoCorpus(Arm + ".map"), image));

        // One instruction per four bytes of code, and no apology in the problems for reading them.
        int codeBytes = image.Sections.Where(s => s.IsCode).Sum(s => (int)(s.RawSize - (s.RawSize % 4)));
        Assert.Equal(codeBytes / 4, document.Statistics["instructions"]);
        Assert.DoesNotContain(document.Problems, problem => problem.Contains("not arm64", StringComparison.Ordinal));

        Assert.Contains(document.Functions, f => f.Name == "_main");
    }

    // ------------------------------------------------------------------ a library

    /// <summary>
    /// A dylib is a library: it has no entry point, and what it offers is its exports. The exports
    /// carry the leading underscore a Mach-O C symbol has, which a comparison has to see past.
    /// </summary>
    [Fact]
    public void A_dylib_is_a_library_with_exports()
    {
        if (!There(Library))
        {
            return;
        }

        var image = Load(Library);
        var document = Inventory(image);

        Assert.True(document.Binary.Dll);
        Assert.Equal(0u, document.Binary.EntryRva);
        Assert.Contains(document.Exports, e => e.Name == "_add");
        Assert.Contains(document.Exports, e => e.Name == "_fib");
    }

    // ------------------------------------------------------------------ the comparison

    /// <summary>
    /// Two Mach-O builds compare by name: an optimized one against an unoptimized one. The names
    /// have to match across the two, the switch has to be matched even though its table sits inside
    /// it, and the stub — six bytes that jump through a pointer — has to compare identical to the
    /// same stub in the other build rather than being left over on both sides.
    /// </summary>
    [Fact]
    public void Two_macho_builds_are_compared_by_name()
    {
        if (!There(Binary) || !There(Debug))
        {
            return;
        }

        var leftImage = Load(Binary);
        var rightImage = Load(Debug);
        var left = Inventory(leftImage, MapFileReader.Read(TestPaths.MachoCorpus(Binary + ".map"), leftImage));
        var right = Inventory(rightImage, MapFileReader.Read(TestPaths.MachoCorpus(Debug + ".map"), rightImage));

        var document = ComparisonBuilder.Build(
            Side(leftImage, TestPaths.MachoCorpus(Binary), "left", left),
            Side(rightImage, TestPaths.MachoCorpus(Debug), "right", right),
            new ComparisonOptions());

        Assert.Contains(document.Functions, pair => pair.Left?.Name == "_dispatch" && pair.Right?.Name == "_dispatch");
        Assert.Contains(document.Functions, pair => pair.Left?.Name == "_fib" && pair.Right?.Name == "_fib");

        // A stub is the same six bytes in both builds: nothing about it can differ but its address,
        // and the table between the arms of _dispatch is not code on either side.
        var stub = Assert.Single(document.Functions, pair => pair.Left?.Name == "_printf");
        Assert.Equal("_printf", stub.Right?.Name);
        Assert.Equal(1.0, stub.Score);
        Assert.Empty(stub.Differences);
    }

    // ------------------------------------------------------------------ the DWARF, which is outside

    /// <summary>
    /// A Mach-O build keeps its DWARF out of the image: clang leaves a debug map in the executable
    /// and <c>dsymutil</c> collects the real thing into a <c>.dSYM</c> bundle beside it. Reading the
    /// bundle is what turns twelve named functions into twelve functions with a source file and a
    /// line number each.
    /// </summary>
    [Fact]
    public void The_dwarf_of_a_macho_build_lives_in_the_bundle_beside_it()
    {
        if (!There(Binary) || !Directory.Exists(TestPaths.MachoCorpus(Binary + ".dSYM")))
        {
            return;
        }

        using var workspace = new TempDir("recon-macho-dsym");
        var context = Project(workspace, Binary, "x64");
        var image = context.LoadImage().Image;

        Assert.NotNull(image);
        Assert.True(context.Dwarf is { Functions.Count: > 0 }, "the .dSYM bundle was not read");

        var debug = context.LoadDebugInfo(image);
        Assert.NotNull(debug);
        Assert.Equal("dwarf", debug.Kind);
        Assert.NotEmpty(debug.Compilands);
        Assert.Contains(debug.Symbols, symbol => symbol.Name == "main" && !symbol.IsData);
    }

    /// <summary>
    /// A map file is names and addresses; debug information is names, addresses, sizes, lines and the
    /// translation unit each function came from. The map beside this binary names more entries than
    /// the DWARF does — the two stubs, which the image names anyway — and that must not be enough to
    /// trade the DWARF away.
    /// </summary>
    [Fact]
    public void A_map_file_beside_the_binary_does_not_displace_its_debug_information()
    {
        if (!There(Binary) || !There(Binary + ".map") || !Directory.Exists(TestPaths.MachoCorpus(Binary + ".dSYM")))
        {
            return;
        }

        using var workspace = new TempDir("recon-macho-map");
        var context = Project(workspace, Binary, "x64");
        var image = context.LoadImage().Image!;
        var debug = context.LoadDebugInfo(image);

        Assert.NotNull(debug);
        Assert.Equal("dwarf", debug.Kind);
        Assert.NotEmpty(debug.Compilands);

        // The stubs the map knows about are still named: they come from the image's own tables.
        Assert.Contains(debug.Symbols, symbol => symbol.Name == "_printf");
    }

    /// <summary>
    /// A <c>__stubs</c> entry has no <c>nlist</c> of its own, so the indirect symbol table is the
    /// only thing in the file that names it — the three instructions in a stub are a jump to an
    /// address the loader fills in, and nothing about them says whose address it is.
    /// </summary>
    [Fact]
    public void A_stub_is_named_from_the_indirect_symbol_table_and_not_from_decoding_it()
    {
        if (!There(Arm))
        {
            return;
        }

        var image = Load(Arm);
        var document = Inventory(image, MachoSymbols.Read(image));

        var stub = Assert.Single(document.Functions, f => f.Name == "_printf");
        Assert.Equal("__stubs", stub.Section);
        Assert.Equal(12u, stub.Ranges[0].Size);
    }

    /// <summary>
    /// The profile that says which compiler this is. A Mach-O image has no <c>.comment</c> section
    /// for it to be read out of, so the sections themselves are the evidence.
    /// </summary>
    [Fact]
    public void A_macho_build_is_recognised_as_clang_19()
    {
        if (!There(Binary))
        {
            return;
        }

        using var workspace = new TempDir("recon-macho-profiles");
        string directory = Path.Combine(workspace.Path, "toolchains");
        Directory.CreateDirectory(directory);
        Recon.Toolchains.BuiltInProfiles.WriteTo(directory);
        var registry = ToolchainRegistry.Load([directory], new Diagnostics());

        var chosen = ToolchainSelector.Choose(registry, null, Load(Binary));

        Assert.NotNull(chosen);
        Assert.Equal("clang-19-macho64", chosen.Id);
    }

    /// <summary>A project in <paramref name="workspace"/> that points at one corpus binary.</summary>
    private static ProjectContext Project(TempDir workspace, string name, string arch)
    {
        string binaryPath = TestPaths.MachoCorpus(name);
        workspace.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "macho"

            [target]
            format = "macho64"
            arch = "{arch}"

            [[input]]
            id = "main"
            role = "original"
            file = "{name}"
            sha256 = "{Recon.Pe.PeImage.HashFile(binaryPath)}"

            [paths]
            profiles = ["{TestPaths.RepositoryRoot}/src/Recon.Core/Toolchains/profiles"]
            """);
        workspace.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.MachoCorpusDirectory}"
            """);

        return ProjectContext.Load(workspace.PathOf("project.toml"), new Diagnostics());
    }

    private static ComparisonSide Side(IBinaryImage image, string path, string label, InventoryDocument inventory)
        => new()
        {
            Label = label,
            Image = image,
            Bytes = image.Bytes,
            Inventory = inventory,
            Index = new SideIndex(image, inventory),
            BinaryPath = path,
        };
}
