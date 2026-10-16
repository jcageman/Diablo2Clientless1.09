using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>Drops the item on the cursor into the cube, the way dropping it on the cube icon does.</summary>
internal class ItemToCubePacket : D2gsPacket
{
    public ItemToCubePacket(Item item, Item cube) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.ItemToCube,
                BitConverter.GetBytes(item.Id),
                BitConverter.GetBytes(cube.Id)
            )
        )
    {
    }
}
