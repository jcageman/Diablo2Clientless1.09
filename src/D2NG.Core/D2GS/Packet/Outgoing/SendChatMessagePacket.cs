﻿using System.Text;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class SendChatMessagePacket : D2gsPacket
    {
        public SendChatMessagePacket(string message) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.Chat,
                    new byte[] { 0x01, 0x00 },
                    Encoding.ASCII.GetBytes($"{message}\0"),
                    new byte[] { 0x00, 0x00 }
                )
            )
        {
        }
    }
}
