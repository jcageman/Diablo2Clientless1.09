using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class MakeEntityMovePacket : D2gsPacket
    {
        public MakeEntityMovePacket(Self self, Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.MakeEntityMove,
                    BitConverter.GetBytes((uint)0x01),
                    BitConverter.GetBytes(entity.Id),
                    BitConverter.GetBytes((uint)self.Location.X),
                    BitConverter.GetBytes((uint)self.Location.Y)
                )
            )
        {
        }
    }
}
