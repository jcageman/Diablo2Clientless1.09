using System;

namespace D2NG.D2GS.Packet.Outgoing
{
    internal class IdentifyItemsPacket : D2gsPacket
    {
        public IdentifyItemsPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.IdentifyItems,
                    BitConverter.GetBytes((uint)entity.Id)
                )
            )
        {
        }
    }
}
