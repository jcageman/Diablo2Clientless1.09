using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class ShiftLeftSkillHoldOnLocationPacket : D2gsPacket
    {
        public ShiftLeftSkillHoldOnLocationPacket(Point point) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.ShiftLeftClickHold,
                    BitConverter.GetBytes(point.X),
                    BitConverter.GetBytes(point.Y)
                )
            )
        {
        }
    }
}
