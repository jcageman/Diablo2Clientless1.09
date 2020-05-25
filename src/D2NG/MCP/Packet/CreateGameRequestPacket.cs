using D2NG.Extensions;
using Serilog;
using System;
using System.Text;

namespace D2NG.MCP.Packet
{
    public class CreateGameRequestPacket : McpPacket
    {
        public CreateGameRequestPacket(ushort id, Difficulty difficulty, string name, string password, string description) :
            base(
                BuildPacket(
                    Mcp.CREATEGAME,
                    BitConverter.GetBytes(id),
                    new byte[] { 0x00, (byte)difficulty, 0x00, 0x00 },
                    new byte[] { 0x01, 0xFF, 0x08 },
                    Encoding.ASCII.GetBytes($"{name}\0"),
                    Encoding.ASCII.GetBytes($"{password.FirstCharToUpper()}\0"),
                    Encoding.ASCII.GetBytes($"{description.FirstCharToUpper()}\0")
                )
            )
        {
            Log.Verbose($"(0x{Type}) CreateGameRequestPacket:\n" +
                $"\tRequest Id {id}\n" +
                $"\tDifficulty {difficulty}\n" +
                $"\tGame: {name} /" + $" {password}");
        }
    }
}