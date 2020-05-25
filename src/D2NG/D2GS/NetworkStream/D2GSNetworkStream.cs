using System;

namespace D2NG.D2GS.NetworkStream
{
    public class D2GSNetworkStream : INetworkStream
    {
        private System.Net.Sockets.NetworkStream stream;

        public D2GSNetworkStream(System.Net.Sockets.NetworkStream ns)
        {
            if (ns == null) throw new ArgumentNullException("ns");
            this.stream = ns;
        }

        public bool CanWrite
        {
            get
            {
                return this.stream.CanWrite;
            }
        }

        public void Close()
        {
            this.stream.Close();
        }

        public void Write(byte[] buffer, int offset, int size)
        {
            this.stream.Write(buffer, offset, size);
        }

        public void WriteByte(byte value)
        {
            this.stream.WriteByte(value);
        }

        public int Read(byte[] buffer, int offset, int size)
        {
            return this.stream.Read(buffer, offset, size);
        }

        public int ReadByte()
        {
            return this.stream.ReadByte();
        }
    }
}
