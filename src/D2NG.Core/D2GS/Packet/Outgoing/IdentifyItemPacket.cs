using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class IdentifyItemPacket : D2gsPacket
{
    public IdentifyItemPacket(Item identifyItem, Item itemToIdentify) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.IdentifyItem,
                BitConverter.GetBytes(itemToIdentify.Id),
                BitConverter.GetBytes(identifyItem.Id)
            )
        )
    {
    }
    public IdentifyItemPacket(byte[] packet) : base(packet)
    {
    }
}
