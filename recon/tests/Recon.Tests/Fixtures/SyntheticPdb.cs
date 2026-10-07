using System.Buffers.Binary;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>
/// Builds a minimal but structurally correct MSF 7.00 (PDB) file in memory.
///
/// No MSVC toolchain exists on Linux, so the PDB path would otherwise be untested. This fixture
/// pins the container layout (superblock, block map, stream directory) and the record layouts the
/// reader depends on: the DBI header field offsets, the MOD record with its two trailing names, and
/// the S_PUB32 / S_PROC32 / S_COMPILE3 payloads. A wrong offset in any of them makes these tests
/// fail instead of silently producing an empty symbol list against a real Microsoft PDB.
/// </summary>
public static class SyntheticPdb
{
    public const int PageSize = 512;

    public static readonly Guid Guid = new("11223344-5566-7788-99aa-bbccddeeff00");
    public const uint Age = 3;
    public const uint FuncARva = 0x1000;
    public const uint FuncASize = 0x40;
    public const uint FuncBRva = 0x1100;
    public const uint FuncBSize = 0x20;
    public const uint DataRva = 0x2000;
    public const string Producer = "Microsoft (R) Optimized Compiler x86";
    public const string ObjectName = @"C:\build\fixture.obj";

    /// <summary>Section base RVAs handed to the reader, in image order.</summary>
    public static readonly uint[] SectionRvas = [0x1000, 0x2000, 0x3000];

    /// <summary>
    /// Builds the file. <paramref name="lldLengths"/> selects the length convention lld uses
    /// (the padded record size minus two, so an S_END record declares 2); the default is the one
    /// link.exe uses (the padded size).
    /// </summary>
    public static byte[] Build(bool lldLengths = false) => Build(out _, lldLengths);

    public static byte[] Build(out List<string> layout, bool lldLengths = false)
    {
        layout = [];

        // Stream list, in index order. Index 0 is the "old" directory MSF keeps for compatibility.
        var streams = new List<byte[]>
        {
            new byte[0],               // 0: old MSF directory
            InfoStream(),              // 1: PDB info
            new byte[0],               // 2: type information (TPI)
            DbiStream(),               // 3: DBI
            PublicStream(lldLengths),  // 4: public symbol records
            ModuleStream(lldLengths),  // 5: module symbol records
        };

        // Blocks: 0 superblock, 1 free block map, 2 block map (which lists the directory pages),
        // 3 the stream directory, then one or more data blocks per stream.
        const int freeBlockMapBlock = 1;
        const int blockMapBlock = 2;
        const int directoryBlock = 3;
        int nextBlock = 4;

        var streamBlocks = new List<int[]>();
        foreach (var stream in streams)
        {
            int blocks = (stream.Length + PageSize - 1) / PageSize;
            var pages = new int[blocks];
            for (int i = 0; i < blocks; i++)
            {
                pages[i] = nextBlock++;
            }

            streamBlocks.Add(pages);
            layout.Add($"{stream.Length,6} bytes, {blocks} page(s) at {string.Join(',', pages)}");
        }

        int directorySizeBytes = 4 + (streams.Count * 4) + (streamBlocks.Sum(p => p.Length) * 4);
        int directoryBlocks = (directorySizeBytes + PageSize - 1) / PageSize;
        if (directoryBlocks != 1)
        {
            throw new InvalidOperationException("the fixture assumes a single directory block");
        }

        int totalBlocks = nextBlock;
        var file = new byte[totalBlocks * PageSize];

        // Superblock.
        // \u001a, not \x1a: the \x escape would swallow the following 'D' as a hex digit.
        Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), freeBlockMapBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), (uint)directoryBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(48), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(52), blockMapBlock);

        // The block map itself: the pages that hold the stream directory.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(blockMapBlock * PageSize), directoryBlock);

        // Directory: stream count, stream sizes, then a page list per stream.
        int directory = directoryBlock * PageSize;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(directory), (uint)streams.Count);
        int cursor = directory + 4;
        foreach (var stream in streams)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(cursor), (uint)stream.Length);
            cursor += 4;
        }

        foreach (var pages in streamBlocks)
        {
            foreach (int page in pages)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(cursor), (uint)page);
                cursor += 4;
            }
        }

        // Stream payloads.
        for (int i = 0; i < streams.Count; i++)
        {
            if (streamBlocks[i].Length == 0)
            {
                continue; // index 0 and the TPI stream are empty on purpose
            }

            int offset = streamBlocks[i][0] * PageSize;
            streams[i].CopyTo(file, offset);
        }

        return file;
    }

    private static byte[] InfoStream()
    {
        var stream = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(0), 20000404); // Version
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(4), 0x5EED1234); // Signature
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(8), Age);
        Guid.ToByteArray().CopyTo(stream, 12);
        return stream;
    }

    private static byte[] DbiStream()
    {
        var modules = ModuleInfo();
        int optionalHeaderSize = 22 * 11;

        var stream = new byte[64 + modules.Length + optionalHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(0), 0xFFFFFFFF); // VersionSignature
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(4), 19990903);   // VersionHeader
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(8), Age);
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(12), 0);         // GlobalSymbolStream
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(14), 0);         // BuildNumber
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(16), 4);         // PublicSymbolStream
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(18), 0);         // PdbDllVersion
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(20), 4);         // SymRecordStream
        BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(24), modules.Length); // ModInfoSize
        BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(48), optionalHeaderSize); // OptionalDbgHeaderSize
        modules.CopyTo(stream, 64);
        return stream;
    }

    /// <summary>
    /// One MOD record: a section contribution, the module name, and an empty object file name, the
    /// way link.exe and lld both write it. The object file name comes from S_OBJNAME instead.
    /// </summary>
    private static byte[] ModuleInfo()
    {
        string moduleName = "fixture.obj";
        string objectName = string.Empty;
        int size = 64 + moduleName.Length + 1 + objectName.Length + 1;
        size = (size + 3) & ~3;

        var record = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(32), 0); // flags
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(34), 5); // moduleSymStream
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(36), 512); // symByteSize
        Encoding.ASCII.GetBytes(moduleName + "\0").CopyTo(record, 64);
        Encoding.ASCII.GetBytes(objectName + "\0").CopyTo(record, 64 + moduleName.Length + 1);
        return record;
    }

    /// <summary>S_PUB32 records: names without sizes, the way a linker emits them.</summary>
    private static byte[] PublicStream(bool lldLengths)
    {
        var stream = new List<byte>();
        AppendRecord(stream, 0x110E, Pub32(FuncARva, "func_a", isFunction: true), lldLengths);
        AppendRecord(stream, 0x110E, Pub32(FuncBRva, "func_b", isFunction: true), lldLengths);

        // A public whose flags do not say "function" is data: it must not enter the inventory.
        AppendRecord(stream, 0x110E, Pub32(DataRva, "data_global", isFunction: false), lldLengths);
        return [.. stream];

        static byte[] Pub32(uint rva, string name, bool isFunction)
        {
            var payload = new byte[10 + name.Length + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), isFunction ? 2u : 0u); // flags
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rva - 0x1000); // offset in section
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 1); // segment (1-based)
            Encoding.ASCII.GetBytes(name + "\0").CopyTo(payload, 10);
            return payload;
        }
    }

    /// <summary>
    /// A module stream: the C13 signature, then S_OBJNAME, S_COMPILE3, procedure records with sizes,
    /// a thunk and a data symbol, closed by S_END, the way a real one looks.
    /// </summary>
    private static byte[] ModuleStream(bool lldLengths)
    {
        var stream = new List<byte> { 4, 0, 0, 0 }; // CV_SIGNATURE_C13
        AppendRecord(stream, 0x1101, ObjName(ObjectName), lldLengths);
        AppendRecord(stream, 0x113C, Compile3(), lldLengths);
        AppendRecord(stream, 0x110F, Proc32(FuncARva, FuncASize, "func_a"), lldLengths);
        AppendRecord(stream, 0x110F, Proc32(FuncBRva, FuncBSize, "func_b"), lldLengths);
        AppendRecord(stream, 0x1102, Thunk32(FuncBRva, "thunk_alias"), lldLengths);
        AppendRecord(stream, 0x110C, Data32(DataRva, "local_data"), lldLengths);
        AppendRecord(stream, 0x0006, [], lldLengths); // S_END
        return [.. stream];
    }

    private static byte[] ObjName(string name)
    {
        var payload = new byte[4 + name.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 0); // signature
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(payload, 4);
        return payload;
    }

    private static byte[] Data32(uint rva, string name)
    {
        var payload = new byte[10 + name.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 0x0074); // type index
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rva - 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 1);
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(payload, 10);
        return payload;
    }

    private static byte[] Compile3()
    {
        var payload = new byte[24 + Producer.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 0x000C); // flags
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 0x0000); // machine: x86
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 0x00A0); // front end version
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 0x0000);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 0x00A0); // back end version
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), 0x0000);
        payload[12] = 0;   // language: C
        payload[13] = 0;   // flags
        Encoding.ASCII.GetBytes(Producer + "\0").CopyTo(payload, 24);
        return payload;
    }

    private static byte[] Proc32(uint rva, uint size, string name)
    {
        var payload = new byte[35 + name.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rva - 0x1000 + size); // pEnd
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), size); // code size
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28), rva - 0x1000); // offset
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(32), 1); // segment
        payload[34] = 0; // flags: no debug info recorded for the frame
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(payload, 35);
        return payload;
    }

    private static byte[] Thunk32(uint rva, string name)
    {
        var payload = new byte[21 + name.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), rva - 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 1);
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(payload, 21);
        return payload;
    }

    /// <summary>Appends a CodeView record: length, kind, payload, padded to four bytes.</summary>
    private static void AppendRecord(List<byte> stream, ushort kind, byte[] payload, bool lldLengths = false)
    {
        int size = (payload.Length + 4 + 3) & ~3;
        var record = new byte[size];
        ushort declared = (ushort)(lldLengths ? size - 2 : size);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0), declared);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), kind);
        payload.CopyTo(record, 4);
        stream.AddRange(record);
    }
}
