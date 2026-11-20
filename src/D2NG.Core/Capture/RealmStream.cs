using System;
using System.Collections.Generic;

namespace D2NG.Core.Capture;

public enum CapturedProtocol : byte
{
    D2gs,
    Bncs,
    Mcp,
}

/// <summary>
/// One direction of a chat (BNCS) or realm (MCP) connection: length-prefixed packets, kept whole the way
/// the bot's connections record them. The client opens both with a protocol byte (0x01) that is no packet.
/// </summary>
public sealed class RealmStream
{
    private const int MaxPacket = 16 * 1024;

    private readonly CapturedProtocol _protocol;
    private byte[] _buffer = new byte[4096];
    private int _length;
    private bool _protocolByteChecked;
    private bool _synced;

    public RealmStream(CapturedProtocol protocol, bool clientSide, bool fromStart)
    {
        if (protocol == CapturedProtocol.D2gs)
        {
            throw new ArgumentException("A game server stream is not length prefixed", nameof(protocol));
        }

        _protocol = protocol;
        _protocolByteChecked = !(clientSide && fromStart);
        _synced = fromStart;
    }

    public int Resyncs { get; private set; }

    public long SkippedBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes, List<byte[]> packets)
    {
        if (_length + bytes.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        var offset = 0;
        if (!_protocolByteChecked && _length > 0)
        {
            _protocolByteChecked = true;
            if (_buffer[0] == 0x01)
            {
                offset = 1;
            }
        }

        while (offset < _length)
        {
            var rest = _buffer.AsSpan(offset, _length - offset);
            var size = Size(rest);
            if (size == 0)
            {
                break;
            }

            if (size < 0 || !_synced && !Plausible(rest))
            {
                _synced = false;
                SkippedBytes++;
                offset++;
                continue;
            }

            if (!_synced)
            {
                _synced = true;
                Resyncs++;
            }

            if (rest.Length < size)
            {
                break;
            }

            packets.Add(rest[..size].ToArray());
            offset += size;
        }

        Array.Copy(_buffer, offset, _buffer, 0, _length - offset);
        _length -= offset;
    }

    public void Lost()
    {
        SkippedBytes += _length;
        _length = 0;
        _synced = false;
    }

    /// <summary>0 while the header is incomplete, -1 when the header cannot start a packet.</summary>
    private int Size(ReadOnlySpan<byte> data)
    {
        if (_protocol == CapturedProtocol.Bncs)
        {
            if (data.Length < 1)
            {
                return 0;
            }

            if (data[0] != 0xFF)
            {
                return -1;
            }

            if (data.Length < 4)
            {
                return 0;
            }

            var bncs = data[2] | (data[3] << 8);
            return bncs is >= 4 and <= MaxPacket ? bncs : -1;
        }

        if (data.Length < 3)
        {
            return 0;
        }

        var mcp = data[0] | (data[1] << 8);
        return mcp is >= 3 and <= MaxPacket ? mcp : -1;
    }

    /// <summary>Out of step, a header is only believed when its packet id is one the protocol has.</summary>
    private bool Plausible(ReadOnlySpan<byte> data)
        => _protocol == CapturedProtocol.Bncs
            ? data.Length >= 2 && Enum.IsDefined(typeof(BNCS.Packet.Sid), data[1])
            : data.Length >= 3 && Enum.IsDefined(typeof(MCP.Packet.Mcp), data[2]);
}
