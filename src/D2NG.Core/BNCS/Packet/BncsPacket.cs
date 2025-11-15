using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.BNCS.Packet;

public class BncsPacket : Core.Packet.Packet
{
    protected const byte PrefixByte = 0xFF;

    protected const string PlatformCode = "IX86";

    protected const string ProductCode = "D2XP";

    protected const int Version = 0x9;

    public Sid Type { get => (Sid)Raw[1]; }

    public BncsPacket(byte[] packet) : base(packet)
    {
    }

    protected static byte[] BuildPacket(Sid command, params IEnumerable<byte>[] args)
    {
        var packet = new List<byte> { PrefixByte, (byte)command };
        var packetArray = args.SelectMany(a => a).ToArray();
        packet.AddRange(BitConverter.GetBytes((ushort)(packetArray.Length + 4)));
        packet.AddRange(packetArray);
        return packet.ToArray();
    }
}
