using System.Buffers.Binary;
using System.Text;

namespace RailWerkz.Network;

/// Builds a message: id byte first, then little-endian fields.
public sealed class NetWriter
{
    private byte[] _buffer = new byte[256];

    public int Length { get; private set; }

    public NetWriter Begin(byte messageId)
    {
        Length = 0;
        return U8((byte)(Session.UserBase + messageId));
    }

    private Span<byte> Take(int count)
    {
        if (Length + count > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));

        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }

    public NetWriter U8(byte value)
    {
        Take(1)[0] = value;
        return this;
    }

    public NetWriter Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public NetWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);
        return this;
    }

    public NetWriter I16(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(Take(2), value);
        return this;
    }

    public NetWriter I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
        return this;
    }

    public NetWriter U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);
        return this;
    }

    public NetWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);
        return this;
    }

    public NetWriter Vec3(float3 value) => F32(value.X).F32(value.Y).F32(value.Z);

    /// An angle in 16 bits.
    public NetWriter Angle(float radians) => U16((ushort)(((radians / MathF.Tau) % 1f + 1f) % 1f * 65535f));

    public NetWriter Str(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var count = Math.Min(bytes.Length, 255);
        U8((byte)count);
        bytes.AsSpan(0, count).CopyTo(Take(count));
        return this;
    }

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);
}

/// Reads a message written by NetWriter, past its id byte. Reading off the end gives zeros rather than throwing.
public ref struct NetReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = 1;

    public readonly byte MessageId => (byte)(_data.Length > 0 ? _data[0] - Session.UserBase : 0);
    public readonly bool Overrun => _position > _data.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        var start = _position;
        _position += count;
        return _position <= _data.Length ? _data.Slice(start, count) : ReadOnlySpan<byte>.Empty;
    }

    public byte U8()
    {
        var s = Take(1);
        return s.Length == 1 ? s[0] : (byte)0;
    }

    public bool Bool() => U8() != 0;

    public ushort U16()
    {
        var s = Take(2);
        return s.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(s) : (ushort)0;
    }

    public short I16()
    {
        var s = Take(2);
        return s.Length == 2 ? BinaryPrimitives.ReadInt16LittleEndian(s) : (short)0;
    }

    public int I32()
    {
        var s = Take(4);
        return s.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(s) : 0;
    }

    public ulong U64()
    {
        var s = Take(8);
        return s.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(s) : 0;
    }

    public float F32()
    {
        var s = Take(4);
        return s.Length == 4 ? BinaryPrimitives.ReadSingleLittleEndian(s) : 0f;
    }

    public float3 Vec3() => new(F32(), F32(), F32());

    public float Angle() => U16() / 65535f * MathF.Tau;

    public string Str()
    {
        var count = U8();
        var s = Take(count);
        return s.Length == count ? Encoding.UTF8.GetString(s) : string.Empty;
    }
}
