using System;

namespace D2NG.D2GS.Packet
{
    internal class RightSkillOnLocationPacket : D2gsPacket
    {
        public RightSkillOnLocationPacket(Point point) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RightSkillOnLocation,
                    BitConverter.GetBytes(point.X),
                    BitConverter.GetBytes(point.Y)
                )
            )
        {
        }
    }
}
