using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.MCP;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace D2NG.Core.D2GS
{
    internal class GameServer : IDisposable
    {
        private const ushort Port = 4000;

        private bool InGame = false;

        private GameServerConnection Connection { get; } = new GameServerConnection();

        protected ConcurrentDictionary<InComingPacket, Action<D2gsPacket>> PacketReceivedEventHandlers { get; } = new ConcurrentDictionary<InComingPacket, Action<D2gsPacket>>();

        protected ConcurrentDictionary<OutGoingPacket, Action<D2gsPacket>> PacketSentEventHandlers { get; } = new ConcurrentDictionary<OutGoingPacket, Action<D2gsPacket>>();

        protected ConcurrentDictionary<InComingPacket, ManualResetEvent> IncomingPacketEvents { get; } = new ConcurrentDictionary<InComingPacket, ManualResetEvent>();

        private Thread _listener;

        public GameServer()
        {
            Connection.PacketReceived += (obj, eventArgs) =>
            {
                if (Enum.IsDefined(typeof(InComingPacket), eventArgs.Type))
                {
                    var incomingPacketType = (InComingPacket)eventArgs.Type;
                    if (incomingPacketType == InComingPacket.GameFlags)
                    {
                        InGame = true;
                    }
                    Log.Debug($"Received D2GS packet of type: {incomingPacketType} with data {eventArgs.Raw.ToPrintString()}");
                    PacketReceivedEventHandlers.GetValueOrDefault(incomingPacketType, p => Log.Debug($"Received unhandled D2GS packet of type: {incomingPacketType}"))?.Invoke(eventArgs);
                    SetPacketEventType(incomingPacketType);
                }
                else
                {
                    Log.Warning($"Received unknown D2GS packet of type: 0x{(byte)eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
                }
            };

            Connection.PacketSent += (obj, eventArgs) =>
            {
                if (Enum.IsDefined(typeof(OutGoingPacket), eventArgs.Type))
                {
                    var outgoingPacketType = (OutGoingPacket)eventArgs.Type;
                    Log.Debug($"Sent D2GS packet of type: {outgoingPacketType} with data {eventArgs.Raw.ToPrintString()}");
                    PacketSentEventHandlers.GetValueOrDefault(outgoingPacketType, null)?.Invoke(eventArgs);
                }
                else
                {
                    Log.Warning($"Send unknown D2GS packet of type: 0x{(byte)eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
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
                    Log.Debug("GameServer Connection was terminated");
                    Thread.Sleep(300);
                }
            }
        }

        internal void Disconnect()
        {
            Connection.Terminate();
            _listener.Join();
        }

        public void LeaveGame()
        {
            var leaveGameConfirmed = GetResetEventOfType(InComingPacket.LeaveGameConfirmed);
            InGame = false;
            Connection.WritePacket(OutGoingPacket.LeaveGame);
            leaveGameConfirmed.WaitOne(2000);
            Disconnect();
        }

        internal bool GameLogon(uint gameHash, ushort gameToken, Character character)
        {
            var successLoadEvent = GetResetEventOfType(InComingPacket.LoadSuccessful);
            Connection.WritePacket(new GameLogonPacket(gameHash, gameToken, character));
            if (!successLoadEvent.WaitOne(5000))
            {
                return false;
            }
            var loadActComplete = GetResetEventOfType(InComingPacket.LoadActComplete);
            Connection.WritePacket(OutGoingPacket.Startup);
            loadActComplete.WaitOne();
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

        internal void Ping() => Connection.WritePacket(new PingPacket());

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
}
