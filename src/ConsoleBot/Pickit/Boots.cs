using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit;

public static class Boots
{
    private static readonly HashSet<ItemName> casterBoots = [
        ItemName.Boots, ItemName.HeavyBoots, ItemName.ChainBoots, ItemName.LightPlatedBoots, ItemName.DemonhideBoots, ItemName.SharkskinBoots ];
    public static bool ShouldPickupItemClassic(Item item)
    {
        if (item.Quality == QualityType.Rare)
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
                //case ItemName.DemonhideBoots:
                //case ItemName.SharkskinBoots:
                case ItemName.BattleBoots:
                //case ItemName.MeshBoots:
                    return true;
                case ItemName.WarBoots:
                    return item.Ethereal;
            }
        }

        return ShouldKeepItemClassic(item);
    }

    public static bool ShouldKeepItemClassic(Item item)
    {
        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30
            && item.GetTotalResistFrLrCr() >= 70
            && (item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 100 || item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 50))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResistFrLrCr() >= 90 && (item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 90 || item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResistFrLrCr() >= 120)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30
            && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= 80
            && item.GetValueOfStatType(StatType.ExtraGold) > 90)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30
        && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= 50
        && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 20
        && item.GetValueOfStatType(StatType.ExtraGold) > 90)
        {
            return true;
        }

        if (casterBoots.Contains(item.Name))
        {
            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10
                && item.GetValueOfStatType(StatType.ColdResistance) >= 30
                && item.GetTotalResistFrLrCr() >= 50
                && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 30
                && item.GetValueOfStatType(StatType.ReplenishLife) >= 4)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 20 && item.GetTotalResistFrLrCr() >= 120)
            {
                return true;
            }
        }

        return false;
    }
}
