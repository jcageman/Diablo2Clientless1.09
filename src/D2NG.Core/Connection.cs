using D2NG.Core.D2GS.NetworkStream;
using D2NG.Core.Exceptions;
using Serilog;
using System.Net;
using System.Net.Sockets;

namespace D2NG.Core;

internal abstract class Connection : System.IDisposable
{
    protected enum State
    {
        NotConnected,
        Connected
    }

    /**
    * The actual TCP Connection
    */
    internal INetworkStream _stream;

    protected TcpClient _tcpClient;

    private State _state = State.NotConnected;

    internal abstract byte[] ReadPacket();

    public void WritePacket(Packet.Packet packet) => WritePacket(packet.Raw);

    internal abstract void WritePacket(byte[] packet);

    public void Connect(IPAddress ip, int port)
    {
        Log.Debug("[{0}] Connecting to {1}:{2}", GetType(), ip, port);
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
        Log.Debug("[{0}] Successfully connected to {1}:{2}", GetType(), ip, port);
        _state = State.Connected;
    }

    internal abstract void Initialize();

    public bool Connected => _state == State.Connected;

    public void Terminate()
    {
        _state = State.NotConnected;
        _tcpClient.Close();
        _stream.Close();
    }

    public void Dispose()
    {
        throw new System.NotImplementedException();
    }
}
