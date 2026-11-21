using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>Puts the item on the cursor - a gem, a rune, a jewel - into a free socket of <c>target</c>.</summary>
internal class SocketItemPacket : D2gsPacket
{
    public SocketItemPacket(Item item, Item target) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.SocketItem,
                BitConverter.GetBytes(item.Id),
                BitConverter.GetBytes(target.Id)
            )
        )
    {
    }
}
