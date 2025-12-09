using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit;

public static class Jewels
{
    public static bool ShouldPickupItemExpansion(Item item)
    {
        return true;
    }

    public static bool ShouldKeepItemExpansion(Item item)
    {
        if (item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 30)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.MinimumDamage) + item.GetValueOfStatType(StatType.MaximumDamage) >= 20)
        {
            return true;
        }

        if (item.GetTotalLifeFromStats(D2NG.Core.D2GS.Enums.CharacterClass.Sorceress) >= 40)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.ExtraGold) >= 20)
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= 15 && item.GetTotalLifeFromStats(D2NG.Core.D2GS.Enums.CharacterClass.Sorceress) >= 20)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 15 && item.GetValueOfStatType(StatType.EnhancedMaximumDamage) > 0)
        {
            return true;
        }
        /*
        if(item.Quality == QualityType.Magical)
        {
            return true;
        }
        */

        return false;
    }
}
