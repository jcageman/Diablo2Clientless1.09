using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class LeftSkillOnUnitPacket : D2gsPacket
{
    public LeftSkillOnUnitPacket(Entity entity) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.LeftClickUnit,
                BitConverter.GetBytes((uint)entity.Type),
                BitConverter.GetBytes(entity.Id)
            )
        )
    {
    }
}
