using D2NG.Core.D2GS.Objects;
using System;
using System.Linq;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class RepairPacket : D2gsPacket
{
    public RepairPacket(Entity entity) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.Repair,
                BitConverter.GetBytes(entity.Id),
                Enumerable.Repeat<byte>(0x00, 11).ToArray(),
                new byte[] { 0x80 }
            )
        )
    {
    }
}
