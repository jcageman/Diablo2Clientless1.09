using D2NG.Core.MCP.Exceptions;
using Serilog;
using System.IO;
using System.Net;
using System.Text;

namespace D2NG.Core.MCP.Packet;

internal class JoinGameResponsePacket : McpPacket
{
    public ushort RequestId { get; }
    public ushort GameToken { get; }
    public IPAddress D2gsIp { get; }
    public uint GameHash { get; }
    public uint Result { get; }

    public JoinGameResponsePacket(byte[] packet) : base(packet)
    {
        var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
        if (Raw.Length != reader.ReadUInt16())
        {
            throw new McpPacketException("Packet length does not match");
        }
        if (Mcp.JOINGAME != (Mcp)reader.ReadByte())
        {
            throw new McpPacketException("Expected Packet Type Not Found");
        }

        RequestId = reader.ReadUInt16();
        GameToken = reader.ReadUInt16();
        _ = reader.ReadUInt16();

        D2gsIp = new IPAddress(reader.ReadUInt32());

        GameHash = reader.ReadUInt32();
        Result = reader.ReadUInt32();
        Validate(Result);
    }

    private static void Validate(uint result)
    {
        switch (result)
        {
            case 0x00:
                break;
            case 0x29:
                Log.Warning("Join refused: password incorrect");
                break;
            case 0x2A:
                Log.Warning("Join refused: game does not exist (d2cs NOT_EXIST - the game is created but d2gs has not confirmed it yet)");
                break;
            case 0x2B:
                Log.Warning("Join refused: game is full");
                break;
            case 0x2C:
                Log.Warning("Join refused: level requirements not met");
                break;
            case 0x6E:
                Log.Warning("Join refused: dead hardcore character");
                break;
            case 0x71:
                Log.Warning("Join refused: non-hardcore character in a hardcore game");
                break;
            case 0x73:
                Log.Warning("Join refused: cannot join a nightmare game");
                break;
            case 0x74:
                Log.Warning("Join refused: cannot join a hell game");
                break;
            case 0x78:
                Log.Warning("Join refused: non-expansion character in an expansion game");
                break;
            case 0x79:
                Log.Warning("Join refused: expansion character in a non-expansion game");
                break;
            case 0x7D:
                Log.Warning("Join refused: non-ladder character in a ladder game");
                break;
            default:
                Log.Warning("Join refused: unknown reason, result {Result:X2}", result);
                break;
        }
    }
}