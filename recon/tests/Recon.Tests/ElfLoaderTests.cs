using Recon.Elf;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M6 — the ELF front end. The synthetic fixture is where a parsing rule is checked against the
/// exact bytes that rule is about, including the byte order and word size no desktop machine has;
/// the tests at the end run over a real corpus when <c>tools/build-elf-corpus.sh</c> has produced
/// one, and skip when it has not.
/// </summary>
public class ElfLoaderTests
{
    // ------------------------------------------------------------------ what the file is

    [Fact]
    public void A_file_that_is_not_elf_says_so_instead_of_guessing()
    {
        var result = ElfLoader.LoadBytes([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00]);

        Assert.Null(result.Image);
        Assert.Contains("not an ELF file", string.Join("; ", result.Problems));
    }

    [Fact]
    public void A_file_too_small_to_hold_a_header_is_reported_not_thrown()
    {
        var result = ElfLoader.LoadBytes(SyntheticElf.Build()[..8]);

        Assert.Null(result.Image);
        Assert.Contains("too small", string.Join("; ", result.Problems));
    }

    [Fact]
    public void An_unknown_class_or_byte_order_is_a_problem_and_not_a_guess()
    {
        byte[] bytes = SyntheticElf.Build();
        bytes[4] = 7; // neither ELFCLASS32 nor ELFCLASS64
        var classResult = ElfLoader.LoadBytes(bytes);
        Assert.Contains("unknown ELF class", string.Join("; ", classResult.Problems));

        byte[] order = SyntheticElf.Build();
        order[5] = 9;
        var orderResult = ElfLoader.LoadBytes(order);
        Assert.Contains("unknown ELF data encoding", string.Join("; ", orderResult.Problems));
    }

    // ------------------------------------------------------------------ the synthetic file

    [Fact]
    public void A_64_bit_executable_is_parsed_into_its_sections_symbols_and_relocations()
    {
        var result = ElfLoader.LoadBytes(SyntheticElf.Build());

        Assert.Empty(result.Problems);
        var image = result.Image;
        Assert.NotNull(image);
        Assert.Equal(ElfClass.Elf64, image.Class);
        Assert.Equal(ElfData.LittleEndian, image.Data);
        Assert.Equal(ElfType.Executable, image.Type);
        Assert.Equal("x64", image.Architecture);
        Assert.Equal("elf64", image.Format);
        Assert.Equal(SyntheticElf.FuncA, image.EntryPoint);
        Assert.True(image.Is64);
        Assert.False(image.IsSharedObject);

        // Sections: the code section is executable and readable, the data one is writable, and
        // .bss has an address and a size but no bytes in the file.
        var text = image.SectionNamed(".text");
        Assert.NotNull(text);
        Assert.True(text.IsCode);
        Assert.True(text.IsExecutable);
        Assert.False(text.IsWritable);
        Assert.Equal("code", text.Kind);
        Assert.Equal(0x40ul, text.Size);

        var bss = image.SectionNamed(".bss");
        Assert.NotNull(bss);
        Assert.Equal("bss", bss.Kind);
        Assert.True(bss.OccupiesNoFileSpace);
        Assert.Null(bss.AddressToOffset(bss.Address));

        var data = image.SectionNamed(".data");
        Assert.NotNull(data);
        Assert.Equal("data", data.Kind);
        Assert.True(data.IsWritable);

        // The code section is the only one the analyser disassembles.
        Assert.Single(image.CodeSections);
        Assert.Equal(".text", image.CodeSections[0].Name);
    }

    [Fact]
    public void Symbols_come_from_both_tables_and_keep_what_each_one_knows()
    {
        var image = ElfLoader.LoadBytes(SyntheticElf.Build()).Image!;

        var funcA = image.Symbols.FirstOrDefault(s => s.Name == "func_a" && s.Source == "symtab");
        Assert.NotNull(funcA);
        Assert.Equal(SyntheticElf.FuncA, funcA.Address);
        Assert.Equal(0x20ul, funcA.Size);
        Assert.True(funcA.IsFunction);
        Assert.Equal(ElfSymbolBinding.Global, funcA.Binding);
        Assert.Equal(".text", funcA.SectionName);
        Assert.True(funcA.IsDefined);

        var funcB = image.Symbols.FirstOrDefault(s => s.Name == "func_b");
        Assert.NotNull(funcB);
        Assert.Equal(ElfSymbolBinding.Local, funcB.Binding);

        var counter = image.Symbols.FirstOrDefault(s => s.Name == "g_counter");
        Assert.NotNull(counter);
        Assert.True(counter.IsObject);
        Assert.Equal(SyntheticElf.Counter, counter.Address);
        Assert.Equal(".data", counter.SectionName);

        // An import is an undefined symbol in .dynsym: it names no bytes in this file.
        var printf = image.Symbols.FirstOrDefault(s => s.Name == "printf");
        Assert.NotNull(printf);
        Assert.Equal("dynsym", printf.Source);
        Assert.False(printf.IsDefined);
        Assert.Contains(printf, image.ImportedSymbols);

        // func_a is both defined in .symtab and exported from .dynsym: two entries, one function.
        Assert.Equal(2, image.Symbols.Count(s => s.Name == "func_a"));
    }

    [Fact]
    public void Relocations_are_read_with_their_target_not_only_their_address()
    {
        var image = ElfLoader.LoadBytes(SyntheticElf.Build()).Image!;

        Assert.Equal(2, image.Relocations.Count);
        var call = image.Relocations[0];
        Assert.Equal("R_X86_64_PC32", call.TypeName);
        Assert.Equal(SyntheticElf.FuncA + 5, call.Address);
        Assert.Equal("func_b", call.SymbolName);
        Assert.Equal(4, call.Width);
        // The branch target is the symbol plus the addend, which is what the code actually reaches.
        Assert.Equal(SyntheticElf.FuncB - 4, call.TargetAddress);
        Assert.True(call.IsRelativeToProgramCounter);

        var pointer = image.Relocations[1];
        Assert.Equal("R_X86_64_64", pointer.TypeName);
        Assert.Equal(SyntheticElf.Counter, pointer.TargetAddress);
        Assert.Equal(8, pointer.Width);
        Assert.Equal("g_counter", pointer.SymbolName);
    }

    [Fact]
    public void The_reserved_symbol_zero_does_not_shift_the_names_relocations_refer_to()
    {
        // Symbol index 0 is reserved and carries no name; a loader that dropped it would name the
        // first relocation after the file symbol instead of after func_b.
        var image = ElfLoader.LoadBytes(SyntheticElf.Build()).Image!;

        Assert.Equal("func_b", image.Relocations[0].SymbolName);
        Assert.Equal("g_counter", image.Relocations[1].SymbolName);
    }

    [Fact]
    public void A_32_bit_file_is_parsed_the_same_way()
    {
        var result = ElfLoader.LoadBytes(SyntheticElf.Build(new SyntheticElfOptions { Is64 = false }));

        Assert.Empty(result.Problems);
        var image = result.Image!;
        Assert.Equal(ElfClass.Elf32, image.Class);
        Assert.Equal("elf32", image.Format);
        Assert.Equal("x86", image.Architecture);
        Assert.Equal("x86", image.Isa);
        Assert.False(image.Is64);
        Assert.Equal(SyntheticElf.FuncA, image.EntryPoint);

        var funcA = image.Symbols.First(s => s.Name == "func_a");
        Assert.Equal(SyntheticElf.FuncA, funcA.Address);
        Assert.Equal(0x20ul, funcA.Size);

        // The relocation numbers mean different things per machine, so the names have to come from
        // the machine: R_386_PC32 is 2, and R_X86_64_PC32 is 2 as well, but R_386_32 is 1 where
        // R_X86_64_64 is 1.
        Assert.Equal("R_386_PC32", image.Relocations[0].TypeName);
        Assert.Equal("R_386_32", image.Relocations[1].TypeName);
        Assert.Equal(SyntheticElf.FuncB - 4, image.Relocations[0].TargetAddress);
        Assert.Equal(SyntheticElf.Counter, image.Relocations[1].TargetAddress);
        Assert.Equal("/lib/ld-linux.so.2", image.Interpreter);
    }

    [Fact]
    public void A_big_endian_file_is_read_in_its_own_byte_order()
    {
        var image = ElfLoader.LoadBytes(SyntheticElf.Build(new SyntheticElfOptions { BigEndian = true })).Image!;

        Assert.Equal(ElfData.BigEndian, image.Data);
        Assert.Equal(SyntheticElf.FuncA, image.EntryPoint);
        Assert.Equal("func_a", image.Symbols.First(s => s.IsFunction).Name);
        Assert.Equal(SyntheticElf.FuncA, image.Symbols.First(s => s.Name == "func_a").Address);
    }

    [Fact]
    public void Program_headers_give_the_image_base_and_the_interpreter()
    {
        var image = ElfLoader.LoadBytes(SyntheticElf.Build()).Image!;

        Assert.Equal(SyntheticElf.Base, image.ImageBase);
        Assert.Equal("/lib64/ld-linux-x86-64.so.2", image.Interpreter);
        Assert.Equal(3, image.Segments.Count);
        Assert.Contains(image.Segments, s => s.TypeName == "INTERP");
        Assert.Contains(image.Segments, s => s.TypeName == "LOAD" && s.IsExecutable);
        Assert.Contains(image.Segments, s => s.TypeName == "LOAD" && s.IsWritable && !s.IsExecutable);
        Assert.All(image.Segments, s => Assert.NotEqual("0x", s.TypeName[..2]));
    }

    [Fact]
    public void The_compiler_comment_and_the_build_id_are_read_when_they_are_there()
    {
        var image = ElfLoader.LoadBytes(SyntheticElf.Build()).Image!;

        Assert.Contains("GCC: (GNU) 14.2.0", image.CommentStrings);
        Assert.Equal("deadbeef", image.BuildId);
        Assert.Contains(image.Notes, n => n.Name == "GNU" && n.Type == 3);
    }

    // ------------------------------------------------------------------ the corpus

    private static bool Corpus => TestPaths.ElfCorpusExists("sample-elf64-release");

    private static ElfImage Load(string name)
    {
        var result = ElfLoader.Load(TestPaths.ElfCorpus(name));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        return result.Image!;
    }

    [Fact]
    public void Every_corpus_binary_parses_without_a_problem()
    {
        if (!Corpus)
        {
            return;
        }

        foreach (string path in Directory.GetFiles(TestPaths.ElfCorpusDirectory)
            .Where(p => !p.EndsWith(".map", StringComparison.Ordinal)))
        {
            var result = ElfLoader.Load(path);
            Assert.True(result.Ok, $"{Path.GetFileName(path)}: {string.Join("; ", result.Problems)}");
            Assert.NotNull(result.Image);
            Assert.True(result.Image!.Sections.Count > 0 || result.Image.Segments.Count > 0,
                $"{Path.GetFileName(path)}: neither sections nor segments");
        }
    }

    [Fact]
    public void A_corpus_executable_names_the_functions_its_source_defines()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");

        foreach (string name in new[] { "add", "mul_std", "sub_fast", "dispatch", "fib", "loop_sum", "main" })
        {
            Assert.Contains(image.Functions, f => f.Name == name);
        }

        // Every function of the program sits in an allocated, executable section.
        foreach (var function in image.Functions)
        {
            var section = image.SectionContainingAddress(function.Address);
            Assert.NotNull(section);
            Assert.True(section!.IsExecutable, $"{function.Name} is in {section.Name}, which is not executable");
        }
    }

    [Fact]
    public void A_stripped_binary_has_no_symbols_and_invents_none()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-stripped"))
        {
            return;
        }

        var image = Load("sample-elf64-stripped");

        Assert.DoesNotContain(image.Sections, s => s.Name == ".symtab");
        Assert.DoesNotContain(image.Symbols, s => s.Source == "symtab");
        Assert.Empty(image.Functions);

        // What is left is still enough to work with: the code sections and the imports.
        Assert.Contains(image.Sections, s => s.IsCode);
        Assert.NotEmpty(image.ImportedSymbols);
    }

    [Fact]
    public void Imports_are_named_with_the_version_the_binary_asks_for()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");

        var printf = image.ImportedSymbols.FirstOrDefault(s => s.Name == "printf");
        Assert.NotNull(printf);
        Assert.StartsWith("GLIBC_", printf.VersionedName.Substring(printf.VersionedName.IndexOf('@') + 1));
        Assert.NotEmpty(image.Needed);
        Assert.Contains(image.Needed, n => n.Contains("libc", StringComparison.Ordinal));
    }

    [Fact]
    public void A_shared_object_is_a_shared_object_and_says_what_it_is_called()
    {
        if (!TestPaths.ElfCorpusExists("shapes-elf64.so"))
        {
            return;
        }

        var image = Load("shapes-elf64.so");

        Assert.Equal(ElfType.SharedObject, image.Type);
        Assert.True(image.IsSharedObject);
        Assert.Equal("shapes-elf64.so", image.SoName);
        Assert.NotEmpty(image.ExportedSymbols);
    }

    [Fact]
    public void Relocations_of_a_pie_executable_name_their_type_and_their_target()
    {
        if (!Corpus)
        {
            return;
        }

        var image = Load("sample-elf64-release");

        Assert.NotEmpty(image.Relocations);
        foreach (var relocation in image.Relocations)
        {
            Assert.False(string.IsNullOrEmpty(relocation.TypeName));
            Assert.StartsWith("R_X86_64_", relocation.TypeName);
        }

        // A PIE's dynamic relocations are against symbols this file imports or against its own
        // addresses (R_X86_64_RELATIVE), and both are resolved here.
        Assert.Contains(image.Relocations, r => r.TypeName is "R_X86_64_JUMP_SLOT" or "R_X86_64_GLOB_DAT"
            || r.TypeName == "R_X86_64_RELATIVE");
        Assert.Contains(image.Relocations, r => r.TargetAddress is not null);
    }

    [Fact]
    public void The_32_bit_and_the_64_bit_corpus_agree_on_what_they_are()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf32-release"))
        {
            return;
        }

        var elf32 = Load("sample-elf32-release");
        var elf64 = Load("sample-elf64-release");

        Assert.Equal(ElfClass.Elf32, elf32.Class);
        Assert.Equal("elf32", elf32.Format);
        Assert.Equal("x86", elf32.Architecture);
        Assert.Equal(ElfClass.Elf64, elf64.Class);
        Assert.Equal("x64", elf64.Architecture);

        Assert.Contains(elf32.Functions, f => f.Name == "add");
        Assert.Contains(elf64.Functions, f => f.Name == "add");
    }

    [Fact]
    public void The_entry_point_of_a_corpus_binary_is_inside_a_code_section()
    {
        if (!Corpus)
        {
            return;
        }

        foreach (string name in new[] { "sample-elf64-debug", "sample-elf64-release", "sample-elf64-stripped" })
        {
            var image = Load(name);
            var section = image.SectionContainingAddress(image.EntryPoint);
            Assert.NotNull(section);
            Assert.True(section!.IsExecutable, $"{name}: entry point is in {section.Name}");
            Assert.Equal(image.Segments.Where(s => s.Type == 1).Min(s => s.VirtualAddress), image.ImageBase);
        }
    }

    [Fact]
    public void A_binary_built_by_clang_is_read_the_same_way()
    {
        if (!TestPaths.ElfCorpusExists("sample-elf64-clang"))
        {
            return;
        }

        var image = Load("sample-elf64-clang");

        Assert.Contains(image.Functions, f => f.Name == "add");
        Assert.NotEmpty(image.CommentStrings);
        Assert.Contains(image.CommentStrings, c => c.Contains("clang", StringComparison.OrdinalIgnoreCase));
    }
}
