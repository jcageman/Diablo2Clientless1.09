using D2NG.Core.D2GS.Players;
using System;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class AddStatPointPacket(Attribute stat)
    : D2gsPacket(BuildPacket((byte)OutGoingPacket.AddStatPoint, BitConverter.GetBytes((ushort)stat)))
{
}

internal class AddSkillPointPacket(Skill skill)
    : D2gsPacket(BuildPacket((byte)OutGoingPacket.AddSkillPoint, BitConverter.GetBytes((ushort)skill)))
{
}

internal class DropGoldPacket(uint playerId, uint amount)
    : D2gsPacket(BuildPacket((byte)OutGoingPacket.DropGold, BitConverter.GetBytes(playerId), BitConverter.GetBytes(amount)))
{
}
