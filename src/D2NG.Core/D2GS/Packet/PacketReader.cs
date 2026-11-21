using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace D2NG.Core.D2GS.Packet;

/// <summary>
/// Little-endian reads over a packet, in place of a BinaryReader over a MemoryStream. Reading past the
/// end throws the EndOfStreamException, with the message, that the BinaryReader threw, so a short packet
/// fails exactly as it did; a skipped field still has to be there.
/// </summary>
internal ref struct PacketReader(ReadOnlySpan<byte> data)
{
    internal const string EndOfStreamMessage = "Unable to read beyond the end of the stream.";

    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            ThrowEndOfStream();
        }

        return _data[_position++];
    }

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public void Skip(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (_data.Length - _position < count)
        {
            ThrowEndOfStream();
        }

        var taken = _data.Slice(_position, count);
        _position += count;
        return taken;
    }

    [DoesNotReturn]
    private static void ThrowEndOfStream() => throw new EndOfStreamException(EndOfStreamMessage);
}
