using System.Buffers.Binary;
using System.Text;

namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// Little-endian payload writer. All multi-byte integers on the wire use
/// <see cref="BinaryPrimitives"/> LE — never <see cref="BitConverter"/>, whose
/// result is platform dependent.
/// </summary>
public sealed class PayloadWriter
{
    private byte[] _buf;
    private int _len;

    public PayloadWriter(int capacity = 64) => _buf = new byte[capacity];

    public int Length => _len;

    private Span<byte> Reserve(int count)
    {
        if (_len + count > _buf.Length)
        {
            int next = Math.Max(_buf.Length * 2, _len + count);
            Array.Resize(ref _buf, next);
        }
        Span<byte> span = _buf.AsSpan(_len, count);
        _len += count;
        return span;
    }

    public void WriteByte(byte value) => Reserve(1)[0] = value;
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    public void WriteRaw(ReadOnlySpan<byte> value) => value.CopyTo(Reserve(value.Length));

    /// <summary>Length-prefixed (uint32) byte blob.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        WriteRaw(value);
    }

    /// <summary>Length-prefixed (uint32) UTF-8 string.</summary>
    public void WriteString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(byteCount);
        Encoding.UTF8.GetBytes(value, Reserve(byteCount));
    }

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
}

/// <summary>
/// Little-endian payload reader over a byte span. Throws
/// <see cref="ProtocolException"/> on any short/invalid read so a malformed peer
/// never causes an out-of-range access.
/// </summary>
public ref struct PayloadReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public PayloadReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new ProtocolException($"Payload underrun: need {count} bytes, have {Remaining}.");
        ReadOnlySpan<byte> span = _data.Slice(_pos, count);
        _pos += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => ReadByte() != 0;
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public ReadOnlySpan<byte> ReadRaw(int count) => Take(count);

    public byte[] ReadBytes()
    {
        int len = ReadInt32();
        return Take(len).ToArray();
    }

    public string ReadString()
    {
        int len = ReadInt32();
        return Encoding.UTF8.GetString(Take(len));
    }
}

/// <summary>Raised on any malformed frame or payload. Callers drop the connection.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
