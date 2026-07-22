using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit;

public static class Belts
{
    private static int Min(int value) => PickitThresholdScaling.Min(PickitItemType.Belts, value);

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

        if (item.Quality == QualityType.Unique && item.Name == ItemName.HeavyBelt && item.GetValueOfStatType(StatType.ExtraGold) >= Min(80))
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= Min(40) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(40) && item.GetValueOfStatType(StatType.ExtraGold) >= Min(110))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemClassic(Item item)
    {
        if (item.GetTotalResistFrLrCr() >= Min(60) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(80))
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= Min(40) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(100))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(40) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(60))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(70) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(50))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(10) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(120))
        {
            return true;
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.HeavyBelt && item.GetValueOfStatType(StatType.ExtraGold) >= Min(80))
        {
            return true;
        }

        return false;
    }
}
