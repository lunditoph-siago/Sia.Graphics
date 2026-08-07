using System.Buffers.Binary;

namespace Sia.Graphics.Text;

internal ref struct BigEndianReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    public int Position { get; set; }

    public readonly int Length => _data.Length;

    public byte ReadUInt8()
    {
        var value = _data[Position];
        Position += 1;
        return value;
    }

    public sbyte ReadInt8() => (sbyte)ReadUInt8();

    public ushort ReadUInt16()
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public short ReadInt16()
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public int ReadInt32()
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public readonly ReadOnlySpan<byte> Slice(int offset, int length) => _data.Slice(offset, length);

    public readonly BigEndianReader At(int offset) => new(_data) { Position = offset };
}
