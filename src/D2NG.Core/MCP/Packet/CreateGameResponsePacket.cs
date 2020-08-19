using D2NG.Core.MCP.Exceptions;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.Core.MCP.Packet
{
    internal class CreateGameResponsePacket : McpPacket
    {
        public uint ResultCode { get; set; }
        public CreateGameResponsePacket(byte[] packet) : base(packet)
        {
            var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
            if (Raw.Length != reader.ReadUInt16())
            {
                throw new McpPacketException("Packet length does not match");
            }
            if (Mcp.CREATEGAME != (Mcp)reader.ReadByte())
            {
                throw new McpPacketException("Expected Packet Type Not Found");
            }
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt16();
            ResultCode = reader.ReadUInt32();

            switch (ResultCode)
            {
                case 0x00:
                    Log.Debug("Game created successfully");
                    break;
                case 0x1E:
                    Log.Debug("Invalid Game Name");
                    break;
                case 0x1F:
                    Log.Debug("Game name already exists");
                    break;
                case 0x20:
                    Log.Debug("Game servers are down");
                    break;
                case 0x6E:
                    Log.Debug("A dead hardcore character cannot create games");
                    break;
                default:
                    Log.Debug("Unknown game creation failure");
                    break;
            }
        }
    }
}