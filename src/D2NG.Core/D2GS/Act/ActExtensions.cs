using System;

namespace D2NG.Core.D2GS.Act;

public static class ActExtensions
{
    public static Act MapToAct(this Area area)
    {
        return (int)area switch
        {
            var n when (n <= (int)Area.CowLevel) => Act.Act1,
            var n when (n <= (int)Area.ArcaneSanctuary) => Act.Act2,
            var n when (n <= (int)Area.DuranceOfHateLevel3) => Act.Act3,
            var n when (n <= (int)Area.ChaosSanctuary) => Act.Act4,
            var n when (n <= (int)Area.TheWorldStoneChamber) => Act.Act5,
            _ => throw new InvalidOperationException($"specified invalid area {area}"),
        };
    }
}
