using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class TerminateEntityChatPacket : D2gsPacket
    {
        public TerminateEntityChatPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.TerminateEntityChat,
                    BitConverter.GetBytes((uint)0x01),
                    BitConverter.GetBytes((uint)entity.Id)
                )
            )
        {
        }
    }
}
