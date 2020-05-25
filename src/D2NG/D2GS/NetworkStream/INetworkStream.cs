using System;
using System.Collections.Generic;
using System.Text;

namespace D2NG.D2GS.NetworkStream
{
    public interface INetworkStream
    {
        bool CanWrite { get; }
        public void Close();

        void Write(byte[] buffer, int offset, int size);


        void WriteByte(byte value);

        int Read(byte[] buffer, int offset, int size);

        int ReadByte();
    }
}
