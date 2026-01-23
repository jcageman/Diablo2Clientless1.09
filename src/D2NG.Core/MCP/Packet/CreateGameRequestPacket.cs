using D2NG.Core.D2GS.Enums;
using D2NG.Core.Extensions;
using System;
using System.Text;

namespace D2NG.Core.MCP.Packet;

public class CreateGameRequestPacket : McpPacket
{
    public CreateGameRequestPacket(ushort id, Difficulty difficulty, string name, string password, string description) :
        base(
            BuildPacket(
                Mcp.CREATEGAME,
                BitConverter.GetBytes(id),
                [0x00, (byte)((byte)difficulty << 4), 0x00, 0x00],
                [0x01, 0xFF, 0x08],
                Encoding.ASCII.GetBytes($"{name}\0"),
                Encoding.ASCII.GetBytes($"{password?.FirstCharToUpper()}\0"),
                Encoding.ASCII.GetBytes($"{description?.FirstCharToUpper()}\0")
            )
        )
    {
    }
}