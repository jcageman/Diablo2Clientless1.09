using System;

namespace D2NG.D2GS.Packet.Outgoing
{
    internal class RightSkillOnUnitPacket : D2gsPacket
    {
        public RightSkillOnUnitPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RightSkillOnUnit,
                    BitConverter.GetBytes((uint)0x01),
                    BitConverter.GetBytes(entity.Id)
                )
            )
        {
        }
    }
}
