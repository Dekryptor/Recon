using System.Buffers.Binary;

namespace Recon.Pe;

/// <summary>One entry of a Rich header: how many object files one tool build contributed.</summary>
public sealed class RichHeaderEntry
{
    /// <summary>Product id: which tool emitted the objects.</summary>
    public uint ProdId { get; set; }

    /// <summary>Build number of that tool. Zero for the pseudo entry that counts imports.</summary>
    public ushort Build { get; set; }

    /// <summary>Number of objects (or, for the import entry, of imported functions).</summary>
    public uint Count { get; set; }

    /// <summary>Best-effort tool label. See <see cref="RichHeader.LegacyProdIds"/> for the caveat.</summary>
    public string Tool { get; set; } = "unknown";

    public bool IsLast => Last;

    internal bool Last { get; set; }

    /// <summary>
    /// True when this entry was written by a linker. It is decided by the product id, not by the
    /// entry's position: the last record is usually the linker's own, but not always, and in the
    /// Visual Studio 98 files this was checked against it is never true — those end with the
    /// resource converter, and a Visual Basic 6 program ends with the Basic compiler.
    /// </summary>
    public bool IsLinker { get; set; }
}

/// <summary>
/// The Rich header: an undocumented block the Microsoft linker writes between the DOS stub and the
/// PE header. It is evidence of which tool builds produced the objects in the image, nothing more;
/// <c>toolchain</c> decisions are drawn elsewhere.
/// </summary>
public sealed class RichHeader
{
    private const uint DanSMagic = 0x536E6144; // "DanS"
    private const uint RichMagic = 0x68636952; // "Rich"

    /// <summary>
    /// Product ids of the Visual Studio 97/98 era, the ones this tool meets in Visual Basic 6
    /// targets. The names are the ones in <c>comp_id.txt</c> of the public <c>richprint</c> project
    /// (https://github.com/dishather/richprint), whose VS98 entries are observed rather than
    /// interpolated; every id here was also read back out of a real 1998-2004 binary before it was
    /// written down. Ids outside this range are reported as <c>tool_0xNNNN</c> rather than guessed.
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, string> LegacyProdIds = new Dictionary<uint, string>
    {
        [0] = "unmarked",       // objects with no @comp.id at all; VBA6.DLL carries 970 of them
        [1] = "imports",        // prodidImport0: the pseudo entry counting imported symbols
        [2] = "linker_5.10",    // prodidLinker510
        [3] = "cvtomf_5.10",    // prodidCvtomf510
        [4] = "linker_6.0",     // prodidLinker600: the Visual Studio 98 linker
        [5] = "cvtomf_6.0",     // prodidCvtomf600
        [6] = "cvtres_5.0",     // prodidCvtres500: the resource converter, not the linker
        [7] = "vb5_basic",      // prodidUtc11_Basic: Visual Basic 5
        [8] = "vc5_c",          // prodidUtc11_C
        [9] = "vb6_basic",      // prodidUtc12_Basic: the Visual Basic 6 Basic front end
        [10] = "cl_c",          // prodidUtc12_C
        [11] = "cl_cpp",        // prodidUtc12_CPP
        [12] = "alias_obj_6.0", // prodidAliasObj60
        [13] = "vb60",          // prodidVisualBasic60
        [14] = "masm_6.13",     // prodidMasm613
        [15] = "masm_7.10",     // prodidMasm710
        [16] = "linker_5.11",   // prodidLinker511
        [17] = "cvtomf_5.11",   // prodidCvtomf511
        [18] = "masm_6.14",     // prodidMasm614
        [19] = "linker_5.12",   // prodidLinker512
        [24] = "masm_6.15",     // prodidMasm615
        [42] = "masm_6.20",     // prodidMasm620

        // The Visual Studio 2008 set: the ids the msvc-2008 profile and the PE fixture speak about.
        [0x83] = "cl_c_9.0",     // prodidUtc1500_C
        [0x84] = "cl_cpp_9.0",   // prodidUtc1500_CPP
        [0x91] = "linker_9.0",   // prodidLinker900
        [0x92] = "export_9.0",   // prodidExport900
        [0x93] = "implib_9.0",   // prodidImplib900
        [0x94] = "cvtres_9.0",   // prodidCvtres900
        [0x95] = "masm_9.0",     // prodidMasm900
    };

    /// <summary>
    /// The product ids that stand for a linker, in the same source as the names above: 0x0004 is the
    /// Visual Studio 98 linker, 0x0091 the Visual Studio 2008 one and 0x0102 the modern (VS2015 and
    /// later) one. Which entry is the linker is read from this table, never from an entry's position.
    /// </summary>
    public static readonly IReadOnlySet<uint> LinkerProdIds = new HashSet<uint>
    {
        0x0002, // linker510
        0x0004, // linker600 — Visual Studio 98
        0x0010, // linker511
        0x0013, // linker512
        0x001E, // linker610
        0x0020, // linker601
        0x0025, // linker620
        0x0028, // linker621
        0x003C, // linker622
        0x003D, // linker700 — Visual Studio 2002
        0x0047, // linker710p
        0x0056, // linker624
        0x005A, // linker710
        0x0078, // linker800 — Visual Studio 2005
        0x0091, // linker900 — Visual Studio 2008
        0x009D, // linker1000 — Visual Studio 2010
        0x00BA, // linker1010
        0x00CC, // linker1100 — Visual Studio 2012
        0x00DE, // linker1200 — Visual Studio 2013
        0x00F0, // linker1210
        0x0102, // linker1400 — Visual Studio 2015 and later
    };

    public uint XorKey { get; set; }

    /// <summary>File offset of the XORed <c>DanS</c> marker.</summary>
    public uint StartOffset { get; set; }

    /// <summary>File offset of the <c>Rich</c> marker.</summary>
    public uint EndOffset { get; set; }

    public List<RichHeaderEntry> Entries { get; set; } = [];

    /// <summary>
    /// The last entry that was written by a linker, or null when the header holds no linker record
    /// at all — which is the normal case for a Visual Basic 6 program, where the last record is the
    /// Basic compiler and no Microsoft linker took part.
    /// </summary>
    public RichHeaderEntry? LinkerEntry => Entries.LastOrDefault(e => e.IsLinker);

    /// <summary>SHA-256 over the decrypted header bytes: a stable fingerprint for build-environment clustering.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Parses the header if present. The structure is: <c>DanS</c> + three padding dwords, then
    /// <c>(prod_id &lt;&lt; 16 | build, count)</c> pairs, then <c>Rich</c> and the XOR key.
    /// Everything from <c>DanS</c> up to <c>Rich</c> is XORed with that key — the entries included —
    /// which is the one thing every description of this structure agrees on, and which a real file
    /// settles: read without the key, the counts come out in the billions and the product ids match
    /// nothing in the table below.
    /// </summary>
    public static RichHeader? TryParse(ByteReader reader)
    {
        var data = reader.Data;
        int searchLimit = Math.Min(data.Length, 0x400);
        int richOffset = -1;
        for (int i = 0x40; i + 8 <= searchLimit; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == RichMagic)
            {
                richOffset = i;
                break;
            }
        }

        if (richOffset < 0)
        {
            return null;
        }

        uint key = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(richOffset + 4, 4));
        uint? start = null;
        for (int i = richOffset - 16; i >= 0; i -= 4)
        {
            uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) ^ key;
            if (candidate == DanSMagic)
            {
                start = (uint)i;
                break;
            }
        }

        if (start is null)
        {
            return null;
        }

        var header = new RichHeader
        {
            XorKey = key,
            StartOffset = start.Value,
            EndOffset = (uint)richOffset,
        };

        int entriesStart = (int)start.Value + 16;
        int entriesEnd = richOffset;
        for (int i = entriesStart; i + 8 <= entriesEnd; i += 8)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) ^ key;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4, 4)) ^ key;
            uint prodId = id >> 16;
            ushort build = (ushort)(id & 0xFFFF);
            header.Entries.Add(new RichHeaderEntry
            {
                ProdId = prodId,
                Build = build,
                Count = count,
                Tool = LegacyProdIds.TryGetValue(prodId, out var name) ? name : $"tool_0x{prodId:X4}",
                IsLinker = LinkerProdIds.Contains(prodId),
            });
        }

        if (header.Entries.Count > 0)
        {
            header.Entries[^1].Last = true;
        }

        // The fingerprint covers the decrypted header, so it is independent of the XOR key.
        var decrypted = new byte[entriesEnd - entriesStart + 16];
        for (int i = 0; i < 16 && (int)start.Value + i < data.Length; i++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)start.Value + i, 4)) ^ key;
            BinaryPrimitives.WriteUInt32LittleEndian(decrypted.AsSpan(i, 4), value);
        }

        // The entries are encrypted too, so the fingerprint has to decrypt them as it copies.
        var entries = decrypted.AsSpan(16);
        for (int i = 0; i + 4 <= entries.Length; i += 4)
        {
            int at = entriesStart + i;
            BinaryPrimitives.WriteUInt32LittleEndian(
                entries.Slice(i, 4),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4)) ^ key);
        }
        header.Fingerprint = PeImage.HashBytes(decrypted);
        return header;
    }
}
