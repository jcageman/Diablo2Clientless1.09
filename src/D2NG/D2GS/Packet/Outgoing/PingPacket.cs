using System;

namespace D2NG.D2GS.Packet
{
    internal class PingPacket : D2gsPacket
    {
        public PingPacket() :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.Ping,
                    BitConverter.GetBytes(Environment.TickCount),
                    new byte[] { 0x00, 0x00, 0x00, 0x00 }
                )
            )
        {
        }
    }
}