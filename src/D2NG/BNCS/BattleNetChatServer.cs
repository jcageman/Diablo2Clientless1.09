using D2NG.BNCS.Packet;
using Serilog;
using Stateless;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using D2NG.BNCS.Exceptions;

namespace D2NG.BNCS
{
    internal class BattleNetChatServer
    {
        private const string RealmLogonPassword = "password";

        private const string DefaultChannel = "Diablo II";

        private BncsConnection Connection { get; } = new BncsConnection();

        protected ConcurrentDictionary<Sid, Action<BncsPacket>> PacketReceivedEventHandlers { get; } = new ConcurrentDictionary<Sid, Action<BncsPacket>>();

        protected ConcurrentDictionary<Sid, Action<BncsPacket>> PacketSentEventHandlers { get; } = new ConcurrentDictionary<Sid, Action<BncsPacket>>();

        private readonly StateMachine<State, Trigger> _machine = new StateMachine<State, Trigger>(State.NotConnected);

        private readonly StateMachine<State, Trigger>.TriggerWithParameters<string> _connectTrigger;
        private readonly StateMachine<State, Trigger>.TriggerWithParameters<string, string> _loginTrigger;
        private readonly StateMachine<State, Trigger>.TriggerWithParameters<uint, string> _joinChannelTrigger;
        private readonly StateMachine<State, Trigger>.TriggerWithParameters<string> _chatCommandTrigger;

        enum State
        {
            NotConnected,
            Connected,
            Verified,
            KeysAuthorized,
            UserAuthenticated,
            Chatting,
            InChat
        }

        enum Trigger
        {
            Connect,
            Disconnect,
            VerifyClient,
            AuthorizeKeys,
            Login,
            EnterChat,
            JoinChannel,
            ChatCommand,
            LeaveChat
        }

        public BncsContext Context { get; private set; }

        private readonly BncsEvent AuthCheckEvent = new BncsEvent();
        private readonly BncsEvent AuthInfoEvent = new BncsEvent();
        private readonly BncsEvent EnterChatEvent = new BncsEvent();
        private readonly BncsEvent LogonEvent = new BncsEvent();
        private readonly BncsEvent ListRealmsEvent = new BncsEvent();
        private readonly BncsEvent RealmLogonEvent = new BncsEvent();

        internal BattleNetChatServer()
        {
            _connectTrigger = _machine.SetTriggerParameters<string>(Trigger.Connect);

            _loginTrigger = _machine.SetTriggerParameters<string, string>(Trigger.Login);
            _joinChannelTrigger = _machine.SetTriggerParameters<uint, string>(Trigger.JoinChannel);
            _chatCommandTrigger = _machine.SetTriggerParameters<string>(Trigger.ChatCommand);

            _machine.Configure(State.NotConnected)
                .Permit(Trigger.Connect, State.Connected);

            _machine.Configure(State.Connected)
                .OnEntryFrom<String>(_connectTrigger, OnConnect)
                .Permit(Trigger.VerifyClient, State.Verified)
                .Permit(Trigger.AuthorizeKeys, State.KeysAuthorized)
                .Permit(Trigger.Disconnect, State.NotConnected)
                .Ignore(Trigger.ChatCommand);

            _machine.Configure(State.KeysAuthorized)
                .SubstateOf(State.Connected)
                .OnEntryFrom(Trigger.AuthorizeKeys, OnAuthorizeKeys)
                .Permit(Trigger.Login, State.UserAuthenticated)
                .Permit(Trigger.Disconnect, State.NotConnected);

            _machine.Configure(State.UserAuthenticated)
                .SubstateOf(State.Connected)
                .SubstateOf(State.KeysAuthorized)
                .OnEntryFrom(_loginTrigger, (username, password) => OnLogin(username, password))
                .OnEntryFrom(Trigger.LeaveChat, OnLeaveChat)
                .Permit(Trigger.EnterChat, State.InChat)
                .Permit(Trigger.Disconnect, State.NotConnected);

            _machine.Configure(State.InChat)
                .SubstateOf(State.Connected)
                .SubstateOf(State.KeysAuthorized)
                .SubstateOf(State.UserAuthenticated)
                .OnEntryFrom(Trigger.EnterChat, OnEnterChat)
                .InternalTransition(_joinChannelTrigger, (flags, channel, t) => OnJoinChannel(flags, channel))
                .InternalTransition(_chatCommandTrigger, (message, t) => OnChatCommand(message))
                .Permit(Trigger.LeaveChat, State.UserAuthenticated)
                .Permit(Trigger.Disconnect, State.NotConnected);

            Connection.PacketReceived += (obj, packet)
                => PacketReceivedEventHandlers.GetValueOrDefault(packet.Type, p => Log.Verbose($"Received unhandled BNCS packet of type: {p.Type}"))?.Invoke(packet);
            Connection.PacketSent += (obj, packet) => PacketSentEventHandlers.GetValueOrDefault(packet.Type, null)?.Invoke(packet);

            OnReceivedPacketEvent(Sid.PING, packet => Connection.WritePacket(packet.Raw));
            OnReceivedPacketEvent(Sid.QUERYREALMS2, ListRealmsEvent.Set);
            OnReceivedPacketEvent(Sid.LOGONREALMEX, RealmLogonEvent.Set);
            OnReceivedPacketEvent(Sid.AUTH_CHECK, AuthCheckEvent.Set);
            OnReceivedPacketEvent(Sid.AUTH_INFO, AuthInfoEvent.Set);
            OnReceivedPacketEvent(Sid.ENTERCHAT, EnterChatEvent.Set);
            OnReceivedPacketEvent(Sid.LOGONRESPONSE2, LogonEvent.Set);
            OnReceivedPacketEvent(Sid.REQUIREDWORK, _ => { });
        }

        internal void LeaveGame() => Connection.WritePacket(new LeaveGamePacket());

        public void EnterChat() => _machine.Fire(Trigger.EnterChat);
        private void OnEnterChat()
        {
            EnterChatEvent.Reset();
            Connection.WritePacket(new EnterChatRequestPacket(Context.Username));
            OnJoinChannel(0x05, DefaultChannel);
            _ = EnterChatEvent.WaitForPacket();
        }

        public void LeaveChat() => _machine.Fire(Trigger.LeaveChat);
        private void OnLeaveChat() => Connection.WritePacket(new LeaveChatPacket());

        public void JoinChannel(string channel) => _machine.Fire(_joinChannelTrigger, 0x02U, channel);
        private void OnJoinChannel(uint flags, string channel) => Connection.WritePacket(new JoinChannelRequestPacket(flags, channel));

        public void ChatCommand(string message) => _machine.Fire(_chatCommandTrigger, message);
        private void OnChatCommand(string message) => Connection.WritePacket(new ChatCommandPacket(message));

        public bool ConnectTo(string realm, string keyOwner, string gamefolder)
        {
            Log.Information($"Connecting to {realm}");

            Context = new BncsContext
            {
                ClientToken = (uint)Environment.TickCount,
                KeyOwner = keyOwner,
                Gamefolder = gamefolder
            };

            _machine.Fire(_connectTrigger, realm);
            _machine.Fire(Trigger.AuthorizeKeys);
            if (!_machine.IsInState(State.Connected))
            {
                Log.Warning($"Failed connecting to {realm}");
                return false;
            }
            Log.Information($"Connected to {realm}");
            return true;
        }

        private void Listen()
        {
            while (_machine.IsInState(State.Connected))
            {
                try
                {
                    _ = Connection.ReadPacket();
                }
                catch (Exception)
                {
                    Log.Debug("Chat Connection was terminated");
                    Thread.Sleep(300);
                }
            }
        }

        public void Login(string username, string password) => _machine.Fire(_loginTrigger, username, password);

        private void OnConnect(string realm)
        {
            Connection.Connect(realm);

            var listener = new Thread(Listen);
            listener.Start();
        }

        private void OnLogin(string username, string password)
        {
            Context.Username = username;

            LogonEvent.Reset();
            Connection.WritePacket(new LogonRequestPacket(
                Context.ClientToken,
                Context.ServerToken,
                Context.Username,
                password));
            var response = LogonEvent.WaitForPacket();
            _ = new LogonResponsePacket(response);
        }

        private void OnAuthorizeKeys()
        {
            AuthInfoEvent.Reset();
            Connection.WritePacket(new AuthInfoRequestPacket());
            var response = AuthInfoEvent.WaitForPacket(5000);
            if (response == null)
            {
                Log.Warning("Did not receive response on auth info event, disconnecting chat server");
                Disconnect();
                return;
            }

            AuthInfoResponsePacket packet;
            try
            {
                packet = new AuthInfoResponsePacket(response);
            }
            catch (BncsPacketException)
            {
                Log.Warning("Received non valid response from auth info event, disconnecting chat server");
                Disconnect();
                return;
            }

            Context.ServerToken = packet.ServerToken;

            var gameFile = Path.Combine(Context.Gamefolder, "Game.exe");

            var fi = new FileInfo(gameFile);
            var fileInfo = $"{fi.Name} {fi.LastWriteTimeUtc:MM\\/dd\\/yy HH:mm:ss} {fi.Length}";

            var fvi = FileVersionInfo.GetVersionInfo(gameFile);
            var exeversion = ((fvi.FileMajorPart << 24) | (fvi.FileMinorPart << 16) | (fvi.FileBuildPart << 8) | 0);

            //var checkVersionHash = CheckRevisionV1.FastComputeHash(packet.FormulaString, mpqFile, gameFile, bnetFile, d2File);
            var checkVersionHash = BitConverter.ToUInt32(new byte[] { 218, 18, 86, 73 });
            AuthCheckEvent.Reset();
            Connection.WritePacket(new AuthCheckRequestPacket(
                Context.ClientToken,
                Context.ServerToken,
                exeversion,
                checkVersionHash,
                fileInfo,
                Context.KeyOwner));

            var checkResponse = AuthCheckEvent.WaitForPacket(5000);
            if (checkResponse == null)
            {
                Log.Warning("Did not receive response on auth check event, disconnecting chat server");
                Disconnect();
                return;
            }
            _ = new AuthCheckResponsePacket(checkResponse);
        }

        public void OnReceivedPacketEvent(Sid type, Action<BncsPacket> handler)
            => PacketReceivedEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

        public void OnSentPacketEvent(Sid type, Action<BncsPacket> handler)
            => PacketSentEventHandlers.AddOrUpdate(type, handler, (t, h) => h += handler);

        public List<string> ListMcpRealms()
        {
            ListRealmsEvent.Reset();
            Connection.WritePacket(new QueryRealmsRequestPacket());
            var packet = ListRealmsEvent.WaitForPacket();
            return new QueryRealmsResponsePacket(packet.Raw).Realms;
        }

        public RealmLogonResponsePacket RealmLogon(string realmName)
        {
            RealmLogonEvent.Reset();
            Connection.WritePacket(new RealmLogonRequestPacket(
                Context.ClientToken,
                Context.ServerToken,
                realmName,
                RealmLogonPassword));
            var packet = RealmLogonEvent.WaitForPacket(5000);
            if (packet == null)
            {
                return null;
            }

            return new RealmLogonResponsePacket(packet.Raw);
        }

        internal void NotifyJoin(string name, string password) => Connection.WritePacket(new NotifyJoinPacket(name, password));

        public void Disconnect()
        {
            Connection.Terminate();
            _machine.Fire(Trigger.Disconnect);
        }
    }
}