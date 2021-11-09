using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class ActivateBufferItemPacket : D2gsPacket
    {
        public ActivateBufferItemPacket(Self self, Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.ActivateBufferItem,
                    BitConverter.GetBytes(item.Id),
                    BitConverter.GetBytes((uint)self.Location.X),
                    BitConverter.GetBytes((uint)self.Location.Y)
                )
            )
        {
        }
        public ActivateBufferItemPacket(byte[] packet) : base(packet)
        {
        }

        public uint GetItemId()
        {
            return BitConverter.ToUInt32(Raw, 1);
        }
    }
}
