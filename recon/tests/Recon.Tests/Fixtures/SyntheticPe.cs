using System.Buffers.Binary;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>Switches for the synthetic fixture, so one builder can cover many test cases.</summary>
public sealed class SyntheticPeOptions
{
    /// <summary>Write a Rich header with a fake VS2008 toolset.</summary>
    public bool RichHeader { get; init; } = true;

    /// <summary>Build number of the (always last) linker entry.</summary>
    public ushort LinkerBuild { get; init; } = 21022; // Visual C++ 2008 RTM

    /// <summary>
    /// Write the Visual Studio 97 toolset's bill of materials instead of the 2008 one: product id
    /// 0x0013, the linker of the Visual C++ 5.x line, with the masm and resource-compiler records
    /// that sit either side of it in a real file. Those three ids and their builds were read off
    /// VBA6.DLL with `recon inspect producers` — see the <c>msvc-5</c> profile — and the point of
    /// writing them here is the last one: in that file the final record is the resource converter,
    /// so a parser that reads the last entry as the linker gets this image wrong.
    /// </summary>
    public bool RichVs97Toolset { get; init; }

    /// <summary>
    /// <c>MajorLinkerVersion.MinorLinkerVersion</c> in the optional header: 9.0, the Visual C++ 2008
    /// linker, because that is what the rest of this file pretends to be. Set it to 6.0 for a
    /// Visual Studio 98-era image — the linker Visual Basic 6 shipped with — so that the linker stamp
    /// does not claim a toolset the imports contradict. Only the major and minor are set, never a
    /// build number: those are read from a real file, not invented here.
    /// </summary>
    public byte LinkerMajor { get; init; } = 9;

    public byte LinkerMinor { get; init; }

    public bool CoffSymbols { get; init; } = true;

    /// <summary>Add a second symbol for one function's address: identical-code folding.</summary>
    public bool FoldedAlias { get; init; }

    public bool Imports { get; init; } = true;

    public bool Exports { get; init; } = true;

    public bool JumpTable { get; init; } = true;

    public bool BaseRelocations { get; init; } = true;

    /// <summary>
    /// A dispatch table in `.rdata` whose entries are the *starts* of named functions, and a function
    /// that jumps through it. What a vtable, an import stub table or a CLR jump-stub table looks like,
    /// and the shape that used to make the analysis delete the functions it pointed at: the three named
    /// functions this table names must still be functions. See `PdataTests`/`JumpTableTests`.
    /// </summary>
    public bool DispatchTable { get; init; }

    /// <summary>MSVC-style exception data instead of MinGW-style unwinding.</summary>
    public bool Pdata { get; init; }

    /// <summary>MSVC-style runtime imports, so the MSVC profiles have DLL evidence too.</summary>
    public bool MsvcRuntime { get; init; }

    /// <summary>
    /// Import the Visual Basic 6 virtual machine instead: the one thing a VB6 program always carries
    /// and nothing else ever imports, which is the whole evidence case for the VB6 profile. The
    /// functions are real MSVBVM60 exports, so the names in an inventory of this file are the names
    /// a VB6 binary would show.
    /// </summary>
    public bool Vb6Runtime { get; init; }

    /// <summary>Adds a <c>.comment</c> section holding this text, the way a Microsoft linker does.</summary>
    public string? Comment { get; init; }

    /// <summary>Adds a CodeView debug directory entry pointing at a PDB path.</summary>
    public bool PdbReference { get; init; }

    /// <summary>
    /// A second copy of <c>func_a</c>'s code, at another address and under another name: what a
    /// build looks like when a function was dead-stripped from the source but its body lives on
    /// under a different name. The comparison has to report that as folding, not as a missing
    /// function.
    /// </summary>
    public bool DuplicateFuncA { get; init; }

    /// <summary>
    /// Rewrites <c>func_a</c>'s first instruction: same length, different opcode. The function is
    /// then present on both sides and not equal, which is the case the diff viewer exists for.
    /// </summary>
    public bool MutateFuncA { get; init; }

    /// <summary>
    /// Writes the string literal this many bytes later in `.rdata` than it usually goes, and leaves
    /// everything else where it was: the same program, with one thing the linker is free to put
    /// somewhere else. What references it moves with it, because the fixture writes that reference
    /// from <see cref="SyntheticPe.StringRva"/>, so two images made this way differ in an address and
    /// in nothing else — which is what a comparison has to be able to see past.
    /// </summary>
    public uint StringShift { get; init; }
}

/// <summary>
/// Builds a complete, deterministic PE32 image in memory. Real corpora are the better test when a
/// toolchain is present, but they cannot be checked in, they differ between compiler versions and
/// they never contain MSVC artefacts on a Linux CI box. This fixture covers every structure the
/// loader and the analyser care about: headers, sections, imports, exports, base relocations, a
/// jump table inside code, an import thunk, IA-32 code with each calling convention, 0xCC padding,
/// a COFF symbol table with file symbols and long names, a folded (aliased) symbol and a Rich
/// header whose final entry is the linker.
/// </summary>
public static class SyntheticPe
{
    public const uint ImageBase = 0x400000;
    public const uint EntryPointRva = 0x1000;
    public const uint FuncARva = 0x1020;
    public const uint FuncBRva = 0x1030;
    public const uint FuncCRva = 0x1050;
    public const uint ThunkRva = 0x1070;

    public const uint FuncACopyRva = 0x10A0;
    public const uint JumpTableRva = 0x2008;

    /// <summary>Where <see cref="SyntheticPeOptions.DispatchTable"/> puts its table, and the function that uses it.</summary>
    public const uint DispatchTableRva = 0x2180;
    public const uint DispatchRva = 0x10C0;
    public const uint DataARva = 0x2000;
    public const uint StringRva = 0x2014;
    public const uint IatRva = 0x2030;
    public const uint GlobalRva = 0x3000;
    public const uint PdataRva = 0x4000;

    /// <summary>Bytes per exception-directory entry on PE32 — the width this fixture's table uses.</summary>
    public const int PdataEntrySize = 8;
    public const uint RelocRva = 0x5000;
    public const uint CommentRva = 0x6000;
    public const string CommentText = "Microsoft (R) Optimized Compiler x86";
    public const string PdbPath = @"C:\build\fixture.pdb";
    public const uint PdbAge = 7;
    public static readonly uint[] JumpTargets = [0x1038, 0x103f, 0x1046];

    private const int TextFile = 0x400;
    private const int RdataFile = 0x800;
    private const int DataFile = 0xC00;
    private const int PdataFile = 0xE00;
    private const int RelocFile = 0x1000;
    private const int SymbolTableFile = 0x1200;
    private const int CommentFile = 0x1400;

    public static byte[] Build(SyntheticPeOptions? options = null)
    {
        var o = options ?? new SyntheticPeOptions();
        var buffer = new byte[0x1900];

        // ---------------------------------------------------------------- headers
        Put16(buffer, 0, 0x5A4D);            // "MZ"
        Put32(buffer, 0x3C, 0x100);          // e_lfanew

        if (o.RichHeader)
        {
            WriteRichHeader(buffer, 0x40, o.LinkerBuild, o.MsvcRuntime, o.RichVs97Toolset);
        }

        Put32(buffer, 0x100, 0x00004550);    // "PE\0\0"
        Put16(buffer, 0x104, 0x014C);        // IMAGE_FILE_MACHINE_I386
        var sectionNames = new List<string> { ".text", ".rdata", ".data" };
        if (o.Pdata)
        {
            sectionNames.Add(".pdata");
        }

        sectionNames.Add(".reloc");
        if (o.Comment is not null)
        {
            sectionNames.Add(".comment");
        }

        Put16(buffer, 0x106, (ushort)sectionNames.Count);
        Put32(buffer, 0x108, 0x5F000000);    // TimeDateStamp
        Put32(buffer, 0x10C, (uint)(o.CoffSymbols ? SymbolTableFile : 0));
        Put32(buffer, 0x110, 0);
        Put16(buffer, 0x114, 0xE0);          // SizeOfOptionalHeader
        Put16(buffer, 0x116, 0x010F);        // executable | 32bit | line numbers stripped

        const int optional = 0x118;
        Put16(buffer, optional, 0x010B);     // PE32
        buffer[optional + 2] = o.LinkerMajor; // MajorLinkerVersion
        buffer[optional + 3] = o.LinkerMinor; // MinorLinkerVersion
        Put32(buffer, optional + 4, 0x600);  // SizeOfCode
        Put32(buffer, optional + 8, 0x800);  // SizeOfInitializedData
        Put32(buffer, optional + 12, 0x100); // SizeOfUninitializedData
        Put32(buffer, optional + 16, EntryPointRva);
        Put32(buffer, optional + 20, 0x1000); // BaseOfCode
        Put32(buffer, optional + 24, 0x2000); // BaseOfData
        Put32(buffer, optional + 28, ImageBase);
        Put32(buffer, optional + 32, 0x1000); // SectionAlignment
        Put32(buffer, optional + 36, 0x200);  // FileAlignment
        Put16(buffer, optional + 40, 4);
        Put16(buffer, optional + 44, 0);
        Put16(buffer, optional + 48, 4);
        Put32(buffer, optional + 56, (uint)(o.Comment is null ? 0x6000 : 0x7000));
        Put32(buffer, optional + 60, 0x400);  // SizeOfHeaders
        Put16(buffer, optional + 68, 3);      // Subsystem: console
        Put16(buffer, optional + 70, 0x8140); // DllCharacteristics: NX, no SEH, terminal server aware
        Put32(buffer, optional + 72, 0x100000);
        Put32(buffer, optional + 76, 0x1000);
        Put32(buffer, optional + 80, 0x100000);
        Put32(buffer, optional + 84, 0x1000);
        Put32(buffer, optional + 92, 16);     // NumberOfRvaAndSizes

        const int directories = optional + 96;
        if (o.Exports)
        {
            Put32(buffer, directories + 0, 0x20A0); // export directory
            Put32(buffer, directories + 4, 0x28);
        }

        if (o.Imports)
        {
            Put32(buffer, directories + 8, 0x2064); // import directory
            Put32(buffer, directories + 12, 0x14);
        }

        if (o.Pdata)
        {
            Put32(buffer, directories + 24, PdataRva); // exception directory
            Put32(buffer, directories + 28, 4 * PdataEntrySize);
        }

        if (o.BaseRelocations)
        {
            Put32(buffer, directories + 40, RelocRva);
            Put32(buffer, directories + 44, 0x2C);
        }

        if (o.PdbReference)
        {
            Put32(buffer, directories + 48, 0x2140); // debug directory
            Put32(buffer, directories + 52, 28);
        }

        // ---------------------------------------------------------------- sections
        int sectionTable = optional + 0xE0;
        int index = 0;
        WriteSection(buffer, sectionTable + (index++ * 40), ".text", 0x300, 0x1000, 0x400, TextFile, 0x60000020);
        WriteSection(buffer, sectionTable + (index++ * 40), ".rdata", 0x300, 0x2000, 0x400, RdataFile, 0x40000040);
        WriteSection(buffer, sectionTable + (index++ * 40), ".data", 0x100, 0x3000, 0x200, DataFile, 0xC0000040);
        if (o.Pdata)
        {
            WriteSection(buffer, sectionTable + (index++ * 40), ".pdata", 0x100, PdataRva, 0x200, PdataFile, 0x40000040);
        }

        WriteSection(buffer, sectionTable + (index++ * 40), ".reloc", 0x100, RelocRva, 0x200, RelocFile, 0x42000040);
        if (o.Comment is not null)
        {
            WriteSection(buffer, sectionTable + (index++ * 40), ".comment", 0x200, CommentRva, 0x200, CommentFile, 0x42100040);
            Encoding.ASCII.GetBytes(o.Comment + "\0").CopyTo(buffer, CommentFile);
        }

        // ---------------------------------------------------------------- code
        // Entry point: two direct calls, one call through an import thunk, then return.
        buffer[TextFile + 0x000] = 0x55;                       // push ebp
        Put16(buffer, TextFile + 0x001, 0xEC8B);               // mov ebp, esp
        WriteCall(buffer, TextFile + 0x003, 0x1000, FuncARva);
        WriteCall(buffer, TextFile + 0x008, 0x1000, FuncCRva);
        WriteCall(buffer, TextFile + 0x00D, 0x1000, ThunkRva);
        Put16(buffer, TextFile + 0x012, 0xC033);               // xor eax, eax
        buffer[TextFile + 0x014] = 0x5D;                       // pop ebp
        buffer[TextFile + 0x015] = 0xC3;                       // ret

        // func_a: reads a global through an absolute address, so it needs a relocation.
        buffer[TextFile + 0x020] = 0xA1;                       // mov eax, [0x402000]
        Put32(buffer, TextFile + 0x021, ImageBase + DataARva);
        buffer[TextFile + 0x025] = 0xC3;

        if (o.MutateFuncA)
        {
            // mov eax, imm32 instead of a load: one function that exists on both sides and differs.
            buffer[TextFile + 0x020] = 0xB8;
        }

        // func_b: a switch compiled to a jump table, including the case bodies it jumps to.
        buffer[TextFile + 0x030] = 0xFF;                       // jmp dword ptr [ecx*4 + table]
        buffer[TextFile + 0x031] = 0x24;
        buffer[TextFile + 0x032] = 0x8D;
        Put32(buffer, TextFile + 0x033, ImageBase + JumpTableRva);
        buffer[TextFile + 0x037] = 0x90;
        buffer[TextFile + 0x038] = 0xB8;                       // mov eax, 1
        Put32(buffer, TextFile + 0x039, 1);
        buffer[TextFile + 0x03D] = 0xC3;
        buffer[TextFile + 0x03E] = 0x90;
        buffer[TextFile + 0x03F] = 0xB8;                       // mov eax, 2
        Put32(buffer, TextFile + 0x040, 2);
        buffer[TextFile + 0x044] = 0xC3;
        buffer[TextFile + 0x045] = 0x90;
        Put16(buffer, TextFile + 0x046, 0xC033);               // xor eax, eax
        buffer[TextFile + 0x048] = 0xC3;

        // func_c: pushes a string address (relocation) and calls through the IAT.
        buffer[TextFile + 0x050] = 0x55;
        Put16(buffer, TextFile + 0x051, 0xEC8B);
        buffer[TextFile + 0x053] = 0x68;                       // push 0x402014
        Put32(buffer, TextFile + 0x054, ImageBase + StringRva + o.StringShift);
        buffer[TextFile + 0x058] = 0xFF;                       // call dword ptr [0x402030]
        buffer[TextFile + 0x059] = 0x15;
        Put32(buffer, TextFile + 0x05A, ImageBase + IatRva);
        buffer[TextFile + 0x05E] = 0x5D;
        buffer[TextFile + 0x05F] = 0xC3;

        // The thunk that the entry point calls: a single jump through the second IAT slot.
        buffer[TextFile + 0x070] = 0xFF;                       // jmp dword ptr [0x402034]
        buffer[TextFile + 0x071] = 0x25;
        Put32(buffer, TextFile + 0x072, ImageBase + IatRva + 4);

        for (int i = 0x016; i < 0x020; i++)
        {
            buffer[TextFile + i] = 0xCC;
        }

        for (int i = 0x026; i < 0x030; i++)
        {
            buffer[TextFile + i] = 0xCC;
        }

        for (int i = 0x049; i < 0x050; i++)
        {
            buffer[TextFile + i] = 0xCC;
        }

        for (int i = 0x060; i < 0x070; i++)
        {
            buffer[TextFile + i] = 0xCC;
        }

        for (int i = 0x076; i < 0x300; i++)
        {
            buffer[TextFile + i] = 0xCC;
        }

        if (o.DispatchTable)
        {
            // A function whose whole body is "jump through the table", the way a switch on a register
            // the compiler could not bound looks. `jmp dword ptr [eax*4 + table]`.
            buffer[TextFile + 0x0C0] = 0xFF;
            buffer[TextFile + 0x0C1] = 0x24;
            buffer[TextFile + 0x0C2] = 0x8D;                       // SIB: scale 4, index ecx, disp32
            Put32(buffer, TextFile + 0x0C3, ImageBase + DispatchTableRva);
            buffer[TextFile + 0x0C7] = 0xC3;                       // ret
        }

        if (o.DuplicateFuncA)
        {
            // func_a's code again, byte for byte, at another address.
            buffer[TextFile + 0x0A0] = 0xA1;                       // mov eax, [0x402000]
            Put32(buffer, TextFile + 0x0A1, ImageBase + DataARva);
            buffer[TextFile + 0x0A5] = 0xC3;                       // ret
        }

        // ---------------------------------------------------------------- .rdata
        Put32(buffer, RdataFile + 0x000, 0x12345678);          // data_a
        Put32(buffer, RdataFile + 0x004, 0x2A);                // data_b
        if (o.DispatchTable)
        {
            // Absolute addresses, the encoding a 32-bit image uses for a table in `.rdata`: the
            // entries are the RVAs of three functions the symbol table also names.
            Put32(buffer, RdataFile + 0x180, ImageBase + FuncARva);
            Put32(buffer, RdataFile + 0x184, ImageBase + FuncCRva);
            Put32(buffer, RdataFile + 0x188, ImageBase + DispatchRva);
        }

        if (o.JumpTable)
        {
            for (int i = 0; i < JumpTargets.Length; i++)
            {
                Put32(buffer, RdataFile + 0x008 + (i * 4), ImageBase + JumpTargets[i]);
            }
        }

        Encoding.ASCII.GetBytes("recon fixture\0").CopyTo(buffer, RdataFile + 0x014 + (int)o.StringShift);

        // Import tables: IAT points at the name entries while the loader has not run yet.
        const uint firstName = 0x2044;
        const uint secondName = 0x2054;
        Put32(buffer, RdataFile + 0x030, firstName);
        Put32(buffer, RdataFile + 0x034, secondName);
        Put32(buffer, RdataFile + 0x038, firstName);
        Put32(buffer, RdataFile + 0x03C, secondName);

        string firstImport = o.Vb6Runtime ? "__vbaStrCopy" : o.MsvcRuntime ? "printf" : "GetTickCount";
        string secondImport = o.Vb6Runtime ? "rtcMsgBox" : o.MsvcRuntime ? "malloc" : "ExitProcess";
        WriteHintName(buffer, RdataFile + 0x044, 0, firstImport);
        WriteHintName(buffer, RdataFile + 0x054, 0, secondImport);

        // Import directory: ILT, name, IAT. The 20 zero bytes after the name of the DLL are the
        // directory terminator, which is what tells a loader there are no further modules.
        Put32(buffer, RdataFile + 0x064, 0x2038);
        Put32(buffer, RdataFile + 0x068, 0);
        Put32(buffer, RdataFile + 0x06C, 0);
        Put32(buffer, RdataFile + 0x070, 0x2078);
        Put32(buffer, RdataFile + 0x074, IatRva);
        string importDll = o.Vb6Runtime ? "MSVBVM60.DLL" : o.MsvcRuntime ? "msvcr90.dll" : "KERNEL32.dll";
        Encoding.ASCII.GetBytes(importDll + "\0").CopyTo(buffer, RdataFile + 0x078);

        if (o.Exports)
        {
            const int exportDirectory = 0x0A0;
            Put32(buffer, RdataFile + exportDirectory + 0x00, 0);          // Characteristics
            Put32(buffer, RdataFile + exportDirectory + 0x04, 0x5F000000); // TimeDateStamp
            Put32(buffer, RdataFile + exportDirectory + 0x0C, 0x20D8);     // Name
            Put32(buffer, RdataFile + exportDirectory + 0x10, 1);          // Base
            Put32(buffer, RdataFile + exportDirectory + 0x14, 1);          // NumberOfFunctions
            Put32(buffer, RdataFile + exportDirectory + 0x18, 1);          // NumberOfNames
            Put32(buffer, RdataFile + exportDirectory + 0x1C, 0x20C8);     // AddressOfFunctions
            Put32(buffer, RdataFile + exportDirectory + 0x20, 0x20CC);     // AddressOfNames
            Put32(buffer, RdataFile + exportDirectory + 0x24, 0x20D0);     // AddressOfNameOrdinals
            Put32(buffer, RdataFile + 0x0C8, FuncBRva);                    // export address table
            Put32(buffer, RdataFile + 0x0CC, 0x20E4);                      // export name pointer table
            Put16(buffer, RdataFile + 0x0D0, 0);                           // export ordinal table
            Encoding.ASCII.GetBytes("fixture.dll\0").CopyTo(buffer, RdataFile + 0x0D8);
            Encoding.ASCII.GetBytes("func_b\0").CopyTo(buffer, RdataFile + 0x0E4);
        }

        if (o.PdbReference)
        {
            // CodeView RSDS record: signature, GUID, age, path.
            Encoding.ASCII.GetBytes("RSDS").CopyTo(buffer, RdataFile + 0x100);
            Put32(buffer, RdataFile + 0x104, 0x11223344);
            Put16(buffer, RdataFile + 0x108, 0x5566);
            Put16(buffer, RdataFile + 0x10A, 0x7788);
            for (int i = 0; i < 8; i++)
            {
                buffer[RdataFile + 0x10C + i] = (byte)(0xA0 + i);
            }

            Put32(buffer, RdataFile + 0x114, PdbAge);
            Encoding.ASCII.GetBytes(PdbPath + "\0").CopyTo(buffer, RdataFile + 0x118);

            // One debug directory entry: type 2 (CodeView), size 24 + path, RVA and file offset.
            int entry = RdataFile + 0x140;
            Put32(buffer, entry + 12, 2);
            Put32(buffer, entry + 16, 24 + (uint)PdbPath.Length + 1);
            Put32(buffer, entry + 20, 0x2100);
            Put32(buffer, entry + 24, RdataFile + 0x100);
        }

        // ---------------------------------------------------------------- .data
        Put32(buffer, DataFile + 0x000, 0xDEADBEEF);
        Put32(buffer, DataFile + 0x004, ImageBase + FuncBRva); // a pointer into code

        // ---------------------------------------------------------------- .pdata
        //
        // Four entries of the 32-bit form, which is eight bytes each: a begin and an end. The third
        // word of the 64-bit form is not written here, because this fixture is a PE32 image and its
        // table does not have one — a reader that assumed twelve bytes would read this table's second
        // entry from the first entry's tail and see a function at 0x1016.
        if (o.Pdata)
        {
            // The entry point's own range, exactly what the code section holds.
            Put32(buffer, PdataFile + 0x000, EntryPointRva);
            Put32(buffer, PdataFile + 0x004, 0x1016);

            // The import thunk's range: a function whose size nothing else states — no symbol covers
            // it, so before this table was read its size was a guess to the next thing in the section.
            Put32(buffer, PdataFile + 0x008, ThunkRva);
            Put32(buffer, PdataFile + 0x00C, ThunkRva + 0x20);

            // An entry that is not a function: it begins in `.rdata`. A table is a statement about
            // code, and a start outside a code section is not one — the analysis has to skip it.
            Put32(buffer, PdataFile + 0x010, DataARva);
            Put32(buffer, PdataFile + 0x014, 0x2010);

            // func_b with no end: the form the 64-bit unwinder chains, and this reader's answer is
            // "no extent stated" rather than a rule it would be inventing.
            Put32(buffer, PdataFile + 0x018, FuncBRva);
            Put32(buffer, PdataFile + 0x01C, 0x0000);
        }

        // ---------------------------------------------------------------- .reloc
        if (o.BaseRelocations)
        {
            int p = RelocFile;
            Put32(buffer, p, 0x1000);                 // page
            Put32(buffer, p + 4, 8 + (2 * 2));        // block size
            Put16(buffer, p + 8, (3 << 12) | 0x021);  // HIGHLOW at 0x1021 (mov immediate)
            Put16(buffer, p + 10, (3 << 12) | 0x054); // HIGHLOW at 0x1054 (push immediate)
            p += 8 + (2 * 2);

            Put32(buffer, p, 0x2000);
            Put32(buffer, p + 4, 8 + (3 * 2));
            Put16(buffer, p + 8, (3 << 12) | 0x008);
            Put16(buffer, p + 10, (3 << 12) | 0x00C);
            Put16(buffer, p + 12, (3 << 12) | 0x010);
            p += 8 + (3 * 2);

            Put32(buffer, p, 0x3000);
            Put32(buffer, p + 4, 8 + (1 * 2));
            Put16(buffer, p + 8, (3 << 12) | 0x004);
            p += 8 + (1 * 2);

            // A terminator block: the loader stops here.
            Put32(buffer, p, 0);
            Put32(buffer, p + 4, 0);
        }

        // ---------------------------------------------------------------- COFF symbols
        int count = o.CoffSymbols ? WriteSymbols(buffer, o) : 0;
        if (o.CoffSymbols)
        {
            Put32(buffer, 0x10C, (uint)SymbolTableFile);
            Put32(buffer, 0x110, (uint)count);
        }

        return buffer;
    }

    private static void WriteRichHeader(byte[] buffer, int offset, ushort linkerBuild, bool msvcRuntime, bool vs97Toolset)
    {
        const uint key = 0x5A17C0DE;

        // Everything from "DanS" to "Rich" is XORed with the key, the bill-of-materials entries
        // included; "Rich" and the key itself are plain, which is how a parser finds them.
        Put32(buffer, offset, 0x536E6144 ^ key);
        Put32(buffer, offset + 4, key);
        Put32(buffer, offset + 8, key);
        Put32(buffer, offset + 12, key);

        if (vs97Toolset)
        {
            // VBA6.DLL's own bill of materials: nine assembler objects, one linker object, and one
            // resource-converter object last. The shape is the test, so it is reproduced exactly.
            Entry(buffer, offset + 16, 0x000E, 7299, 9);            // masm_6.13
            Entry(buffer, offset + 24, 0x0013, linkerBuild, 1);     // linker_5.12
            Entry(buffer, offset + 32, 0x0006, 1735, 1);            // cvtres_5.0, the last record

            Put32(buffer, offset + 40, 0x68636952);                 // "Rich"
            Put32(buffer, offset + 44, key);
            return;
        }

        // Product ids are the real ones for a Visual Studio 2008 build: 0x83/0x84 for the two
        // compilers, 0x91 for the linker and 0x93 for import library objects. The linker record is
        // written last, as a real linker writes it, and is the only one the linker-id table marks.
        Entry(buffer, offset + 16, 0x83, 21022, 12);  // C compiler objects
        Entry(buffer, offset + 24, 0x84, 21022, 4);   // C++ compiler objects
        Entry(buffer, offset + 32, 1, 0, 26);         // prodidImport0: imported symbol count
        if (msvcRuntime)
        {
            Entry(buffer, offset + 40, 0x93, 21022, 3); // CRT import library objects
        }

        int tail = offset + (msvcRuntime ? 48 : 40);
        Entry(buffer, tail, 0x91, linkerBuild, 1);    // the linker
        Put32(buffer, tail + 8, 0x68636952);                // "Rich"
        Put32(buffer, tail + 12, key);
    }

    /// <summary>One bill-of-materials record, XORed with the key the way a real linker writes it.</summary>
    private static void Entry(byte[] buffer, int offset, uint prodId, uint build, uint count)
    {
        const uint key = 0x5A17C0DE;
        Put32(buffer, offset, ((prodId << 16) | (build & 0xFFFF)) ^ key);
        Put32(buffer, offset + 4, count ^ key);
    }

    private static int WriteSymbols(byte[] buffer, SyntheticPeOptions options)
    {
        var symbols = new List<(string Name, int Value, short Section, ushort Type, byte Class, byte Aux, string? AuxName)>
        {
            (".file", 0, -2, 0, 103, 1, "fixture.obj"),
            (".text", 0, 1, 0, 3, 0, null),
            ("_start", 0x000, 1, 0x20, 2, 0, null),
            ("_func_a", 0x020, 1, 0x20, 2, 0, null),
            ("_func_b", 0x030, 1, 0x20, 2, 0, null),
            ("_func_c", 0x050, 1, 0x20, 2, 0, null),
            ("_data_a", 0x000, 2, 0x00, 3, 0, null),
            ("_jump_table", 0x008, 2, 0x00, 3, 0, null),
            ("_g_value", 0x000, 3, 0x00, 2, 0, null),
        };

        if (options.DispatchTable)
        {
            symbols.Add(("_dispatch", 0x0C0, 1, 0x20, 2, 0, null));
        }

        if (options.FoldedAlias)
        {
            // Two names, one address: what identical-code folding looks like in a symbol table.
            symbols.Add(("_func_b_folded", 0x030, 1, 0x20, 2, 0, null));
        }

        if (options.DuplicateFuncA)
        {
            symbols.Add(("_func_a_copy", 0x0A0, 1, 0x20, 2, 0, null));
        }

        // Long names (> 8 bytes) live in the string table that follows the symbols.
        var stringTable = new List<byte> { 0, 0, 0, 0 };
        var records = new List<(byte[] Name, int Value, short Section, ushort Type, byte Class, byte Aux, string? AuxName)>();

        foreach (var symbol in symbols)
        {
            byte[] name;
            var ascii = Encoding.ASCII.GetBytes(symbol.Name);
            if (ascii.Length > 8)
            {
                uint stringOffset = (uint)stringTable.Count;
                stringTable.AddRange(ascii);
                stringTable.Add(0);
                name = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(name.AsSpan(4), stringOffset);
            }
            else
            {
                name = new byte[8];
                ascii.CopyTo(name, 0);
            }

            records.Add((name, symbol.Value, symbol.Section, symbol.Type, symbol.Class, symbol.Aux, symbol.AuxName));
            if (symbol.Aux > 0)
            {
                records.Add((new byte[8], 0, 0, 0, 0, 0, symbol.AuxName));
            }
        }

        int position = SymbolTableFile;
        foreach (var record in records)
        {
            record.Name.CopyTo(buffer, position);
            Put32(buffer, position + 8, (uint)record.Value);
            Put16(buffer, position + 12, (ushort)record.Section);
            Put16(buffer, position + 14, record.Type);
            buffer[position + 16] = record.Class;
            buffer[position + 17] = record.Aux;

            if (record.Aux > 0 && record.AuxName is not null)
            {
                Encoding.ASCII.GetBytes(record.AuxName).CopyTo(buffer, position + 18);
            }

            position += 18;
        }

        // The string table starts with its own size; the names follow it directly. The list is
        // seeded with four bytes so that a name's offset is its position in the table, which is the
        // convention the reader (and the specification) use: offset 4 is the first name.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(position), (uint)stringTable.Count);
        stringTable.CopyTo(4, buffer, position + 4, stringTable.Count - 4);

        // The header counts records, auxiliary records included.
        return records.Count;
    }

    private static void WriteSection(byte[] buffer, int offset, string name, uint virtualSize, uint virtualAddress, uint rawSize, int rawOffset, uint characteristics)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(buffer, offset);
        Put32(buffer, offset + 8, virtualSize);
        Put32(buffer, offset + 12, virtualAddress);
        Put32(buffer, offset + 16, rawSize);
        Put32(buffer, offset + 20, (uint)rawOffset);
        Put32(buffer, offset + 36, characteristics);
    }

    /// <summary>Writes a relative <c>call</c>; <paramref name="sectionRva"/> is the RVA of offset 0 in .text.</summary>
    private static void WriteCall(byte[] buffer, int offset, int sectionRva, uint targetRva)
    {
        buffer[offset] = 0xE8;
        uint instructionRva = (uint)(sectionRva + (offset - TextFile));
        Put32(buffer, offset + 1, targetRva - (instructionRva + 5));
    }

    private static void WriteHintName(byte[] buffer, int offset, ushort hint, string name)
    {
        Put16(buffer, offset, hint);
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(buffer, offset + 2);
    }

    private static void Put16(byte[] buffer, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);

    private static void Put32(byte[] buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
}
