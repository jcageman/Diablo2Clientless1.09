using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class SellItemPacket : D2gsPacket
    {
        public SellItemPacket(Entity entity, Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.SellItem,
                    BitConverter.GetBytes(entity.Id),
                    BitConverter.GetBytes(item.Id),
                    BitConverter.GetBytes((uint)0x00),
                    BitConverter.GetBytes((uint)0x00)
                )
            )
        {
        }
        public SellItemPacket(byte[] packet) : base(packet)
        {
        }

        public uint GetItemId()
        {
            return BitConverter.ToUInt32(Raw, 5);
        }
    }
}
