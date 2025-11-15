using System;

namespace D2NG.Core.D2GS.NetworkStream;

public class D2GSNetworkStream : INetworkStream
{
    private System.Net.Sockets.NetworkStream stream;

    public D2GSNetworkStream(System.Net.Sockets.NetworkStream ns)
    {
        if (ns == null) throw new ArgumentNullException("ns");
        stream = ns;
    }

    public bool CanWrite
    {
        get
        {
            return stream.CanWrite;
        }
    }

    public bool DataAvailable
    {
        get
        {
            return stream.DataAvailable;
        }
    }

    public void Close()
    {
        stream.Close();
    }

    public void Write(byte[] buffer, int offset, int size)
    {
        stream.Write(buffer, offset, size);
    }

    public void WriteByte(byte value)
    {
        stream.WriteByte(value);
    }

    public int Read(byte[] buffer, int offset, int size)
    {
        return stream.Read(buffer, offset, size);
    }

    public int ReadByte()
    {
        return stream.ReadByte();
    }
}
