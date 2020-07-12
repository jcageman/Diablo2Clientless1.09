using D2NG.MCP.Packet;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace D2NG.MCP
{
    public class RealmServer
    {
        private McpConnection Connection { get; } = new McpConnection();

        protected ConcurrentDictionary<Mcp, Action<McpPacket>> PacketReceivedEventHandlers { get; } = new ConcurrentDictionary<Mcp, Action<McpPacket>>();
        protected ConcurrentDictionary<Mcp, Action<McpPacket>> PacketSentEventHandlers { get; } = new ConcurrentDictionary<Mcp, Action<McpPacket>>();
        public ushort RequestId { get; private set; } = 0x02;

        private readonly McpEvent CharLogonEvent = new McpEvent();
        private readonly McpEvent CreateGameEvent = new McpEvent();
        private readonly McpEvent ListCharactersEvent = new McpEvent();
        private readonly McpEvent StartupEvent = new McpEvent();
        private readonly McpEvent JoinGameEvent = new McpEvent();

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

        internal bool CharLogon(Character character)
        {
            CharLogonEvent.Reset();
            var packet = new CharLogonRequestPacket(character.Name);
            Connection.WritePacket(packet);
            var loginResponsePacket = CharLogonEvent.WaitForPacket(2000);
            if (loginResponsePacket == null)
            {
                return false;
            }
            var response = new CharLogonResponsePacket(loginResponsePacket);
            if (response.Result != 0x00)
            {
                return false;
            }

            return true;
        }

        internal void Disconnect()
        {
            Log.Verbose("Disconnecting from MCP");
            Connection.Terminate();
            _listener.Join();
            Log.Verbose("Disconnected from MCP");
        }

        internal void Logon(uint mcpCookie, uint mcpStatus, List<byte> mcpChunk, string mcpUniqueName)
        {
            StartupEvent.Reset();
            var packet = new McpStartupRequestPacket(mcpCookie, mcpStatus, mcpChunk, mcpUniqueName);
            Connection.WritePacket(packet);
            var response = StartupEvent.WaitForPacket();
            _ = new McpStartupResponsePacket(response.Raw);
        }

        internal List<Character> ListCharacters()
        {
            ListCharactersEvent.Reset();
            Connection.WritePacket(new ListCharactersClientPacket());
            var packet = ListCharactersEvent.WaitForPacket();
            var response = new ListCharactersServerPacket(packet.Raw);
            return response.Characters;
        }

        internal void CreateGame(Difficulty difficulty, string gameName, string password, string description)
        {
            CreateGameEvent.Reset();
            Connection.WritePacket(new CreateGameRequestPacket(RequestId++, difficulty, gameName, password, description));
            _ = new CreateGameResponsePacket(CreateGameEvent.WaitForPacket().Raw);
        }

        internal JoinGameResponsePacket JoinGame(string name, string password)
        {
            JoinGameEvent.Reset();
            Connection.WritePacket(new JoinGameRequestPacket(RequestId++, name, password));
            return new JoinGameResponsePacket(JoinGameEvent.WaitForPacket().Raw);
        }

        internal void OnReceivedPacketEvent(Mcp type, Action<McpPacket> handler)
            => PacketReceivedEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

        internal void OnSentPacketEvent(Mcp type, Action<McpPacket> handler)
            => PacketSentEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);
    }
}