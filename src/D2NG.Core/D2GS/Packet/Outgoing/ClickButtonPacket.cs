﻿using D2NG.Core.D2GS.Enums;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class ClickButtonPacket : D2gsPacket
    {
        public ClickButtonPacket(ClickType clickType, int gold) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.ClickButton,
                    BitConverter.GetBytes((ushort)clickType),
                    BitConverter.GetBytes((ushort)Math.DivRem(gold, 65536, out var remainder)),
                    BitConverter.GetBytes((ushort)remainder)
                )
            )
        {
        }
    }
}
