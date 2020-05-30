﻿using D2NG.D2GS.Items;
using System;

namespace D2NG.D2GS.Packet
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