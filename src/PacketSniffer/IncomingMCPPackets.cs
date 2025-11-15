using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.MCP.Packet;
using Serilog;
using System;

namespace PacketSniffer;

public static class IncomingMCPPackets
{
    public static void HandleIncomingPacket(McpPacket eventArgs)
    {
        if (!Enum.IsDefined(typeof(Mcp), eventArgs.Type))
        {
            Log.Information($"Received unknown MCP packet of type: 0x{eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
            return;
        }

        var incomingPacketType = (Mcp)eventArgs.Type;
        switch (incomingPacketType)
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
                    Log.Debug($"Received MCP packet of type: {incomingPacketType} with data { eventArgs.Raw.ToPrintString()}");
                }
                else
                {
                    Log.Information($"Received MCP packet of type: {incomingPacketType}");
                }
                break;

        }


    }
}
