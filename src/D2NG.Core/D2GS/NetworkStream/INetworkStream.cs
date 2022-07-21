﻿namespace D2NG.Core.D2GS.NetworkStream
{
    public interface INetworkStream
    {
        bool CanWrite { get; }

        public bool DataAvailable { get; }

        public void Close();

        void Write(byte[] buffer, int offset, int size);


        void WriteByte(byte value);

        int Read(byte[] buffer, int offset, int size);

        int ReadByte();
    }
}
