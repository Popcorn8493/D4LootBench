using System.Text;

namespace D4LootBench.Core.Codec;

/// <summary>
/// Minimal protobuf reader. Every malformed-input path (truncation, overlong varints,
/// out-of-range lengths, unsupported wire types) throws <see cref="FormatException"/> —
/// share codes are user-pasted, so garbage must never surface as IndexOutOfRange or OOM.
/// </summary>
internal sealed class ProtoReader
{
    private readonly byte[] _data;
    private int _pos;

    internal ProtoReader(byte[] data) { _data = data; _pos = 0; }

    internal bool HasData => _pos < _data.Length;
    internal int Position => _pos;
    private int Remaining => _data.Length - _pos;

    internal void Seek(int position)
    {
        if (position < 0 || position > _data.Length)
            throw new FormatException($"Invalid seek to {position} in a {_data.Length}-byte message.");
        _pos = position;
    }

    internal (int FieldNumber, int WireType) ReadTag()
    {
        var raw = ReadVarint();
        if (raw > uint.MaxValue)
            throw new FormatException($"Invalid protobuf tag at offset {_pos}.");
        var tag = (uint)raw;
        return ((int)(tag >> 3), (int)(tag & 0x07));
    }

    internal ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _data.Length)
                throw new FormatException("Truncated varint: unexpected end of data.");
            if (shift > 63)
                throw new FormatException($"Malformed varint at offset {_pos}: more than 10 bytes.");
            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    internal uint ReadFixed32()
    {
        Require(4, "fixed32");
        var v = (uint)(_data[_pos]
            | (_data[_pos + 1] << 8)
            | (_data[_pos + 2] << 16)
            | (_data[_pos + 3] << 24));
        _pos += 4;
        return v;
    }

    internal byte[] ReadLenBytes()
    {
        var len = ReadVarint();
        // Bounds-check BEFORE allocating: a garbage length must not trigger a huge allocation.
        if (len > (ulong)Remaining)
            throw new FormatException(
                $"Length-delimited field of {len} bytes exceeds the {Remaining} bytes remaining.");
        var bytes = new byte[(int)len];
        Array.Copy(_data, _pos, bytes, 0, (int)len);
        _pos += (int)len;
        return bytes;
    }

    internal string ReadString() => Encoding.UTF8.GetString(ReadLenBytes());

    internal void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: Require(8, "fixed64"); _pos += 8; break;
            case 2: ReadLenBytes(); break;
            case 5: Require(4, "fixed32"); _pos += 4; break;
            // 3/4 are the deprecated group markers (never emitted by the game); 6/7 are undefined.
            default: throw new FormatException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    private void Require(int count, string what)
    {
        if (Remaining < count)
            throw new FormatException($"Truncated {what}: need {count} bytes, {Remaining} remaining.");
    }
}
