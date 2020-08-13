using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RequestUpdatePacket : D2gsPacket
    {
        public RequestUpdatePacket(uint id) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RequestEntityUpdate,
                    BitConverter.GetBytes(0),
                    BitConverter.GetBytes(id)
                )
            )
        {
        }
    }
}
