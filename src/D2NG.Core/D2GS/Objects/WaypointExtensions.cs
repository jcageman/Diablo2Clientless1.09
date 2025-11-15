using D2NG.Core.D2GS.Act;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Objects;

public static class WaypointExtensions
{
    private static readonly IDictionary<Waypoint, Area> WaypointToArea = new Dictionary<Waypoint, Area>()
    {
        { Waypoint.RogueEncampment, Area.RogueEncampment },
        { Waypoint.ColdPlains , Area.ColdPlains },
        { Waypoint.StonyFields , Area.StonyFields },
        { Waypoint.DarkWood , Area.DarkWood },
        { Waypoint.BlackMarsh , Area.BlackMarsh },
        { Waypoint.OuterCloister , Area.OuterCloister },
        { Waypoint.JailLevel1 , Area.JailLevel1 },
        { Waypoint.InnerCloister , Area.InnerCloister },
        { Waypoint.CatacombsLevel2 , Area.CatacombsLevel2 },
        { Waypoint.LutGholein , Area.LutGholein },
        { Waypoint.SewersLevel2 , Area.SewersLevel2 },
        { Waypoint.DryHills , Area.DryHills },
        { Waypoint.HallsOfTheDeadLevel2 , Area.HallsOfTheDeadLevel2 },
        { Waypoint.FarOasis , Area.FarOasis },
        { Waypoint.LostCity , Area.LostCity },
        { Waypoint.PalaceCellarLevel1 , Area.PalaceCellarLevel1 },
        { Waypoint.ArcaneSanctuary , Area.ArcaneSanctuary },
        { Waypoint.CanyonOfTheMagi , Area.CanyonOfTheMagi },
        { Waypoint.KurastDocks , Area.KurastDocks },
        { Waypoint.SpiderForest , Area.SpiderForest },
        { Waypoint.GreatMarsh , Area.GreatMarsh },
        { Waypoint.FlayerJungle , Area.FlayerJungle },
        { Waypoint.LowerKurast , Area.LowerKurast },
        { Waypoint.KurastBazaar , Area.KurastBazaar },
        { Waypoint.UpperKurast , Area.UpperKurast },
        { Waypoint.Travincal , Area.Travincal },
        { Waypoint.DuranceOfHateLevel2 , Area.DuranceOfHateLevel2 },
        { Waypoint.ThePandemoniumFortress , Area.ThePandemoniumFortress },
        { Waypoint.CityOfTheDamned , Area.CityOfTheDamned },
        { Waypoint.RiverOfFlame , Area.RiverOfFlame },
        { Waypoint.Harrogath , Area.Harrogath },
        { Waypoint.FrigidHighlands , Area.FrigidHighlands },
        { Waypoint.ArreatPlateau , Area.ArreatPlateau },
        { Waypoint.CrystallinePassage , Area.CrystallinePassage },
        { Waypoint.GlacialTrail , Area.GlacialTrail },
        { Waypoint.HallsOfPain , Area.HallsOfPain },
        { Waypoint.FrozenTundra , Area.FrozenTundra },
        { Waypoint.TheAncientsWay , Area.TheAncientsWay },
        { Waypoint.TheWorldStoneKeepLevel2 , Area.TheWorldStoneKeepLevel2 }
    };

    private static readonly IDictionary<Area, Waypoint> AreaToWaypoint = WaypointToArea.ToDictionary(x => x.Value, x => x.Key);

    public static Area ToArea(this Waypoint waypoint)
    {
        if(WaypointToArea.TryGetValue(waypoint, out var area))
        {
            return area;
        }

        return Area.None;
    }

    public static Waypoint? ToWaypoint(this Area area)
    {
        if (AreaToWaypoint.TryGetValue(area, out var waypoint))
        {
            return waypoint;
        }

        return null;
    }

    public static EntityCode ToEntityCode(this Waypoint waypoint)
    {
        return waypoint switch
        {
            Waypoint.RogueEncampment => EntityCode.WaypointAct1,
            Waypoint.ColdPlains => EntityCode.WaypointAct1,
            Waypoint.StonyFields => EntityCode.WaypointAct1,
            Waypoint.DarkWood => EntityCode.WaypointAct1,
            Waypoint.BlackMarsh => EntityCode.WaypointAct1,
            Waypoint.OuterCloister => EntityCode.WaypointAct1,
            Waypoint.JailLevel1 => EntityCode.WaypointAct1JailAndUp,
            Waypoint.InnerCloister => EntityCode.WaypointAct1JailAndUp,
            Waypoint.CatacombsLevel2 => EntityCode.WaypointAct1JailAndUp,
            Waypoint.LutGholein => EntityCode.WaypointAct2,
            Waypoint.SewersLevel2 => EntityCode.WaypointAct2Sewer,
            Waypoint.DryHills => EntityCode.WaypointAct2,
            Waypoint.HallsOfTheDeadLevel2 => EntityCode.WaypointAct2,
            Waypoint.FarOasis => EntityCode.WaypointAct2,
            Waypoint.LostCity => EntityCode.WaypointAct2,
            Waypoint.PalaceCellarLevel1 => EntityCode.WaypointAct2Cellar,
            Waypoint.ArcaneSanctuary => EntityCode.WaypointAct2Arcane,
            Waypoint.CanyonOfTheMagi => EntityCode.WaypointAct2Arcane,
            Waypoint.KurastDocks => EntityCode.WaypointAct3,
            Waypoint.SpiderForest => EntityCode.WaypointAct3,
            Waypoint.GreatMarsh => EntityCode.WaypointAct3,
            Waypoint.FlayerJungle => EntityCode.WaypointAct3,
            Waypoint.LowerKurast => EntityCode.WaypointAct3,
            Waypoint.KurastBazaar => EntityCode.WaypointAct3,
            Waypoint.UpperKurast => EntityCode.WaypointAct3,
            Waypoint.Travincal => EntityCode.WaypointAct3,
            Waypoint.DuranceOfHateLevel2 => EntityCode.WaypointAct3Durance,
            Waypoint.ThePandemoniumFortress => EntityCode.WaypointAct4,
            Waypoint.CityOfTheDamned => EntityCode.WaypointAct4Levels,
            Waypoint.RiverOfFlame => EntityCode.WaypointAct4Levels,
            Waypoint.Harrogath => EntityCode.WaypointAct5,
            Waypoint.FrigidHighlands => EntityCode.WaypointAct5,
            Waypoint.ArreatPlateau => EntityCode.WaypointAct5,
            Waypoint.CrystallinePassage => EntityCode.WaypointAct5Glacial,
            Waypoint.GlacialTrail => EntityCode.WaypointAct5Glacial,
            Waypoint.HallsOfPain => EntityCode.WaypointAct5Glacial,
            Waypoint.FrozenTundra => EntityCode.WaypointAct5Glacial,
            Waypoint.TheAncientsWay => EntityCode.WaypointAct5Glacial,
            Waypoint.TheWorldStoneKeepLevel2 => EntityCode.WaypointAct5Baal,
            _ => throw new ArgumentOutOfRangeException(nameof(waypoint), waypoint, null)
        };
    }
}
