using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Upgrades;

/// <summary>
/// The presets the mule manager starts with, taken from what the bots' and the PvP characters'
/// gear is built around. A configuration that lists presets replaces these.
/// </summary>
public static class UpgradePresets
{
    private static StatWeight W(string stat, double weight, double? cap = null, string slot = null)
        => new() { Stat = stat, Weight = weight, Cap = cap, Slot = slot };

    private static Dictionary<string, double> Resists(double fire, double cold, double lightning, double poison)
        => new() { ["Fire"] = fire, ["Cold"] = cold, ["Lightning"] = lightning, ["Poison"] = poison };

    public static IReadOnlyList<UpgradePreset> Defaults { get; } =
    [
        new()
        {
            Id = "sorceress-pvp", Name = "Sorceress · PvP", DefaultFor = ["Sorceress"], Mode = ResistMode.PvP,
            Description = "Cast and hit recovery breakpoints, resists at the cap with cold over, life, +skills and block.",
            Weights = [W("Cast Frames", -30), W("Hit Recovery Frames", -8), W("Life", 1), W("Mana", 0.3), W("AllSkills", 35), W("ClassSkills", 35), W("Block %", 1, 75), W("SorceressColdSpells", 20), W("SorceressColdSpells", 60, slot: GearSlots.Charms)],
            ResistWeights = Resists(1, 1, 1, 0),
        },
        new()
        {
            Id = "static-sorceress", Name = "Static sorceress · Chaos", Mode = ResistMode.PvM,
            Description = "Cast and hit recovery breakpoints, life, +skills, and strength for heavy armor.",
            Weights = [W("Cast Frames", -30), W("Hit Recovery Frames", -8), W("Life", 1), W("Mana", 0.3), W("AllSkills", 25), W("ClassSkills", 25), W("SorceressLightningSpells", 15), W("SorceressLightningSpells", 60, slot: GearSlots.Charms), W("FasterRunWalk", 0.3, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0.3),
        },
        new()
        {
            Id = "barbarian-pvp", Name = "Vitality barbarian · PvP", DefaultFor = ["Barbarian"], Mode = ResistMode.PvP,
            Description = "Life, hit recovery, life leech, attack rating and +skills; resists at the cap with cold over.",
            Weights = [W("Life", 1), W("Hit Recovery Frames", -8), W("MinimumLifeStolenPerHit", 12, 50), W("AttackRating", 0.1), W("ClassSkills", 10), W("AllSkills", 10), W("EnhancedMaximumDamage", 0.3, slot: GearSlots.RightHand), W("IncreasedAttackSpeed", 0.5), W("FasterRunWalk", 3, 30, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0),
        },
        new()
        {
            Id = "gold-barbarian", Name = "Gold barbarian · Travincal", Mode = ResistMode.PvM,
            Description = "Extra gold first, fire and lightning resist, life leech; magic find a bonus, run/walk on boots.",
            Weights = [W("ExtraGold", 1), W("ExtraGold", 4, slot: GearSlots.Charms), W("MinimumLifeStolenPerHit", 12, 50), W("BetterChanceOfGettingMagicItem", 0.3), W("FasterRunWalk", 1, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 0, 1, 0),
        },
        new()
        {
            Id = "bow-amazon", Name = "Bow amazon", DefaultFor = ["Amazon"], Mode = ResistMode.PvP, Weapons = ["Bow", "AmazonBow"],
            Description = "Attack speed breakpoints first, then dexterity and minimum damage, life and mana leech, attack rating, attack speed, a little +skills; resists at the cap.",
            Weights = [W("Attack Frames", -40), W("Dexterity", 3), W("MinimumDamage", 4), W("MaximumDamage", 1.5), W("MinimumLifeStolenPerHit", 12, 50), W("MinimumManaStolenPerHit", 3, 15), W("AttackRating", 0.15), W("ClassSkills", 12), W("AllSkills", 12), W("AmazonBowAndCrossbowSkills", 10), W("Life", 0.5), W("Hit Recovery Frames", -5), W("EnhancedMaximumDamage", 3, slot: GearSlots.RightHand), W("FasterRunWalk", 3, 30, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0),
        },
        new()
        {
            Id = "javelin-amazon", Name = "Javelin amazon", DefaultFor = ["Amazon"], Mode = ResistMode.PvP, Weapons = ["Javelin", "AmazonJavelin"],
            Description = "Javelin and spear skills with the lightning javelin skills and absorb, life, hit recovery breakpoints, block, life leech and attack speed; resists at the cap.",
            Weights = [W("AllSkills", 30), W("ClassSkills", 30), W("AmazonJavelinAndSpearSkills", 30), W("LightningFury", 20), W("ChargedStrike", 12), W("LightningStrike", 12), W("LightningAbsorption", 1.5), W("Life", 1), W("Hit Recovery Frames", -8), W("Block %", 1, 75), W("MinimumLifeStolenPerHit", 12, 50), W("IncreasedAttackSpeed", 2.5), W("AttackRating", 0.1), W("EnhancedMaximumDamage", 0.5, slot: GearSlots.RightHand), W("FasterRunWalk", 3, 30, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0),
        },
        new()
        {
            Id = "hammerdin", Name = "Hammerdin", DefaultFor = ["Paladin"], Mode = ResistMode.PvP, Weapons = ["Scepter"],
            Description = "+Skills with Blessed Hammer and Concentration, cast and hit recovery breakpoints, life, defense on armor and block; resists at the cap with cold over.",
            Weights = [W("AllSkills", 35), W("ClassSkills", 35), W("PaladinCombatSkills", 25), W("PaladinCombatSkills", 60, slot: GearSlots.Charms), W("BlessedHammer", 25), W("Concentration", 6), W("Vigor", 1), W("Cast Frames", -40), W("Hit Recovery Frames", -8), W("Life", 1), W("Mana", 0.3), W("Armor Defense", 0.08), W("Block %", 1, 75), W("FasterRunWalk", 3, 30, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0),
        },
        new()
        {
            Id = "caster-pvp", Name = "Caster · PvP", DefaultFor = ["Necromancer", "Druid", "Assassin"], Mode = ResistMode.PvP,
            Description = "+Skills, life, cast and hit recovery breakpoints and block; resists at the cap with cold over.",
            Weights = [W("AllSkills", 35), W("ClassSkills", 35), W("NecromancerPoisonAndBoneSpells", 20), W("DruidElemental", 20), W("AssasinTraps", 20), W("NecromancerPoisonAndBoneSpells", 60, slot: GearSlots.Charms), W("DruidElemental", 60, slot: GearSlots.Charms), W("AssasinTraps", 60, slot: GearSlots.Charms), W("Life", 1), W("Mana", 0.3), W("Cast Frames", -30), W("Hit Recovery Frames", -8), W("Block %", 1, 75), W("FasterRunWalk", 3, 30, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0),
        },
        new()
        {
            Id = "magic-find", Name = "Magic find", Mode = ResistMode.PvM,
            Description = "Magic find first, then +skills and life; resists count up to the cap.",
            Weights = [W("BetterChanceOfGettingMagicItem", 1), W("AllSkills", 10), W("ClassSkills", 10), W("Life", 0.5), W("ExtraGold", 0.1), W("FasterRunWalk", 0.5, slot: GearSlots.Boots)],
            ResistWeights = Resists(1, 1, 1, 0.3),
        },
    ];

    /// <summary>
    /// The preset a character starts on: the first that names its class and takes the weapon it wields,
    /// then the first that names its class, else the first.
    /// </summary>
    public static UpgradePreset DefaultFor(IReadOnlyList<UpgradePreset> presets, string characterClass, string wielded = null)
        => presets.FirstOrDefault(p => p.DefaultFor.Contains(characterClass) && (p.Weapons.Count == 0 || wielded == null || p.Weapons.Contains(wielded)))
            ?? presets.FirstOrDefault(p => p.DefaultFor.Contains(characterClass))
            ?? (presets.Count > 0 ? presets[0] : null);
}
