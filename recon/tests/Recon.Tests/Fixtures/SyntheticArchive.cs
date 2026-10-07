using System.Buffers.Binary;
using System.Text;

namespace Recon.Tests.Fixtures;

/// <summary>
/// Builds an <c>!&lt;arch&gt;</c> archive in memory: the container every Windows <c>.lib</c> and every
/// Unix <c>.a</c> uses, and the one <c>recon lib</c> reads.
///
/// These are shapes, not specimens. They cover the walk — alignment, long names, a member that runs
/// off the end — which cannot be checked in, being Microsoft binaries, and so is only read when
/// <c>RECON_LIB_INPUTS</c> names a directory holding them; see <see cref="CoffArchiveTests"/>.
/// </summary>
public static class SyntheticArchive
{
    private const int HeaderSize = 60;

    /// <summary>A COFF header describing an object with no sections and no symbols: empty, but shaped like one.</summary>
    public static byte[] EmptyObject(int size = 20)
    {
        var bytes = new byte[size];
        return bytes;
    }

    /// <summary>
    /// A short import member: the record an import library is made of, saying "this symbol comes from
    /// that DLL". The layout is the documented one, and <c>CoffArchiveTests</c> checks the reader
    /// against a real import library with 20,551 of them — this fixture is only here so that the
    /// command's behaviour can be tested without a five-megabyte input.
    /// </summary>
    public static byte[] ShortImport(string dll, string symbol, ushort machine = 0x8664, ushort ordinal = 0)
    {
        var bytes = new List<byte>();
        var head = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0), 0x0000);       // Sig1
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), 0xFFFF);       // Sig2
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), 1);            // version
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), 0x5F000000);   // timestamp
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 0);           // size of data
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(16), ordinal);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(18), 1);           // name type: import by name
        bytes.AddRange(head);
        bytes.AddRange(Encoding.ASCII.GetBytes(symbol));
        bytes.Add(0);
        bytes.AddRange(Encoding.ASCII.GetBytes(dll));
        bytes.Add(0);
        return bytes.ToArray();
    }

    /// <summary>
    /// An archive holding the given members. A name longer than sixteen characters is written into a
    /// long-name table and referred to as <c>/N</c>, the way both librarians do it.
    /// </summary>
    public static byte[] Build(params (string Name, byte[] Data)[] members)
    {
        var bytes = new List<byte>();
        bytes.AddRange("!<arch>\n"u8);

        // The long-name member comes first, because later members refer to it by offset.
        var longNames = new StringBuilder();
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, _) in members)
        {
            if (name.Length <= 15)
            {
                continue;
            }

            offsets[name] = longNames.Length;
            longNames.Append(name).Append('/').Append('\n');
        }

        if (longNames.Length > 0)
        {
            bytes.AddRange(Member("//", Encoding.ASCII.GetBytes(longNames.ToString())));
        }

        foreach (var (name, data) in members)
        {
            string header = offsets.TryGetValue(name, out int at) ? $"/{at}" : name;
            bytes.AddRange(Member(header, data));
        }

        return bytes.ToArray();
    }

    /// <summary>One member: a 60-byte header of decimal ASCII, its contents, and any pad byte.</summary>
    public static byte[] Member(string name, byte[] data) => Member(name, data, data.Length);

    /// <summary>
    /// One member whose header claims a size that need not match the contents, which is how a
    /// truncated file looks: the walk has to notice before it reads past the end.
    /// </summary>
    public static byte[] Member(string name, byte[] data, int declaredSize)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Header(name, declaredSize));

        for (int i = 0; i < declaredSize; i++)
        {
            bytes.Add(i < data.Length ? data[i] : (byte)0);
        }

        // The alignment byte is not part of the member, which is why it comes after the contents.
        if ((declaredSize & 1) == 1)
        {
            bytes.Add((byte)'\n');
        }

        return bytes.ToArray();
    }

    public static byte[] Header(string name, int size)
    {
        var header = new byte[HeaderSize];
        Array.Fill(header, (byte)' ');
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0").CopyTo(header, 16);        // date
        Encoding.ASCII.GetBytes(size.ToString()).CopyTo(header, 48);
        header[58] = 0x60;                                      // "`\n": the terminator a walk looks for
        header[59] = 0x0A;
        return header;
    }
}
