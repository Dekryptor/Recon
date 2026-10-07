using System.Diagnostics;
using System.Text.Json;
using Recon.Analysis;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The exception directory: the compiler's own table of function ranges, which PE keeps in `.pdata`.
/// It is the only thing in a binary that states where a function <em>ends</em> — a symbol table says
/// where one begins — so a stripped image that has the table does not have to guess every size.
///
/// The round this file holds turned up two things worth keeping in the tests themselves:
///
/// * the table has **two widths** — 8 bytes per entry on PE32, 12 on PE32+ — and reading the wrong one
///   puts a function boundary in the middle of another entry. The fixture is 32-bit and the real
///   binary below is 64-bit, so both are read;
/// * **the loader had never read any 64-bit data directory at all**: it read
///   <c>NumberOfRvaAndSizes</c> at the 32-bit offset, which in a PE32+ header is the high half of
///   <c>SizeOfHeapReserve</c> — zero — so every directory on every 64-bit image read as absent. That
///   is why there is a test here that counts a real 64-bit image's imports, and not only its `.pdata`.
/// </summary>
[Collection("cli")]
public class PdataTests
{
    private static bool Mingw64 => ToolDetection.Exists("x86_64-w64-mingw32-gcc");

    /// <summary>The source of the real pair, kept in the corpus beside its recipe's build script.</summary>
    private static string SourceFile => Path.Combine(TestPaths.CorpusSourceDirectory, "pdata-seh", "t.c");

    // ------------------------------------------------------------------ the fixture

    /// <summary>
    /// Every entry, its width and its two forms, read from a PE32 image whose table is eight bytes per
    /// entry. The third entry is not in code and the fourth states no end, so the reader has to keep
    /// both facts rather than smooth them over: interpreting the table is the analysis's job.
    /// </summary>
    [Fact]
    public void Reads_both_words_of_every_entry_and_neither_interprets_them()
    {
        var image = PeLoader.LoadBytes(SyntheticPe.Build(new SyntheticPeOptions { Pdata = true }), "pdata32.exe").Image!;

        Assert.Equal(4, image.RuntimeFunctions.Count);

        var entryPoint = image.RuntimeFunctions[0];
        Assert.Equal(SyntheticPe.EntryPointRva, entryPoint.BeginRva);
        Assert.Equal(0x1016u, entryPoint.EndRva);
        Assert.Equal(0x16u, entryPoint.Size);

        // PE32 entries are eight bytes: there is no third word to read, and none is invented — the
        // next word along is the next entry's begin, which is exactly how a reader that assumed the
        // 64-bit width would turn one entry into two wrong ones.
        Assert.Null(entryPoint.UnwindInfoRva);

        Assert.Equal(SyntheticPe.ThunkRva, image.RuntimeFunctions[1].BeginRva);
        Assert.Equal(0x20u, image.RuntimeFunctions[1].Size);

        // An entry pointing into `.rdata` is still an entry: the file states it, and the loader says
        // what the file states.
        Assert.Equal(SyntheticPe.DataARva, image.RuntimeFunctions[2].BeginRva);
        Assert.Equal(SyntheticPe.DataARva + 0x10, image.RuntimeFunctions[2].EndRva);

        // A zero end is "no extent stated", not "ends at zero" and not "ends where the next one starts".
        Assert.Equal(SyntheticPe.FuncBRva, image.RuntimeFunctions[3].BeginRva);
        Assert.Null(image.RuntimeFunctions[3].EndRva);
        Assert.Null(image.RuntimeFunctions[3].Size);
    }

    /// <summary>
    /// What the analysis does with them: an entry seeds a function, the extent becomes its size — and
    /// an entry that is not in a code section seeds nothing.
    /// </summary>
    [Fact]
    public void An_entry_seeds_a_function_and_its_extent_is_the_size()
    {
        byte[] bytes = SyntheticPe.Build(new SyntheticPeOptions { Pdata = true });
        var loaded = ImageLoader.LoadBytes(bytes, "pdata32.exe");
        var pe = PeLoader.LoadBytes(bytes, "pdata32.exe").Image!;
        var debug = CoffSymbols.Read(pe, bytes);

        var options = new AnalysisOptions { BuildXrefs = false, MinFunctionConfidence = "low" };
        var analysis = new InventoryAnalyzer(loaded.Image!, bytes, null, options).Analyze(debug, null);

        // The thunk: seeded by the import it jumps through, sized by the table. Nothing else states
        // its extent, which is what the table is for.
        var thunk = analysis.Functions.Single(f => f.Ranges[0].Rva == SyntheticPe.ThunkRva);
        Assert.True(thunk.ImportThunk is not null, "the fixture's thunk should still be an import thunk");
        Assert.Contains("pdata", thunk.FoundBy.Select(s => s.Value));
        Assert.Equal(0x20u, thunk.Ranges[0].Size);

        // func_c is covered by the table's *absence*: its size comes from its symbol, and a table that
        // says nothing about it changes nothing about it.
        Assert.DoesNotContain("pdata", analysis.Functions.Single(f => f.Ranges[0].Rva == SyntheticPe.FuncCRva).FoundBy.Select(s => s.Value));

        // The entry in `.rdata` is not a function's start and did not become one.
        Assert.DoesNotContain(analysis.Functions, f => f.Ranges[0].Rva == SyntheticPe.DataARva);

        // The entry with no stated end still names a function — func_b was already one, from its
        // symbol — and says nothing about how big it is: 25 bytes is what the fixture's symbol states,
        // where reading the zero as an end would have made it the extent (0x30).
        var funcB = analysis.Functions.Single(f => f.Ranges[0].Rva == SyntheticPe.FuncBRva);
        Assert.Contains("pdata", funcB.FoundBy.Select(s => s.Value));
        Assert.Equal(25u, funcB.Ranges[0].Size);
    }

    // ------------------------------------------------------------------ the real pair

    /// <summary>
    /// A real 64-bit image: a MinGW build of a five-line C file, whose `.pdata` is 96 entries. Every
    /// one of them is read, in the 12-byte form, with its unwind info pointing into `.xdata` — and the
    /// test reads the table itself, so what it compares against is the file and not the reader.
    /// </summary>
    [Fact]
    public void Reads_a_real_64_bit_exception_directory_entry_for_entry()
    {
        if (!Mingw64)
        {
            return;
        }

        using var temp = new TempDir("recon-pdata");
        string exe = Compile(temp, "-O2 -g -Wall");
        byte[] bytes = File.ReadAllBytes(exe);
        var image = PeLoader.Load(exe).Image!;

        var mine = ReadTable(bytes, out uint pdataRva, out uint pdataSize);
        Assert.True(mine.Count > 50, $"the table has only {mine.Count} entries");

        Assert.Equal(mine.Count, image.RuntimeFunctions.Count);
        for (int i = 0; i < mine.Count; i++)
        {
            Assert.Equal(mine[i].Begin, image.RuntimeFunctions[i].BeginRva);
            Assert.Equal(mine[i].End, image.RuntimeFunctions[i].EndRva);
            Assert.Equal(mine[i].Unwind, image.RuntimeFunctions[i].UnwindInfoRva);
        }

        // Every one of them is a range in the code section, and every one's unwind data is in .xdata.
        Assert.All(image.RuntimeFunctions, f => Assert.True(image.SectionContainingRva(f.BeginRva)?.IsCode == true, $"{f.BeginRva:x} is not in code"));
        Assert.All(image.RuntimeFunctions, f => Assert.Equal(".xdata", image.SectionContainingRva(f.UnwindInfoRva ?? 0)?.Name));

        // The directory the loader read is the one the file points at.
        var directory = image.DataDirectories.Single(d => d.Name == "exception");
        Assert.Equal(pdataRva, directory.Rva);
        Assert.Equal(pdataSize, directory.Size);

        // And the same reload reads the *other* directories of a 64-bit image, which is the bug this
        // round found on the way: before it, every one of them read as absent on every PE32+ image,
        // because the count was taken from the 32-bit offset. These are the directories this file has
        // — there is no debug directory, because a MinGW build keeps its DWARF in sections.
        Assert.Contains(image.DataDirectories, d => d.Name == "import" && d.Present);
        Assert.Contains(image.DataDirectories, d => d.Name == "iat" && d.Present);
        Assert.Contains(image.DataDirectories, d => d.Name == "basereloc" && d.Present);
        Assert.Contains(image.Imports, i => i.Dll.Contains("msvcrt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The round's acceptance: the same source, stripped of its debug information, where the exception
    /// directory is then the only thing that knows where the functions are. Measured on this machine:
    /// 96 functions before, one for every entry, every size estimated; 126 after — the entries plus the
    /// call targets nothing else could see — with the 96 extents stated as sizes, no start invented,
    /// and two functions still missed (leaf functions with neither unwind data nor a call to them).
    ///
    /// The unstripped build is the oracle, because DWARF there says where the truth is independently of
    /// the table: every start the stripped analysis reports is one DWARF also names, so nothing was
    /// invented to reach the number.
    /// </summary>
    [Fact]
    public void A_stripped_64_bit_image_gets_its_functions_and_their_sizes_from_the_table()
    {
        if (!Mingw64)
        {
            return;
        }

        using var temp = new TempDir("recon-pdata-pair");
        string oracleExe = Compile(temp, "-O2 -g -Wall");
        string strippedExe = Compile(temp, "-O2 -s -Wall");

        var oracle = Inventory(temp, oracleExe);
        var stripped = Inventory(temp, strippedExe);

        List<JsonElement> oracleFunctions = [.. oracle.GetProperty("functions").EnumerateArray()];
        List<JsonElement> strippedFunctions = [.. stripped.GetProperty("functions").EnumerateArray()];
        HashSet<uint> oracleStarts = [.. oracleFunctions.Select(Start)];
        HashSet<uint> strippedStarts = [.. strippedFunctions.Select(Start)];

        // The oracle build is read as it was before this round: DWARF names the functions and the
        // table agrees with it about every extent it states.
        Assert.True(oracleStarts.Count > 100, $"the oracle has only {oracleStarts.Count} functions");
        Assert.Equal(oracleStarts.Count, oracleFunctions.Count);

        // Every entry the file declares is a function, with the extent the file states as its size.
        var entries = ReadTable(File.ReadAllBytes(strippedExe), out _, out _);
        Assert.True(entries.Count > 50, $"the table has only {entries.Count} entries");
        var strippedByStart = strippedFunctions.ToDictionary(Start);
        foreach (var entry in entries)
        {
            Assert.True(strippedByStart.TryGetValue(entry.Begin, out var function), $"no function at {entry.Begin:x}, which the table declares");
            Assert.Contains("pdata", function.GetProperty("found_by").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal(entry.End, Start(function) + function.GetProperty("ranges")[0].GetProperty("size").GetUInt32());
        }

        // Nothing was invented: every start in the stripped inventory is a start DWARF names too.
        Assert.Empty(strippedStarts.Except(oracleStarts));

        // And almost all of the oracle is covered — the two that are not are leaf functions with
        // neither unwind data nor a call into them, which no source in this binary states.
        Assert.True(
            strippedStarts.Count >= oracleStarts.Count - 4,
            $"the stripped analysis found {strippedStarts.Count} of {oracleStarts.Count} functions");

        // The point of the extents: the stripped inventory no longer reports every function's size as
        // a guess. Measured: 30 unknown of 126, against 96 of 96 before the table was read.
        int unknown = stripped.GetProperty("statistics").GetProperty("functions_with_unknown_size").GetInt32();
        Assert.True(unknown <= 40, $"{unknown} functions still have an unknown size");
        Assert.True(unknown >= 1, "every size known would mean the sizes were not coming from the table");

        // The unstripped build is unchanged by any of this: DWARF still names the functions, and the
        // table adds starts nobody had missed.
        int named = oracleFunctions.Count(f => f.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            && !name.GetString()!.StartsWith("sub_", StringComparison.Ordinal));
        Assert.True(named >= oracleFunctions.Count - 2, $"only {named} of {oracleFunctions.Count} functions kept their name");
    }

    // ------------------------------------------------------------------ helpers

    private static string Compile(TempDir temp, string flags)
    {
        string exe = temp.PathOf(flags.Contains("-s", StringComparison.Ordinal) ? "t-stripped.exe" : "t.exe");
        var start = new ProcessStartInfo("x86_64-w64-mingw32-gcc")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(exe);
        start.ArgumentList.Add(SourceFile);

        using var process = Process.Start(start)!;
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        Assert.True(process.ExitCode == 0, $"gcc {flags}: {errors}");
        return exe;
    }

    /// <summary>Runs the CLI over a binary the way a user would, and returns the inventory document.</summary>
    private static JsonElement Inventory(TempDir temp, string exe)
    {
        string sha = PeImage.HashFile(exe);
        string project = temp.PathOf(Path.GetFileNameWithoutExtension(exe) + "-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.toml"), $"""
            schema_version = 1

            [project]
            name = "pdata"

            [target]
            format = "pe64"
            arch = "x64"

            [[input]]
            id = "main"
            role = "original"
            file = "{Path.GetFileName(exe)}"
            sha256 = "{sha}"
            """);
        // A project's inputs live in its `inputs` directory unless local.toml says otherwise.
        Directory.CreateDirectory(Path.Combine(project, "inputs"));
        File.Copy(exe, Path.Combine(project, "inputs", Path.GetFileName(exe)), overwrite: true);

        string output = temp.PathOf(Path.GetFileNameWithoutExtension(exe) + "-inventory.json");
        var run = CliRun.Run("inventory", "--project", project, "-o", output);
        Assert.True(run.ExitCode == 0, run.All);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        return document.RootElement.Clone();
    }

    private static uint Start(JsonElement function) => function.GetProperty("ranges")[0].GetProperty("rva").GetUInt32();

    private sealed record Entry(uint Begin, uint End, uint Unwind);

    /// <summary>
    /// Reads the exception directory out of the file's own bytes. This is deliberately not the
    /// loader's reader: what a test asserts has to come from somewhere the code under test cannot
    /// reach, or it asserts that the code agrees with itself.
    /// </summary>
    private static List<Entry> ReadTable(byte[] data, out uint directoryRva, out uint directorySize)
    {
        uint pe = BitConverter.ToUInt32(data, 0x3C);
        int optional = (int)pe + 24;
        bool plus = BitConverter.ToUInt16(data, optional) == 0x20B;
        int offset = optional + (plus ? 112 : 96);
        directoryRva = BitConverter.ToUInt32(data, offset + (3 * 8));
        directorySize = BitConverter.ToUInt32(data, offset + (3 * 8) + 4);

        int sectionCount = BitConverter.ToUInt16(data, (int)pe + 6);
        int sectionTable = optional + BitConverter.ToUInt16(data, (int)pe + 20);
        (uint Rva, uint Offset, uint Size) pdata = (0, 0, 0);
        for (int i = 0; i < sectionCount; i++)
        {
            int header = sectionTable + (i * 40);
            uint virtualSize = BitConverter.ToUInt32(data, header + 8);
            uint rva = BitConverter.ToUInt32(data, header + 12);
            if (rva == directoryRva)
            {
                pdata = (rva, BitConverter.ToUInt32(data, header + 20), virtualSize);
            }
        }

        Assert.True(pdata.Offset != 0, $"no section holds the exception directory at {directoryRva:x}");
        int width = plus ? 12 : 8;
        var entries = new List<Entry>();
        for (int i = 0; (i + 1) * width <= directorySize; i++)
        {
            int at = (int)pdata.Offset + (i * width);
            entries.Add(new Entry(
                BitConverter.ToUInt32(data, at),
                BitConverter.ToUInt32(data, at + 4),
                plus ? BitConverter.ToUInt32(data, at + 8) : 0));
        }

        return entries;
    }
}
