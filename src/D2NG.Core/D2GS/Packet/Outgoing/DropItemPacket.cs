using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class DropItemPacket : D2gsPacket
    {
        public DropItemPacket(Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.DropItem,
                    BitConverter.GetBytes(item.Id)
                )
            )
        {
        }
    }
}
