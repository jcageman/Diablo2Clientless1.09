using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.Extensions;
using D2NG.Core.MCP;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public GameServer()
    {
        InstanceId = ++instanceCounter;
        Connection = new GameServerConnection(InstanceId);

        Connection.PacketReceived += (obj, eventArgs) =>
        {
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
            catch (Exception)
            {
                Log.Debug($"InstanceId {InstanceId} GameServer Connection was terminated");
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
