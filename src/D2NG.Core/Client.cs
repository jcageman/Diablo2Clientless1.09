using D2NG.Core.BNCS;
using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MCP;
using D2NG.Core.MCP.Packet;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.Core;

public class Client
{
    internal BattleNetChatServer Bncs { get; } = new BattleNetChatServer();
    internal RealmServer Mcp { get; } = new RealmServer();
    internal GameServer D2gs { get; } = new GameServer();

    public Chat Chat { get; }
    public Game Game { get; }

    private Character _character;
    private string _userName;
    private string _mcpRealm;

    public Client()
    {
        Chat = new Chat(Bncs);
        Game = new Game(D2gs);
    }

    public void OnReceivedPacketEvent(Sid sid, Action<BncsPacket> action)
        => Bncs.OnReceivedPacketEvent(sid, action);
    public void OnSentPacketEvent(Sid sid, Action<BncsPacket> action)
        => Bncs.OnSentPacketEvent(sid, action);

    public void OnReceivedPacketEvent(Mcp mcp, Action<McpPacket> action)
        => Mcp.OnReceivedPacketEvent(mcp, action);
    public void OnSentPacketEvent(Mcp mcp, Action<McpPacket> action)
        => Mcp.OnSentPacketEvent(mcp, action);

    public void OnReceivedPacketEvent(InComingPacket type, Action<D2gsPacket> action)
        => D2gs.OnReceivedPacketEvent(type, action);
    public void OnSentPacketEvent(OutGoingPacket type, Action<D2gsPacket> action)
        => D2gs.OnSentPacketEvent(type, action);

    /// <summary>
    /// Connect to a Battle.net Realm
    /// </summary>
    public bool Connect(string realm, string keyOwner, string gamefolder)
        => Bncs.ConnectTo(realm, keyOwner, gamefolder);

    /// <summary>
    /// Login to Battle.Net with credentials and receive the list of available characters to select.
    /// </summary>
    /// <param name="username">Account name</param>
    /// <param name="password">Password used to login</param>
    /// <returns>A list of Characters associated with the account</returns>
    public async Task<List<Character>> Login(string username, string password)
    {
        if (!Bncs.Login(username, password))
        {
            Log.Warning($"Logged failed as {username}");
            return null;
        }
        Log.Information($"Logged in as {username}");
        _userName = username;
        if (!await RealmLogon())
        {
            return null;
        }

        return await Mcp.ListCharacters();
    }

    /// <summary>
    /// Select one of the available characters on the account.
    /// </summary>
    /// <param name="character">Character with name matching one of the account characters</param>
    public async Task SelectCharacter(Character character)
    {
        Log.Information($"Selecting {character.Name}");
        await Mcp.CharLogon(character);
        _character = character;
        Game.SelectCharacter(character);
    }

    /// <summary>
    /// Create a new game 
    /// </summary>
    /// <param name="difficulty">One of Normal, Nightmare or Hell</param>
    /// <param name="name">Name of the game to be created</param>
    /// <param name="password">Password used to protect the game</param>
    public async Task<bool> CreateGame(Difficulty difficulty, string name, string password, string description)
    {
        Log.Information($"Creating {difficulty} game: {name}");
        if (!await Mcp.CreateGame(difficulty, name, password, description))
        {
            Log.Warning($"Creating {difficulty} game: {name} failed");
            return false;
        }
        Log.Debug($"Game {name} with {password} created");
        return await JoinGame(name, password);
    }

    /// <summary>
    /// Join a game
    /// </summary>
    /// <param name="name">Name of the game being joined</param>
    /// <param name="password">Password used to protect the game</param>
    public async Task<bool> JoinGame(string name, string password)
    {
        Log.Information($"Joining game: {name} with {LoggedInUserName()}");
        var packet = await Mcp.JoinGame(name, password);
        if (packet == null)
        {
            return false;
        }

        if (packet.Result != 0x00)
        {
            return false;
        }

        Mcp.Disconnect();
        Log.Debug($"Connecting to D2GS Server {packet.D2gsIp}");
        try
        {
            D2gs.Connect(packet.D2gsIp);
        }
        catch
        {
            if (D2gs.IsConnected())
            {
                D2gs.Disconnect();
            }

            return false;
        }

        if (!D2gs.GameLogon(packet.GameHash, packet.GameToken, _character))
        {
            if (D2gs.IsConnected())
            {
                D2gs.Disconnect();
            }
            return false;
        }
        Bncs.NotifyJoin(name, password);
        return true;
    }

    public async Task<bool> RejoinMCP()
    {
        if (Mcp.IsConnected())
        {
            return true;
        }

        Log.Debug("Joining MCP again");
        if (!await RealmLogon())
        {
            return false;
        }

        var result = await Mcp.CharLogon(_character);
        return result;
    }

    private async Task<bool> RealmLogon()
    {
        _mcpRealm ??= Bncs.ListMcpRealms()?.First();

        if (_mcpRealm == null)
        {
            Log.Warning("RealmLogin failed, no mcp realm found");
            return false;
        }

        if (!Bncs.IsConnected())
        {
            Log.Warning("RealmLogin failed, bncs is not connected");
            return false;
        }

        var packet = Bncs.RealmLogon(_mcpRealm);
        if (packet == null)
        {
            Log.Warning("RealmLogin failed");
            return false;
        }

        Log.Debug($"Connecting to {packet.McpIp}:{packet.McpPort}");
        Mcp.Connect(packet.McpIp, packet.McpPort);
        if (!await Mcp.Logon(packet.McpCookie, packet.McpStatus, packet.McpChunk, packet.McpUniqueName))
        {
            Log.Warning("RealmLogin Connecting failed");
            return false;
        }
        Log.Debug($"Connected to MCP {packet.McpIp}:{packet.McpPort}");
        return true;
    }

    public void Disconnect()
    {
        if (Bncs.IsConnected())
        {
            Bncs.Disconnect();
        }

        if (Mcp.IsConnected())
        {
            Mcp.Disconnect();
        }

        if (D2gs.IsConnected())
        {
            D2gs.Disconnect();
        }
    }

    public string LoggedInUserName()
    {
        return _userName;
    }

    public Character SelectedCharacter()
    {
        return _character;
    }
}
