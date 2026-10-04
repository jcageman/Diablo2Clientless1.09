using D2NG.Core.MCP.Exceptions;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.Core.MCP.Packet;

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
                Log.Warning("Create refused: invalid game name");
                break;
            case 0x1F:
                Log.Warning("Create refused: game name already exists (d2cs NAME_EXIST - the previous game of this name has not been closed by d2gs yet)");
                break;
            case 0x20:
                Log.Warning("Create refused: game servers are down");
                break;
            case 0x6E:
                Log.Warning("Create refused: dead hardcore character");
                break;
            default:
                Log.Warning("Create refused: unknown reason, result {Result:X2}", ResultCode);
                break;
        }
    }
}