using D2NG.D2GS.Act;
using System.Collections.Generic;

namespace D2NG.D2GS.Objects
{
    public static class WaypointExtensions
    {
        public static Area ToArea(this Waypoint waypoint)
        {
            return waypoint switch
            {
                Waypoint.RogueEncampment => Area.RogueEncampment,
                Waypoint.ColdPlains => Area.ColdPlains,
                Waypoint.StonyFields => Area.StonyFields,
                Waypoint.DarkWood => Area.DarkWood,
                Waypoint.BlackMarsh => Area.BlackMarsh,
                Waypoint.OuterCloister => Area.OuterCloister,
                Waypoint.JailLevel1 => Area.JailLevel1,
                Waypoint.InnerCloister => Area.InnerCloister,
                Waypoint.CatacombsLevel2 => Area.CatacombsLevel2,
                Waypoint.LutGholein => Area.LutGholein,
                Waypoint.SewersLevel2 => Area.SewersLevel2,
                Waypoint.DryHills => Area.DryHills,
                Waypoint.HallsOfTheDeadLevel2 => Area.HallsOfTheDeadLevel2,
                Waypoint.FarOasis => Area.FarOasis,
                Waypoint.LostCity => Area.LostCity,
                Waypoint.PalaceCellarLevel1 => Area.PalaceCellarLevel1,
                Waypoint.ArcaneSanctuary => Area.ArcaneSanctuary,
                Waypoint.CanyonOfTheMagi => Area.CanyonOfTheMagi,
                Waypoint.KurastDocks => Area.KurastDocks,
                Waypoint.SpiderForest => Area.SpiderForest,
                Waypoint.GreatMarsh => Area.GreatMarsh,
                Waypoint.FlayerJungle => Area.FlayerJungle,
                Waypoint.LowerKurast => Area.LowerKurast,
                Waypoint.KurastBazaar => Area.KurastBazaar,
                Waypoint.UpperKurast => Area.UpperKurast,
                Waypoint.Travincal => Area.Travincal,
                Waypoint.DuranceOfHateLevel2 => Area.DuranceOfHateLevel2,
                Waypoint.ThePandemoniumFortress => Area.ThePandemoniumFortress,
                Waypoint.CityOfTheDamned => Area.CityOfTheDamned,
                Waypoint.RiverOfFlame => Area.RiverOfFlame,
                Waypoint.Harrogath => Area.Harrogath,
                Waypoint.FrigidHighlands => Area.FrigidHighlands,
                Waypoint.ArreatPlateau => Area.ArreatPlateau,
                Waypoint.CrystallinePassage => Area.CrystallinePassage,
                Waypoint.GlacialTrail => Area.GlacialTrail,
                Waypoint.HallsOfPain => Area.HallsOfPain,
                Waypoint.FrozenTundra => Area.FrozenTundra,
                Waypoint.TheAncientsWay => Area.TheAncientsWay,
                Waypoint.TheWorldStoneKeepLevel2 => Area.TheWorldStoneKeepLevel2,
                _ => Area.None,
            };
        }
    }
}
