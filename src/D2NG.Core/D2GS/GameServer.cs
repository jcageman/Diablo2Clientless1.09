using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.Extensions;
using D2NG.Core.MCP;
using Serilog;
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
    private readonly int InstanceId;

    private GameServerConnection Connection { get; }

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

        Connection.PacketReceived += (obj, eventArgs) =>
        {
            Remember(false, eventArgs.Raw);
            if (Enum.IsDefined(typeof(InComingPacket), eventArgs.Type))
            {
                var incomingPacketType = (InComingPacket)eventArgs.Type;
                if (incomingPacketType == InComingPacket.GameFlags)
                {
                    InGame = true;
                }
                Log.Debug($"Instance {InstanceId} Received D2GS packet of type: {incomingPacketType} with data {eventArgs.Raw.ToPrintString()}");
                PacketReceivedEventHandlers.GetValueOrDefault(incomingPacketType, p => Log.Debug($"Received unhandled D2GS packet of type: {incomingPacketType}"))?.Invoke(eventArgs);
                SetPacketEventType(incomingPacketType);
            }
            else
            {
                Log.Warning($"Instance {InstanceId} Received unknown D2GS packet of type: 0x{eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
            }
        };

        Connection.PacketSent += (obj, eventArgs) =>
        {
            Remember(true, eventArgs.Raw);
            if (Enum.IsDefined(typeof(OutGoingPacket), eventArgs.Type))
            {
                var outgoingPacketType = (OutGoingPacket)eventArgs.Type;
                Log.Debug($"Instance {InstanceId} send D2GS packet of type: {outgoingPacketType} with data {eventArgs.Raw.ToPrintString()}");
                PacketSentEventHandlers.GetValueOrDefault(outgoingPacketType, null)?.Invoke(eventArgs);
            }
            else
            {
                Log.Warning($"Instance {InstanceId} send unknown D2GS packet of type: 0x{eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
            }
        };
    }

    internal void OnReceivedPacketEvent(InComingPacket type, Action<D2gsPacket> handler)
        => PacketReceivedEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

    internal void OnSentPacketEvent(OutGoingPacket type, Action<D2gsPacket> handler)
        => PacketSentEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

    public void Connect(IPAddress ip)
    {
        Connection.Connect(ip, Port);
        _listener = new Thread(Listen);
        _listener.Start();
    }

    public bool IsInGame()
    {
        return Connection.Connected && InGame;
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
