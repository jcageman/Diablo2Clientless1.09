using D2NG.Core.D2GS.NetworkStream;
using D2NG.Core.Exceptions;
using Serilog;
using Stateless;
using System.Net;
using System.Net.Sockets;

namespace D2NG.Core
{
    internal abstract class Connection
    {
        protected enum State
        {
            NotConnected,
            Connected
        }

        protected enum Trigger
        {
            ConnectSocket,
            Terminate,
            Write,
            Read
        }

        /**
         * State Machine for the connection
         */
        protected readonly StateMachine<State, Trigger> _machine;

        private readonly StateMachine<State, Trigger>.TriggerWithParameters<IPAddress, int> _connectTrigger;

        /**
        * The actual TCP Connection
        */
        internal INetworkStream _stream;

        protected TcpClient _tcpClient;

        protected Connection()
        {
            _machine = new StateMachine<State, Trigger>(State.NotConnected);
            _connectTrigger = _machine.SetTriggerParameters<IPAddress, int>(Trigger.ConnectSocket);

            _machine.Configure(State.NotConnected)
                .OnEntryFrom(Trigger.Terminate, OnTerminate)
                .Permit(Trigger.ConnectSocket, State.Connected);

            _machine.Configure(State.Connected)
                .OnEntryFrom(_connectTrigger, (ip, port) => OnConnect(ip, port))
                .Permit(Trigger.Terminate, State.NotConnected);
        }

        internal abstract byte[] ReadPacket();

        public void WritePacket(Packet.Packet packet) => WritePacket(packet.Raw);

        internal abstract void WritePacket(byte[] packet);

        public void Connect(IPAddress ip, int port) => _machine.Fire(_connectTrigger, ip, port);

        protected void OnConnect(IPAddress ip, int port)
        {
            Log.Information("[{0}] Connecting to {1}:{2}", GetType(), ip, port);
            _tcpClient = new TcpClient()
            {
                SendTimeout = 10000,
                ReceiveTimeout = 10000
            };
            _tcpClient.Connect(ip, port);
            _stream = new D2GSNetworkStream(_tcpClient.GetStream());
            if (!_stream.CanWrite)
            {
                Log.Error("[{0}] Unable to write to {1}:{2}, closing connection", GetType(), ip, port);
                Terminate();
                throw new UnableToConnectException($"Unable to establish {GetType()}");
            }
            Initialize();
            Log.Information("[{0}] Successfully connected to {1}:{2}", GetType(), ip, port);
        }

        internal abstract void Initialize();

        public bool Connected => _machine.IsInState(State.Connected);

        public void Terminate() => _machine.Fire(Trigger.Terminate);

        protected void OnTerminate()
        {
            _tcpClient.Close();
            _stream.Close();
        }
    }
}
