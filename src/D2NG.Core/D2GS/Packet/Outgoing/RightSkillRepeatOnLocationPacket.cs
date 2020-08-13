using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class RightSkillRepeatOnLocationPacket : D2gsPacket
    {
        public RightSkillRepeatOnLocationPacket(Point point) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.RightSkillRepeatOnLocation,
                    BitConverter.GetBytes(point.X),
                    BitConverter.GetBytes(point.Y)
                )
            )
        {
        }
    }
}
