using D2NG.D2GS.Items;
using System;

namespace D2NG.D2GS.Packet
{
    internal class IdentifyItemFromGamblePacket : D2gsPacket
    {
        public IdentifyItemFromGamblePacket(Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.IdentifyFromGamble,
                    BitConverter.GetBytes((uint)item.Id)
                )
            )
        {
        }
        public IdentifyItemFromGamblePacket(byte[] packet) : base(packet)
        {
        }

        public uint GetItemId()
        {
            return BitConverter.ToUInt32(Raw, 1);
        }
    }
}
