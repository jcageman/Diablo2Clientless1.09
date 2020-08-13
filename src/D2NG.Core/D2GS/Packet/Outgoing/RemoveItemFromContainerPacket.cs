using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RemoveItemFromContainerPacket : D2gsPacket
    {
        public RemoveItemFromContainerPacket(Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RemoveItemFromBuffer,
                    BitConverter.GetBytes(item.Id)

                )
            )
        {
        }
    }
}
