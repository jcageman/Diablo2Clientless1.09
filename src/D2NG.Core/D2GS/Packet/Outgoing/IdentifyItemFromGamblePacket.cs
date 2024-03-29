﻿using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class IdentifyItemFromGamblePacket : D2gsPacket
    {
        public IdentifyItemFromGamblePacket(Item item) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.IdentifyFromGamble,
                    BitConverter.GetBytes(item.Id)
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
