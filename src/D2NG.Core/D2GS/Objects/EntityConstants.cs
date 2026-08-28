using System.Collections.Generic;

namespace D2NG.Core.D2GS.Objects;

public static class EntityConstants
{
    /// <summary>
    /// Value in the trailing field of an entity action that picks the travel entry of a travel NPC
    /// menu, keyed by the act travelled from. The value differs per NPC and the server does not
    /// advertise it: 1.09 captures show Warriv sending 0x28 to leave act 1 and Meshif sending 0x4B
    /// to leave act 2, while the message ids those NPCs offered through 0x27 were unrelated.
    /// </summary>
    /// <remarks>
    /// Only these two transitions are driven by an NPC. Act 3 to 4 and act 4 to 5 are portals that
    /// open once their act boss is dead, so they are taken as warps rather than as a dialog.
    /// <para>
    /// The value belongs to the pair of NPC and destination rather than to the act: the same Warriv
    /// offers act 2 as 0x28 from act 1 but offers act 1 as 0x01 from act 2. Only the forward values
    /// are listed here because travelling back is done with the town waypoint, which every character
    /// has for the towns it has visited. Captures across normal and nightmare, three games and three
    /// entity ids all produced the same values, so these are constants per menu entry and the entity
    /// id has to be resolved per game.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<Act.Act, uint> TravelMenuActionByAct =
        new Dictionary<Act.Act, uint>
        {
            [Act.Act.Act1] = 0x28,
            [Act.Act.Act2] = 0x4B,
        };

    public static readonly HashSet<EntityCode> WayPointEntityCodes =
    [
        EntityCode.WaypointAct1,
        EntityCode.WaypointAct2,
        EntityCode.WaypointAct3,
        EntityCode.WaypointAct4,
        EntityCode.WaypointAct5,
    ];
}
