using D2NG.Extensions;
using Serilog;
using System;
using System.Text;

namespace D2NG.MCP.Packet
{
    internal class JoinGameRequestPacket : McpPacket
    {
        public JoinGameRequestPacket(ushort id, string name, string password) : 
            base(
                BuildPacket(
                    Mcp.JOINGAME,
                    BitConverter.GetBytes(id),
                    Encoding.ASCII.GetBytes($"{name.FirstCharToUpper()}\0"),
                    Encoding.ASCII.GetBytes($"{password.FirstCharToUpper()}\0")
                )
            )
        {
            Log.Verbose($"JoinGameRequestPacket\n" +
                $"\tRequest Id: {id}\n" +
                $"\tGame: {name.FirstCharToUpper()} /" + $" {password.FirstCharToUpper()}");
        }
    }
}