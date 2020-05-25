using D2NG.D2GS.Items;
using System;

namespace D2NG.D2GS.Packet
{
    internal class PickupItemFromGroundPacket : D2gsPacket
    {
        public PickupItemFromGroundPacket(Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.PickItem,
                    BitConverter.GetBytes((uint)0x04),
                    BitConverter.GetBytes((uint)item.Id),
                    BitConverter.GetBytes((uint)0x00)
                )
            )
        {
        }
    }
}
