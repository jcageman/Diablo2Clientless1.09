using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using System;

namespace ConsoleBot.Helpers;

public static class WayPointHelpers
{
    public static EntityCode MapTownWayPointCode(this Act act)
    {
        return act switch
        {
            Act.Act1 => EntityCode.WaypointAct1,
            Act.Act2 => EntityCode.WaypointAct2,
            Act.Act3 => EntityCode.WaypointAct3,
            Act.Act4 => EntityCode.WaypointAct4,
            Act.Act5 => EntityCode.WaypointAct5,
            _ => throw new InvalidOperationException($"specified invalid act {act}"),
        };
    }

    public static Waypoint MapTownWayPoint(this Act act)
    {
        return act switch
        {
            Act.Act1 => Waypoint.RogueEncampment,
            Act.Act2 => Waypoint.LutGholein,
            Act.Act3 => Waypoint.KurastDocks,
            Act.Act4 => Waypoint.ThePandemoniumFortress,
            Act.Act5 => Waypoint.Harrogath,
            _ => throw new InvalidOperationException($"specified invalid act {act}"),
        };
    }

    public static Area MapTownArea(this Act act)
    {
        return act switch
        {
            Act.Act1 => Area.RogueEncampment,
            Act.Act2 => Area.LutGholein,
            Act.Act3 => Area.KurastDocks,
            Act.Act4 => Area.ThePandemoniumFortress,
            Act.Act5 => Area.Harrogath,
            _ => throw new InvalidOperationException($"specified invalid act {act}"),
        };
    }
}
