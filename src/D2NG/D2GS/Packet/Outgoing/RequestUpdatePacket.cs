using System;
using System.Collections.Generic;
using System.Text;

namespace D2NG.D2GS.Packet
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
