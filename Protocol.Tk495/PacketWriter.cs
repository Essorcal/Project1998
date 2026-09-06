using System.Buffers.Binary;

namespace Protocol.Tk495;

/// <summary>Big-endian packet bodies. Callers own text encoding and any era-specific field limits.</summary>
public sealed class PacketWriter
{
    private readonly List<byte> _bytes = new();

    public PacketWriter U8(byte value) { _bytes.Add(value); return this; }
    public PacketWriter U16BE(ushort value) { AppendU16BE(_bytes, value); return this; }
    public PacketWriter U32BE(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return Bytes(bytes);
    }
    public PacketWriter Bytes(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value) _bytes.Add(b);
        return this;
    }

    /// <summary>A byte-counted string of already encoded bytes. Oversized fields are rejected before writing.</summary>
    public PacketWriter Str8(ReadOnlySpan<byte> value)
    {
        if (value.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        return U8((byte)value.Length).Bytes(value);
    }

    public byte[] ToArray() => _bytes.ToArray();

    /// <summary>Append without allocating a temporary array, including in the terrain-cell hot loop.</summary>
    public static void AppendU16BE(List<byte> into, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        into.Add(bytes[0]);
        into.Add(bytes[1]);
    }

    // Adapters for the remaining List<byte> builders, preserving their allocations while they migrate.
    public static byte[] U16BEBytes(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U32BEBytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
