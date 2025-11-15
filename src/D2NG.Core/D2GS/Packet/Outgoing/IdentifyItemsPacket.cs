using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class IdentifyItemsPacket : D2gsPacket
{
    public IdentifyItemsPacket(Entity entity) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.IdentifyItems,
                BitConverter.GetBytes(entity.Id)
            )
        )
    {
    }
}
