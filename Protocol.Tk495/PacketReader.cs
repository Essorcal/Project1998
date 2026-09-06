using System.Buffers.Binary;

namespace Protocol.Tk495;

/// <summary>A bounds-checked cursor over a packet body. A failed field read never advances the cursor.</summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _bytes;
    public int Position { get; private set; }
    public int Remaining => _bytes.Length - Position;

    public PacketReader(ReadOnlySpan<byte> bytes) { _bytes = bytes; Position = 0; }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || count > Remaining) throw new ArgumentOutOfRangeException(nameof(count));
        var result = _bytes.Slice(Position, count);
        Position += count;
        return result;
    }

    public byte U8() => Bytes(1)[0];
    public ushort U16BE() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));
    public uint U32BE() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));

    public ReadOnlySpan<byte> Str8()
    {
        if (Remaining < 1 || _bytes[Position] > Remaining - 1)
            throw new ArgumentOutOfRangeException(nameof(Position), "Truncated byte-counted string.");
        int count = U8();
        return Bytes(count);
    }
}
