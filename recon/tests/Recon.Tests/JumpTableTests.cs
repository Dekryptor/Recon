using Recon.Analysis;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// A table of addresses is not a statement about where functions begin, and it must not be allowed to
/// contradict one that is.
///
/// The rule this class holds was written for a good reason: a switch's jump table points at case labels
/// *inside* the function that owns the table, so a seed at such an address is not a function entry.
/// What it did not allow for is a table whose entries are the starts of functions — a vtable, an import
/// stub table, a CLR jump-stub table — which is exactly what a real MSVC binary is full of. Measured on
/// Microsoft's own `coreclr.dll`: 320 detected tables, and the unconditional rule deleted **2,080
/// functions the PDB named**, every folded three-byte stub a dispatch table under `.rdata` pointed at.
/// </summary>
public class JumpTableTests
{
    private static (IBinaryImage Image, byte[] Bytes, PeImage Pe) Load(bool dispatch)
    {
        byte[] bytes = SyntheticPe.Build(new SyntheticPeOptions { DispatchTable = dispatch });
        var loaded = ImageLoader.LoadBytes(bytes, "dispatch.exe");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Problems));
        return (loaded.Image!, bytes, PeLoader.LoadBytes(bytes, "dispatch.exe").Image!);
    }

    private static AnalysisResult Analyze(bool dispatch)
    {
        var (image, bytes, pe) = Load(dispatch);
        var options = new AnalysisOptions { BuildXrefs = false, MinFunctionConfidence = "low" };
        return new InventoryAnalyzer(image, bytes, null, options).Analyze(CoffSymbols.Read(pe, bytes), null);
    }

    /// <summary>
    /// The table's entries are the starts of three functions the symbol table also names, and all three
    /// survive. Two of them (`func_a`, `func_c`) have no other reference pointing at them in this
    /// fixture, so before the fix they were not in the inventory at all.
    /// </summary>
    [Fact]
    public void A_table_pointing_at_a_functions_start_does_not_delete_the_function()
    {
        var analysis = Analyze(dispatch: true);

        foreach (uint rva in new[] { SyntheticPe.FuncARva, SyntheticPe.FuncCRva, SyntheticPe.DispatchRva })
        {
            Assert.True(
                analysis.Functions.Any(f => f.Ranges[0].Rva == rva),
                $"the table names 0x{rva:X} and the symbol table names it, but it is not a function");
        }

        // The function that jumps through the table is a function whether or not anything calls it.
        var dispatch = analysis.Functions.Single(f => f.Ranges[0].Rva == SyntheticPe.DispatchRva);
        Assert.Contains("coff", dispatch.FoundBy.Select(s => s.Value));

        // And the table was found: the fixture's purpose is the conflict, not an undetected table.
        Assert.Contains(analysis.JumpTables, table => table.Targets.Contains(SyntheticPe.FuncCRva));

        // The control: without the option the same addresses are the case labels of func_b's switch,
        // which is the case the rule exists for — an interior address the symbol table does *not* name
        // must still not become a function of its own.
        var plain = Analyze(dispatch: false);
        Assert.DoesNotContain(plain.Functions, f => f.Ranges[0].Rva == SyntheticPe.JumpTargets[0]);
        Assert.All(plain.JumpTables.SelectMany(t => t.Targets), target =>
            Assert.DoesNotContain(plain.Functions, f => f.Ranges[0].Rva == target));
    }
}
