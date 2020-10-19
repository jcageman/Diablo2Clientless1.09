using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RightSkillOnUnitPacket : D2gsPacket
    {
        public RightSkillOnUnitPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RightSkillOnUnit,
                    BitConverter.GetBytes((uint)entity.Type),
                    BitConverter.GetBytes(entity.Id)
                )
            )
        {
        }
    }
}
