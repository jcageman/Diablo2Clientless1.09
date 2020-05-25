using System;

namespace D2NG.D2GS.Packet
{
    internal class SelectSkillPacket : D2gsPacket
    {
        public SelectSkillPacket(Hand hand, Skill skill) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.SelectSkill,
                    BitConverter.GetBytes((ushort)skill),
                    hand == Hand.Left ? new byte[] { 0x00, 0x80 } : new byte[] { 0x00, 0x00 },
                    BitConverter.GetBytes(-1)
                )
            )
        {
        }
    }
}
