using System.Diagnostics;
using Recon.Config;
using Recon.DebugInfo;
using Recon.Elf;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M6 — the analysis over ELF. The loader tests cover the file format; these cover what the tool
/// then does with one: the inventory, the debug information the symbol table becomes, the shared
/// object case, and a comparison. They run over the corpus when <c>tools/build-elf-corpus.sh</c>
/// has built it, and skip when it has not.
/// </summary>
[Collection("cli")]
public class ElfInventoryTests
{
    private static bool Corpus => TestPaths.ElfCorpusExists("sample-elf64-release");

    private static IBinaryImage Load(string name)
    {
        var result = ImageLoader.Load(TestPaths.ElfCorpus(name));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    private static InventoryDocument Inventory(IBinaryImage image, bool xrefs = true)
    {
        var project = new ProjectConfig
        {
            Project = new ProjectMeta { Name = "elf" },
            Target = new TargetSpec { Format = image.Format, Arch = image.ArchName },
        };

        return InventoryBuilder.Build(new InventoryInputs
        {
            Project = project,
            Image = image,
            Bytes = image.Bytes,
            Debug = ElfSymbols.Read(image),
            Options = new Recon.Analysis.AnalysisOptions { BuildXrefs = xrefs },
        });
    }

    // ------------------------------------------------------------------ nothing to go on but the code

    /// <summary>
    /// The blind case: the stripped binary with no map beside it, so every boundary has to come from
    /// the image. Call targets and the entry point cover the functions the program calls; what they
    /// miss is what the C runtime calls — <c>main</c>, handed to <c>__libc_start_main</c> as an
    /// argument, the initialisers the loader runs from <c>.init_array</c> and <c>.fini_array</c>, and
    /// <c>_init</c> and <c>_fini</c>, which nothing names but <c>DT_INIT</c> and <c>DT_FINI</c>.
    /// </summary>
    [Fact]
    public void A_binary_with_no_symbols_at_all_finds_the_functions_the_runtime_calls()
    {
        if (!TestPaths.ElfCorpusExists("blind/sample"))
        {
            return;
        }

        var image = Load("blind/sample");
        var document = Inventory(image);

        // Nothing names these functions. The dynamic symbol table survives stripping, but it names
        // the object the binary exports and no code at all, so there is no function to be had from it.
        Assert.Equal(0, document.Binary.Debug.Functions);

        var callbacks = document.Functions
            .Where(f => f.FoundBy.Contains("runtime_callback"))
            .ToList();

        // main, the two the initialiser and finaliser arrays point at, and the two the loader calls.
        Assert.True(callbacks.Count >= 5, $"expected the runtime's callbacks to be found, got {callbacks.Count}");
        Assert.All(callbacks, f =>
        {
            Assert.NotEqual("entry", f.Name);
            Assert.Contains("code", SectionFlags(document, f.Ranges[0].Rva));
        });

        // Everything the entry point loads before its first call is an argument it is about to pass,
        // and main is the one that matters: without it an inventory of a stripped binary is a list of
        // leaf functions with no program around them.
        var start = Assert.Single(document.Functions, f => f.Name == "entry");
        Assert.Contains(callbacks, f => f.Ranges[0].Rva > start.Ranges[0].Rva);

        // And the two the loader itself calls, which bracket the program: nothing is below _init and
        // nothing is above _fini, and no call in the file reaches either of them.
        uint lastCallTarget = document.Functions
            .Where(f => f.FoundBy.Contains("call_target"))
            .Max(f => f.Ranges[0].Rva);
        Assert.Contains(callbacks, f => f.Ranges[0].Rva < start.Ranges[0].Rva);
        Assert.Contains(callbacks, f => f.Ranges[0].Rva > lastCallTarget);
    }

    private static List<string> SectionFlags(InventoryDocument document, uint rva)
        => document.Sections
            .Where(s => rva >= s.Rva && rva < s.Rva + Math.Max(s.VirtualSize, s.RawSize))
            .Select(s => s.Code ? "code" : "data")
            .ToList();

    // ----------------------------------------------------------------------- jump tables and xrefs

    /// <summary>
    /// A switch in a position-independent 64-bit binary: the table's address is loaded into a register
    /// (<c>lea rdx, [rip+...]</c>) and the entries are offsets from it. Finding it needs the register
    /// to be followed, which is the difference between seeing the switch and seeing an opaque jump.
    /// </summary>
    [Fact]
    public void A_switch_in_a_64_bit_elf_binary_is_a_jump_table()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");
        var document = Inventory(image);

        var table = Assert.Single(document.JumpTables);
        Assert.Equal("relative", table.Kind);
        Assert.Equal(11, table.Entries);
        Assert.Equal(11, table.Targets.Count);

        // The table belongs to the function that reads it — `owner` is that function's id — and
        // everything it points at is inside it: a switch's arms are part of the switch.
        var dispatch = Assert.Single(document.Functions, f => f.Name == "dispatch");
        Assert.Equal(dispatch.Id, table.Owner);
        Assert.Contains(table.UsedAtRva, Range(dispatch));

        foreach (uint target in table.Targets)
        {
            Assert.True(
                dispatch.Ranges.Any(r => target >= r.Rva && target < r.Rva + r.Size),
                $"target 0x{target:X} is outside dispatch");
        }

        // The table itself is data, not code.
        Assert.Contains(document.Data, d => d.Rva <= table.Rva && d.Rva + Math.Max(d.Size, 1U) > table.Rva);
    }

    /// <summary>
    /// The 32-bit position-independent idiom: <c>call __x86.get_pc_thunk.ax</c> puts the return address
    /// in a register and <c>add $delta, eax</c> turns it into the table's base. The stub is recognised
    /// by its bytes, because the analysis has no symbol names to go on.
    /// </summary>
    [Fact]
    public void A_switch_in_a_32_bit_pie_elf_binary_is_a_jump_table()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf32-release"))
        {
            return;
        }

        var document = Inventory(Load("sample-elf32-release"));

        var table = Assert.Single(document.JumpTables);
        Assert.Equal("relative", table.Kind);
        Assert.Equal(11, table.Entries);

        var dispatch = Assert.Single(document.Functions, f => f.Name == "dispatch");
        Assert.Equal(dispatch.Id, table.Owner);
    }

    /// <summary>A binary with no switch in it has nothing to find, and must not invent a table.</summary>
    [Fact]
    public void A_binary_with_no_switch_has_no_jump_tables()
    {
        if (!TestPaths.ElfCorpusExists("shapes-elf64.so"))
        {
            return;
        }

        var document = Inventory(Load("shapes-elf64.so"));

        Assert.Empty(document.JumpTables);
    }

    /// <summary>
    /// A data xref names a place in the image, not an offset from a register: `[esi+4]` is not `[4]`,
    /// and resolving it invented references into the header. `[edi + 0x403004]` is different — that
    /// displacement is an address, which is how a compiler indexes a global array — so it survives.
    /// </summary>
    [Fact]
    public void A_data_xref_points_into_the_image_and_not_into_the_header()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");
        var document = Inventory(image);

        uint firstSection = document.Sections
            .Where(s => s.Rva > 0 && (s.RawSize > 0 || s.VirtualSize > 0))
            .Select(s => s.Rva)
            .Min();

        Assert.All(document.Xrefs, xref =>
        {
            if (!xref.Kind.StartsWith("data", StringComparison.Ordinal))
            {
                return;
            }

            Assert.True(
                xref.ToRva >= firstSection,
                $"data xref from 0x{xref.FromRva:X} points at 0x{xref.ToRva:X}, which is in the header");
        });

        // The globals the program really reads are still there: g_table is read through a
        // RIP-relative operand, which is an address the file states.
        Assert.Contains(document.Xrefs, x => x.Kind == "data_read" && x.ToRva == 0x405C);
    }

    private static IEnumerable<uint> Range(FunctionInfo function)
        => function.Ranges.SelectMany(r => Enumerable.Range((int)r.Rva, (int)Math.Max(r.Size, 1)).Select(i => (uint)i));

    // ------------------------------------------------------------------ the loader's choice

    [Fact]
    public void The_loader_knows_an_elf_file_from_a_pe_file()
    {
        var elf = ImageLoader.LoadBytes(SyntheticElf.Build(), "fixture.elf");
        Assert.Equal("elf64", elf.Format);
        Assert.True(elf.Ok, string.Join("; ", elf.Problems));
        Assert.Null(elf.Image!.Pe);
        Assert.NotNull(elf.Image.Elf);

        var pe = ImageLoader.LoadBytes(SyntheticPe.Build(new SyntheticPeOptions()), "fixture.exe");
        Assert.Equal("pe32", pe.Format);
        Assert.True(pe.Ok, string.Join("; ", pe.Problems));
        Assert.NotNull(pe.Image!.Pe);
        Assert.Null(pe.Image.Elf);
    }

    [Fact]
    public void A_file_of_no_known_format_says_so()
    {
        var result = ImageLoader.LoadBytes([0x01, 0x02, 0x03, 0x04, 0x05, 0x06], "mystery.bin");

        Assert.Null(result.Image);
        Assert.Contains("cannot tell what", string.Join("; ", result.Problems));
    }

    /// <summary>
    /// An image whose instruction set the decoder does not speak. The loader still reads it —
    /// its sections, symbols and relocations — but the analysis decodes nothing and says why,
    /// instead of reading ARM bytes as x86 and inventing a program out of them. Silence is the one
    /// failure this tool must not have, because silent output looks like an answer.
    /// </summary>
    [Fact]
    public void An_image_of_another_machine_is_loaded_but_not_decoded()
    {
        var loaded = ImageLoader.LoadBytes(
            SyntheticElf.Build(new SyntheticElfOptions { Machine = 40 }), // EM_ARM, 32-bit
            "fixture.elf");

        Assert.True(loaded.Ok, string.Join("; ", loaded.Problems));
        Assert.Equal("arm", loaded.Image!.Isa);

        var document = Inventory(loaded.Image);

        // The file is still described: none of this depends on decoding the code.
        Assert.Equal("arm", document.Binary.Isa);
        Assert.NotEmpty(document.Sections);
        Assert.Contains(document.Functions, f => f.Name == "func_a");

        // What is missing is said out loud, in the document and not only on the console.
        Assert.Equal(0, document.Statistics["instructions"]);
        Assert.Contains(document.Problems, problem => problem.Contains("not arm", StringComparison.Ordinal));
    }

    /// <summary>
    /// An AArch64 image is decoded. It is the one instruction set other than x86 that the decoder
    /// speaks, and for the reason in <see cref="Arm64Decoder"/>: every instruction is four bytes, so
    /// a walk that recognises nothing still knows where it is. What it does with the words it does
    /// recognise — every form of branch, the PC-relative addressing, the pairs that open a frame — is
    /// checked in <see cref="Arm64DecoderTests"/> against the linker's own record of this binary.
    /// </summary>
    [Fact]
    public void An_aarch64_image_is_loaded_and_decoded()
    {
        var loaded = ImageLoader.LoadBytes(
            SyntheticElf.Build(new SyntheticElfOptions { Machine = 183 }), // EM_AARCH64
            "fixture.elf");

        Assert.True(loaded.Ok, string.Join("; ", loaded.Problems));
        Assert.Equal("arm64", loaded.Image!.Isa);

        var document = Inventory(loaded.Image);

        Assert.DoesNotContain(document.Problems, problem => problem.Contains("not arm64", StringComparison.Ordinal));

        // And what is there is named by the machine the file says it is, not by the one this machine
        // has: a relocation number is meaningless without saying whose number it is.
        Assert.All(document.Relocations, r => Assert.StartsWith("R_AARCH64_", r.Kind, StringComparison.Ordinal));
    }

    /// <summary>
    /// An AArch64 object's relocation types. The names in the loader are not remembered from a
    /// specification: this test assembles one with LLVM's own assembler and makes the two agree, so a
    /// wrong number fails here rather than in someone's inventory. Skipped where no assembler is
    /// installed — the synthetic file above covers the same code path without one.
    /// </summary>
    [Fact]
    public void An_aarch64_object_names_its_relocations()
    {
        string? assembler = new[] { "llvm-mc-19", "llvm-mc" }.FirstOrDefault(ToolDetection.Exists);
        if (assembler is null)
        {
            return;
        }

        using var temp = new TempDir("recon-elf-aarch64");
        string source = Path.Combine(temp.Path, "a.s");
        string objectFile = Path.Combine(temp.Path, "a.o");
        File.WriteAllText(source, string.Join('\n',
            "\t.text",
            "\t.globl\tfunc_a",
            "func_a:",
            "\tadrp\tx0, g_data",
            "\tadd\tx0, x0, :lo12:g_data",
            "\tbl\textern_b",     // undefined, so the assembler has to leave a relocation for it
            "\tret",
            "\t.data",
            "\t.globl\tg_data",
            "g_data:",
            "\t.quad\t7",
            string.Empty));

        using var run = Process.Start(new ProcessStartInfo(assembler)
        {
            Arguments = $"-triple=aarch64-linux-gnu -filetype=obj -o \"{objectFile}\" \"{source}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });

        run?.WaitForExit(30_000);
        Assert.True(File.Exists(objectFile), "the assembler produced no object file");

        var result = ImageLoader.Load(objectFile);
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Equal("arm64", result.Image!.Isa);

        var kinds = result.Image.Relocations.Select(r => r.Kind).ToList();
        Assert.Contains("R_AARCH64_ADR_PREL_PG_HI21", kinds);
        Assert.Contains("R_AARCH64_ADD_ABS_LO12_NC", kinds);
        Assert.Contains("R_AARCH64_CALL26", kinds);
    }

    /// <summary>
    /// The same refusal at the command line: the inventory says which instruction set went unread,
    /// and `recon disasm` refuses rather than printing a listing of instructions that are not there.
    /// </summary>
    [Fact]
    public void The_cli_says_so_too_when_it_cannot_read_the_instruction_set()
    {
        using var temp = new TempDir("recon-elf-arm");
        string inputs = Path.Combine(temp.Path, "inputs");
        Directory.CreateDirectory(inputs);
        string file = Path.Combine(inputs, "fixture.elf");
        File.WriteAllBytes(file, SyntheticElf.Build(new SyntheticElfOptions { Machine = 40 })); // EM_ARM

        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "arm"

            [target]
            format = "elf32"
            arch = "arm"

            [[input]]
            id = "main"
            role = "original"
            file = "fixture.elf"
            sha256 = "{PeImage.HashFile(file)}"

            [paths]
            profiles = ["{TestPaths.RepositoryRoot.Replace('\\', '/')}/src/Recon.Core/Toolchains/profiles"]
            """);

        var inventory = CliRun.Run("inventory", "--project", temp.Path, "-o", Path.Combine(temp.Path, "inv.json"));

        Assert.Equal(0, inventory.ExitCode);
        Assert.Contains("not arm", inventory.All);

        var disasm = CliRun.Run("disasm", "--project", temp.Path, "func_a");

        Assert.NotEqual(0, disasm.ExitCode);
        Assert.Contains("not arm", disasm.All);
    }

    // ------------------------------------------------------------------ the inventory

    [Fact]
    public void An_elf_binary_is_inventoried_the_way_a_pe_one_is()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");
        var document = Inventory(image);

        Assert.Equal("elf64", document.Binary.Format);
        Assert.Equal("x64", document.Binary.Arch);
        Assert.Equal("x64", document.Binary.Isa);
        Assert.False(document.Binary.Dll);

        // The functions of the program are the ones its source defines, and every one of them is
        // described as 64-bit code rather than being assumed to be 32-bit.
        foreach (string name in new[] { "add", "mul_std", "sub_fast", "dispatch", "fib", "loop_sum", "main" })
        {
            var function = Assert.Single(document.Functions, f => f.Name == name);
            Assert.Equal("x64", function.Isa);
            Assert.Equal(".text", function.Section);
            Assert.NotEmpty(function.Ranges);
        }

        Assert.All(document.Functions, f => Assert.NotEqual("x86", f.Isa));
        Assert.NotEmpty(document.Sections);
        Assert.NotEmpty(document.Relocations);

        // Imports and the thunk that calls them: a PLT stub names the import it jumps through.
        Assert.Contains(document.Imports, i => i.Name == "printf");
        Assert.Contains(document.Functions, f => f.ImportThunk is not null && f.ImportThunk.Contains("printf", StringComparison.Ordinal));
    }

    [Fact]
    public void The_symbol_table_becomes_debug_information()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");
        var debug = ElfSymbols.Read(image);

        Assert.NotNull(debug);
        Assert.Equal("elf", debug!.Kind);
        Assert.Contains(debug.Symbols, s => s.Name == "add" && !s.IsData);
        Assert.Contains(debug.Symbols, s => s.Name == "g_counter" && s.IsData);
        Assert.Contains(debug.Compilands, c => c.Unit == "sample.c");
    }

    [Fact]
    public void A_shared_object_is_inventoried_as_a_library()
    {
        if (!TestPaths.ElfCorpusExists("shapes-elf64.so"))
        {
            return;
        }

        var image = Load("shapes-elf64.so");

        Assert.True(image.IsSharedObject);
        Assert.True(image.Elf!.IsLibrary);
        Assert.False(image.Elf.IsPositionIndependent is false && image.Elf.Interpreter is not null);

        var document = Inventory(image);
        Assert.True(document.Binary.Dll);
        Assert.NotEmpty(document.Exports);
    }

    [Fact]
    public void A_stripped_elf_has_no_symbols_and_no_invented_functions()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-stripped"))
        {
            return;
        }

        var image = Load("sample-elf64-stripped");

        Assert.DoesNotContain(image.Symbols, s => s.Source == "symtab");

        // No function is invented from the dynamic table: what is left there is the one data
        // symbol the dynamic linker exports, not a description of the code.
        var debug = ElfSymbols.Read(image);
        if (debug is not null)
        {
            Assert.Equal(0, debug.FunctionCount);
        }

        // What is left is still an inventory rather than an error.
        var document = Inventory(image);
        Assert.Equal("elf64", document.Binary.Format);
        Assert.NotEmpty(document.Imports);
    }

    [Fact]
    public void A_32_bit_elf_is_inventoried_the_same_way()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf32-release"))
        {
            return;
        }

        var image = Load("sample-elf32-release");
        Assert.Equal("elf32", image.Format);
        Assert.Equal("x86", image.ArchName);

        var document = Inventory(image);
        Assert.Equal("elf32", document.Binary.Format);
        Assert.Contains(document.Functions, f => f.Name == "add");
        Assert.All(document.Functions, f => Assert.Equal("x86", f.Isa));
    }

    // ------------------------------------------------------------------ end to end

    [Fact]
    public void The_cli_inventories_an_elf_project_and_compares_one_with_itself()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir("recon-elf");
        string binary = TestPaths.ElfCorpus("sample-elf64-release");
        string sha = PeImage.HashFile(binary);

        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "elf"

            [target]
            format = "elf64"
            arch = "x64"

            [[input]]
            id = "main"
            role = "original"
            file = "sample-elf64-release"
            sha256 = "{sha}"

            [paths]
            profiles = ["{TestPaths.RepositoryRoot.Replace('\\', '/')}/src/Recon.Core/Toolchains/profiles"]
            """);

        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.ElfCorpusDirectory.Replace('\\', '/')}"
            """);

        var inventory = CliRun.Run("inventory", "--project", temp.Path);
        Assert.Equal(0, inventory.ExitCode);
        Assert.Contains("functions", inventory.StandardOutput);
        Assert.Contains("wrote", inventory.StandardOutput);

        var diff = CliRun.Run("diff", binary, binary, "--summary");
        Assert.Equal(0, diff.ExitCode);
        Assert.Contains("score        1", diff.StandardOutput);
        Assert.Contains("0 only left, 0 only right", diff.StandardOutput);
    }

    [Fact]
    public void Delink_stays_a_pe_command_and_says_why()
    {
        if (!Corpus)
        {
            return;
        }

        using var temp = new TempDir("recon-elf-delink");
        string binary = TestPaths.ElfCorpus("sample-elf64-release");

        temp.Write("project.toml", $"""
            schema_version = 1

            [project]
            name = "elf"

            [target]
            format = "elf64"
            arch = "x64"

            [[input]]
            id = "main"
            role = "original"
            file = "sample-elf64-release"
            sha256 = "{PeImage.HashFile(binary)}"

            [paths]
            profiles = ["{TestPaths.RepositoryRoot.Replace('\\', '/')}/src/Recon.Core/Toolchains/profiles"]
            """);

        temp.Write("local.toml", $"""
            schema_version = 1

            [inputs]
            dir = "{TestPaths.ElfCorpusDirectory.Replace('\\', '/')}"
            """);

        var delink = CliRun.Run("delink", "--project", temp.Path);
        Assert.NotEqual(0, delink.ExitCode);
        Assert.Contains("works on PE images", delink.StandardError);
    }
}
