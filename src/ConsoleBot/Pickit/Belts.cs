using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;

namespace ConsoleBot.Pickit;

public static class Belts
{
    public static bool ShouldPickupItemClassic(Item item)
    {
        if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldPickupItemExpansion(Item item)
    {
        if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemExpansion(Item item)
    {
        if (item.Quality == QualityType.Unique)
        {
            switch (item.Name)
            {
                case ItemName.DemonhideSash:
                    return item.Ethereal;
                    //case ItemName.MeshBelt:
                    //    return !item.Ethereal;
                    //case ItemName.WarBelt: 
                    //case ItemName.VampirefangBelt:
                    //    return item.Ethereal;
            }
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.HeavyBelt && item.GetValueOfStatType(StatType.ExtraGold) >= 80)
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 40 && item.GetValueOfStatType(StatType.ExtraGold) >= 110)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemClassic(Item item)
    {
        if (item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 80)
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 100)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 60)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 50)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 10 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 120)
        {
            return true;
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.HeavyBelt && item.GetValueOfStatType(StatType.ExtraGold) >= 80)
        {
            return true;
        }

        return false;
    }
}
