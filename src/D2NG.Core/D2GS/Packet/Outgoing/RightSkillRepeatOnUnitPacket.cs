using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RightSkillRepeatOnUnitPacket : D2gsPacket
    {
        public RightSkillRepeatOnUnitPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RightClickHoldUnit,
                    BitConverter.GetBytes((uint)entity.Type),
                    BitConverter.GetBytes(entity.Id)
                )
            )
        {
        }
    }
}
