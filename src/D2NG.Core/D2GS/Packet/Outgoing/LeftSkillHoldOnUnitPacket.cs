using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class LeftSkillHoldOnUnitPacket : D2gsPacket
{
    public LeftSkillHoldOnUnitPacket(Entity entity) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.LeftClickHoldUnit,
                BitConverter.GetBytes((uint)entity.Type),
                BitConverter.GetBytes(entity.Id)
            )
        )
    {
    }
}
