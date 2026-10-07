using System.Buffers.Binary;
using System.Text;
using Recon.Pe;

namespace Recon.DebugInfo;

/// <summary>
/// Reads what M1 needs out of a PDB: the info stream (GUID/age, so it can be matched against the
/// PE's CodeView record), the DBI stream's module list, public symbols and procedure symbols with
/// their sizes, plus compiland producer strings.
/// </summary>
/// <remarks>
/// Implemented directly against the MSF container and the DBI/CodeView records rather than through
/// DIA, so it works on any host without a Windows-only dependency. CodeView records that are not
/// needed here are skipped by length. Validation status is tracked in docs/pdb.md.
/// </remarks>
public sealed class PdbReader
{
    private const string MsfSignature = "Microsoft C/C++ MSF 7.00\r\n\u001aDS";

    // CodeView symbol record kinds (cvinfo.h). The numeric values matter: 0x110D is S_GDATA32,
    // not a thunk, and 0x1125 is S_PROCREF, not a procedure.
    private const ushort SCompile3 = 0x113C;
    private const ushort SCompile2 = 0x1116;
    private const ushort SProcRefSym = 0x1125;
    private const ushort SPub32 = 0x110E;
    private const ushort SProc32 = 0x1110;
    private const ushort SProc32Id = 0x1147;
    private const ushort SLProc32 = 0x110F;
    private const ushort SLProc32Id = 0x1146;
    private const ushort SThunk32 = 0x1102;
    private const ushort SLocalData32 = 0x110C;
    private const ushort SGlobalData32 = 0x110D;
    private const ushort SObjName = 0x1101;
    private const ushort SEnd = 0x0006;

    /// <summary>CV_SIGNATURE_C13, which every module stream starts with.</summary>
    private const int CvSignature = 4;

    /// <summary>PUB32 flag bit that marks a symbol as code rather than data.</summary>
    private const uint PubFunction = 0x0002;

    private readonly byte[] _bytes;
    private readonly int _pageSize;
    private readonly uint _pageCount;
    private readonly uint _directorySize;
    private readonly uint _blockMapAddr;
    private readonly List<uint> _streamSizes = [];
    private readonly List<uint[]> _streamPages = [];

    private PdbReader(byte[] bytes, int pageSize, uint pageCount, uint directorySize, uint blockMapAddr)
    {
        _bytes = bytes;
        _pageSize = pageSize;
        _pageCount = pageCount;
        _directorySize = directorySize;
        _blockMapAddr = blockMapAddr;
    }

    public Guid? Guid { get; private set; }

    public uint? Age { get; private set; }

    public string Kind => "pdb";

    public static DebugInfoResult Read(string path, IEnumerable<uint> sectionRvas)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DebugInfoResult
            {
                Kind = "pdb",
                FilePath = path,
                Problems = [$"cannot read PDB {path}: {ex.Message}"],
            };
        }

        return ReadBytes(bytes, sectionRvas, path);
    }

    public static DebugInfoResult ReadBytes(byte[] bytes, IEnumerable<uint> sectionRvas, string path = "<memory>")
    {
        var result = new DebugInfoResult { Kind = "pdb", FilePath = path };
        var signature = Encoding.ASCII.GetString(bytes, 0, Math.Min(32, bytes.Length));
        if (!signature.StartsWith(MsfSignature, StringComparison.Ordinal))
        {
            result.Problems.Add("not an MSF 7.00 file (plain-text PDBs and MSF 2.0 are not supported)");
            return result;
        }

        int pageSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
        uint pageCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
        uint directorySizeBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44));
        uint blockMapAddr = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52));
        if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0)
        {
            result.Problems.Add($"invalid MSF page size {pageSize}");
            return result;
        }

        uint directoryBlocks = (directorySizeBytes + (uint)pageSize - 1) / (uint)pageSize;
        var reader = new PdbReader(bytes, pageSize, pageCount, directoryBlocks, blockMapAddr);
        reader.SetSectionBases(sectionRvas);
        reader.LoadDirectory(result);
        if (result.Problems.Count > 0)
        {
            return result;
        }

        reader.ReadInfoStream(result);
        var dbi = reader.ReadDbiStream(result);
        if (dbi is not null)
        {
            reader.ReadSymbolRecords(dbi);
            reader.ReadModules(dbi, result);
        }

        return result;
    }

    private void LoadDirectory(DebugInfoResult result)
    {
        // The block map is an array of page numbers: the pages that hold the stream directory.
        int mapOffset = (int)(_blockMapAddr * _pageSize);
        if (mapOffset + (_directorySize * 4) > _bytes.Length)
        {
            result.Problems.Add("stream directory block map is outside the file");
            return;
        }

        var directoryBytes = new byte[_directorySize * _pageSize];
        for (uint i = 0; i < _directorySize; i++)
        {
            uint page = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(mapOffset + (int)(i * 4)));
            int source = (int)(page * _pageSize);
            if (source + _pageSize > _bytes.Length)
            {
                result.Problems.Add($"stream directory page {page} is outside the file");
                return;
            }

            Array.Copy(_bytes, source, directoryBytes, (int)(i * _pageSize), _pageSize);
        }

        uint streamCount = BinaryPrimitives.ReadUInt32LittleEndian(directoryBytes.AsSpan(0));
        if (streamCount > 0x10000)
        {
            result.Problems.Add($"implausible stream count {streamCount}");
            return;
        }

        var sizes = new List<uint>((int)streamCount);
        for (uint i = 0; i < streamCount; i++)
        {
            sizes.Add(BinaryPrimitives.ReadUInt32LittleEndian(directoryBytes.AsSpan(4 + ((int)i * 4))));
        }

        int cursor = (int)(4 + (streamCount * 4));
        foreach (uint size in sizes)
        {
            int pages = (int)((size + (uint)_pageSize - 1) / (uint)_pageSize);
            var pageList = new uint[pages];
            for (int i = 0; i < pages; i++)
            {
                if (cursor + 4 > directoryBytes.Length)
                {
                    result.Problems.Add("stream page list is truncated");
                    return;
                }

                pageList[i] = BinaryPrimitives.ReadUInt32LittleEndian(directoryBytes.AsSpan(cursor));
                cursor += 4;
            }

            _streamSizes.Add(size);
            _streamPages.Add(pageList);
        }
    }

    private byte[]? GetStream(int index)
    {
        if (index < 0 || index >= _streamSizes.Count)
        {
            return null;
        }

        var stream = new byte[_streamSizes[index]];
        int written = 0;
        foreach (uint page in _streamPages[index])
        {
            int source = (int)(page * _pageSize);
            if (source + _pageSize > _bytes.Length)
            {
                return null;
            }

            int count = Math.Min(_pageSize, stream.Length - written);
            Array.Copy(_bytes, source, stream, written, count);
            written += count;
        }

        return stream;
    }

    private void ReadInfoStream(DebugInfoResult result)
    {
        var info = GetStream(1);
        if (info is null || info.Length < 28)
        {
            result.Problems.Add("PDB info stream is missing or truncated");
            return;
        }

        // Version (4), Signature/Timestamp (4), Age (4), GUID (16).
        Age = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8));
        Guid = new Guid(info.AsSpan(12, 16));
        result.Age = Age;
        result.Guid = Guid;
    }

    private sealed class DbiStream
    {
        public int[] ModuleSymStreams { get; set; } = [];
        public int PublicSymbolStream { get; set; }
        public int GlobalSymbolStream { get; set; }
        public int SymRecordStream { get; set; }
        public List<(int Rva, int Size, int Characteristics, string Name)> Sections { get; } = [];
        public byte[]? ModuleInfo { get; set; }
        public byte[]? SectionHeaderStream { get; set; }
        public int PdbDllVersion { get; set; }
    }

    private DbiStream? ReadDbiStream(DebugInfoResult result)
    {
        var dbi = GetStream(3);
        if (dbi is null || dbi.Length < 64)
        {
            result.Problems.Add("DBI stream is missing or truncated");
            return null;
        }

        // DBI header layout: VersionSignature (0), VersionHeader (4), Age (8), GlobalSymbolStream
        // (12), BuildNumber (14), PublicSymbolStream (16), PdbDllVersion (18), SymRecordStream (20).
        var header = new DbiStream
        {
            GlobalSymbolStream = BinaryPrimitives.ReadUInt16LittleEndian(dbi.AsSpan(12)),
            PublicSymbolStream = BinaryPrimitives.ReadUInt16LittleEndian(dbi.AsSpan(16)),
            SymRecordStream = BinaryPrimitives.ReadUInt16LittleEndian(dbi.AsSpan(20)),
            PdbDllVersion = BinaryPrimitives.ReadUInt16LittleEndian(dbi.AsSpan(18)),
        };

        uint modInfoSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(24));
        uint sectionContributionSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(28));
        uint sectionMapSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(32));
        uint sourceInfoSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(36));
        uint typeServerMapSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(40));
        uint optionalDbgHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(48));
        uint ecSubstreamSize = BinaryPrimitives.ReadUInt32LittleEndian(dbi.AsSpan(52));

        int cursor = 64;
        header.ModuleInfo = Slice(dbi, cursor, (int)modInfoSize);
        cursor += (int)modInfoSize + (int)sectionContributionSize + (int)sectionMapSize + (int)sourceInfoSize + (int)typeServerMapSize + (int)ecSubstreamSize;

        var optionalHeader = Slice(dbi, cursor, (int)optionalDbgHeaderSize);
        if (optionalHeader is not null && optionalHeader.Length >= 24)
        {
            // Entry 5 is the section header stream: an array of IMAGE_SECTION_HEADER records.
            int offset = 0;
            for (int i = 0; i < 11; i++)
            {
                ushort size = BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader.AsSpan(i * 2));
                if (i == 5 && size > 0)
                {
                    header.SectionHeaderStream = Slice(optionalHeader, offset, size);
                }

                offset += size;
            }
        }

        return header;
    }

    private static byte[]? Slice(byte[] source, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > source.Length)
        {
            return null;
        }

        var result = new byte[length];
        Array.Copy(source, offset, result, 0, length);
        return result;
    }

    /// <summary>
    /// Walks the public symbol stream. Publics cover every function that has a name outside its
    /// object file, but carry no size; procedure symbols from module streams carry both.
    /// </summary>
    private void ReadSymbolRecords(DbiStream dbi)
    {
        int streamIndex = dbi.SymRecordStream != 0 ? dbi.SymRecordStream : dbi.GlobalSymbolStream;
        var records = GetStream(streamIndex);
        if (records is null)
        {
            return;
        }

        _publics = [];
        foreach (var record in EnumerateRecords(records))
        {
            if (record.Kind != SPub32)
            {
                continue;
            }

            var payload = record.Payload;
            if (payload.Length < 10)
            {
                continue;
            }

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
            ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8));
            string name = ReadName(payload, 10);
            if (name.Length == 0)
            {
                continue;
            }

            _publics.Add(new DebugSymbol
            {
                Name = name,
                Rva = SegmentOffsetToRva(segment, offset),
                Source = SymbolSource.Pdb,

                // Publics cover data as well as functions; the flags say which is which, and a
                // data symbol must not enter the function inventory.
                IsData = (flags & PubFunction) == 0,
            });
        }
    }

    private List<DebugSymbol> _publics = [];

    private void ReadModules(DbiStream dbi, DebugInfoResult result)
    {
        var moduleInfo = dbi.ModuleInfo;
        if (moduleInfo is null)
        {
            result.Symbols.AddRange(_publics);
            return;
        }

        int cursor = 0;
        int moduleIndex = 0;
        while (cursor + 64 < moduleInfo.Length)
        {
            // MOD layout: unused (4), SectionContrib (28), flags (2), moduleSymStream (2),
            // symByteSize (4), c11ByteSize (4), c13ByteSize (4), sourceFileCount (2), padding (2),
            // unused2 (4), sourceFileNameIndex (4), pdbFilePathNameIndex (4) = 64 bytes, then the
            // module name and the object file name.
            int recordStart = cursor;
            if (cursor + 64 > moduleInfo.Length)
            {
                break;
            }

            int symStream = BinaryPrimitives.ReadUInt16LittleEndian(moduleInfo.AsSpan(cursor + 34));
            int symByteSize = BinaryPrimitives.ReadInt32LittleEndian(moduleInfo.AsSpan(cursor + 36));
            cursor += 64;

            string moduleName = ReadName(moduleInfo, cursor);
            if (moduleName.Length == 0)
            {
                break;
            }

            cursor += moduleName.Length + 1;
            string objectName = ReadName(moduleInfo, cursor);
            cursor += objectName.Length + 1;

            // Records are aligned to four bytes.
            cursor = (cursor + 3) & ~3;
            if (cursor <= recordStart)
            {
                break;
            }

            var compiland = new CompilandInfo
            {
                Unit = moduleName,
                ObjectFile = objectName,
                ModuleIndex = moduleIndex++,
            };

            var moduleStream = GetStream(symStream);
            if (moduleStream is not null)
            {
                ReadModuleSymbols(moduleStream, symByteSize, compiland, result);
            }

            result.Compilands.Add(compiland);
        }

        result.Symbols.AddRange(_publics);
    }

    private void ReadModuleSymbols(byte[] stream, int byteSize, CompilandInfo compiland, DebugInfoResult result)
    {
        int limit = byteSize > 0 && byteSize <= stream.Length ? byteSize : stream.Length;
        // Module streams start with the C13 signature; the symbol-record stream does not.
        foreach (var record in EnumerateRecords(stream, limit, signature: true))
        {
            switch (record.Kind)
            {
                case SCompile2:
                case SCompile3:
                    compiland.Producer ??= ExtractProducer(record.Payload);
                    break;

                case SProc32:
                case SProc32Id:
                case SLProc32:
                case SLProc32Id:
                    {
                        var payload = record.Payload;
                        if (payload.Length < 35)
                        {
                            break;
                        }

                        uint codeSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
                        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(28));
                        ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(32));
                        string name = ReadName(payload, 35);
                        if (name.Length == 0)
                        {
                            break;
                        }

                        result.Symbols.Add(new DebugSymbol
                        {
                            Name = name,
                            Rva = SegmentOffsetToRva(segment, offset),
                            Size = codeSize,
                            Source = SymbolSource.Pdb,
                            Unit = compiland.Unit,
                        });
                        break;
                    }

                case SThunk32:
                    {
                        // Thunks are aliases of another function: keep the name, drop the body.
                        var payload = record.Payload;
                        if (payload.Length < 20)
                        {
                            break;
                        }

                        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
                        ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(16));
                        string name = ReadName(payload, 21);
                        result.Symbols.Add(new DebugSymbol
                        {
                            Name = name,
                            Rva = SegmentOffsetToRva(segment, offset),
                            Source = SymbolSource.Pdb,
                            Unit = compiland.Unit,
                        });
                        break;
                    }

                case SObjName:
                    {
                        // The MOD record leaves the object file name empty; this record is where
                        // the linker records it.
                        string objectName = ReadName(record.Payload, 4);
                        if (objectName.Length > 0)
                        {
                            compiland.ObjectFile = objectName;
                        }

                        break;
                    }

                case SLocalData32:
                case SGlobalData32:
                    {
                        var payload = record.Payload;
                        if (payload.Length < 10)
                        {
                            break;
                        }

                        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
                        ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8));
                        string name = ReadName(payload, 10);
                        if (name.Length == 0)
                        {
                            break;
                        }

                        result.Symbols.Add(new DebugSymbol
                        {
                            Name = name,
                            Rva = SegmentOffsetToRva(segment, offset),
                            Source = SymbolSource.Pdb,
                            IsData = true,
                            Unit = compiland.Unit,
                        });
                        break;
                    }

                case SProcRefSym:
                case SEnd:
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// S_COMPILE2/3 records carry a language byte and a producer string; the exact fixed part has
    /// changed over PDB versions, so scan the payload for the first printable string instead of
    /// trusting offsets.
    /// </summary>
    private static string? ExtractProducer(byte[] payload)
    {
        var builder = new StringBuilder();
        foreach (byte b in payload)
        {
            if (b == 0)
            {
                if (builder.Length >= 8 && builder.ToString().Any(char.IsLetter))
                {
                    return builder.ToString();
                }

                builder.Clear();
                continue;
            }

            if (b is >= 32 and < 127)
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Clear();
            }
        }

        return builder.Length >= 8 ? builder.ToString() : null;
    }

    private readonly record struct CvRecord(ushort Kind, byte[] Payload);

    /// <summary>
    /// CodeView records: length (2), kind (2), payload, padded to four bytes.
    ///
    /// Two conventions for the length field exist in the wild, and both have to work: link.exe
    /// writes the padded size of the whole record, while lld writes that size minus two (an S_END
    /// record, 4 bytes long, declares 2). Advancing to the next four-byte boundary after at least
    /// four bytes lands on the right record under either convention, and copying the whole padded
    /// extent into the payload keeps a name that end in the padding from being cut short.
    /// </summary>
    private IEnumerable<CvRecord> EnumerateRecords(byte[] stream, int? limit = null, bool signature = false)
    {
        int end = Math.Min(stream.Length, limit ?? stream.Length);
        int cursor = signature ? CvSignature : 0;
        while (cursor + 4 <= end)
        {
            ushort declared = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(cursor));
            ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(cursor + 2));
            if (declared == 0 && kind == 0)
            {
                yield break; // padding or the end of the record area
            }

            int advance = Math.Max(4, (declared + 3) & ~3);
            int payloadStart = cursor + 4;
            int payloadEnd = Math.Min(end, cursor + advance);
            var payload = payloadStart < payloadEnd ? stream[payloadStart..payloadEnd] : [];
            yield return new CvRecord(kind, payload);
            cursor += advance;
        }
    }

    private static string ReadName(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        int end = offset;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    /// <summary>Translates a (segment, offset) pair from a symbol record into an RVA.</summary>
    private uint SegmentOffsetToRva(ushort segment, uint offset)
    {
        // Segment 1 is the first section; the PDB knows sections through the section header stream,
        // which may be absent. Without it, fall back to the section list captured from the image.
        int index = segment - 1;
        if (index < 0 || index >= _sections.Count)
        {
            return offset;
        }

        return _sections[index] + offset;
    }

    private readonly List<uint> _sections = [];

    /// <summary>Section base RVAs, in image order. Symbols address sections by index, not by RVA.</summary>
    public void SetSectionBases(IEnumerable<uint> sectionRvas)
    {
        _sections.Clear();
        _sections.AddRange(sectionRvas);
    }
}
