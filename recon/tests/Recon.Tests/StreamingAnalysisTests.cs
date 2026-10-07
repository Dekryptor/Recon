using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The analysis decodes a program a window at a time and keeps what it learned rather than what it
/// read: an 11 MB Visual Basic client is 4.4 million instructions, and holding them all was not
/// survivable. What that changed is invisible on a small binary — every test fixture fits in one
/// window — so these tests are the ones that cross a window boundary, and the one that asks whether
/// the answer is still the answer when nothing is retained.
/// </summary>
[Collection("cli")]
public class StreamingAnalysisTests
{
    /// <summary>
    /// How much code is decoded before the next window starts. The image below is deliberately
    /// larger than it, so the walk has to continue at the end of the last whole instruction.
    /// </summary>
    private const int WindowSize = 0x40000;

    [Fact]
    public void A_code_section_larger_than_one_window_is_decoded_whole()
    {
        // 0x90 is `nop`: one byte, one instruction, no operands, no branch. A section of them decodes
        // to exactly one instruction per byte, which makes the count an arithmetic fact rather than
        // an expectation about what the decoder does with real code.
        const int codeSize = (WindowSize * 2) + 0x1234;
        var run = Inventory(ProgramOfNops(codeSize));

        Assert.Equal(0, run.ExitCode);
        JsonElement statistics = JsonDocument.Parse(run.StandardOutput).RootElement.GetProperty("statistics");

        // Not one instruction short, and not one past the end of the section: three windows' worth,
        // continued at a boundary that falls in the middle of the second one.
        Assert.Equal(codeSize, statistics.GetProperty("instructions").GetInt32());
    }

    [Fact]
    public void An_instruction_straddling_a_window_boundary_is_counted_once()
    {
        // A section a few bytes longer than one window, of one-byte `nop`s, with a five-byte
        // `call rel32` placed so that it starts two bytes before the window ends. That instruction is
        // cut in half by the boundary: it has to be decoded again from its own start in the next
        // window, counted once, and the bytes after it still have to be walked.
        const int codeSize = WindowSize + 8;
        const int callAt = WindowSize - 2;
        int expected = callAt + 1 + (codeSize - callAt - 5);

        var run = Inventory(ProgramWithStraddlingCall(codeSize, callAt));

        Assert.Equal(0, run.ExitCode);
        JsonElement statistics = JsonDocument.Parse(run.StandardOutput).RootElement.GetProperty("statistics");
        Assert.Equal(expected, statistics.GetProperty("instructions").GetInt32());
    }

    /// <summary>
    /// A verbose run says where its time went, and the phases it names are the analysis's own.
    ///
    /// This is the feature that made the performance work possible rather than guesswork: the phases
    /// answer different questions and cost wildly different amounts — on an 11 MB program, one pass
    /// over every instruction is most of the total, and which pass it is depends on the input — so a
    /// slow run has to be able to say which phase it was slow in. The names are asserted rather than
    /// the times, because a time is a fact about the machine and a name is a fact about the tool.
    /// </summary>
    [Fact]
    public void A_verbose_run_reports_what_each_phase_of_the_analysis_cost()
    {
        using var temp = new TempDir("recon-phases");
        string inputs = Path.Combine(temp.Path, "inputs");
        Directory.CreateDirectory(inputs);
        string binary = Path.Combine(inputs, "small.exe");
        File.WriteAllBytes(binary, ProgramOfNops(0x4000));

        Assert.Equal(0, CliRun.Run("init", temp.Path, "--binary", binary, "--json").ExitCode);
        var verbose = CliRun.Run("inventory", "--project", temp.Path, "--verbose");
        Assert.Equal(0, verbose.ExitCode);

        foreach (string phase in new[] { "scan", "prologues", "functions", "conventions", "xrefs" })
        {
            Assert.Contains(phase, verbose.StandardError);
        }

        // The jump tables are not a phase beside the scan but a **share of it**: one pass over every
        // instruction now does what three did, and the report says so by naming the table work
        // underneath the pass instead of beside it. A run that reported `jump_tables` at the top level
        // again would be claiming a pass that does not exist.
        Assert.Contains("scan/jump_tables", verbose.StandardError);
        // (A top-level line would be `debug:   jump_tables` — the share is printed two spaces in.)
        Assert.DoesNotContain("debug:   jump_tables", verbose.StandardError);

        Assert.Contains("ms", verbose.StandardError);

        // And a run that did not ask stays quiet: the phases are diagnostics, not output.
        var quiet = CliRun.Run("inventory", "--project", temp.Path);
        Assert.DoesNotContain("scan", quiet.StandardError);
    }

    /// <summary>
    /// A listing reads the inventory that is already on disk when it is this build's reading of this
    /// binary — and says exactly what a fresh analysis says.
    ///
    /// This is the interactive path. Building the whole inventory to disassemble four instructions cost
    /// 18 seconds on an 11.8 MB client, and that is the loop a reversing session lives in: build once,
    /// then hundreds of listings. Reading back the three things a listing needs costs half a second. The
    /// equivalence is the point: the fast path is only allowed to be a different *route* to the same
    /// document, so both routes are run here and their output compared byte for byte.
    /// </summary>
    [Fact]
    public void A_listing_reads_the_inventory_on_disk_and_says_what_a_fresh_analysis_says()
    {
        using var temp = new TempDir("recon-cache");
        string inputs = Path.Combine(temp.Path, "inputs");
        Directory.CreateDirectory(inputs);
        string binary = Path.Combine(inputs, "cached.exe");
        File.WriteAllBytes(binary, ProgramWithStraddlingCall(0x2000, 0x100));

        Assert.Equal(0, CliRun.Run("init", temp.Path, "--binary", binary, "--json").ExitCode);
        Assert.Equal(0, CliRun.Run("inventory", "--project", temp.Path).ExitCode);

        var cached = CliRun.Run("disasm", "0x1000", "--project", temp.Path, "--json", "--count", "8");
        Assert.Equal(0, cached.ExitCode);

        var verbose = CliRun.Run("disasm", "0x1000", "--project", temp.Path, "--verbose");
        Assert.Contains("listed from", verbose.StandardError);

        // And with the document gone, the same listing comes out of an analysis run from the bytes.
        string document = Path.Combine(temp.Path, "build", "inventory.json");
        Assert.True(File.Exists(document));
        File.Delete(document);

        var fresh = CliRun.Run("disasm", "0x1000", "--project", temp.Path, "--json", "--count", "8");
        Assert.Equal(0, fresh.ExitCode);
        Assert.Equal(fresh.StandardOutput, cached.StandardOutput);

        // A document older than the binary it describes is not this binary's, so it is not used: the
        // dates rather than the contents are what a listing can check cheaply, and the hash is checked
        // besides — an inventory of some other build is the plausible-looking wrong answer.
        Assert.Equal(0, CliRun.Run("inventory", "--project", temp.Path).ExitCode);
        File.SetLastWriteTimeUtc(binary, DateTime.UtcNow.AddHours(1));

        var stale = CliRun.Run("disasm", "0x1000", "--project", temp.Path, "--verbose");
        Assert.DoesNotContain("listed from", stale.StandardError);
    }

    private static CliRun Inventory(byte[] image)
    {
        using var temp = new TempDir("recon-stream");
        string inputs = Path.Combine(temp.Path, "inputs");
        Directory.CreateDirectory(inputs);
        string binary = Path.Combine(inputs, "big.exe");
        File.WriteAllBytes(binary, image);

        var init = CliRun.Run("init", temp.Path, "--binary", binary, "--json");
        Assert.Equal(0, init.ExitCode);
        return CliRun.Run("inventory", "--project", temp.Path, "--json", "--check-schema");
    }

    /// <summary>
    /// A one-section PE32 whose whole code section is the given bytes. Nothing else is needed: no
    /// imports, no exports, no symbols — the inventory reports on the code it can read.
    /// </summary>
    private static byte[] Image(byte[] code)
    {
        const int headersSize = 0x400;
        var image = new byte[headersSize + code.Length];

        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0x40);

        int pe = 0x40;
        "PE\0\0"u8.CopyTo(image.AsSpan(pe, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 4), 0x014C);   // i386
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 6), 1);        // one section
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 20), 0xE0);    // PE32 optional header
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 22), 0x0102);  // executable, 32-bit

        int optional = pe + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), 0x1000);       // entry point
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 28), 0x400000);     // image base
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 32), 0x1000);       // section alignment
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), 0x200);        // file alignment
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional + 68), 2);            // GUI
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 56), (uint)(0x2000 + code.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), (uint)headersSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 92), 16);           // data directories

        int section = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0").CopyTo(image, section);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), (uint)code.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), (uint)code.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), (uint)headersSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 36), 0x60000020);    // code, execute, read

        code.CopyTo(image, headersSize);
        return image;
    }

    private static byte[] ProgramOfNops(int size)
    {
        var code = new byte[size];
        code.AsSpan().Fill(0x90);
        return Image(code);
    }

    /// <summary>
    /// A code section of <paramref name="size"/> one-byte <c>nop</c>s with one five-byte
    /// <c>call rel32</c> at <paramref name="callAt"/>, aimed at the entry point so that the program
    /// has one call target rather than one per instruction.
    /// </summary>
    private static byte[] ProgramWithStraddlingCall(int size, int callAt)
    {
        var code = new byte[size];
        code.AsSpan().Fill(0x90);

        // rel32 is relative to the end of the instruction, and 0x1000 is the entry point: one target,
        // so the program has one function to find instead of thousands.
        int nextRva = 0x1000 + callAt + 5;
        code[callAt] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(callAt + 1), 0x1000 - nextRva);
        return Image(code);
    }
}
