using System.Buffers.Binary;
using System.Text;

namespace Recon.Pe;

/// <summary>
/// The header of a Visual Basic 5/6 program: the structure whose address the entry point pushes
/// before calling <c>ThunRTMain</c> in MSVBVM60.DLL.
/// </summary>
/// <remarks>
/// The layout is the one the VB reversing literature publishes (Alex Ionescu's "Visual Basic Image
/// Internal Structure Format", Andrea Geddon's "VISUAL BASIC REVERSED", and the structure tables at
/// vb-decompiler.org), and it was checked against a real VB6 binary: the signature is <c>VB5!</c>,
/// the language DLL reads <c>VB6DE.DLL</c>, the template version is 0x1F4 (500, which is VB6), and
/// the pointers that follow land inside the image.
///
/// What this is for is one field: <see cref="NativeCodeRva"/>. VB6 compiles either to native x86 or
/// to p-code that MSVBVM60 interprets at run time, and no x86 decoder can say anything true about a
/// p-code stream — it would read tokens as opcodes and produce a program that looks like an answer.
/// So a program with <c>aNativeCode == 0</c> is reported as p-code rather than decoded.
/// </remarks>
public sealed class Vb6HeaderInfo
{
    /// <summary>Always <c>VB5!</c>, including in a VB6 program — the signature was never changed.</summary>
    public string Signature { get; set; } = string.Empty;

    public uint HeaderRva { get; set; }

    /// <summary>The runtime build word that follows the signature.</summary>
    public ushort RuntimeBuild { get; set; }

    /// <summary>The language DLL the program asks for, as in <c>VB6DE.DLL</c>.</summary>
    public string LanguageDll { get; set; } = string.Empty;

    public string BackupLanguageDll { get; set; } = string.Empty;

    /// <summary>The version of the runtime DLL the program was built against.</summary>
    public ushort RuntimeDllVersion { get; set; }

    public uint LanguageId { get; set; }

    /// <summary>The procedure the runtime calls after it starts, or 0 when a form is shown instead.</summary>
    public uint SubMainRva { get; set; }

    public uint ProjectInfoRva { get; set; }

    /// <summary>
    /// The project's template version: 0x1F4 (500) for a VB6 program. It is the number that says
    /// which VB made this, whatever the four-byte signature still says.
    /// </summary>
    public uint TemplateVersion { get; set; }

    /// <summary>Where the program's code begins, as the compiler recorded it.</summary>
    public uint CodeStartRva { get; set; }

    public uint CodeEndRva { get; set; }

    public uint DataBufferSize { get; set; }

    public uint ExceptionHandlerRva { get; set; }

    /// <summary>
    /// Where the raw native code starts, or <b>0 when there is none</b> — which is what p-code
    /// means. This is the whole reason the structure is read.
    /// </summary>
    /// <remarks>
    /// Every pointer in this structure is stored as a virtual address, assuming the image loads at
    /// its preferred base — which a VB program always does, VB6 not producing relocatable images.
    /// All of them are converted to RVAs here, so that a field named <c>rva</c> holds one.
    /// </remarks>
    public uint NativeCodeRva { get; set; }

    /// <summary>True when the program is interpreted p-code rather than native x86.</summary>
    public bool IsPcode => NativeCodeRva == 0;

    /// <summary>One line for a report: what kind of program it is, and the evidence for saying so.</summary>
    public string Describe()
    {
        string kind = IsPcode
            ? "p-code (no native code in the image: aNativeCode is 0)"
            : $"native code at rva 0x{NativeCodeRva:X}";
        string version = TemplateVersion == 0x1F4 ? "VB6" : $"template 0x{TemplateVersion:X}";
        string language = LanguageDll.Length > 0 ? $" {LanguageDll}" : string.Empty;
        return $"VB5! header: {version}{language}, {kind}";
    }
}

/// <summary>Finds and reads the VB5! structure in a PE image, if there is one.</summary>
public static class Vb6Header
{
    private const string Signature = "VB5!";

    // Offsets inside the VB header, from the published structure tables.
    private const int RuntimeBuildOffset = 4;
    private const int LanguageDllOffset = 6;
    private const int BackupLanguageDllOffset = 20;
    private const int RuntimeDllVersionOffset = 34;
    private const int LanguageIdOffset = 36;
    private const int SubMainOffset = 44;
    private const int ProjectInfoOffset = 48;

    // Offsets inside ProjectInfo.
    private const int TemplateVersionOffset = 0;
    private const int CodeStartOffset = 12;
    private const int CodeEndOffset = 16;
    private const int DataBufferSizeOffset = 20;
    private const int ExceptionHandlerOffset = 28;
    private const int NativeCodeOffset = 32;

    /// <summary>
    /// Reads the header a VB program's entry point points at. Returns null when this is not a VB5/6
    /// program: no push of a structure address at the entry point, or no <c>VB5!</c> where it lands.
    /// </summary>
    public static Vb6HeaderInfo? TryRead(PeImage image, ReadOnlySpan<byte> bytes)
    {
        // The entry point of a VB program is:  push <address of VB header>  /  call ThunRTMain.
        int? entry = PeLoader.RvaToOffset(image, image.EntryPointRva);
        if (entry is null || entry.Value + 5 > bytes.Length || bytes[entry.Value] != 0x68)
        {
            return null;
        }

        uint pushed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entry.Value + 1)..]);
        if (pushed < image.ImageBase)
        {
            return null;
        }

        uint headerRva = pushed - (uint)image.ImageBase;
        int? header = PeLoader.RvaToOffset(image, headerRva);
        if (header is null || header.Value + ProjectInfoOffset + 4 > bytes.Length)
        {
            return null;
        }

        if (Encoding.ASCII.GetString(bytes.Slice(header.Value, 4)) != Signature)
        {
            return null;
        }

        int h = header.Value;
        var info = new Vb6HeaderInfo
        {
            Signature = Signature,
            HeaderRva = headerRva,
            RuntimeBuild = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(h + RuntimeBuildOffset)..]),
            LanguageDll = FixedString(bytes, h + LanguageDllOffset, 14),
            BackupLanguageDll = FixedString(bytes, h + BackupLanguageDllOffset, 14),
            RuntimeDllVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(h + RuntimeDllVersionOffset)..]),
            LanguageId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(h + LanguageIdOffset)..]),
            SubMainRva = ToRva(image.ImageBase, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(h + SubMainOffset)..])),
            ProjectInfoRva = ToRva(image.ImageBase, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(h + ProjectInfoOffset)..])),
        };

        // A pointer that does not point into the image is not a pointer, however right the signature
        // looked: this is the one field the p-code answer depends on, so it is checked rather than
        // trusted.
        int? project = PeLoader.RvaToOffset(image, info.ProjectInfoRva);
        if (project is null || project.Value + NativeCodeOffset + 4 > bytes.Length)
        {
            return null;
        }

        int p = project.Value;
        info.TemplateVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + TemplateVersionOffset)..]);
        info.CodeStartRva = ToRva(image.ImageBase, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + CodeStartOffset)..]));
        info.CodeEndRva = ToRva(image.ImageBase, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + CodeEndOffset)..]));
        info.DataBufferSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + DataBufferSizeOffset)..]);
        info.ExceptionHandlerRva = ToRva(image.ImageBase, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + ExceptionHandlerOffset)..]));
        info.NativeCodeRva = ToRva(image.ImageBase, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + NativeCodeOffset)..]));
        return info;
    }

    /// <summary>
    /// A VB pointer is a virtual address, not an RVA: this turns it into one. A pointer below the
    /// image base is not a pointer at all, and zero stays zero wherever it appears — which is the
    /// p-code signal and must survive the conversion.
    /// </summary>
    private static uint ToRva(ulong imageBase, uint va) => va == 0 ? 0u : va - (uint)imageBase;

    /// <summary>A VB string field: fixed width, NUL-padded, NUL-terminated.</summary>
    private static string FixedString(ReadOnlySpan<byte> bytes, int offset, int width)
    {
        if (offset + width > bytes.Length)
        {
            return string.Empty;
        }

        int end = bytes[offset..(offset + width)].IndexOf((byte)0);
        return Encoding.ASCII.GetString(bytes.Slice(offset, end < 0 ? width : end));
    }
}
