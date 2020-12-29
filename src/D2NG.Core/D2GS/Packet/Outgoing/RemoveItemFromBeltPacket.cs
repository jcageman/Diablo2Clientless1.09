using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RemoveItemFromBeltPacket : D2gsPacket
    {
        public RemoveItemFromBeltPacket(Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.ItemFromBelt,
                    BitConverter.GetBytes(item.Id)

                )
            )
        {
        }
    }
}
