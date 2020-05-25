using System;

namespace D2NG.D2GS.Packet
{
    internal class InitiateEntityChatPacket : D2gsPacket
    {
        public InitiateEntityChatPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.InitiateEntityChat,
                    BitConverter.GetBytes((uint)0x01),
                    BitConverter.GetBytes((uint)entity.Id)
                )
            )
        {
        }
    }
}
