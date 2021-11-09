using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.MCP.Packet;
using Serilog;
using System;

namespace PacketSniffer
{
    public static class OutgoingMCPPackets
    {
        public static void HandleOutgoingPacket(byte[] bytes)
        {
            if(bytes.Length < 3)
            {
                return;
            }

            if (!Enum.IsDefined(typeof(Mcp), bytes[2]))
            {
                Log.Information($"Send unknown MCP packet of type: 0x{bytes[0],2:X2} with data {bytes.ToPrintString()}");
                return;
            }

            var packetType = (Mcp)bytes[2];
            switch (packetType)
            {
                case Mcp.STARTUP:
                case Mcp.CHARCREATE:
                case Mcp.CREATEGAME:
                case Mcp.JOINGAME:
                case Mcp.GAMELIST:
                case Mcp.GAMEINFO:
                case Mcp.CHARLOGON:
                case Mcp.CHARDELETE:
                case Mcp.REQUESTLADDERDATA:
                case Mcp.MOTD:
                case Mcp.CANCELGAMECREATE:
                case Mcp.CREATEQUEUE:
                case Mcp.CHARRANK:
                case Mcp.CHARLIST:
                case Mcp.CHARUPGRADE:
                case Mcp.CHARLIST2:
                    if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                    {
                        Log.Debug($"Send MCP packet of type: {packetType} with data {bytes.ToPrintString()}");
                    }
                    else
                    {
                        Log.Information($"Send MCP packet of type: {packetType}");
                    }
                    break;
            }
        }
    }
}
