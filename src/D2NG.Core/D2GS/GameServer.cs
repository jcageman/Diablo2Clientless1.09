using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.Extensions;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.Core.D2GS;

internal class GameServer : IDisposable
{
    private const ushort Port = 4000;

    private bool InGame;

    private static int instanceCounter;
    internal int InstanceId { get; }

    private GameServerConnection Connection { get; }

    private TraceChannel _trace;

    /// <summary>
    /// Keeps client actions, received packets and digests in the trace in the order they touched the state: while
    /// tracing, a received packet is recorded and its core handlers run under it, so a client action from a bot thread
    /// never lands between a packet's record and the state it changes. Only core handlers run under it: they change
    /// <see cref="GameData"/> and nothing else, never block, wait, sleep or send, and take no lock a bot thread holds
    /// while it applies a client action. Everything else - bots' handlers, sends, waits - runs after it is released.
    /// </summary>
    private readonly Lock _traceOrder = new();

    internal Func<TraceDigest> DigestSource { get; set; }

    /// <summary>Handlers that build the game state the replay rebuilds; run first, and under <see cref="_traceOrder"/> while tracing.</summary>
    private readonly ConcurrentDictionary<InComingPacket, Action<D2gsPacket>> _coreHandlers = new();

    /// <summary>Every other handler - bots', and Game's own that send, wait or call out - run after the core ones with no lock held.</summary>
    protected ConcurrentDictionary<InComingPacket, Action<D2gsPacket>> PacketReceivedEventHandlers { get; } = new ConcurrentDictionary<InComingPacket, Action<D2gsPacket>>();

    protected ConcurrentDictionary<OutGoingPacket, Action<D2gsPacket>> PacketSentEventHandlers { get; } = new ConcurrentDictionary<OutGoingPacket, Action<D2gsPacket>>();

    protected ConcurrentDictionary<InComingPacket, ManualResetEvent> IncomingPacketEvents { get; } = new ConcurrentDictionary<InComingPacket, ManualResetEvent>();

    private Thread _listener;

    private const int RecentPacketCount = 3000;

    private static readonly TimeSpan BeforeLastReceived = TimeSpan.FromSeconds(3);

    private const int SentAfterLastReceivedShown = 10;

    /// <summary>
    /// The last packets both ways, kept so a connection the server closes can be explained: it
    /// removes a client for one packet it rejects, and nothing else in the log says which.
    /// </summary>
    private readonly ConcurrentQueue<(DateTime At, bool Sent, byte[] Raw)> _recentPackets = new();

    private static readonly TimeSpan ReportedSilence = TimeSpan.FromSeconds(8);

    private DateTime _lastReceived = DateTime.MinValue;

    private static readonly bool[] DefinedIncoming = Defined<InComingPacket>();

    private static readonly bool[] DefinedOutgoing = Defined<OutGoingPacket>();

    /// <summary>Enum.IsDefined for every byte, without boxing the byte on each packet.</summary>
    private static bool[] Defined<T>() where T : struct, Enum
    {
        var defined = new bool[256];
        for (var i = 0; i < defined.Length; i++)
        {
            defined[i] = Enum.IsDefined(typeof(T), (byte)i);
        }

        return defined;
    }

    private void Remember(bool sent, byte[] raw)
    {
        var now = DateTime.Now;
        if (!sent)
        {
            if (InGame && _lastReceived != DateTime.MinValue && now - _lastReceived > ReportedSilence)
            {
                Log.Warning("Instance {InstanceId} game server was silent for {Seconds:F1}s and then resumed", InstanceId, (now - _lastReceived).TotalSeconds);
            }

            _lastReceived = now;
        }

        _recentPackets.Enqueue((now, sent, raw.Length > 32 ? raw[..32] : raw));
        while (_recentPackets.Count > RecentPacketCount && _recentPackets.TryDequeue(out _))
        {
        }
    }

    internal void LogRecentPackets(string reason)
    {
        var packets = _recentPackets.ToArray();
        var lastReceived = Array.FindLastIndex(packets, p => !p.Sent);
        var from = lastReceived < 0 ? 0 : Array.FindIndex(packets, p => p.At >= packets[lastReceived].At - BeforeLastReceived);
        var after = packets.Skip(lastReceived + 1).ToArray();
        var shown = packets[from..(lastReceived + 1)].Concat(after.Take(SentAfterLastReceivedShown));
        var lines = string.Join(Environment.NewLine, shown.Select(p =>
            $"  {p.At:HH:mm:ss.fff} {(p.Sent ? "sent" : "recv")} {p.Raw.ToPrintString()}"));
        var silence = lastReceived < 0 ? "nothing received in the last " + packets.Length + " packets" : $"last received {packets[lastReceived].At:HH:mm:ss.fff}";
        var sentSince = string.Join(" ", after.GroupBy(p => p.Raw[0]).Select(g => $"0x{g.Key:X2}x{g.Count()}"));
        Log.Warning("Instance {InstanceId} {Reason}, {Silence}, sent since: {SentSince}, packets from {Before}s before it:{NewLine}{Packets}",
            InstanceId, reason, silence, sentSince, BeforeLastReceived.TotalSeconds, Environment.NewLine, lines);
    }

    internal void LogPacketsSince(string reason, DateTime since)
    {
        var packets = _recentPackets.ToArray();
        var shown = packets.Where(p => p.At >= since).ToArray();
        var lines = string.Join(Environment.NewLine, shown.Select(p =>
            $"  {p.At:HH:mm:ss.fff} {(p.Sent ? "sent" : "recv")} {p.Raw.ToPrintString()}"));
        var cut = packets.Length > 0 && packets[0].At > since ? $", the oldest kept is {packets[0].At:HH:mm:ss.fff}" : "";
        Log.Warning("Instance {InstanceId} {Reason}, {Count} packets since {Since:HH:mm:ss.fff}{Cut}:{NewLine}{Packets}",
            InstanceId, reason, shown.Length, since, cut, Environment.NewLine, lines);
    }

    public GameServer()
    {
        InstanceId = ++instanceCounter;
        Connection = new GameServerConnection(InstanceId);

        Connection.PacketReceived += (obj, eventArgs) => OnPacketReceived(eventArgs);

        Connection.PacketSent += (obj, eventArgs) =>
        {
            _trace?.Write(TraceRecordKind.D2gsSent, eventArgs.Raw);
            Remember(true, eventArgs.Raw);
            if (DefinedOutgoing[eventArgs.Type])
            {
                var outgoingPacketType = (OutGoingPacket)eventArgs.Type;
                PacketSentEventHandlers.GetValueOrDefault(outgoingPacketType, null)?.Invoke(eventArgs);
            }
            else
            {
                Log.Warning($"Instance {InstanceId} send unknown D2GS packet of type: 0x{eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
            }
        };
    }

    internal void OnPacketReceived(D2gsPacket packet)
    {
        var trace = _trace;
        Remember(false, packet.Raw);
        if (!DefinedIncoming[packet.Type])
        {
            trace?.Write(TraceRecordKind.D2gsReceived, packet.Raw);
            Log.Warning($"Instance {InstanceId} Received unknown D2GS packet of type: 0x{packet.Type,2:X2} with data {packet.Raw.ToPrintString()}");
        }
        else
        {
            var incomingPacketType = (InComingPacket)packet.Type;
            if (incomingPacketType == InComingPacket.GameFlags)
            {
                InGame = true;
            }

            _coreHandlers.TryGetValue(incomingPacketType, out var core);
            if (trace == null)
            {
                core?.Invoke(packet);
            }
            else
            {
                lock (_traceOrder)
                {
                    trace.Write(TraceRecordKind.D2gsReceived, packet.Raw);
                    core?.Invoke(packet);
                }
            }

            if (incomingPacketType == InComingPacket.GameFlags)
            {
                _leftGame = false;
            }

            if (PacketReceivedEventHandlers.TryGetValue(incomingPacketType, out var external))
            {
                external?.Invoke(packet);
            }
            else if (core == null && Log.IsEnabled(LogEventLevel.Debug))
            {
                Log.Debug($"Received unhandled D2GS packet of type: {incomingPacketType}");
            }

            SetPacketEventType(incomingPacketType);
        }

        if (trace != null && trace.DigestDue())
        {
            WriteDigest(trace);
        }
    }

    private void WriteDigest(TraceChannel trace)
    {
        try
        {
            lock (_traceOrder)
            {
                trace.WriteDigest(DigestSource?.Invoke());
            }
        }
        catch (Exception e)
        {
            trace.PostponeDigest();
            Log.Debug(e, "Instance {InstanceId} could not take a trace digest", InstanceId);
        }
    }

    internal void ApplyClientAction(GameData data, ClientAction action, uint argument)
    {
        lock (_traceOrder)
        {
            _trace?.WriteClientAction(action, argument);
            ClientActions.Apply(data, action, argument);
        }
    }

    /// <summary>Whether a packet id is one the bot knows, and whether anything handles it.</summary>
    internal (bool Known, bool Handled) Handling(byte type, bool sent)
        => sent
            ? (DefinedOutgoing[type], DefinedOutgoing[type] && PacketSentEventHandlers.ContainsKey((OutGoingPacket)type))
            : (DefinedIncoming[type], DefinedIncoming[type] && (_coreHandlers.ContainsKey((InComingPacket)type) || PacketReceivedEventHandlers.ContainsKey((InComingPacket)type)));

    /// <summary>Handlers only: the live path's logging and wait events are left out.</summary>
    internal void Replay(D2gsPacket packet, bool sent)
    {
        if (sent)
        {
            if (DefinedOutgoing[packet.Type])
            {
                PacketSentEventHandlers.GetValueOrDefault((OutGoingPacket)packet.Type, null)?.Invoke(packet);
            }
        }
        else if (DefinedIncoming[packet.Type])
        {
            _coreHandlers.GetValueOrDefault((InComingPacket)packet.Type, null)?.Invoke(packet);
            PacketReceivedEventHandlers.GetValueOrDefault((InComingPacket)packet.Type, null)?.Invoke(packet);
        }
    }

    /// <summary>The trace ends with the connection, when the listener stops.</summary>
    /// <param name="serverGame">The game server's own id for the game, which tells a rejoin from a new game reusing the name.</param>
    internal void TraceGame(string gameName, string clientLabel, string serverGame = null)
    {
        _trace?.Close();
        TraceIdentity = (clientLabel ?? $"instance{InstanceId}", gameName);
        _trace = TraceRecorder.Current?.OpenGame(gameName, TraceIdentity.Client, serverGame);
    }

    /// <summary>The client and game the last trace was opened for, kept after the game ends for the events reported on it.</summary>
    internal (string Client, string Game) TraceIdentity { get; private set; }

    internal TraceChannel Trace { get => _trace; set => _trace = value; }

    internal void EndTrace() => Interlocked.Exchange(ref _trace, null)?.Close();

    /// <summary>A handler run after the core ones, with no lock held: bots', and anything that sends, waits, sleeps or calls out.</summary>
    internal void OnReceivedPacketEvent(InComingPacket type, Action<D2gsPacket> handler)
        => PacketReceivedEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

    /// <summary>
    /// A handler that only changes <see cref="GameData"/>, run before every other and, while tracing, under the lock that
    /// orders the trace: it must not block, wait, sleep, send, call out to a bot, or take a lock a bot thread can hold
    /// while it applies a client action.
    /// </summary>
    internal void OnCoreReceivedPacketEvent(InComingPacket type, Action<D2gsPacket> handler)
        => _coreHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

    internal void OnSentPacketEvent(OutGoingPacket type, Action<D2gsPacket> handler)
        => PacketSentEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

    public void Connect(IPAddress ip)
    {
        Connection.Connect(ip, Port);
        _listener = new Thread(Listen) { IsBackground = true };
        _listener.Start();
    }

    public bool IsInGame()
    {
        return Connection.Connected && InGame;
    }

    private volatile bool _leftGame;

    /// <summary>
    /// The client left the game or lost its connection, and no game has started since. Unlike <see cref="IsInGame"/>
    /// this is false in a replay, which has no connection.
    /// </summary>
    internal bool LeftGame => _leftGame;

    internal void GameLeft() => _leftGame = true;

    public bool IsConnected()
    {
        return Connection.Connected;
    }

    private void Listen()
    {
        var trace = _trace;
        try
        {
            ListenUntilClosed();
        }
        finally
        {
            GameLeft();
            trace?.Close();
            Interlocked.CompareExchange(ref _trace, null, trace);
        }
    }

    private void ListenUntilClosed()
    {
        while (Connection.Connected)
        {
            try
            {
                _ = Connection.ReadPacket();

            }
            catch (Exception exception)
            {
                Log.Debug(
                    exception,
                    "InstanceId {InstanceId} GameServer connection was terminated",
                    InstanceId);
                if (InGame)
                {
                    LogRecentPackets($"game server closed the connection mid-game ({exception.Message})");
                }

                InGame = false;
                GameLeft();
                Connection.Terminate();
                break;
            }
        }
    }

    internal void Disconnect()
    {
        if(Connection.Connected)
        {
            Connection.Terminate();
        }
        
        _listener?.Join();
    }

    public async Task LeaveGame()
    {
        var leaveGameConfirmed = GetResetEventOfType(InComingPacket.LeaveGameConfirmed);
        InGame = false;
        GameLeft();
        Connection.WritePacket(OutGoingPacket.LeaveGame);
        await leaveGameConfirmed.AsTask(TimeSpan.FromSeconds(10));
        Disconnect();
    }

    internal bool GameLogon(uint gameHash, ushort gameToken, Character character)
    {
        var successLoadEvent = GetResetEventOfType(InComingPacket.LoadSuccessful);
        Connection.WritePacket(new GameLogonPacket(gameHash, gameToken, character));
        if (!successLoadEvent.WaitOne(10000))
        {
            Log.Error("Game logon failed");
            return false;
        }
        var loadActComplete = GetResetEventOfType(InComingPacket.LoadActComplete);
        Connection.WritePacket(OutGoingPacket.Startup);
        if (!loadActComplete.WaitOne(10000))
        {
            Log.Error("Load Act failed");
            return false;
        }
        Log.Verbose("Game load complete");
        return true;
    }

    internal void SetPacketEventType(InComingPacket inComingPacket)
    {
        IncomingPacketEvents.GetValueOrDefault(inComingPacket)?.Set();
    }

    internal ManualResetEvent GetResetEventOfType(InComingPacket inComingPacket)
    {
        return IncomingPacketEvents.AddOrUpdate(inComingPacket, new ManualResetEvent(false), (key, oldValue) => new ManualResetEvent(false));
    }

    internal void Ping()
    {
        if (IsInGame())
        {
            Connection.WritePacket(new PingPacket());
        }
    }

    public void Dispose()
    {
        foreach (var packetEvent in IncomingPacketEvents.Values)
        {
            packetEvent.Dispose();
        }
    }
    internal void SendPacket(D2gsPacket packet)
    {
        if (IsInGame())
        {
            Connection.WritePacket(packet);
        }
    }
}
