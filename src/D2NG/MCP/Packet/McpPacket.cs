using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.MCP.Packet
{
    public class McpPacket : D2NG.Packet.Packet
    {
        public McpPacket(byte[] packet) : base(packet)
        {
        }

        public byte Type { get => Raw.Length > 2 ? Raw[2] : Raw[1]; }

        protected static byte[] BuildPacket(Mcp command, params IEnumerable<byte>[] args)
        {
            var packet = new List<byte>();
            var packetArray = args.SelectMany(a => a);
            packet.AddRange(BitConverter.GetBytes((ushort)(packetArray.Count() + 3)));
            packet.Add((byte)command);
            packet.AddRange(packetArray);
            return packet.ToArray();
        }
    }
}
