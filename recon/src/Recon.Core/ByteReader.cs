using System.Buffers.Binary;
using System.Text;

namespace Recon;

/// <summary>
/// Bounds-checked reads over an image, in either byte order. Returns null instead of throwing:
/// a file that ends early is data, not an exception.
/// </summary>
public sealed class ByteReader(byte[] bytes, bool bigEndian = false)
{
    private readonly byte[] _bytes = bytes;

    /// <summary>True when multi-byte fields are most-significant byte first (ELF can be either).</summary>
    public bool BigEndian => bigEndian;

    public byte[] Data => _bytes;

    public int Length => _bytes.Length;

    public bool Contains(int offset, int length) => offset >= 0 && length >= 0 && offset + length <= _bytes.Length;

    public byte? U8(int offset) => Contains(offset, 1) ? _bytes[offset] : null;

    public ushort? U16(int offset)
    {
        if (!Contains(offset, 2))
        {
            return null;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset, 2));
    }

    public uint? U32(int offset)
    {
        if (!Contains(offset, 4))
        {
            return null;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset, 4));
    }

    public ulong? U64(int offset)
    {
        if (!Contains(offset, 8))
        {
            return null;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(_bytes.AsSpan(offset, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(offset, 8));
    }

    public ReadOnlySpan<byte> Span(int offset, int length)
        => Contains(offset, length) ? _bytes.AsSpan(offset, length) : default;

    /// <summary>NUL-terminated ASCII string, at most <paramref name="maxLength"/> bytes.</summary>
    public string? AsciiZ(int offset, int maxLength = 512)
    {
        if (!Contains(offset, 1))
        {
            return null;
        }

        int end = offset;
        int limit = Math.Min(_bytes.Length, offset + maxLength);
        while (end < limit && _bytes[end] != 0)
        {
            end++;
        }

        return Encoding.Latin1.GetString(_bytes, offset, end - offset);
    }
}
