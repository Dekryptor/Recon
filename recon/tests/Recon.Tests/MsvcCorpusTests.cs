using Recon.Config;
using Recon.Inventory;
using Recon.Project;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The MSVC-ABI corpus: a real PE32 built by clang with CodeView debug info and a linker PDB, the
/// closest thing to a Visual C++ binary that can be produced on Linux.
///
/// Every expectation below is ground truth taken from the PDB (llvm-pdbutil dump -symbols):
/// address, code size and name of each procedure, which public symbols are data, and what the
/// decorated publics say the calling conventions are. The corpus is generated, so these tests are
/// skipped rather than failed when it is missing: tools/build-msvc-corpus.sh produces it.
/// </summary>
public class MsvcCorpusTests
{
    private static bool Available => MsvcCorpus.Available("sample");

    /// <summary>(name, RVA, size) for every S_GPROC32 the PDB holds.</summary>
    public static readonly (string Name, uint Rva, uint Size)[] Functions =
    [
        ("add", 0x1000, 9),
        ("mul_std", 0x1010, 12),
        ("sub_fast", 0x1020, 5),
        ("dispatch", 0x1030, 94),
        ("fib", 0x1090, 63),
        ("loop_sum", 0x10D0, 132),
        ("mainCRTStartup", 0x1160, 146),
    ];

    private static InventoryDocument Inventory()
    {
        var (document, _) = MsvcCorpus.Build("sample");
        return document;
    }

    [Fact]
    public void Every_pdb_procedure_appears_at_its_address_with_its_size()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        foreach (var (name, rva, size) in Functions)
        {
            var function = Assert.Single(document.Functions, f => f.Ranges[0].Rva == rva);
            Assert.Equal(name, function.Name);
            Assert.Equal(size, function.Ranges[0].Size);
            Assert.Equal("high", function.Confidence);
        }
    }

    [Fact]
    public void Function_publics_are_in_the_function_list_and_data_publics_are_not()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        // Publics from the PDB: the procedures above, plus three data symbols at 0x3000/0x3004/0x3044.
        foreach (var (name, rva, _) in Functions)
        {
            Assert.Contains(document.Functions, f => f.Name == name && f.Ranges[0].Rva == rva);
        }

        Assert.DoesNotContain(document.Functions, f => f.Ranges[0].Rva is >= 0x3000 and < 0x3100);
        Assert.Contains(document.Data, d => d.Rva == 0x3000 && d.Name == "_g_message_id" && d.Source == "pdb");
        Assert.Contains(document.Data, d => d.Rva == 0x3004 && d.Name == "_g_table" && d.Source == "pdb");
        Assert.Contains(document.Data, d => d.Rva == 0x3044 && d.Name == "_g_counter" && d.Source == "pdb");
    }

    /// <summary>
    /// The corpus deliberately mixes the three conventions: a plain C function, a <c>__stdcall</c>
    /// one and a <c>__fastcall</c> one. Each has to be reported from the decorated name, which is
    /// the only evidence there is.
    /// </summary>
    [Fact]
    public void Calling_conventions_come_from_the_decorated_names()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        var add = document.Functions.Single(f => f.Name == "add");
        Assert.Equal("cdecl", add.CallingConvention.Value);
        Assert.Contains(add.CallingConvention.Evidence, e => e.StartsWith("mangling:", StringComparison.Ordinal));

        var mul = document.Functions.Single(f => f.Name == "mul_std");
        Assert.Equal("stdcall", mul.CallingConvention.Value);

        var sub = document.Functions.Single(f => f.Name == "sub_fast");
        Assert.Equal("fastcall", sub.CallingConvention.Value);
    }

    [Fact]
    public void The_jump_table_is_found_and_attributed_to_its_function()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        var table = Assert.Single(document.JumpTables);
        Assert.Equal(0x2000u, table.Rva);
        Assert.Equal(11, table.Entries);
        Assert.Equal(document.Functions.Single(f => f.Name == "dispatch").Id, table.Owner);

        // Every entry points into the switch body, and none of them was taken for a function start.
        Assert.All(table.Targets, target => Assert.InRange(target, 0x1030u, 0x108Eu));
        Assert.DoesNotContain(document.Functions, f => table.Targets.Skip(1).Contains(f.Ranges[0].Rva));
    }

    [Fact]
    public void The_producer_is_reported_as_the_clang_msvc_toolchain()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        Assert.Equal("pdb", document.Binary.Debug.Kind);
        Assert.True(document.Binary.Debug.Symbols >= Functions.Length);
        // The compiler version comes from the PDB's compiland records; a PDB is the only place that
        // says which cl shows up in this binary, and the detection turns it into a profile.
        var compiland = Assert.Single(document.Binary.Producers, p => p.Kind == "pdb_compiland" && (p.Detail ?? string.Empty).Contains("clang", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("clang-19-msvc", compiland.Matches);
    }

    [Fact]
    public void The_calling_convention_of_every_function_is_decided_or_reported_as_unknown()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        Assert.All(document.Functions, function =>
        {
            Assert.Contains(function.CallingConvention.Value, (string[])["cdecl", "stdcall", "fastcall", "thiscall", "unknown"]);
            if (function.CallingConvention.Value != "unknown")
            {
                Assert.NotEmpty(function.CallingConvention.Evidence);
            }
        });
    }

    [Fact]
    public void Relocations_are_linked_to_the_instructions_that_use_them()
    {
        if (!Available)
        {
            return;
        }

        var document = Inventory();

        Assert.NotEmpty(document.Relocations);
        Assert.All(document.Relocations, r =>
        {
            Assert.Equal("HIGHLOW", r.Kind);
            Assert.NotNull(r.TargetRva);
        });

        // `dispatch` calls through the IAT, so at least one relocation must sit inside a function.
        Assert.Contains(document.Relocations, r => r.InFunction is not null);
    }
}
