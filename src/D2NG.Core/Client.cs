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
using System.IO;
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
    private Tracing.TraceChannel _realmTrace;
    private readonly object _realmTraceGate = new();

    public Client()
    {
        Chat = new Chat(Bncs);
        Game = new Game(D2gs);
        D2gs.DigestSource = () => Tracing.TraceDigest.Capture(Game);
        D2gs.OnReceivedPacketEvent(InComingPacket.ReceiveChat, packet => HearAdministrator(new D2GS.Packet.Incoming.ChatPacket(packet)));
    }

    /// <summary>The game this client joined last.</summary>
    private string _gameName;

    private int _leavingForRestart;

    /// <summary>An administrator's restart countdown: the time is kept, and the client leaves the game just before it.</summary>
    private void HearAdministrator(D2GS.Packet.Incoming.ChatPacket chat)
    {
        if (GameServerRestart.Announced(chat.CharacterName, chat.Message) is not { } until)
        {
            return;
        }

        var at = GameServerRestart.Heard(until, DateTime.UtcNow);
        if (System.Threading.Interlocked.Exchange(ref _leavingForRestart, 1) == 0)
        {
            Log.Information("{Account} heard that the game server restarts at {At:HH:mm:ss}; it leaves {Game} {Seconds:0}s before", LoggedInUserName(),
                at.ToLocalTime(), _gameName, GameServerRestart.LeaveBefore.TotalSeconds);
            var game = _gameName;
            _ = Task.Run(() => LeaveBeforeRestart(game));
        }
    }

    private async Task LeaveBeforeRestart(string game)
    {
        try
        {
            while (Game.IsInGame() && _gameName == game && GameServerRestart.RestartAt is { } at)
            {
                var wait = at - GameServerRestart.LeaveBefore - DateTime.UtcNow;
                if (wait <= TimeSpan.Zero)
                {
                    Log.Information("{Account} leaves {Game} before the game server restarts at {At:HH:mm:ss}", LoggedInUserName(), game, at.ToLocalTime());
                    await Game.LeaveGame();
                    return;
                }

                await Task.Delay(wait < TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception exception)
        {
            Log.Warning("{Account} could not leave {Game} before the restart: {Reason}", LoggedInUserName(), game, exception.Message);
        }
    }

    internal Tracing.TraceChannel RealmTrace => _realmTrace;

    /// <summary>Opened on connecting and closed on <see cref="Disconnect"/>; a reconnect after that writes a new realm file.</summary>
    internal void TraceRealm(Tracing.TraceRecorder recorder)
    {
        lock (_realmTraceGate)
        {
            if (_realmTrace != null || recorder == null)
            {
                return;
            }

            _realmTrace = recorder.OpenRealm($"instance{D2gs.InstanceId}");
            Bncs.TraceTo(_realmTrace);
            Mcp.TraceTo(_realmTrace);
        }
    }

    private void EndRealmTrace()
    {
        lock (_realmTraceGate)
        {
            Bncs.TraceTo(null);
            Mcp.TraceTo(null);
            _realmTrace?.Close();
            _realmTrace = null;
        }
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
    {
        TraceRealm(Tracing.TraceRecorder.Current);
        return Bncs.ConnectTo(realm, keyOwner, gamefolder);
    }

    /// <summary>
    /// Login to Battle.Net with credentials and receive the list of available characters to select.
    /// </summary>
    /// <param name="username">Account name</param>
    /// <summary>
    /// Changes an account's password. Use between <see cref="Connect"/> and <see cref="Login"/>.
    /// </summary>
    public bool ChangePassword(string username, string oldPassword, string newPassword)
        => Bncs.ChangePassword(username, oldPassword, newPassword);

    /// <summary>
    /// Creates a new account. Use between <see cref="Connect"/> and <see cref="Login"/>.
    /// </summary>
    public bool CreateAccount(string username, string password)
        => Bncs.CreateAccount(username, password);

    /// <param name="password">Password used to login</param>
    /// <returns>A list of Characters associated with the account</returns>
    public async Task<List<Character>> Login(string username, string password)
    {
        if (!Bncs.Login(username, password))
        {
            Log.Warning($"Logged failed as {username}");
            return null;
        }
        Log.Debug($"Logged in as {username}");
        _userName = username;
        if (!await RealmLogon())
        {
            return null;
        }

        return await Mcp.ListCharacters();
    }

    /// <summary>
    /// Create a new character on the account currently logged on, which is how a run provisions the
    /// fresh characters a rush needs: quest flags are permanent, so measuring a quest again takes a
    /// character that has never done it.
    /// </summary>
    /// <param name="name">Name to give the character, refused by the realm if it is already taken</param>
    /// <param name="characterClass">Class to create</param>
    /// <param name="flags">Whether the character is expansion and whether it is hardcore</param>
    /// <returns>The created character, or <see langword="null"/> when the realm refused</returns>
    public async Task<Character> CreateCharacter(string name, CharacterClass characterClass, CharacterFlags flags)
    {
        var result = await Mcp.CreateCharacter(name, characterClass, flags);
        if (result == null)
        {
            Log.Warning($"Creating character {name} timed out");
            return null;
        }

        if (result != 0x00)
        {
            Log.Warning($"Creating character {name} failed with result 0x{result:X2}");
            return null;
        }

        Log.Information($"Created {characterClass} {name} ({flags})");
        var characters = await Mcp.ListCharacters();
        return characters.Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the realm connection is still up. Playing a game drops it, so anything that talks to
    /// the realm - listing, creating or deleting characters - has to check first.
    /// </summary>
    public bool IsRealmConnected() => Mcp.IsConnected();

    /// <summary>
    /// Delete a character from the account currently logged on. Meant for cleaning up characters a
    /// test run created; there is no undo.
    /// </summary>
    public async Task<bool> DeleteCharacter(string name)
    {
        var deleted = await Mcp.DeleteCharacter(name);
        if (deleted)
        {
            Log.Information($"Deleted character {name}");
        }
        else
        {
            Log.Warning($"Deleting character {name} failed");
        }

        return deleted;
    }

    /// <summary>
    /// Converts a classic character on the account currently logged on to expansion. There is no way
    /// back, and a converted character can no longer join a classic game.
    /// </summary>
    /// <returns>The character as the realm lists it afterwards, or <see langword="null"/> when the realm refused</returns>
    public async Task<Character> UpgradeCharacter(string name)
    {
        var result = await Mcp.UpgradeCharacter(name);
        if (result == null)
        {
            Log.Warning($"Converting {name} to expansion timed out");
            return null;
        }

        if (result != 0x00)
        {
            Log.Warning($"Converting {name} to expansion failed with result 0x{result:X2}");
            return null;
        }

        Log.Information($"Converted {name} to expansion");
        var characters = await Mcp.ListCharacters();
        return characters.Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Select one of the available characters on the account.
    /// </summary>
    /// <param name="character">Character with name matching one of the account characters</param>
    public async Task SelectCharacter(Character character)
    {
        Log.Debug($"Selecting {character.Name}");
        await Mcp.CharLogon(character);
        _character = character;
        Game.SelectCharacter(character);
    }

    /// <summary>A game created or joined while the server restarts would be dropped with it, or not be there at all.</summary>
    private async Task WaitForRestart()
    {
        var restart = GameServerRestart.RestartAt;
        var wait = GameServerRestart.WaitFor(restart, DateTime.UtcNow);
        if (wait > TimeSpan.Zero)
        {
            Log.Information("{Account} waits {Seconds:0}s for the game server restart announced for {At:HH:mm:ss}", LoggedInUserName(), wait.TotalSeconds,
                restart.Value.ToLocalTime());
            await Task.Delay(wait);
        }
    }

    /// <summary>
    /// Create a new game 
    /// </summary>
    /// <param name="difficulty">One of Normal, Nightmare or Hell</param>
    /// <param name="name">Name of the game to be created</param>
    /// <param name="password">Password used to protect the game</param>
    public async Task<bool> CreateGame(Difficulty difficulty, string name, string password, string description)
    {
        await WaitForRestart();
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
        await WaitForRestart();
        Log.Information($"Joining game: {name} with {LoggedInUserName()} as {_character?.Name}");
        var packet = await Mcp.JoinGame(name, password);
        if (packet == null)
        {
            Log.Warning("Join of {Game} with {Account} got no answer from the realm", name, LoggedInUserName());
            return false;
        }

        if (packet.Result != 0x00)
        {
            return false;
        }

        Mcp.Disconnect();
        await GameServerConnectionGate.WaitForSlot(LoggedInUserName(), packet.D2gsIp);
        Log.Debug($"Connecting to D2GS Server {packet.D2gsIp}");
        D2gs.TraceGame(name, _character?.Name, $"{packet.D2gsIp}#{packet.GameToken}");
        _gameName = name;
        _leavingForRestart = 0;
        try
        {
            D2gs.Connect(packet.D2gsIp);
        }
        catch (Exception e)
        {
            Log.Warning("Connecting {Account} to the game server failed: {Error}", LoggedInUserName(), e.Message);
            D2gs.EndTrace();
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
        // Only a courtesy to the chat server, which is gone when the account was logged on elsewhere; the
        // game itself is joined either way.
        try
        {
            if (Bncs.IsConnected())
            {
                Bncs.NotifyJoin(name, password);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Log.Information("Telling the chat server about {Game} failed, the chat connection is gone", name);
        }

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
        if (!Bncs.IsConnected())
        {
            Log.Information("RealmLogin failed, bncs is not connected");
            return false;
        }

        RealmLogonResponsePacket packet;
        try
        {
            _mcpRealm ??= Bncs.ListMcpRealms()?.First();
            if (_mcpRealm == null)
            {
                Log.Warning("RealmLogin failed, no mcp realm found");
                return false;
            }

            packet = Bncs.RealmLogon(_mcpRealm);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Log.Information("RealmLogin failed, the chat connection is gone: {Reason}", e.Message);
            return false;
        }

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
        // Also when the server already closed it: the chat state has to go back to not connected for
        // the next Connect to be accepted.
        Bncs.Disconnect();

        if (Mcp.IsConnected())
        {
            Mcp.Disconnect();
        }

        if (D2gs.IsConnected())
        {
            D2gs.Disconnect();
        }

        EndRealmTrace();
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
