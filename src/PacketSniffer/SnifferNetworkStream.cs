using D2NG.Core.D2GS.NetworkStream;
using System;
using System.Linq;

namespace PacketSniffer
{
    public class SnifferNetworkStream : INetworkStream
    {
        private byte[] packet;

        public SnifferNetworkStream(byte[] packet)
        {
            this.packet = packet ?? throw new ArgumentNullException("packet");
        }

        public bool CanWrite
        {
            get
            {
                return false;
            }
        }

        public void Close()
        {
        }

        public void Write(byte[] buffer, int offset, int size)
        {
        }

        public void WriteByte(byte value)
        {
        }

        public int Read(byte[] buffer, int offset, int size)
        {
            var readSize = Math.Min(packet.Length, size);
            Array.Copy(packet, 0, buffer, offset, size);
            packet = packet.Skip(size).ToArray();
            return readSize;
        }

        public int ReadByte()
        {
            if (packet.Length == 0)
            {
                return -1;
            }

            var firstByte = packet[0];
            packet = packet.Skip(1).ToArray();
            return firstByte;
        }
    }
}
