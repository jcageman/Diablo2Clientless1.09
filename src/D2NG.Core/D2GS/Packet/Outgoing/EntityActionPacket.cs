using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class EntityActionPacket : D2gsPacket
{
    public EntityActionPacket(Entity entity, TownFolkActionType actionType) :
        this(entity, actionType, (uint)(actionType == TownFolkActionType.RefreshGamble ? 0x01 : 0x00))
    {
    }

    /// <summary>
    /// Entity action with an explicit value in the trailing field, which selects an entry of the NPC
    /// menu. Act travel uses <see cref="TownFolkActionType.Quest"/> with
    /// <see cref="EntityConstants.TravelMenuAction"/> on the caravan NPC; captured from a real 1.09
    /// client as 0x38 00000000 &lt;npcId&gt; 00000028, answered with 0x05 then 0x04 on arrival.
    /// </summary>
    public EntityActionPacket(Entity entity, TownFolkActionType actionType, uint actionData) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.EntityAction,
                BitConverter.GetBytes((uint)actionType),
                BitConverter.GetBytes(entity.Id),
                BitConverter.GetBytes(actionData)
            )
        )
    {
    }

    public EntityActionPacket(byte[] packet) : base(packet)
    {
    }

    /// <summary>
    /// The action, which occupies the first field of this packet. Despite what the position suggests
    /// it is not an entity type: a 1.09 capture shows 0x00 for the quest menu and 0x01 for trade.
    /// </summary>
    public uint GetActionType()
    {
        return BitConverter.ToUInt32(Raw, 1);
    }
    public uint GetEntityId()
    {
        return BitConverter.ToUInt32(Raw, 5);
    }
}
