using System.Buffers.Binary;
using System.Text;
using Recon.Archive;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// COFF archives: the <c>!&lt;arch&gt;</c> member format every Windows <c>.lib</c> uses, and the Unix
/// <c>.a</c> that shares its container.
///
/// The real archives these read are not in the repository — it stores no binaries — so those tests
/// skip when <c>RECON_LIB_INPUTS</c> does not point at a directory holding them. What they pin is what
/// no in-memory fixture can: that the member count adds up over a 21,000-member import library, that
/// an <c>@comp.id</c> agrees with the compiler string in the same object, and that the symbols named
/// by the short import members are the symbols the archive's own index lists.
/// </summary>
public class CoffArchiveTests
{
    private static string? InputsDirectory
    {
        get
        {
            string? directory = Environment.GetEnvironmentVariable("RECON_LIB_INPUTS");
            return string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : directory;
        }
    }

    private static string? Input(string name)
    {
        string? directory = InputsDirectory;
        if (directory is null)
        {
            return null;
        }

        string candidate = Path.Combine(directory, name);
        return File.Exists(candidate) ? candidate : null;
    }

    private static CoffArchive Load(string name) => CoffArchive.Load(File.ReadAllBytes(Input(name)!), Input(name)!);

    // ------------------------------------------------------------------ the structure itself

    /// <summary>
    /// A member header is 60 bytes of decimal ASCII ending in <c>`\n</c>, and that terminator is what
    /// makes the format sequential: a walk that does not find it must stop rather than read on.
    /// </summary>
    [Fact]
    public void A_file_that_is_not_an_archive_is_refused_with_what_it_does_start_with()
    {
        var archive = CoffArchive.Load(Encoding.ASCII.GetBytes("MZ\x90\x00 not an archive at all"), "nope.lib");

        Assert.NotEmpty(archive.Problems);
        Assert.Contains("!<arch>", archive.Problems[0]);
        Assert.Empty(archive.Members);
    }

    [Fact]
    public void A_truncated_member_stops_the_walk_instead_of_reading_past_the_end()
    {
        // A well-formed first member, then a header whose size runs off the end of the file.
        var bytes = new List<byte>();
        bytes.AddRange("!<arch>\n"u8);
        bytes.AddRange(SyntheticArchive.Member("/", new byte[4]));
        bytes.AddRange(SyntheticArchive.Header("truncated.obj", 999));

        var archive = CoffArchive.Load(bytes.ToArray(), "truncated.lib");

        // The one complete member is read, and the shortfall is said rather than guessed at.
        Assert.Single(archive.Members);
        Assert.NotEmpty(archive.Problems);
        Assert.Contains("past the end of the file", string.Join("; ", archive.Problems));
    }

    /// <summary>
    /// Members are two-byte aligned: an odd size is followed by one padding byte, and a reader that
    /// does not skip it reads the next header from the wrong place and sees no terminator.
    /// </summary>
    [Fact]
    public void An_odd_sized_member_is_followed_by_a_padding_byte()
    {
        // An object with no sections and no symbols: empty, but shaped like one, so the only thing
        // this test is about is the byte after it.
        var bytes = new List<byte>();
        bytes.AddRange("!<arch>\n"u8);
        bytes.AddRange(SyntheticArchive.Member("odd.obj", SyntheticArchive.EmptyObject(21)));  // odd: one pad byte
        bytes.AddRange(SyntheticArchive.Member("even.obj", SyntheticArchive.EmptyObject(20)));

        var archive = CoffArchive.Load(bytes.ToArray(), "padded.lib");

        Assert.Empty(archive.Problems);
        Assert.Equal(2, archive.Members.Count);
        Assert.Equal("odd.obj", archive.Members[0].Name);
        Assert.Equal(21, archive.Members[0].Size);
        Assert.Equal("even.obj", archive.Members[1].Name);
    }

    /// <summary>A name too long for a header is held in the <c>//</c> member and referred to as <c>/N</c>.</summary>
    [Fact]
    public void A_long_name_comes_from_the_long_name_table()
    {
        const string longName = "a_very_long_object_name_indeed.obj";
        var bytes = SyntheticArchive.Build((longName, SyntheticArchive.EmptyObject()));

        var archive = CoffArchive.Load(bytes, "longnames.lib");

        var member = Assert.Single(archive.Members, m => m.Kind != ArchiveMemberKind.Longnames);
        Assert.Equal("/0", member.RawName);
        Assert.Equal(longName, member.Name);
    }

    /// <summary>
    /// An archive is not a program and is not loaded as one. What it does is point at the command
    /// that reads it, because "cannot tell what this is" leaves the next question unanswered, and
    /// the person holding a <c>.lib</c> does not want to be told it is not an executable.
    /// </summary>
    [Fact]
    public void The_loader_says_an_archive_is_an_archive_and_names_the_command_that_reads_it()
    {
        var loaded = Recon.Images.ImageLoader.LoadBytes(
            SyntheticArchive.Build(("first.obj", SyntheticArchive.EmptyObject())),
            "fixture.lib");

        Assert.Null(loaded.Image);
        Assert.Equal("coff-archive", loaded.Format);
        Assert.Contains("recon lib", string.Join("; ", loaded.Problems));
        Assert.Contains("not a program", string.Join("; ", loaded.Problems));
    }

    // ----------------------------------------------------------------------- the real 1998 file

    /// <summary>
    /// VBAEXE6.LIB holds one object, built by the assembler that built the Visual Basic 6 runtime.
    /// The object carries two independent records of that: an <c>@comp.id</c> symbol holding
    /// <c>(product id &lt;&lt; 16) | build</c>, and the assembler's own banner as a string. They were
    /// written by the same tool, and a reader that disagrees with itself here is wrong.
    /// </summary>
    [Fact]
    public void The_visual_basic_library_names_the_assembler_that_built_its_object()
    {
        if (Input("VBAEXE6.LIB") is null)
        {
            return;
        }

        var archive = Load("VBAEXE6.LIB");

        // Two symbol indexes, the long-name table, and the object itself.
        Assert.Equal(4, archive.Members.Count);
        Assert.Equal(2, archive.Members.Count(m => m.Kind == ArchiveMemberKind.Linker));
        Assert.Single(archive.Members, m => m.Kind == ArchiveMemberKind.Longnames);
        Assert.Empty(archive.Problems);

        var member = Assert.Single(archive.Objects);
        Assert.EndsWith("natsupp.obj", member.Name, StringComparison.Ordinal);
        Assert.Equal("x86", member.Machine);
        Assert.Equal(".text", member.Sections[0]);
        Assert.Contains("__adjust_fdiv", member.Symbols);

        // 0x000E1C83: product id 14 (masm 6.13) in the high bits, build 7299 in the low ones.
        Assert.Equal(0x000E1C83u, member.CompId);
        Assert.Equal("masm_6.13", member.CompTool);
        Assert.Equal(7299, member.CompBuild);

        // The object's own banner, read out of the same bytes: the second record of the same tool.
        string text = Encoding.ASCII.GetString(File.ReadAllBytes(Input("VBAEXE6.LIB")!));
        Assert.Contains("Microsoft (R) Macro Assembler Version 6.13.7299", text, StringComparison.Ordinal);

        Assert.Equal(["masm_6.13 build 7299 (1 object)"], archive.CompilersUsed);
    }

    // ------------------------------------------------------------- a real Microsoft import library

    /// <summary>
    /// An import library is made of short members, each naming one symbol and the DLL that exports
    /// it. This file holds 20,551 of them, and the counts have to add up over every member — a walk
    /// that loses its place anywhere shows up as an arithmetic error, not as a wrong row.
    /// </summary>
    [Fact]
    public void Every_member_of_a_twenty_thousand_member_import_library_is_accounted_for()
    {
        if (Input("windows.0.52.0.lib") is null)
        {
            return;
        }

        var archive = Load("windows.0.52.0.lib");

        // Two symbol indexes, the long-name table, and every other member classified.
        Assert.Empty(archive.Problems);
        Assert.Equal(
            archive.Members.Count(m => m.Kind is ArchiveMemberKind.Linker)
            + archive.Members.Count(m => m.Kind is ArchiveMemberKind.Longnames)
            + archive.Objects.Count()
            + archive.Imports.Count(),
            archive.Members.Count);

        Assert.Equal(2, archive.Members.Count(m => m.Kind == ArchiveMemberKind.Linker));
        Assert.True(archive.Imports.Count() > 10000, $"expected a large import library, found {archive.Imports.Count()}");
        Assert.All(archive.Imports, m => Assert.False(string.IsNullOrEmpty(m.ImportDll)));
        Assert.All(archive.Imports, m => Assert.False(string.IsNullOrEmpty(m.ImportSymbol)));
    }

    /// <summary>
    /// The archive's own symbol index is a second list of what it offers, written by the librarian
    /// separately from the import records. Reading it here and comparing is what makes the import
    /// members' symbol names measured rather than assumed.
    /// </summary>
    [Fact]
    public void The_symbols_named_by_the_import_members_are_the_ones_the_symbol_index_lists()
    {
        if (Input("windows.0.52.0.lib") is null)
        {
            return;
        }

        var archive = Load("windows.0.52.0.lib");
        var linker = archive.Members.First(m => m.Kind == ArchiveMemberKind.Linker);
        var indexed = SymbolIndex(File.ReadAllBytes(Input("windows.0.52.0.lib")!), linker.DataOffset);

        Assert.NotEmpty(indexed);
        foreach (var member in archive.Imports.Take(50))
        {
            Assert.Contains(member.ImportSymbol!, indexed);
        }
    }

    /// <summary>
    /// Each DLL in an import library also gets one real object: the import descriptor the linker
    /// emits into <c>.idata</c>. It carries no <c>@comp.id</c> of its own, and the reader must say so
    /// rather than name product id 0, which in this table means "object with no record at all".
    /// </summary>
    [Fact]
    public void An_import_descriptor_object_carries_no_compiler_record_and_is_not_given_one()
    {
        if (Input("windows.0.52.0.lib") is null)
        {
            return;
        }

        var archive = Load("windows.0.52.0.lib");

        Assert.NotEmpty(archive.Objects);
        var withNoRecord = archive.Objects.Where(m => m.CompId is null or 0).ToList();
        Assert.NotEmpty(withNoRecord);
        Assert.All(withNoRecord, m => Assert.Null(m.CompTool));
        Assert.DoesNotContain(archive.CompilersUsed, c => c.Contains("unmarked", StringComparison.Ordinal));
    }

    // --------------------------------------------------------------- a real GNU archive

    /// <summary>
    /// A Unix <c>.a</c> is the same container with ELF members. Reading a COFF header out of those
    /// bytes gives a machine called <c>0x457F</c> — the first two bytes of <c>\x7FELF</c> little
    /// endian — and a section count from the class and endianness fields, which is a confident
    /// answer about a format this reader does not have.
    /// </summary>
    [Fact]
    public void A_unix_archive_says_its_members_are_elf_rather_than_reading_them_as_coff()
    {
        if (Input("libkernel32.a") is null)
        {
            return;
        }

        var archive = Load("libkernel32.a");

        Assert.NotEmpty(archive.Objects);
        Assert.All(archive.Objects, m => Assert.Equal("x64", m.Machine));
        Assert.All(archive.Members, m => Assert.DoesNotContain("0x457F", m.Machine, StringComparison.Ordinal));

        // Names longer than a header hold survive the long-name table, and without the trailing
        // slash both librarians use as a terminator.
        Assert.Contains(archive.Objects, m => m.Name.EndsWith(".o", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Objects, m => m.Name.EndsWith("/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first linker member, read here rather than by the parser: a big-endian count, that many
    /// big-endian offsets, then the NUL-terminated names. It is the librarian's own index, so the
    /// names it holds are a second opinion on what the import members say.
    /// </summary>
    private static List<string> SymbolIndex(byte[] bytes, long memberOffset)
    {
        var names = new List<string>();
        long at = memberOffset;
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)at, 4));
        at += 4 + ((long)count * 4);

        for (int i = 0; i < count && at < bytes.Length; i++)
        {
            int end = Array.IndexOf(bytes, (byte)0, (int)at);
            if (end < 0)
            {
                break;
            }

            names.Add(Encoding.ASCII.GetString(bytes, (int)at, end - (int)at));
            at = end + 1;
        }

        return names;
    }
}
