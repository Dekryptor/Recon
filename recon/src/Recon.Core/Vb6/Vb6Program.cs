using System.Buffers.Binary;
using System.Text;
using Recon.Pe;

namespace Recon.Vb6;

/// <summary>One object of a Visual Basic project: a form, a class module or a standard module.</summary>
public sealed class Vb6Object
{
    /// <summary>The name the compiler recorded, as it reads in the project. Empty when unreadable.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// What the object is, from its type word: <c>form</c>, <c>class</c>, <c>module</c>, or the raw
    /// word when it is none of the ones this knows.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The type word itself, so that an unfamiliar kind can be read rather than guessed at.</summary>
    public uint TypeFlags { get; init; }

    /// <summary>The number of methods the object's descriptor declares.</summary>
    public int MethodCount { get; init; }

    /// <summary>The object's type-library GUID, when it has one.</summary>
    public string? Guid { get; init; }

    /// <summary>Where the object's descriptor is, relative to the image base.</summary>
    public uint DescriptorRva { get; init; }

    public string Describe()
        => MethodCount > 0 ? $"{Kind} \"{Name}\", {MethodCount} method(s)" : $"{Kind} \"{Name}\"";
}

/// <summary>
/// The project data of a Visual Basic 5/6 program: the structure the VB header points at, which
/// describes the project rather than any one object in it.
/// </summary>
public sealed class Vb6ProjectData
{
    /// <summary>The project template version: 0x1F4 (500) for a VB6 program.</summary>
    public uint Version { get; init; }

    public uint ObjectTableRva { get; init; }

    /// <summary>The compiled code, as a range. For a p-code program this is the p-code.</summary>
    public uint CodeStartRva { get; init; }

    public uint CodeEndRva { get; init; }

    public uint CodeSize => CodeEndRva > CodeStartRva ? CodeEndRva - CodeStartRva : 0;

    /// <summary>Size of the per-object data the runtime allocates for this project.</summary>
    public uint DataSize { get; init; }

    /// <summary>Where the native code would go, or 0 for a p-code program. This is <c>aNativeCode</c>.</summary>
    public uint NativeCodeRva { get; init; }

    /// <summary>The path the program was built in, recorded by the compiler in UTF-16.</summary>
    public string BuildPath { get; init; } = string.Empty;

    public bool IsPcode => NativeCodeRva == 0;
}

/// <summary>
/// A Visual Basic 5/6 program, read as the structure it is.
///
/// The chain is: the entry point pushes the address of the VB header and calls <c>ThunRTMain</c>; the
/// header names the project data; the project data names the object table; the object table lists one
/// descriptor per form, class and module in the project. Every link is a pointer, and every pointer
/// is checked to land inside the image before it is followed, because a VB program is the one kind of
/// PE whose most interesting structure is reached entirely by pointers.
/// </summary>
/// <remarks>
/// What this reads is the project's *shape* — how many objects, what they are called, what kind each
/// one is, how many methods each declares. That is the same in a native program and a p-code one,
/// which is why it is read from real programs here rather than held back until a p-code specimen
/// turns up.
///
/// What it deliberately does *not* do is pretend to find each method's code. That is where the two
/// compilation modes part company, and the method table that would say so is only meaningful in a
/// program that has one: in five real native VB6 programs the object's method-table pointer is a
/// field the linker never filled in, and following it leads out of the file. Reporting that, rather
/// than reading whatever is there, is the difference between this and a tool that guesses.
/// </remarks>
public sealed class Vb6Program
{
    // Offsets inside ProjectData, from the published structure tables and checked against real files.
    private const int VersionOffset = 0x00;
    private const int ObjectTableOffset = 0x04;
    private const int CodeStartOffset = 0x0C;
    private const int CodeEndOffset = 0x10;
    private const int DataSizeOffset = 0x14;
    private const int NativeCodeOffset = 0x20;
    private const int BuildPathOffset = 0x24;
    private const int BuildPathLength = 528;

    // Offsets inside the object table.
    private const int CompileStateOffset = 0x28;
    private const int TotalObjectsOffset = 0x2A;
    private const int CompiledObjectsOffset = 0x2C;
    private const int ObjectArrayOffset = 0x30;

    /// <summary>What the compiler writes at the object table's compile-state word, in every real file.</summary>
    private const ushort CompiledState = 0x000A;

    /// <summary>One descriptor per object: this is the stride between them.</summary>
    private const int DescriptorSize = 0x30;

    // Offsets inside an object descriptor.
    private const int DescriptorNameOffset = 0x18;
    private const int DescriptorMethodCountOffset = 0x1C;
    private const int DescriptorTypeOffset = 0x28;

    public Vb6HeaderInfo Header { get; init; } = new();

    public Vb6ProjectData Project { get; init; } = new();

    public IReadOnlyList<Vb6Object> Objects { get; init; } = [];

    /// <summary>The compile-state word, which real programs set to 0x000A.</summary>
    public ushort CompileState { get; init; }

    /// <summary>Objects the table declares, and objects it says were compiled.</summary>
    public int ObjectsDeclared { get; init; }

    /// <remarks>
    /// Reported rather than checked against <see cref="ObjectsDeclared"/>: in one of the five real
    /// programs measured here the compiled count is larger — 76 against 73 — so the two are not two
    /// spellings of one number, and a reader that insisted they match would be inventing a rule the
    /// compiler does not follow. The invariant that <i>is</i> measurable is the compile state above.
    /// </remarks>
    public int ObjectsCompiled { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool Ok => Problems.Count == 0;

    /// <summary>Reads a VB program, or returns null when the file is not one.</summary>
    public static Vb6Program? Read(PeImage image, byte[] bytes)
    {
        var header = Vb6Header.TryRead(image, bytes);
        if (header is null)
        {
            return null;
        }

        var problems = new List<string>();
        var project = ReadProjectData(image, bytes, header, problems);
        var objects = new List<Vb6Object>();
        ushort compileState = 0;
        int declared = 0;
        int compiled = 0;

        int? tableOffset = Resolve(image, bytes, project.ObjectTableRva);
        if (tableOffset is null)
        {
            problems.Add($"the project names an object table at rva 0x{project.ObjectTableRva:X}, which is not in the image");
        }
        else
        {
            int t = tableOffset.Value;
            compileState = ReadUInt16(bytes, t + CompileStateOffset);
            int total = ReadUInt16(bytes, t + TotalObjectsOffset);
            declared = total;
            compiled = ReadUInt16(bytes, t + CompiledObjectsOffset);
            uint arrayRva = ToRva(image.ImageBase, ReadUInt32(bytes, t + ObjectArrayOffset));

            if (compileState != CompiledState)
            {
                problems.Add($"the object table's compile-state word is 0x{compileState:X4}, not the 0x{CompiledState:X4} of a compiled program");
            }

            int? arrayOffset = Resolve(image, bytes, arrayRva);
            if (arrayOffset is null)
            {
                problems.Add($"the object array at rva 0x{arrayRva:X} is not in the image");
            }
            else
            {
                for (int i = 0; i < total; i++)
                {
                    int at = arrayOffset.Value + (i * DescriptorSize);
                    if (at + DescriptorSize > bytes.Length)
                    {
                        problems.Add($"object {i} of {total} is past the end of the file");
                        break;
                    }

                    objects.Add(ReadObject(image, bytes, at, arrayRva + (uint)(i * DescriptorSize), i, problems));
                }
            }
        }

        return new Vb6Program
        {
            Header = header,
            Project = project,
            Objects = objects,
            CompileState = compileState,
            ObjectsDeclared = declared,
            ObjectsCompiled = compiled,
            Problems = problems,
        };
    }

    private static Vb6ProjectData ReadProjectData(PeImage image, byte[] bytes, Vb6HeaderInfo header, List<string> problems)
    {
        int? offset = Resolve(image, bytes, header.ProjectInfoRva);
        if (offset is null || offset.Value + BuildPathOffset + 2 > bytes.Length)
        {
            problems.Add("the VB header names project data that is not in the image");
            return new Vb6ProjectData();
        }

        int p = offset.Value;
        return new Vb6ProjectData
        {
            Version = ReadUInt32(bytes, p + VersionOffset),
            ObjectTableRva = ToRva(image.ImageBase, ReadUInt32(bytes, p + ObjectTableOffset)),
            CodeStartRva = ToRva(image.ImageBase, ReadUInt32(bytes, p + CodeStartOffset)),
            CodeEndRva = ToRva(image.ImageBase, ReadUInt32(bytes, p + CodeEndOffset)),
            DataSize = ReadUInt32(bytes, p + DataSizeOffset),
            NativeCodeRva = ToRva(image.ImageBase, ReadUInt32(bytes, p + NativeCodeOffset)),
            BuildPath = FixedWideString(bytes, p + BuildPathOffset, BuildPathLength),
        };
    }

    private static Vb6Object ReadObject(PeImage image, byte[] bytes, int at, uint descriptorRva, int index, List<string> problems)
    {
        uint nameRva = ToRva(image.ImageBase, ReadUInt32(bytes, at + DescriptorNameOffset));
        uint typeFlags = ReadUInt32(bytes, at + DescriptorTypeOffset);
        int methodCount = (int)ReadUInt32(bytes, at + DescriptorMethodCountOffset);

        string name = string.Empty;
        int? nameOffset = Resolve(image, bytes, nameRva);
        if (nameOffset is null)
        {
            problems.Add($"object {index}'s name at rva 0x{nameRva:X} is not in the image");
        }
        else
        {
            name = NullTerminatedAscii(bytes, nameOffset.Value);
            if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7E))
            {
                problems.Add($"object {index}'s name is not readable text");
                name = string.Empty;
            }
        }

        return new Vb6Object
        {
            Name = name,
            Kind = KindOf(typeFlags),
            TypeFlags = typeFlags,
            MethodCount = methodCount,
            DescriptorRva = descriptorRva,
        };
    }

    /// <summary>
    /// What the object is. The type word's low bits are the kind and the rest is flags — a form is
    /// <c>0x00018083</c>, a standard module <c>0x00018001</c>, a class module <c>0x00118003</c> — so
    /// the kind is read out of the low byte and an unfamiliar one is reported as the raw word rather
    /// than forced into a category.
    /// </summary>
    public static string KindOf(uint typeFlags) => (typeFlags & 0xFF) switch
    {
        0x01 => "standard module",
        0x03 => "class module",
        0x83 => "form",
        0x21 => "user control",
        0x22 => "property page",
        _ => $"object (type 0x{typeFlags:X8})",
    };

    /// <summary>
    /// Turns a pointer into a file offset, or null when it does not point into the image. Every
    /// pointer in these structures is a virtual address, and the ones that are not set are not zero
    /// either — they are whatever was in the linker's buffer — so a pointer is followed only after it
    /// has been checked.
    /// </summary>
    private static int? Resolve(PeImage image, byte[] bytes, uint rva)
    {
        int? offset = PeLoader.RvaToOffset(image, rva);
        return offset is >= 0 && offset < bytes.Length ? offset : null;
    }

    private static uint ToRva(ulong imageBase, uint va) => va == 0 ? 0u : va - (uint)imageBase;

    private static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static ushort ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static string NullTerminatedAscii(byte[] bytes, int offset)
    {
        int end = Array.IndexOf(bytes, (byte)0, offset);
        if (end < 0)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    /// <summary>The build path the compiler recorded, which is UTF-16 and NUL-padded.</summary>
    private static string FixedWideString(byte[] bytes, int offset, int length)
    {
        int end = offset;
        while (end + 1 < offset + length && end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(bytes, offset, end - offset);
    }
}
