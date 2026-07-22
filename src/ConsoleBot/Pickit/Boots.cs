using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit;

public static class Boots
{
    private static int Min(int value) => PickitThresholdScaling.Min(PickitItemType.Boots, value);

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
        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= Min(30)
            && item.GetTotalResistFrLrCr() >= Min(70)
            && (item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(100) || item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(50)))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= Min(30) && item.GetTotalResistFrLrCr() >= Min(90) && (item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(90) || item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(40)))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= Min(30) && item.GetTotalResistFrLrCr() >= Min(40))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= Min(30)
            && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= Min(80)
            && item.GetValueOfStatType(StatType.ExtraGold) > Min(90))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.FasterRunWalk) >= Min(30)
        && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= Min(20)
        && (item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= Min(20)
        || item.GetValueOfStatType(StatType.ExtraGold) > Min(90)))
        {
            return true;
        }

        if (casterBoots.Contains(item.Name))
        {
            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(60) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(40))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10)
                && item.GetValueOfStatType(StatType.ColdResistance) >= Min(30)
                && item.GetTotalResistFrLrCr() >= Min(50)
                && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(30)
                && item.GetValueOfStatType(StatType.ReplenishLife) >= Min(4))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(20) && item.GetTotalResistFrLrCr() >= Min(120))
            {
                return true;
            }
        }

        return false;
    }
}
