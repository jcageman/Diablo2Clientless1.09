using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.MCP.Packet
{
    public class McpPacket : Core.Packet.Packet
    {
        public McpPacket(byte[] packet) : base(packet)
        {
        }

        public byte Type { get => Raw.Length > 2 ? Raw[2] : Raw[1]; }

        protected static byte[] BuildPacket(Mcp command, params IEnumerable<byte>[] args)
        {
            var packet = new List<byte>();
            var packetArray = args.SelectMany(a => a).ToArray();
            packet.AddRange(BitConverter.GetBytes((ushort)(packetArray.Length + 3)));
            packet.Add((byte)command);
            packet.AddRange(packetArray);
            return packet.ToArray();
        }
    }
}
