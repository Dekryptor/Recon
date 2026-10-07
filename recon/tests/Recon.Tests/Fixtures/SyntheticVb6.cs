using System.Buffers.Binary;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>
/// The smallest PE that is a Visual Basic 5/6 program: an entry point that pushes a VB header and
/// calls the runtime, a VB5! header, the project data behind it, and — as the real structure has —
/// an object table and thirty-two bytes of object descriptors.
///
/// It exists because the two things that decide how a VB program is read are one field and one table:
/// <c>aNativeCode</c>, which is zero when the program is p-code, and the object table, which lists
/// the forms, classes and modules the project is made of. No p-code VB6 binary could be obtained, so
/// the p-code half of this is built from the published layout and pinned against real native programs
/// (<c>RECON_VB6_INPUTS</c>, <c>RECON_VB6_WILD</c>); the object table is checked on both.
/// </summary>
public static class SyntheticVb6
{
    private const uint ImageBase = 0x400000;

    private const uint EntryRva = 0x1000;
    private const uint HeaderRva = 0x1100;
    private const uint ProjectInfoRva = 0x1200;

    /// <summary>The object table, and the descriptors it points at.</summary>
    private const uint ObjectTableRva = 0x1300;
    private const uint ObjectArrayRva = 0x1360;
    private const uint FormNameRva = 0x13C0;
    private const uint ModuleNameRva = 0x13D0;

    /// <summary>Two objects, as every real project has: a form and a standard module.</summary>
    public const uint FormType = 0x00018083;
    public const uint ModuleType = 0x00018001;


    /// <summary>
    /// A VB6 program whose <c>aNativeCode</c> is <paramref name="aNativeCode"/> — zero for p-code,
    /// an address for a native build — with an object table holding a form and a standard module.
    /// </summary>
    public static byte[] Program(uint aNativeCode = 0, bool objectTable = true)
    {
        const int sectionRva = 0x1000;
        const int sectionSize = 0x1000;
        const int headersSize = 0x400;

        var image = new byte[headersSize + sectionSize];

        // DOS header: the e_lfanew field is all a loader needs from it.
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0x40);

        // COFF header: i386, one section, a PE32 optional header behind it.
        int pe = 0x40;
        "PE\0\0"u8.CopyTo(image.AsSpan(pe, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 4), 0x014C);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 20), 0xE0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 22), 0x0102);

        int optional = pe + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x10B);
        image[optional + 2] = 6;                                                        // linker 6.0
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), EntryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 28), ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 32), 0x1000);   // section alignment
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), 0x200);    // file alignment
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional + 68), 2);        // GUI subsystem
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 56), 0x2000);   // size of image
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), (uint)headersSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 92), 16);       // data directories

        int section = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0").CopyTo(image, section);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), sectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), sectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), headersSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 36), 0x60000020);

        int body = headersSize;

        // The entry point of a VB program: push the address of the VB5! structure, then call the
        // runtime. Only the push is read, but the call is what makes the stub recognisable.
        image[body] = 0x68;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(body + 1), ImageBase + HeaderRva);
        image[body + 5] = 0xE8;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(body + 6), 0);
        image[body + 10] = 0xC3;

        // The VB5! structure.
        int header = body + (int)(HeaderRva - sectionRva);
        "VB5!"u8.CopyTo(image.AsSpan(header, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(header + 4), 0x1FF0);       // runtime build
        FixedString(image, header + 6, 14, "VB6EN.DLL");
        FixedString(image, header + 20, 14, "*");
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(header + 34), 6);           // runtime DLL version
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 36), 0x409);       // language id
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 40), 0x409);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 44), 0);           // no Sub Main
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 48), ImageBase + ProjectInfoRva);

        // The object table: what a real project's data points at, holding one descriptor per form,
        // class and module. The descriptors are the published 0x30-byte records, and the two names
        // sit right after them, where the compiler puts them.
        if (objectTable)
        {
            int table = body + (int)(ObjectTableRva - sectionRva);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(table + 0x28), 0x000A);   // compile state
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(table + 0x2A), 2);        // objects
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(table + 0x2C), 2);        // compiled
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(table + 0x30), ImageBase + ObjectArrayRva);

            int array = body + (int)(ObjectArrayRva - sectionRva);
            WriteObjectDescriptor(image, array, ImageBase + FormNameRva, 6, FormType);
            WriteObjectDescriptor(image, array + 0x30, ImageBase + ModuleNameRva, 10, ModuleType);
            Encoding.ASCII.GetBytes("frmMain\0").CopyTo(image, body + (int)(FormNameRva - sectionRva));
            Encoding.ASCII.GetBytes("modMain\0").CopyTo(image, body + (int)(ModuleNameRva - sectionRva));
        }

        // ProjectInfo: the structure the p-code answer is read out of.
        int project = body + (int)(ProjectInfoRva - sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project), 0x1F4);                        // VB6
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 4), objectTable ? ImageBase + ObjectTableRva : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 12), ImageBase + 0x1400);       // start of code
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 16), ImageBase + 0x1500);       // end of code
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 20), 0x100);                    // data buffer
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 24), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 28), ImageBase + 0x1050);       // exception handler
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(project + 32), aNativeCode);

        WriteVb6RuntimeImport(image, optional, body);
        return image;
    }

    /// <summary>
    /// One object descriptor: the published 0x30-byte record, holding where the object's ObjectInfo
    /// is, where its name is, how many methods it declares, and what kind of object it is.
    /// </summary>
    private static void WriteObjectDescriptor(byte[] image, int at, uint nameVa, uint methodCount, uint typeFlags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), ImageBase + 0x1400);      // object info
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x18), nameVa);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x1C), methodCount);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x28), typeFlags);
    }

    /// <summary>
    /// An import table naming MSVBVM60.DLL, the Visual Basic 6 virtual machine. Without it the image
    /// carries none of the import evidence the <c>vb6-native</c> profile matches on, and the test
    /// could not tell a withdrawn suggestion from one that was never made.
    /// </summary>
    private static void WriteVb6RuntimeImport(byte[] image, int optional, int body)
    {
        const int sectionRva = 0x1000;
        const uint descriptorsRva = 0x1800;
        const uint iltRva = 0x1840;
        const uint iatRva = 0x1860;
        const uint functionNameRva = 0x1880;
        const uint dllNameRva = 0x18A0;

        void WriteRva(uint at, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(body + (int)(at - sectionRva)), value);

        // One descriptor: the ILT, the DLL name, the IAT. A zeroed descriptor ends the array.
        WriteRva(descriptorsRva, ImageBase + iltRva);
        WriteRva(descriptorsRva + 12, ImageBase + dllNameRva);
        WriteRva(descriptorsRva + 16, ImageBase + iatRva);

        WriteRva(iltRva, ImageBase + functionNameRva);
        WriteRva(iatRva, ImageBase + functionNameRva);

        // Hint/name entry: two bytes of hint, then the name a VB program's entry stub calls.
        byte[] function = Encoding.ASCII.GetBytes("\0\0ThunRTMain\0");
        function.CopyTo(image, body + (int)(functionNameRva - sectionRva));
        Encoding.ASCII.GetBytes("MSVBVM60.DLL\0").CopyTo(image, body + (int)(dllNameRva - sectionRva));

        // Data directory 1 is the import table.
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 96 + 8), descriptorsRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 100 + 8), 40);
    }

    /// <summary>A VB string field: fixed width, NUL-padded.</summary>
    private static void FixedString(byte[] image, int offset, int width, string value)
    {
        Array.Clear(image, offset, width);
        Encoding.ASCII.GetBytes(value, image.AsSpan(offset, Math.Min(width, value.Length)));
    }
}
