using Recon.DebugInfo;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// PDB reading is exercised against the synthetic MSF fixture instead of a Microsoft PDB, because
/// no MSVC toolchain exists on this machine. The fixture follows the published container and record
/// layouts, so an offset error in the reader fails here.
/// </summary>
public class PdbTests
{
    private static DebugInfoResult Read(bool lldLengths = false)
        => PdbReader.ReadBytes(SyntheticPdb.Build(lldLengths), SyntheticPdb.SectionRvas, "fixture.pdb");

    /// <summary>
    /// Record lengths are read under both conventions: link.exe writes the padded record size, lld
    /// writes that size minus two (an S_END record declares 2). Getting this wrong once silently
    /// dropped every function after the first S_END in a real lld-produced PDB.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_the_same_symbols_under_both_length_conventions(bool lldLengths)
    {
        var result = Read(lldLengths);

        Assert.Empty(result.Problems);
        Assert.Equal(7, result.Symbols.Count);
        Assert.Equal(2, result.Symbols.Count(s => s.Size is not null));
        Assert.Single(result.Symbols, s => s.Name == "thunk_alias");
        Assert.Single(result.Symbols, s => s.Name == "local_data" && s.IsData);
    }

    [Fact]
    public void Reads_the_info_stream()
    {
        var result = Read();

        Assert.Equal("pdb", result.Kind);
        Assert.Equal(SyntheticPdb.Age, result.Age);
        Assert.Equal(SyntheticPdb.Guid, result.Guid);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Reads_public_symbols()
    {
        var result = Read();

        // Publics carry the name and address only; the same function also arrives as a procedure
        // symbol from its module stream, which is where the size comes from.
        var funcA = Assert.Single(result.Symbols, s => s.Name == "func_a" && s.Size is null);
        Assert.Equal(SyntheticPdb.FuncARva, funcA.Rva);
        Assert.Equal(SymbolSource.Pdb, funcA.Source);
        Assert.False(funcA.IsData);

        // A public whose flags do not mark it as a function is data.
        Assert.True(Assert.Single(result.Symbols, s => s.Name == "data_global").IsData);
    }

    [Fact]
    public void Reads_procedure_symbols_with_sizes_and_their_compiland()
    {
        var result = Read();

        var funcB = Assert.Single(result.Symbols, s => s.Name == "func_b" && s.Size is not null);
        Assert.Equal(SyntheticPdb.FuncBRva, funcB.Rva);
        Assert.Equal(SyntheticPdb.FuncBSize, funcB.Size);
        Assert.Equal("fixture.obj", funcB.Unit);
    }

    [Fact]
    public void Reads_the_compiland_and_its_producer()
    {
        var result = Read();

        var compiland = Assert.Single(result.Compilands);
        Assert.Equal("fixture.obj", compiland.Unit);
        Assert.Equal(@"C:\build\fixture.obj", compiland.ObjectFile);
        Assert.Equal(SyntheticPdb.Producer, compiland.Producer);
    }

    [Fact]
    public void Keeps_thunks_as_aliases_without_a_body()
    {
        var result = Read();

        var thunk = Assert.Single(result.Symbols, s => s.Name == "thunk_alias");
        Assert.Equal(SyntheticPdb.FuncBRva, thunk.Rva);
        Assert.Null(thunk.Size);
    }

    [Fact]
    public void Maps_segments_through_the_section_bases()
    {
        // Segment 2 is the second section; the symbol offset is relative to its base.
        byte[] bytes = SyntheticPdb.Build();
        var result = PdbReader.ReadBytes(bytes, SyntheticPdb.SectionRvas, "fixture.pdb");

        Assert.Equal(0x2000u, SyntheticPdb.SectionRvas[1]);
        Assert.NotEmpty(result.Symbols);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_msf()
    {
        var result = PdbReader.ReadBytes("not a pdb at all"u8.ToArray(), [], "x.pdb");

        Assert.Equal("pdb", result.Kind);
        Assert.Contains(result.Problems, p => p.Contains("MSF"));
        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void Refuses_a_truncated_directory_instead_of_crashing()
    {
        byte[] bytes = SyntheticPdb.Build();
        var truncated = bytes[..(SyntheticPdb.PageSize * 2)];

        var result = PdbReader.ReadBytes(truncated, SyntheticPdb.SectionRvas, "truncated.pdb");

        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void A_missing_file_is_a_problem_not_an_exception()
    {
        var result = PdbReader.Read("/nonexistent/missing.pdb", SyntheticPdb.SectionRvas);

        Assert.Contains(result.Problems, p => p.Contains("cannot read PDB"));
    }
}
