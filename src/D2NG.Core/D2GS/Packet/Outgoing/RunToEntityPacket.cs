using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RunToEntityPacket : D2gsPacket
    {
        public RunToEntityPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RunToUnit,
                    BitConverter.GetBytes((uint)entity.Type),
                    BitConverter.GetBytes(entity.Id)
                )
            )
        {
        }
    }
}
