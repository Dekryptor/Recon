using System.Text;
using System.Text.Json;
using Recon.Delink;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;
using Recon.Schema;
using Recon.Schemas;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M5 — delink and relink. The synthetic fixture drives the planner, the writers and the header
/// finisher directly, where a rule can be checked against the exact bytes that rule is about; one
/// test at the end runs the whole thing over the corpus with a real cross compiler, and is skipped
/// when there is none.
/// </summary>
/// <remarks>
/// In the "cli" collection because these tests drive the CLI in-process: <see cref="CliRun"/>
/// swaps <see cref="Console.Out"/>, which is one stream for the whole test process, so this
/// class must not run beside another that captures it.
/// </remarks>
[Collection("cli")]
public class DelinkTests
{
    private const string MingwCc = "i686-w64-mingw32-gcc";

    // ------------------------------------------------------------------ the plan tiles the image

    [Fact]
    public void Every_section_is_tiled_with_no_gap_and_no_overlap()
    {
        var plan = Plan();

        foreach (var section in plan.Sections)
        {
            var pieces = plan.Pieces
                .Where(p => p.Section == section.Name)
                .OrderBy(p => p.Rva)
                .ToList();

            uint extent = section.RawSize == 0 || section.VirtualSize == 0
                ? Math.Max(section.VirtualSize, section.RawSize)
                : Math.Min(section.VirtualSize, section.RawSize);

            Assert.NotEmpty(pieces);
            Assert.Equal(section.Rva, pieces[0].Rva);
            for (int i = 1; i < pieces.Count; i++)
            {
                // The next piece starts where the last one ended: not before (overlap) or after (gap).
                Assert.Equal(pieces[i - 1].Rva + pieces[i - 1].Size, pieces[i].Rva);
            }

            Assert.Equal(section.Rva + extent, pieces[^1].Rva + pieces[^1].Size);
        }
    }

    /// <summary>
    /// The padding a file rounds a section up with is not part of the image: tiling it would make the
    /// relinked section bigger than the original's, which is a difference that is not in the bytes.
    /// </summary>
    [Fact]
    public void A_section_is_tiled_to_its_virtual_size_not_to_its_size_on_disk()
    {
        var plan = Plan();
        var text = plan.Sections.First(s => s.Name == ".text");

        Assert.Equal(0x300u, text.VirtualSize);
        Assert.Equal(0x400u, text.RawSize);
        Assert.Equal(0x300u, (uint)plan.Pieces.Where(p => p.Section == ".text").Sum(p => (long)p.Size));
    }

    /// <summary>
    /// The relocation table is written back. A linker will happily build one of its own, but its own
    /// describes the relocations it made, not the ones the original's image had.
    /// </summary>
    [Fact]
    public void The_relocation_table_is_planned_rather_than_left_to_the_linker()
    {
        var plan = Plan();
        var reloc = plan.Sections.First(s => s.Name == ".reloc");

        Assert.True(reloc.Emitted);
        Assert.NotNull(reloc.Source);
        Assert.Contains(plan.Pieces, p => p.Section == ".reloc");
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// A symbol table says where a variable is and not how big it is, so it cannot claim bytes — but
    /// it is still a name, and a name the relinked image lacks is a difference that is not there.
    /// </summary>
    [Fact]
    public void A_name_without_a_size_becomes_a_label_at_its_own_address()
    {
        var plan = Plan();
        var labels = plan.Pieces.SelectMany(p => p.Labels).ToList();

        Assert.NotEmpty(labels);
        Assert.Contains(labels, l => l.Name == "g_value" && l.Rva == 0x3000);
        Assert.Contains(labels, l => l.Name == "data_a" && l.Rva == 0x2000);

        // A label claims nothing, and it sits in the piece that covers its address.
        foreach (var piece in plan.Pieces)
        {
            foreach (var label in piece.Labels)
            {
                Assert.True(piece.Size == 0 || (label.Rva >= piece.Rva && label.Rva <= piece.Rva + piece.Size),
                    $"label {label.Name} at 0x{label.Rva:X} is not inside {piece.Id}");
            }
        }
    }

    /// <summary>
    /// One name at two addresses is what a symbol table can hold and a relinked image cannot: the
    /// second keeps the address in its name rather than losing it.
    /// </summary>
    [Fact]
    public void A_name_used_at_two_addresses_is_emitted_twice_with_only_one_of_them_plain()
    {
        var (plan, _) = PlanWith(inventory =>
        {
            inventory.Data.Add(new DataInfo { Rva = 0x2000, Size = 0, Name = "twice", Source = "coff" });
            inventory.Data.Add(new DataInfo { Rva = 0x2100, Size = 0, Name = "twice", Source = "coff" });
        });

        var labels = plan.Pieces.SelectMany(p => p.Labels).Where(l => l.Name.StartsWith("twice")).ToList();

        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, l => l.Name == "twice");
        Assert.Contains(labels, l => l.Name == "twice_at_2100");
        Assert.Contains(plan.Problems, p => p.Contains("\"twice\" is at more than one address"));
    }

    // ------------------------------------------------------------------ what the assembler is told

    /// <summary>
    /// A section's flags are the original's: .text is code, .bss takes no contents, .rdata is read
    /// only. Without them the assembler makes every section writable data.
    /// </summary>
    [Fact]
    public void Section_flags_come_from_the_originals_section_characteristics()
    {
        var plan = Plan();

        Assert.Equal("xr", plan.Sections.First(s => s.Name == ".text").Flags);
        Assert.Equal("r", plan.Sections.First(s => s.Name == ".rdata").Flags);
        Assert.Equal("w", plan.Sections.First(s => s.Name == ".data").Flags);

        // The fixture has no .bss, so the last rule is tested on bytes that describe one: .data with
        // no contents in the file, which is what "uninitialized" means.
        var withBss = Plan(UninitializedData());
        Assert.Equal("b", withBss.Sections.First(s => s.Name == ".data").Flags);
    }

    /// <summary>
    /// A debug section keeps its own name, because the assembler gives a section it recognises a
    /// one-byte alignment — and padding a debug section to four bytes changes the image's sizes.
    /// The exception is a name the assembler writes itself, which is prefixed so the two are never
    /// the same section.
    /// </summary>
    [Fact]
    public void Sections_keep_their_names_except_the_ones_an_assembler_writes_itself()
    {
        Assert.Equal(".debug_info", AsmWriter.InputSectionName(".debug_info"));
        Assert.Equal(".text", AsmWriter.InputSectionName(".text"));
        Assert.Equal(".recon.debug_line", AsmWriter.InputSectionName(".debug_line"));
        Assert.Equal(".recon.comment", AsmWriter.InputSectionName(".comment"));
    }

    /// <summary>The assembler's own sections are thrown away, not left for the linker to place.</summary>
    [Fact]
    public void The_linker_script_discards_the_sections_the_assembler_writes_itself()
    {
        var plan = Plan();
        var script = LinkerScriptWriter.Emit(plan);

        Assert.Contains("/DISCARD/", script);
        Assert.Contains("*(.debug_line)", script);
        Assert.DoesNotContain("*(.debug_info)", script);

        // Every emitted section is placed at the address it came from, and matched by its own name.
        foreach (var section in plan.Sections.Where(s => s.Emitted))
        {
            Assert.Contains($"0x{0x400000 + section.Rva:x}", script);
            Assert.Contains($"{{ *({section.Input}) }}", script);
        }
    }

    // ------------------------------------------------------------------ the assembly itself

    [Fact]
    public void The_assembly_places_every_piece_and_names_every_address_the_image_points_at()
    {
        var (plan, image) = PlanWith();
        var files = AsmWriter.Emit(image.Image!, image.Bytes, plan);

        Assert.NotEmpty(files);
        foreach (var (name, text) in files)
        {
            Assert.EndsWith(".s", name);
            Assert.Contains(".section ", text);
        }

        string code = files.First(f => f.FileName == "text.s").Text;

        // A piece at the address it belongs at, a global typed symbol for a function, and a reference
        // written as a name so the linker computes the address.
        Assert.Contains(".org 0x20", code);
        Assert.Contains(".globl func_a", code);
        Assert.Contains(".def func_a; .scl 2; .type 32; .endef", code);
        Assert.Contains(".long ", code);
    }

    /// <summary>A section with no bytes on disk is emitted as zeroes, not read from the next one.</summary>
    [Fact]
    public void Bytes_that_are_not_in_the_file_are_emitted_as_space()
    {
        var (plan, image) = PlanWith(bytes: UninitializedData());
        var files = AsmWriter.Emit(image.Image!, image.Bytes, plan);
        string data = files.First(f => f.FileName == "data.s").Text;

        Assert.Contains(".section .data, \"b\"", data);
        Assert.Contains(".space ", data);
        Assert.DoesNotContain(".byte ", data);
    }

    [Fact]
    public void The_plan_matches_the_schema_it_is_published_against()
    {
        var plan = Plan();
        string json = JsonSerializer.Serialize(plan);
        var validator = JsonSchemaValidator.Parse(BuiltInSchemas.Get("delink")!);

        Assert.Empty(validator.Validate(json));
    }

    // ------------------------------------------------------------------ finishing the header

    /// <summary>
    /// The linker cannot know where the original's import address table or TLS directory are, because
    /// it did not build them. Their header entries — and the build's timestamp — come from the
    /// original, which is sound because the bytes they point at are already in the image.
    /// </summary>
    [Fact]
    public void Header_entries_the_linker_cannot_know_are_copied_from_the_original()
    {
        var image = Load();
        byte[] bytes = SyntheticPe.Build();

        // Pretend the linker wrote the image: a fresh timestamp and no import address table.
        using var temp = new TempDir("recon-header");
        string path = temp.Write("image.exe", bytes);
        Patch(path, CoffOffset(bytes) + 4, 0x12345678u);
        int imports = DirectoryOffset(bytes, 1);
        Patch(path, imports, 0u);
        Patch(path, imports + 4, 0u);

        var changes = HeaderCompletion.Complete(path, image.Image!);

        Assert.Contains(changes, c => c.Contains("timestamp"));
        Assert.Contains(changes, c => c.Contains("import directory -> 0x2064+0x14"));

        byte[] written = File.ReadAllBytes(path);
        Assert.Equal(image.Image!.TimeDateStamp, ReadU32(written, CoffOffset(written) + 4));
        Assert.Equal(0x2064u, ReadU32(written, imports));
    }

    /// <summary>What the linker wrote for itself is left alone: only what is missing is copied.</summary>
    [Fact]
    public void A_header_entry_the_linker_filled_is_not_overwritten()
    {
        var image = Load();
        using var temp = new TempDir("recon-header-kept");
        string path = temp.Write("image.exe", SyntheticPe.Build());

        var changes = HeaderCompletion.Complete(path, image.Image!);

        Assert.DoesNotContain(changes, c => c.Contains("timestamp"));
        Assert.DoesNotContain(changes, c => c.Contains("directory"));
        Assert.Contains("nothing to copy", HeaderCompletion.Describe(changes));
    }

    // ------------------------------------------------------------------ the verdict

    [Fact]
    public void A_relinked_image_that_is_the_original_reports_so()
    {
        var image = Load();
        byte[] bytes = SyntheticPe.Build();
        using var temp = new TempDir("recon-verdict");
        string path = temp.Write("image.exe", bytes);

        var comparison = RelinkVerifier.Compare(image.Image!, bytes, path);

        Assert.True(comparison.Identical, string.Join("; ", comparison.Differences));
        Assert.Empty(comparison.Differences);
        Assert.Equal(image.Image!.Sections.Count, comparison.SectionsCompared);
        Assert.Contains("identical", comparison.Verdict);
    }

    [Fact]
    public void A_relinked_image_that_differs_says_where()
    {
        var image = Load();
        byte[] bytes = SyntheticPe.Build();
        using var temp = new TempDir("recon-verdict-differs");
        string path = temp.Write("image.exe", bytes);

        byte[] damaged = (byte[])bytes.Clone();
        var code = image.Image!.Sections.First(s => s.Name == ".text");
        damaged[code.RawOffset + 0x30] ^= 0xff;
        File.WriteAllBytes(path, damaged);

        var comparison = RelinkVerifier.Compare(image.Image!, bytes, path);

        Assert.False(comparison.Identical);
        Assert.Equal(1, comparison.BytesDiffering);
        Assert.Contains(comparison.Differences, d => d.Contains("section .text differs in 1 of"));
    }

    // ------------------------------------------------------------------ the whole thing, with a linker

    /// <summary>
    /// The end of M5: the corpus binary is cut into pieces and put back together, and the relinked
    /// image is the original — every section, every byte, and the header entries the image is found
    /// by. Skipped when no cross compiler is installed.
    /// </summary>
    [Fact]
    public void A_corpus_binary_is_delinked_and_relinked_into_the_same_image()
    {
        if (!ToolDetection.Exists(MingwCc) || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var project = new RelinkProject();
        project.CopyInput(TestPaths.Corpus("sample-release.exe"));

        var delink = CliRun.Run("delink", "--project", project.DirectoryPath, "--check-schema");
        Assert.Equal(0, delink.ExitCode);
        Assert.Contains("delink plan matches schema", delink.StandardOutput);
        Assert.True(File.Exists(project.PathOf("build/delink/link.ld")));
        Assert.True(File.Exists(project.PathOf("build/delink/delink.json")));

        var link = CliRun.Run("link", "--project", project.DirectoryPath);
        Assert.Equal(0, link.ExitCode);
        Assert.Contains("relinked identical", link.StandardOutput);
        Assert.True(File.Exists(project.PathOf("build/relinked.exe")));

        // And the tool's own comparison agrees: nothing in the relinked image differs from the
        // original, which is the claim the whole milestone is about.
        var diff = CliRun.Run(
            "diff",
            "--project", project.DirectoryPath,
            project.PathOf("build/relinked.exe"),
            "--summary");
        Assert.Equal(0, diff.ExitCode);
        Assert.Contains("score        1", diff.StandardOutput);
        Assert.Contains("0 only left, 0 only right", diff.StandardOutput);
    }

    // ------------------------------------------------------------------ a unit's own build takes a piece's place

    /// <summary>
    /// The end of M7's rebuild loop: a piece a unit claims is filled from that unit's compiled object
    /// rather than from the original's bytes, and every byte outside the piece is still the
    /// original's. The unit is the original's own source for the function, so the rebuild is the same
    /// code — and the test makes the difference visible by rebuilding it as something else, then
    /// asking the object what it holds and finding exactly those bytes at exactly that address.
    /// </summary>
    [Fact]
    public void A_piece_is_rebuilt_from_its_units_object_and_the_rest_of_the_image_is_the_original()
    {
        if (!ToolDetection.Exists(MingwCc) || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var project = new RelinkProject();
        project.CopyInput(TestPaths.Corpus("sample-release.exe"));
        project.AddUnit("arith", "arith.c", Subtract, "symbol = \"add\"");
        project.RecordInputHash();

        var build = CliRun.Run("build", "--project", project.DirectoryPath, "--unit", "arith");
        Assert.Equal(0, build.ExitCode);

        var delink = CliRun.Run("delink", "--project", project.DirectoryPath);
        Assert.Equal(0, delink.ExitCode);

        // One cover, one function. A cover is resolved against every name the image has, so a symbol
        // that is a substring of others claims nothing else — `add` claims `add`.
        Assert.Contains("providers   295 original, 1 rebuilt", delink.StandardOutput);

        var link = CliRun.Run("link", "--project", project.DirectoryPath);
        Assert.Equal(0, link.ExitCode);
        Assert.Contains("rebuilt     1 of 1 claimed piece(s) from 1 unit(s)", link.StandardOutput);

        // The original's `add`, found in the original's own code: that is where the piece is, and the
        // find is exact — the corpus's function appears once in its .text.
        byte[] original = File.ReadAllBytes(TestPaths.Corpus("sample-release.exe"));
        byte[] relinked = File.ReadAllBytes(project.PathOf("build/relinked.exe"));
        var was = Code(original);
        var is_ = Code(relinked);
        Assert.Equal(was.Length, is_.Length);

        int at = IndexOf(was, OriginalAdd);
        Assert.True(at > 0, "the original's add is not in the corpus image's .text");

        // What is at the piece's address is the object's own bytes for the function, read out of the
        // object — not the original's, and not anything this test wrote down.
        var object_ = Recon.Archive.CoffObject.Load(
            File.ReadAllBytes(project.PathOf("build/obj/arith.o")), "arith.o");
        var symbol = object_.Function("add");
        Assert.NotNull(symbol);
        var section = object_.Section(symbol!.Section);
        Assert.NotNull(section);
        Assert.Equal(
            section!.Bytes[(int)symbol.Value..((int)symbol.Value + OriginalAdd.Length)],
            is_[at..(at + OriginalAdd.Length)]);

        // And nothing else moved: the rest of the code is the original's byte for byte...
        Assert.Empty(Differing(was, is_, at, OriginalAdd.Length));

        // ...and so is every other section, which the tool's own comparison says in the terms the
        // milestone is about: one section differs, in nine bytes, and the file was not rebuilt.
        Assert.Equal(Sections(original), Sections(relinked));
    }

    /// <summary>The .text section's bytes, as the file holds them.</summary>
    private static byte[] Code(byte[] file)
    {
        var image = PeLoader.LoadBytes(file, "image.exe").Image;
        Assert.NotNull(image);
        var section = image!.Sections.First(s => s.Name == ".text");
        return file[(int)section.RawOffset..(int)(section.RawOffset + section.RawSize)];
    }

    /// <summary>
    /// Every section of the file but .text, by name and content: what a relink has to leave alone.
    /// </summary>
    private static List<string> Sections(byte[] file)
    {
        var image = PeLoader.LoadBytes(file, "image.exe").Image;
        Assert.NotNull(image);
        var sections = new List<string>();
        foreach (var section in image!.Sections.Where(s => s.Name != ".text").OrderBy(s => s.Name))
        {
            byte[] bytes = section.RawSize == 0
                ? []
                : file[(int)section.RawOffset..(int)(section.RawOffset + section.RawSize)];
            sections.Add($"{section.Name} {Convert.ToHexString(bytes)}");
        }

        return sections;
    }

    /// <summary>
    /// A unit that is the image's own source, rebuilt and relinked: every function it claims is taken
    /// from its object — the calls and the data references included — and the result is the original
    /// image, byte for byte. This is the loop M7 is for: the source is recovered, the compiler is run
    /// on it, and the image that comes out is the one that went in.
    /// </summary>
    [Fact]
    public void The_corpus_source_as_one_unit_is_rebuilt_and_relinked_into_the_original_image()
    {
        if (!ToolDetection.Exists(MingwCc) || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var project = new RelinkProject();
        project.CopyInput(TestPaths.Corpus("sample-release.exe"));
        project.AddUnit(
            "sample",
            "sample.c",
            File.ReadAllText(TestPaths.CorpusSource("sample.c")),
            new[]
            {
                "add", "mul_std", "sub_fast", "dispatch", "fib", "bump", "loop_sum", "fail",
                "twin_a", "twin_b", "use_twins", "main", "printf.constprop.0", "fprintf.constprop.0",
            }.Select(name => $"symbol = \"{name}\"").ToArray());
        project.RecordInputHash();

        var build = CliRun.Run("build", "--project", project.DirectoryPath);
        Assert.Equal(0, build.ExitCode);

        var delink = CliRun.Run("delink", "--project", project.DirectoryPath);
        Assert.Equal(0, delink.ExitCode);

        // Two of the claimed names are in the runtime, not in this unit — `mainCRTStartup` and
        // `__tmainCRTStartup` contain "main" — and a cover that claims a function the unit does not
        // define is reported, not guessed at.
        Assert.Contains("13 rebuilt", delink.StandardOutput);

        var link = CliRun.Run("link", "--project", project.DirectoryPath, "--check-schema");
        Assert.Equal(0, link.ExitCode);
        Assert.Contains("rebuilt     13 of 13 claimed piece(s) from 1 unit(s)", link.StandardOutput);
        Assert.Contains("relinked identical", link.StandardOutput);

        // The references are the point of this test: a rebuilt function calls the image's functions
        // and reads the image's variables, so the object's unfinished addresses have to be written
        // out as names the linker can finish.
        using var document = JsonDocument.Parse(File.ReadAllText(project.PathOf("build/delink/delink.json")));
        var pieces = document.RootElement.GetProperty("pieces").EnumerateArray()
            .Where(p => p.TryGetProperty("provider", out var provider) && provider.GetString() == "rebuilt")
            .ToList();
        Assert.Equal(13, pieces.Count);
    }

    /// <summary>
    /// A rebuild that refers to something the object placed somewhere the image does not is refused,
    /// and the piece keeps the original's bytes.
    ///
    /// The reference here is a call between two functions of the same object, which the assembler
    /// resolves: the four bytes are a finished distance with no relocation naming it, and nothing in
    /// the object says what it points at. This unit lays the functions out differently from the image
    /// — `bump` first here, with two functions between it and `loop_sum` there — so that distance is
    /// wrong by the difference, and placing it would produce an image that looks reconstructed and
    /// calls the wrong address. The leaf function with no references in the same unit is still placed,
    /// because the layout says nothing about bytes that address nothing.
    /// </summary>
    [Fact]
    public void A_rebuild_that_refers_to_a_moved_function_is_refused_and_the_original_is_kept()
    {
        if (!ToolDetection.Exists(MingwCc) || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var project = new RelinkProject();
        project.CopyInput(TestPaths.Corpus("sample-release.exe"));
        project.AddUnit("arith", "arith.c", AddBumpLoopSum, "rva = 0x1570\nsize = 9", "rva = 0x1730\nsize = 75");
        project.RecordInputHash();

        var build = CliRun.Run("build", "--project", project.DirectoryPath, "--unit", "arith");
        Assert.Equal(0, build.ExitCode);

        var delink = CliRun.Run("delink", "--project", project.DirectoryPath);
        Assert.Equal(0, delink.ExitCode);

        var link = CliRun.Run("link", "--project", project.DirectoryPath);
        Assert.Equal(0, link.ExitCode);

        // The leaf is placed, the caller is not.
        Assert.Contains("rebuilt     1 of 2 claimed piece(s) from 1 unit(s)", link.StandardOutput);
        Assert.Contains("the object puts _bump at +0x0 of .text", link.All);
        Assert.Contains("the relink cannot fix", link.All);
        Assert.Contains("the original's bytes are kept", link.All);

        // Which is what makes the image the original one: the piece that was refused is still the
        // original's code, so nothing changed at all.
        Assert.Contains("relinked identical", link.StandardOutput);
    }

    /// <summary>
    /// A unit whose object is not its current build is refused, by name and with the reason — the
    /// build system's own answer, not a second opinion about staleness. Linking a stale object is the
    /// one mistake that produces an image that looks reconstructed and is not.
    /// </summary>
    [Fact]
    public void A_unit_whose_object_is_not_its_current_build_is_refused()
    {
        if (!ToolDetection.Exists(MingwCc) || !TestPaths.CorpusExists("sample-release.exe"))
        {
            return;
        }

        using var project = new RelinkProject();
        project.CopyInput(TestPaths.Corpus("sample-release.exe"));
        project.AddUnit("arith", "arith.c", Add, "symbol = \"add\"");
        project.RecordInputHash();

        Assert.Equal(0, CliRun.Run("build", "--project", project.DirectoryPath, "--unit", "arith").ExitCode);
        Assert.Equal(0, CliRun.Run("delink", "--project", project.DirectoryPath).ExitCode);

        // The source changes and is not built again: the object is still there, and it is no longer
        // what this unit says.
        project.Write("src/arith.c", Subtract);

        var link = CliRun.Run("link", "--project", project.DirectoryPath);
        Assert.Equal(0, link.ExitCode);
        Assert.Contains("rebuilt     0 of 1 claimed piece(s) from 0 unit(s)", link.StandardOutput);
        Assert.Contains("unit[arith] is claimed as rebuilt but its object is not its current build", link.All);
        Assert.Contains("changed dependency", link.All);
        Assert.Contains("relinked identical", link.StandardOutput);
    }

    // The corpus's own `add`, and the same function rebuilt as something else: same size, different
    // bytes, so a rebuild that was placed can be seen and one that was not cannot be confused with it.
    private static readonly byte[] OriginalAdd = [0x8B, 0x44, 0x24, 0x08, 0x03, 0x44, 0x24, 0x04, 0xC3];

    private const string Add = """
        /* The reconstruction of one function of the original: `add` as sample.c wrote it. */
        int __attribute__((noinline)) add(int a, int b)
        {
            return a + b;
        }
        """;

    private const string Subtract = """
        /* The same function, rebuilt as something else: the same nine bytes, subtracted. */
        int __attribute__((noinline)) add(int a, int b)
        {
            return a - b;
        }
        """;

    private const string AddBumpLoopSum = """
        /* Two functions of the original, but laid out the other way round: this object has `bump`
         * first, and the image has it after two other functions. The call inside `loop_sum` is a
         * distance the assembler resolved against this object's layout. */
        extern int g_counter;
        extern int g_table[8];

        int __attribute__((noinline)) add(int a, int b)
        {
            return a + b;
        }

        static void __attribute__((noinline)) bump(void)
        {
            g_counter++;
        }

        void __attribute__((noinline)) loop_sum(int n)
        {
            int sum = 0;
            for (int i = 0; i < n; i++) {
                sum += i * g_table[i & 7];
                bump();
            }

            g_counter = sum;
        }
        """;

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int at = 0; at + needle.Length <= haystack.Length; at++)
        {
            if (haystack.AsSpan(at, needle.Length).SequenceEqual(needle))
            {
                return at;
            }
        }

        return -1;
    }

    private static List<int> Differing(byte[] left, byte[] right, int skipAt, int skipLength)
    {
        var differing = new List<int>();
        for (int at = 0; at < Math.Min(left.Length, right.Length); at++)
        {
            if (at >= skipAt && at < skipAt + skipLength)
            {
                continue;
            }

            if (left[at] != right[at])
            {
                differing.Add(at);
            }
        }

        return differing;
    }

    // ------------------------------------------------------------------ helpers

    private static DelinkDocument Plan(byte[]? bytes = null) => PlanWith(bytes: bytes).Plan;

    private static (DelinkDocument Plan, PeLoadResult Image) PlanWith(
        Action<InventoryDocument>? edit = null,
        byte[]? bytes = null)
    {
        var image = Load(bytes);
        var project = new Recon.Config.ProjectConfig
        {
            Project = new Recon.Config.ProjectMeta { Name = "fixture" },
            Target = new Recon.Config.TargetSpec { Format = "pe32", Arch = "x86" },
        };

        var inventory = InventoryBuilder.Build(new InventoryInputs
        {
            Project = project,
            Image = new PeBinaryImage(image.Image!, image.Bytes),
            Bytes = image.Bytes,
            Debug = Recon.DebugInfo.CoffSymbols.Read(image.Image!, image.Bytes),
            Options = new Recon.Analysis.AnalysisOptions { BuildXrefs = false },
        });

        edit?.Invoke(inventory);

        var plan = DelinkPlanner.Plan(image.Image!, inventory, project, "test", "delink");
        return (plan, image);
    }

    private static PeLoadResult Load(byte[]? bytes = null)
    {
        var result = PeLoader.LoadBytes(bytes ?? SyntheticPe.Build(new SyntheticPeOptions { CoffSymbols = true }), "fixture.exe");
        Assert.NotNull(result.Image);
        return result;
    }

    /// <summary>
    /// The fixture's .data as a section with no bytes in the file: the shape .bss has, which no
    /// section of the fixture is born with.
    /// </summary>
    private static byte[] UninitializedData()
    {
        byte[] bytes = SyntheticPe.Build(new SyntheticPeOptions { CoffSymbols = true });
        int entry = 0x1F8 + (2 * 40); // the section table starts at 0x1F8; .data is the third entry
        Put32(bytes, entry + 16, 0);           // SizeOfRawData
        Put32(bytes, entry + 36, 0xC0000080);  // read | write | uninitialized data
        return bytes;
    }

    private static void Put32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value & 0xff);
        bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        bytes[offset + 2] = (byte)((value >> 16) & 0xff);
        bytes[offset + 3] = (byte)((value >> 24) & 0xff);
    }

    private static int CoffOffset(byte[] bytes) => (int)ReadU32(bytes, 0x3c) + 4;

    private static int DirectoryOffset(byte[] bytes, int index)
    {
        int optional = CoffOffset(bytes) + 20;
        return optional + 96 + (index * 8);
    }

    private static void Patch(string path, int offset, uint value)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bytes[offset] = (byte)(value & 0xff);
        bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        bytes[offset + 2] = (byte)((value >> 16) & 0xff);
        bytes[offset + 3] = (byte)((value >> 24) & 0xff);
        File.WriteAllBytes(path, bytes);
    }

    private static uint ReadU32(byte[] bytes, int offset)
        => bytes[offset]
           | ((uint)bytes[offset + 1] << 8)
           | ((uint)bytes[offset + 2] << 16)
           | ((uint)bytes[offset + 3] << 24);

    /// <summary>A project over one binary, with this machine's cross compiler named in local.toml.</summary>
    private sealed class RelinkProject : IDisposable
    {
        private readonly TempDir _temp = new("recon-relink");

        private string _digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

        public RelinkProject()
        {
            string digest = _digest;

            var project = new StringBuilder();
            project.Append("schema_version = 1\n\n[project]\nname = \"sample\"\n\n");
            project.Append("[target]\nformat = \"pe32\"\narch = \"x86\"\n\n");
            project.Append("[[input]]\nid = \"main\"\nrole = \"original\"\nfile = \"sample.exe\"\n");
            project.Append($"sha256 = \"{digest}\"\n");
            Write("project.toml", project.ToString());

            var local = new StringBuilder("schema_version = 1\n\n[inputs]\ndir = \"inputs\"\n\n");
            // Both MinGW generations: the binary's own code was compiled by the newer one, and its
            // runtime objects by the older, so detection names the newer and the relink has to be
            // able to find it.
            local.Append($"[toolchain.gcc-13-mingw]\nroot = \"/usr\"\ncc = \"/usr/bin/{MingwCc}\"\nlink = \"/usr/bin/{MingwCc}\"\n\n");
            local.Append($"[toolchain.gcc-14-mingw]\nroot = \"/usr\"\ncc = \"/usr/bin/{MingwCc}\"\nlink = \"/usr/bin/{MingwCc}\"\n");
            Write("local.toml", local.ToString());
            Directory.CreateDirectory(PathOf("inputs"));
        }

        /// <summary>
        /// A unit that rebuilds part of the original from its own source, with the covers that claim
        /// what it rebuilds. Written after the project, because a project with a unit needs the
        /// toolchain named: what a filtered build is for is the object, and the relink that follows
        /// has to know which compiler wrote it.
        /// </summary>
        public void AddUnit(string name, string fileName, string source, params string[] covers)
        {
            Write("src/" + fileName, source);

            var project = new StringBuilder();
            project.Append("schema_version = 1\n\n[project]\nname = \"sample\"\n\n");
            project.Append($"[target]\nformat = \"pe32\"\narch = \"x86\"\ndefault_toolchain = \"gcc-13-mingw\"\n\n");
            project.Append("[[input]]\nid = \"main\"\nrole = \"original\"\nfile = \"sample.exe\"\n");
            project.Append($"sha256 = \"{_digest}\"\n");
            project.Append($"\n[[unit]]\nname = \"{name}\"\nsource = \"src/{fileName}\"\nstatus = \"wip\"\n");
            project.Append("flags = [\"-O2\", \"-g\", \"-Wall\", \"-frandom-seed=recon-corpus\"]\n");
            foreach (string cover in covers)
            {
                project.Append("\n[[unit.covers]]\n").Append(cover).Append('\n');
            }

            Write("project.toml", project.ToString());
        }

        /// <summary>Records the input's real hash, which the build checks and the fixture does not.</summary>
        public void RecordInputHash()
        {
            _digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(PathOf("inputs/sample.exe"))))
                .ToLowerInvariant();
            string text = File.ReadAllText(PathOf("project.toml"));
            int start = text.IndexOf("sha256 = ", StringComparison.Ordinal);
            int end = text.IndexOf('\n', start);
            Write("project.toml", text[..start] + $"sha256 = \"{_digest}\"\n" + text[(end + 1)..]);
        }

        public string DirectoryPath => _temp.Path;

        public void CopyInput(string file) => File.Copy(file, PathOf("inputs/sample.exe"), true);

        public string PathOf(string relative) => Path.Combine(_temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            string path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => _temp.Dispose();
    }
}
