using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class InsertItemIntoContainerPacket : D2gsPacket
{
    public InsertItemIntoContainerPacket(Item item, Point location, ItemContainer container) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.InsertItemToBuffer,
                BitConverter.GetBytes(item.Id),
                BitConverter.GetBytes((uint)location.X),
                BitConverter.GetBytes((uint)location.Y),
                BitConverter.GetBytes((uint)container)
            )
        )
    {
    }
}
