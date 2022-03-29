using D2NG.Core.D2GS.Players;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class SetPlayerRelationPacket : D2gsPacket
    {
        public SetPlayerRelationPacket(Player player) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.SetPlayerRelation,
                    new byte[] { 0x01, 0x01 },
                    BitConverter.GetBytes(player.Id)
                )
            )
        {
        }
        public SetPlayerRelationPacket(byte[] packet) : base(packet)
        {
        }
    }
}
