using System.Text.Json;
using Recon.Tests.Fixtures;
using Recon.Vb6;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// A Visual Basic 5/6 program read as the structure it is: the header, the project data behind it,
/// and the objects the project is made of.
///
/// The chain is all pointers, which is what makes it worth testing against real files rather than
/// against a fixture: a fixture agrees with the reader that wrote it, while a program built by VB6 in
/// 1998 has its own opinion about where every field is. Five of them are here — Microsoft's own
/// VISDATA sample, and four programs from the wild — and they are read for what they are, which is
/// the same in a native program and a p-code one. They are not in the repository, so these tests skip
/// without <c>RECON_VB6_INPUTS</c> and <c>RECON_VB6_WILD</c>.
/// </summary>
[Collection("cli")]
public class Vb6ProgramTests
{
    private static string? InDirectory(string variable, string name)
    {
        string? directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string candidate = Path.Combine(directory, name);
        return File.Exists(candidate) ? candidate : null;
    }

    private static Vb6Program? Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var loaded = Recon.Images.ImageLoader.LoadBytes(bytes, path);
        return loaded.Image?.Pe is null ? null : Vb6Program.Read(loaded.Image.Pe, bytes);
    }

    /// <summary>
    /// The five real programs, with the objects each one turns out to hold. The numbers are measured
    /// from the files, so this is a record of what they are rather than an expectation written from
    /// the reader: a descriptor stride that was wrong would not find 73 objects, 39 of them forms,
    /// with names that read as the identifiers a VB programmer would have written.
    /// </summary>
    public static TheoryData<string, string, int, int, int, int> RealPrograms => new()
    {
        // which directory to look in, file, forms, class modules, standard modules, total
        { "RECON_VB6_WILD", "ElementEvil.exe", 39, 9, 25, 73 },
        { "RECON_VB6_INPUTS", "VISDATA.EXE", 34, 1, 1, 36 },
        { "RECON_VB6_WILD", "XiasporaServer.exe", 1, 0, 9, 10 },
        { "RECON_VB6_WILD", "Basic Server.exe", 1, 0, 1, 2 },
    };

    [Theory]
    [MemberData(nameof(RealPrograms))]
    public void A_real_visual_basic_program_reports_the_objects_its_project_is_made_of(
        string variable, string name, int forms, int classes, int modules, int total)
    {
        string? path = InDirectory(variable, name);
        if (path is null)
        {
            return;
        }

        var program = Read(path);
        Assert.NotNull(program);
        Assert.Empty(program!.Problems);

        Assert.Equal(total, program.Objects.Count);
        Assert.Equal(forms, program.Objects.Count(o => o.Kind == "form"));
        Assert.Equal(classes, program.Objects.Count(o => o.Kind == "class module"));
        Assert.Equal(modules, program.Objects.Count(o => o.Kind == "standard module"));

        // Every object has a name, and the names read like the identifiers they are: a wrong offset
        // anywhere in the descriptor would produce empty names or binary.
        Assert.All(program.Objects, o => Assert.False(string.IsNullOrWhiteSpace(o.Name)));
        Assert.All(program.Objects, o => Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", o.Name));

        // And every object is one of the kinds the compiler writes, rather than an unknown word.
        Assert.All(program.Objects, o => Assert.DoesNotContain("type 0x", o.Kind));
    }

    /// <summary>
    /// The build path is the most human thing in the file: the compiler records where it was run from,
    /// in UTF-16, and it is still there decades later. Microsoft's own sample says so — VISDATA's path
    /// is the one it was built at — which is also a check on the field's offset and encoding in one.
    /// </summary>
    [Fact]
    public void A_real_program_still_carries_the_path_it_was_built_in()
    {
        string? path = InDirectory("RECON_VB6_INPUTS", "VISDATA.EXE");
        if (path is null)
        {
            return;
        }

        var program = Read(path);
        Assert.NotNull(program);
        Assert.Contains("visdata.vbp", program!.Project.BuildPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vb98", program.Project.BuildPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A native program says where its native code is, and the code region it names is inside the
    /// image. That region is what the VB compiler produced — p-code for a p-code program, x86 for
    /// this one — and the tool reports it rather than reading it as either without being told.
    /// </summary>
    [Fact]
    public void A_native_program_names_its_native_code_and_its_code_region_is_in_the_image()
    {
        string? path = InDirectory("RECON_VB6_WILD", "Basic Server.exe");
        if (path is null)
        {
            return;
        }

        var program = Read(path);
        Assert.NotNull(program);
        Assert.False(program!.Project.IsPcode);
        Assert.True(program.Project.NativeCodeRva > 0);
        Assert.True(program.Project.CodeSize > 0);
        Assert.Equal(0x000A, program.CompileState);
    }

    /// <summary>
    /// A p-code program is what the same structures look like with <c>aNativeCode</c> cleared, and
    /// the reader has to say <c>vb6-pcode</c> and not decode the region: interpreter tokens are not
    /// machine code, and a decoder handed them produces a program that looks like an answer.
    /// </summary>
    [Fact]
    public void A_p_code_program_is_reported_as_p_code_and_its_region_is_left_alone()
    {
        byte[] image = SyntheticVb6.Program(aNativeCode: 0);

        var program = ReadProgram(image);
        Assert.NotNull(program);
        Assert.True(program!.Project.IsPcode);
        Assert.Equal(0u, program.Project.NativeCodeRva);
        Assert.Equal(2, program.Objects.Count);
    }

    /// <summary>
    /// The table's object count is authoritative, and the reader walks exactly that many — which is
    /// also why it cannot be the thing that catches a wrong table. Here the file holds two
    /// descriptors and declares three: the third is read out of whatever follows the second, and it
    /// comes back as a plausible object called "MZ" because the bytes there are the start of the DOS
    /// header. No reader could tell that from a project with a three-character object name, which is
    /// why it is the compile-state word below that catches a table that is not one.
    /// </summary>
    [Fact]
    public void The_object_count_is_taken_from_the_table_and_a_short_file_yields_what_follows()
    {
        byte[] image = SyntheticVb6.Program(aNativeCode: 0);
        int table = 0x400 + (0x1300 - 0x1000);
        image[table + 0x2A] = 3;   // three objects declared, two descriptors written

        // Clear the phantom descriptor's name pointer, so it points at nothing in particular.
        int phantom = 0x400 + (0x1360 - 0x1000) + (2 * 0x30);
        Array.Clear(image, phantom + 0x18, 4);

        var program = ReadProgram(image);
        Assert.NotNull(program);

        // Three declared, three reported: nothing is invented and nothing is silently dropped.
        Assert.Equal(3, program!.Objects.Count);
        Assert.Equal(["frmMain", "modMain"], program.Objects.Take(2).Select(o => o.Name));

        // The third read what a zero pointer points at — the file's own header — and the reader has
        // no way to know that is not a name. It is reported, and it is marked as of no known kind,
        // because 0 is not one of the type words the compiler writes.
        Assert.Equal("MZ", program.Objects[2].Name);
        Assert.StartsWith("object (type 0x", program.Objects[2].Kind);
    }

    /// <summary>
    /// A compile-state word other than the 0x000A of a compiled program means the pointer did not
    /// land on an object table, and the number that gives it away is worth more than the objects
    /// that would otherwise be read out of whatever is there.
    /// </summary>
    [Fact]
    public void An_object_table_that_is_not_one_is_reported_rather_than_read()
    {
        byte[] image = SyntheticVb6.Program(aNativeCode: 0);
        int table = 0x400 + (0x1300 - 0x1000);
        image[table + 0x28] = 0x00;
        image[table + 0x29] = 0x00;

        var program = ReadProgram(image);
        Assert.NotNull(program);
        Assert.Contains(program!.Problems, p => p.Contains("compile-state"));
    }

    [Fact]
    public void A_program_with_no_object_table_reports_that_rather_than_inventing_objects()
    {
        byte[] image = SyntheticVb6.Program(aNativeCode: 0, objectTable: false);

        var program = ReadProgram(image);
        Assert.NotNull(program);
        Assert.Empty(program!.Objects);
        Assert.Contains(program.Problems, p => p.Contains("object table"));
    }

    /// <summary>
    /// The object table is the one structure here that both compilation modes have, which is why it
    /// is read from a native program as well as a p-code one. A real project's objects come back in
    /// the order the project declared them.
    /// </summary>
    [Fact]
    public void The_objects_of_a_real_project_come_back_in_the_order_the_project_declared_them()
    {
        string? path = InDirectory("RECON_VB6_WILD", "Basic Server.exe");
        if (path is null)
        {
            return;
        }

        var program = Read(path);
        Assert.NotNull(program);
        Assert.Equal(["Main", "Local"], program!.Objects.Select(o => o.Name));

        Assert.Equal("form", program.Objects[0].Kind);
        Assert.Equal(6, program.Objects[0].MethodCount);
        Assert.Equal("standard module", program.Objects[1].Kind);
        Assert.Equal(10, program.Objects[1].MethodCount);
    }

    // ------------------------------------------------------------------------------- the command

    [Fact]
    public void The_command_reads_a_program_and_its_document_matches_its_schema()
    {
        using var temp = new TempDir("recon-vb6-prog");
        string binary = temp.Write("program.exe", SyntheticVb6.Program());

        var run = CliRun.Run("vb6", binary, "--check-schema");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("vb6 document matches schema", run.StandardOutput);

        // The document is the same one the schema describes: the objects are in it, at the top level.
        var run2 = CliRun.Run("vb6", binary, "--json");
        var document = JsonDocument.Parse(run2.StandardOutput).RootElement;
        Assert.Equal(2, document.GetProperty("objects").GetArrayLength());
        Assert.Equal("frmMain", document.GetProperty("objects")[0].GetProperty("name").GetString());
        Assert.Equal("vb6-pcode", document.GetProperty("project").GetProperty("isa").GetString());
    }

    [Fact]
    public void The_command_lists_the_objects_of_a_real_program()
    {
        string? path = InDirectory("RECON_VB6_WILD", "Basic Server.exe");
        if (path is null)
        {
            return;
        }

        var run = CliRun.Run("vb6", path);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Main", run.StandardOutput);
        Assert.Contains("Local", run.StandardOutput);
        Assert.Contains("form", run.StandardOutput);
        Assert.Contains("standard module", run.StandardOutput);
    }

    /// <summary>
    /// A file that is not a VB program is refused by what its entry point does, not by its name: a VB
    /// program's entry point pushes the address of its VB5! header, and a runtime DLL does not.
    /// </summary>
    [Fact]
    public void The_command_refuses_a_pe_that_is_not_a_visual_basic_program()
    {
        using var temp = new TempDir("recon-vb6-none");
        string binary = temp.Write("plain.exe", SyntheticPe.Build());

        var run = CliRun.Run("vb6", binary);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("not a Visual Basic 5/6 program", run.StandardError);
    }

    [Fact]
    public void The_command_without_a_file_is_a_usage_error()
    {
        var run = CliRun.Run("vb6");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("usage: recon vb6", run.StandardError);
    }

    private static Vb6Program? ReadProgram(byte[] image)
    {
        var loaded = Recon.Images.ImageLoader.LoadBytes(image, "program.exe");
        return loaded.Image?.Pe is null ? null : Vb6Program.Read(loaded.Image.Pe, image);
    }
}
