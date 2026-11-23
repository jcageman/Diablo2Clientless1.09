using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace D2NG.Mule.Upgrades;

/// <summary>Stat names and lines in the words the game's tooltips use.</summary>
public static partial class StatText
{
    private static readonly Dictionary<string, (string Short, string Line)> Known = new(StringComparer.Ordinal)
    {
        ["Strength"] = ("Strength", "+{0} to Strength"),
        ["Dexterity"] = ("Dexterity", "+{0} to Dexterity"),
        ["Vitality"] = ("Vitality", "+{0} to Vitality"),
        ["Energy"] = ("Energy", "+{0} to Energy"),
        ["Life"] = ("Life", "+{0} to Life"),
        ["Mana"] = ("Mana", "+{0} to Mana"),
        ["EnhancedLife"] = ("Life %", "Increase Maximum Life {0}%"),
        ["EnhancedMana"] = ("Mana %", "Increase Maximum Mana {0}%"),
        ["FireResistance"] = ("Fire res", "Fire Resist +{0}%"),
        ["ColdResistance"] = ("Cold res", "Cold Resist +{0}%"),
        ["LightningResistance"] = ("Lightning res", "Lightning Resist +{0}%"),
        ["PoisonResistance"] = ("Poison res", "Poison Resist +{0}%"),
        ["MaximumFireResistance"] = ("Max fire res", "+{0}% to Maximum Fire Resist"),
        ["MaximumColdResistance"] = ("Max cold res", "+{0}% to Maximum Cold Resist"),
        ["MaximumLightningResistance"] = ("Max lightning res", "+{0}% to Maximum Lightning Resist"),
        ["MaximumPoisonResistance"] = ("Max poison res", "+{0}% to Maximum Poison Resist"),
        ["MinimumLifeStolenPerHit"] = ("Life leech", "{0}% Life Stolen per Hit"),
        ["MinimumManaStolenPerHit"] = ("Mana leech", "{0}% Mana Stolen per Hit"),
        ["FasterCastRate"] = ("FCR", "+{0}% Faster Cast Rate"),
        ["FasterHitRecovery"] = ("FHR", "+{0}% Faster Hit Recovery"),
        ["FasterBlockRate"] = ("FBR", "+{0}% Faster Block Rate"),
        ["FasterRunWalk"] = ("Run/walk", "+{0}% Faster Run/Walk"),
        ["IncreasedAttackSpeed"] = ("IAS", "+{0}% Increased Attack Speed"),
        ["IncreasedBlocking"] = ("Block", "{0}% Increased Chance of Blocking"),
        ["AttackRating"] = ("Attack rating", "+{0} to Attack Rating"),
        ["ToHitPercent"] = ("AR %", "+{0}% Bonus to Attack Rating"),
        ["Defense"] = ("Defense", "+{0} Defense"),
        ["EnhancedDefense"] = ("Defense %", "+{0}% Enhanced Defense"),
        ["EnhancedMaximumDamage"] = ("Damage %", "+{0}% Enhanced Damage"),
        ["EnhancedDamage"] = ("Damage %", "+{0}% Enhanced Damage"),
        ["MinimumDamage"] = ("Min damage", "+{0} to Minimum Damage"),
        ["MaximumDamage"] = ("Max damage", "+{0} to Maximum Damage"),
        ["ExtraGold"] = ("Gold", "{0}% Extra Gold from Monsters"),
        ["BetterChanceOfGettingMagicItem"] = ("Magic find", "{0}% Better Chance of Getting Magic Items"),
        ["AllSkills"] = ("All skills", "+{0} to All Skills"),
        ["ReducedRequirements"] = ("Requirements", "Requirements {0}%"),
        ["DamageReduction"] = ("Damage reduced", "Damage Reduced by {0}"),
        ["MagicalDamageReduction"] = ("Magic damage reduced", "Magic Damage Reduced by {0}"),
        ["ReplenishLife"] = ("Replenish life", "Replenish Life +{0}"),
        ["AttackerTakesDamage"] = ("Thorns", "Attacker Takes Damage of {0}"),
        ["PoisonLengthReduction"] = ("Poison length", "Poison Length Reduced by {0}%"),
        ["ManaRecoveryBonus"] = ("Mana regen", "Regenerate Mana {0}%"),
        ["CannotBeFrozen"] = ("Cannot be frozen", "Cannot Be Frozen"),
        ["HalfFreezeDuration"] = ("Half freeze", "Half Freeze Duration"),
        ["LightRadius"] = ("Light radius", "+{0} to Light Radius"),
        ["DamageToMana"] = ("Damage to mana", "{0}% Damage Taken Goes To Mana"),
        ["ManaAfterEachKill"] = ("Mana per kill", "+{0} to Mana after each Kill"),
        ["Cast Frames"] = ("Cast frames", "{0} cast frames"),
        ["Hit Recovery Frames"] = ("Hit recovery frames", "{0} hit recovery frames"),
        ["Attack Frames"] = ("Attack frames", "{0} attack frames"),
        ["Block %"] = ("Block", "{0}% chance to block"),
        ["Armor Defense"] = ("Defense", "{0} defense"),
        ["MinimumFireDamage"] = ("Min fire damage", "+{0} to Minimum Fire Damage"),
        ["MaximumFireDamage"] = ("Max fire damage", "+{0} to Maximum Fire Damage"),
        ["MinimumLightningDamage"] = ("Min lightning damage", "+{0} to Minimum Lightning Damage"),
        ["MaximumLightningDamage"] = ("Max lightning damage", "+{0} to Maximum Lightning Damage"),
        ["MinimumColdDamage"] = ("Min cold damage", "+{0} to Minimum Cold Damage"),
        ["MaximumColdDamage"] = ("Max cold damage", "+{0} to Maximum Cold Damage"),
        ["SecondaryMinimumDamage"] = ("Min damage", "+{0} to Minimum Damage"),
        ["SecondaryMaximumDamage"] = ("Max damage", "+{0} to Maximum Damage"),
        ["MinimumThrowingDamage"] = ("Min damage", "+{0} to Minimum Damage"),
        ["MaximumThrowingDamage"] = ("Max damage", "+{0} to Maximum Damage"),
        ["ClassSkills"] = ("Class skills", "+{0} to class skills"),
    };

    private static readonly string[] Classes = ["Amazon", "Sorceress", "Necromancer", "Paladin", "Barbarian", "Druid", "Assassin"];

    /// <summary>A short column name: <c>FasterCastRate</c> is "FCR".</summary>
    public static string Short(string stat)
    {
        if (Known.TryGetValue(stat, out var known))
        {
            return known.Short;
        }

        foreach (var characterClass in Classes)
        {
            if (stat == characterClass + "Skills")
            {
                return characterClass + " skills";
            }
        }

        return Humanize(stat);
    }

    public static string Humanize(string stat)
    {
        var words = Words().Replace(stat, " $1").Trim();
        return words.Length == 0 ? stat : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex Words();
}
