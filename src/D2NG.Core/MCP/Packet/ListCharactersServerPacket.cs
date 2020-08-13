using D2NG.Core.Extensions;
using D2NG.Core.MCP.Exceptions;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2NG.Core.MCP.Packet
{
    public class ListCharactersServerPacket : McpPacket
    {
        public ListCharactersServerPacket(byte[] packet) : base(packet)
        {
            var reader = new BinaryReader(new MemoryStream(packet), Encoding.ASCII);
            if (packet.Length != reader.ReadUInt16())
            {
                throw new McpPacketException("Packet length does not match");
            }
            if (Mcp.CHARLIST != (Mcp)reader.ReadByte())
            {
                throw new McpPacketException("Expected Packet Type Not Found");
            }

            var test1 = reader.ReadUInt16();
            var test2 = reader.ReadUInt32();
            var totalReturned = reader.ReadUInt16();

            Characters = new List<Character>();
            for (int x = 0; x < totalReturned; x++)
            {
                Characters.Add(new Character(
                    reader.ReadNullTerminatedString(),
                    reader.ReadNullTerminatedString()
                    ));
            }
        }

        public List<Character> Characters { get; }
    }
}