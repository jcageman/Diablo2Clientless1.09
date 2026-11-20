using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace D2NG.Core.D2GS.Helpers;

/// <summary>
/// Reads bits least significant first, as a BitArray over the bytes numbers them. Reading past the end
/// throws what that BitArray's indexer threw, for the first index past the end.
/// </summary>
internal class BitReader
{
    private readonly byte[] _bytes;

    private readonly int _bitCount;

    // Current offset in the stream
    private int Offset;

    public BitReader(byte[] bytes)
    {
        if (bytes == null)
        {
            _ = new BitArray(bytes);
        }

        _bytes = bytes;
        _bitCount = bytes.Length * 8;
        Offset = 0;
    }

    /**
      * Read a bit from the stream, the offset then points
      * to the next value in the BitArray
      */
    public bool ReadBit()
    {
        var offset = Offset++;
        if (offset >= _bitCount)
        {
            ThrowPastEnd(offset);
        }

        return ((_bytes[offset >> 3] >> (offset & 7)) & 1) != 0;
    }

    public byte ReadByte() => (byte)Read(8);
    public short ReadInt16() => (short)Read(16);
    public int ReadInt32() => Read(32);
    public ushort ReadUInt16() => (ushort)Read(16);
    public uint ReadUInt32() => (uint)Read(32);

    /**
      * Read up to 32 bits in the stream, read in
      * assumes Little Endian
      * Throws: ArgumentOutOfRangeException if the passed in parameter is greater than 32.
      */
    public int Read(int length)
    {
        if (length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "BitReader cannot return more than 32 bits");
        }
        return ReadBitsLittleEndian(length);
    }

    /**
      * Read in bytes one at a time until a null character (0x00) is found
      * Return as a Base64 String.
      */
    public string ReadString()
    {
        var str = new List<byte>();
        do
        {
            str.Add((byte)Read(8));
        } while (str.Last() != 0x00);

        return Convert.ToBase64String(str.ToArray());
    }

    protected int ReadBitsLittleEndian(int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        var end = Offset + length;
        if (end > _bitCount)
        {
            var failed = Math.Max(Offset, _bitCount);
            Offset = failed + 1;
            ThrowPastEnd(failed);
        }

        var first = Offset >> 3;
        var shift = Offset & 7;
        var last = (end - 1) >> 3;
        ulong chunk = 0;
        for (var i = first; i <= last; i++)
        {
            chunk |= (ulong)_bytes[i] << ((i - first) * 8);
        }

        Offset = end;
        return (int)((chunk >> shift) & ((1UL << length) - 1));
    }

    public int ReadBitsBigEndian(int length)
    {
        var bits = 0;
        while (length > 0)
        {
            bits <<= 1;
            bool bit = ReadBit();
            bits += bit ? 1 : 0;
            length -= 1;
        }
        return bits;
    }

    [DoesNotReturn]
    private void ThrowPastEnd(int index)
    {
        _ = new BitArray(_bytes)[index];
        throw new InvalidOperationException("unreachable");
    }
}
