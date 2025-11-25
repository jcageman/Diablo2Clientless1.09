using D2NG.Core.D2GS.Enums;
using D2NG.Core.MCP.Packet;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.Core.MCP;

public class RealmServer
{
    private McpConnection Connection { get; } = new McpConnection();

    protected ConcurrentDictionary<Mcp, Action<McpPacket>> PacketReceivedEventHandlers { get; } = new ConcurrentDictionary<Mcp, Action<McpPacket>>();
    protected ConcurrentDictionary<Mcp, Action<McpPacket>> PacketSentEventHandlers { get; } = new ConcurrentDictionary<Mcp, Action<McpPacket>>();
    public ushort RequestId { get; private set; } = 0x02;

    private readonly McpEvent CharLogonEvent = new();
    private readonly McpEvent CreateGameEvent = new();
    private readonly McpEvent ListCharactersEvent = new();
    private readonly McpEvent StartupEvent = new();
    private readonly McpEvent JoinGameEvent = new();

    private Thread _listener;

    internal RealmServer()
    {
        Connection.PacketReceived += (obj, eventArgs) => PacketReceivedEventHandlers.GetValueOrDefault((Mcp)eventArgs.Type, null)?.Invoke(eventArgs);
        Connection.PacketSent += (obj, eventArgs) => PacketSentEventHandlers.GetValueOrDefault((Mcp)eventArgs.Type, null)?.Invoke(eventArgs);

        OnReceivedPacketEvent(Mcp.STARTUP, StartupEvent.Set);
        OnReceivedPacketEvent(Mcp.CHARLIST, ListCharactersEvent.Set);
        OnReceivedPacketEvent(Mcp.CHARLOGON, CharLogonEvent.Set);
        OnReceivedPacketEvent(Mcp.CREATEGAME, CreateGameEvent.Set);
        OnReceivedPacketEvent(Mcp.JOINGAME, JoinGameEvent.Set);
    }

    internal void Connect(IPAddress ip, short port)
    {
        Connection.Connect(ip, port);
        _listener = new Thread(Listen);
        _listener.Start();
    }

    public bool IsConnected()
    {
        return Connection.Connected;
    }

    private void Listen()
    {
        while (Connection.Connected)
        {
            try
            {
                _ = Connection.ReadPacket();
            }
            catch (Exception)
            {
                Log.Debug("RealmServer Connection was terminated");
                Thread.Sleep(300);
            }
        }
    }

    internal async Task<bool> CharLogon(Character character)
    {
        CharLogonEvent.Reset();
        var packet = new CharLogonRequestPacket(character.Name);
        Connection.WritePacket(packet);
        var loginResponsePacket = await CharLogonEvent.WaitForPacket(2000);
        if (loginResponsePacket == null)
        {
            return false;
        }

        var response = new CharLogonResponsePacket(loginResponsePacket);
        return response.Result == 0x00;
    }

    internal void Disconnect()
    {
        Log.Verbose("Disconnecting from MCP");
        Connection.Terminate();
        _listener.Join();
        Log.Verbose("Disconnected from MCP");
    }

    internal async Task<bool> Logon(uint mcpCookie, uint mcpStatus, List<byte> mcpChunk, string mcpUniqueName)
    {
        StartupEvent.Reset();
        var packet = new McpStartupRequestPacket(mcpCookie, mcpStatus, mcpChunk, mcpUniqueName);
        Connection.WritePacket(packet);
        var response = await StartupEvent.WaitForPacket(5000);
        if (response == null)
        {
            return false;
        }
        _ = new McpStartupResponsePacket(response.Raw);
        return true;
    }

    internal async Task<List<Character>> ListCharacters()
    {
        ListCharactersEvent.Reset();
        Connection.WritePacket(new ListCharactersClientPacket());
        var packet = await ListCharactersEvent.WaitForPacket(5000);
        if(packet == null)
        {
            return [];
        }
        var response = new ListCharactersServerPacket(packet.Raw);
        return response.Characters;
    }

    internal async Task<bool> CreateGame(Difficulty difficulty, string gameName, string password, string description)
    {
        CreateGameEvent.Reset();
        Connection.WritePacket(new CreateGameRequestPacket(RequestId++, difficulty, gameName, password, description));
        var packet = await CreateGameEvent.WaitForPacket(5000);
        if(packet == null)
        {
            return false;
        }
        var result = new CreateGameResponsePacket(packet.Raw);
        return result.ResultCode == 0x00;
    }

    internal async Task<JoinGameResponsePacket> JoinGame(string name, string password)
    {
        JoinGameEvent.Reset();
        Connection.WritePacket(new JoinGameRequestPacket(RequestId++, name, password));
        var response = await JoinGameEvent.WaitForPacket(5000);
        if(response == null)
        {
            return null;
        }

        return new JoinGameResponsePacket(response.Raw);
    }

    internal void OnReceivedPacketEvent(Mcp type, Action<McpPacket> handler)
        => PacketReceivedEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

    internal void OnSentPacketEvent(Mcp type, Action<McpPacket> handler)
        => PacketSentEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);
}