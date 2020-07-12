using D2NG.D2GS.Players;
using System;

namespace D2NG.D2GS.Packet.Outgoing
{
    internal class MakeEntityMovePacket : D2gsPacket
    {
        public MakeEntityMovePacket(Self self, Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.MakeEntityMove,
                    BitConverter.GetBytes((uint)0x01),
                    BitConverter.GetBytes((uint)entity.Id),
                    BitConverter.GetBytes((uint)self.Location.X),
                    BitConverter.GetBytes((uint)self.Location.Y)
                )
            )
        {
        }
    }
}
