using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;
using D2NG.Mule.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Upgrades;

public enum Difficulty
{
    Normal,
    Nightmare,
    Hell,
}

/// <summary>One item as the evaluation adds it up: its stats with per level stats at the character's level.</summary>
public sealed class GearItem
{
    public MuleItemDb Source { get; init; }
    public BaseItemEntry Base { get; init; }
    public IReadOnlyDictionary<string, double> Stats { get; init; }
    public bool Ethereal => Source.Ethereal;

    public static readonly string[] ClassSkills =
        ["AmazonSkills", "SorceressSkills", "NecromancerSkills", "PaladinSkills", "BarbarianSkills", "DruidSkills", "AssassinSkills"];

    public static GearItem From(MuleItemDb item, GearTables tables, int characterLevel)
    {
        var stats = new Dictionary<string, double>();
        foreach (var stat in item.Stats ?? [])
        {
            if (stat.PerLevel != 0 && stat.Type.EndsWith("PerLevel", StringComparison.Ordinal))
            {
                Add(stats, stat.Type[..^"PerLevel".Length], stat.PerLevel * characterLevel / 8);
            }
            else
            {
                Add(stats, stat.Type, stat.Value);
            }
        }

        // The packet repeats an item's damage as plain, throwing and two-handed; on a weapon one of
        // them holds the real value, elsewhere all hold the same one.
        foreach (var bound in new[] { "Minimum", "Maximum" })
        {
            var variants = new[] { $"{bound}Damage", $"{bound}ThrowingDamage", $"Secondary{bound}Damage" }
                .Select(key => stats.Remove(key, out var value) ? value : 0).ToList();
            if (variants.Any(v => v != 0))
            {
                stats[$"{bound}Damage"] = variants.Max();
            }
        }

        if (stats.ContainsKey("AllSkills"))
        {
            foreach (var classSkills in ClassSkills)
            {
                stats.Remove(classSkills);
            }
        }

        return new GearItem { Source = item, Base = tables?.Base(item), Stats = stats };
    }

    private static void Add(Dictionary<string, double> stats, string key, double amount)
        => stats[key] = stats.GetValueOrDefault(key) + amount;
}

/// <summary>A resist after the difficulty penalty: what the stats add up to, what counts, and the cap.</summary>
/// <summary>One set the character wears pieces of: how many, of how many, and what the set adds for that.</summary>
public sealed record SetBonus(string Name, int Worn, int Total, List<(string Stat, double Value)> Stats);

public readonly record struct ResistLine(string Element, int Raw, int Effective, int Cap)
{
    public int OverCap => Math.Max(0, Raw - Cap);
}

/// <summary>
/// Everything one set of gear adds up to for a character: totals, resists after the difficulty
/// penalty, cast and hit recovery frames, block, and whether every item can be worn.
/// </summary>
public sealed class GearEvaluation
{
    public static readonly string[] Elements = ["Fire", "Cold", "Lightning", "Poison"];
    public const int ResistCap = 75;

    public IReadOnlyDictionary<string, double> Totals { get; private init; }
    public IReadOnlyDictionary<string, ResistLine> Resists { get; private init; }

    public IReadOnlyList<SetBonus> Sets { get; private init; } = [];

    /// <summary>Why the gear cannot be worn as it stands; null when it can.</summary>
    public string RequirementFailure { get; private init; }

    public double Total(string key) => Totals.GetValueOrDefault(key);

    public static GearEvaluation Of(GearCharacterDb character, IReadOnlyDictionary<string, GearItem> slots, IEnumerable<GearItem> charms, GearTables tables, Difficulty difficulty)
    {
        var totals = new Dictionary<string, double>
        {
            ["Strength"] = character.Strength,
            ["Dexterity"] = character.Dexterity,
            ["Vitality"] = character.Vitality,
            ["Energy"] = character.Energy,
        };
        foreach (var (stat, amount) in character.BaseStats ?? [])
        {
            totals[stat] = totals.GetValueOrDefault(stat) + amount;
        }

        var worn = slots.Values.Concat(charms ?? []).ToList();
        foreach (var item in worn)
        {
            foreach (var (stat, amount) in item.Stats)
            {
                if (stat is "Life" or "Mana")
                {
                    totals["Item" + stat] = totals.GetValueOrDefault("Item" + stat) + amount;
                }
                else
                {
                    totals[stat] = totals.GetValueOrDefault(stat) + amount;
                }
            }
        }

        var sets = tables?.SetBonuses(slots.Values) ?? [];
        foreach (var (stat, amount) in sets.SelectMany(s => s.Stats))
        {
            var key = stat is "Life" or "Mana" ? "Item" + stat : stat;
            totals[key] = totals.GetValueOrDefault(key) + amount;
        }

        var (lifePerVitality, manaPerEnergy) = ClassTables.Conversion(character.Class);
        var itemVitality = totals["Vitality"] - character.Vitality;
        var itemEnergy = totals["Energy"] - character.Energy;
        totals["Life"] = Math.Floor((character.BaseLife + totals.GetValueOrDefault("ItemLife") + itemVitality * lifePerVitality) * (1 + totals.GetValueOrDefault("EnhancedLife") / 100));
        totals["Mana"] = Math.Floor((character.BaseMana + totals.GetValueOrDefault("ItemMana") + itemEnergy * manaPerEnergy) * (1 + totals.GetValueOrDefault("EnhancedMana") / 100));
        totals.Remove("ItemLife");
        totals.Remove("ItemMana");

        if (ClassTables.CastBreakpoints.TryGetValue(character.Class ?? "", out var cast))
        {
            totals["Cast Frames"] = Frames(totals.GetValueOrDefault("FasterCastRate"), cast);
            totals["Hit Recovery Frames"] = Frames(totals.GetValueOrDefault("FasterHitRecovery"), ClassTables.HitRecoveryBreakpoints[character.Class]);
        }

        if (slots.TryGetValue(GearSlots.RightHand, out var weapon) && AttackAnimation(character.Class, weapon) is { } animation)
        {
            totals["Attack Frames"] = AttackFrames(animation, weapon.Base.Speed, totals.GetValueOrDefault("IncreasedAttackSpeed"));
        }

        foreach (var tab in Enum.GetNames<SkillTab>().Where(t => totals.ContainsKey(t) && !t.StartsWith(ClassTables.TabPrefix(character.Class), StringComparison.Ordinal)))
        {
            totals.Remove(tab);
        }

        foreach (var (stat, amount) in (charms ?? []).SelectMany(c => c.Stats))
        {
            totals[CharmsPrefix + stat] = totals.GetValueOrDefault(CharmsPrefix + stat) + amount;
        }

        totals["Block %"] = Block(character, slots, totals);
        totals["Armor Defense"] = slots.Values.Sum(ArmorDefense);
        if (character.Class != null)
        {
            totals["ClassSkills"] = totals.GetValueOrDefault(character.Class + "Skills");
        }

        var resists = new Dictionary<string, ResistLine>();
        var penalty = Penalty(character.Expansion, difficulty);
        foreach (var element in Elements)
        {
            var raw = (int)totals.GetValueOrDefault(element + "Resistance") + penalty;
            var cap = ResistCap + (int)totals.GetValueOrDefault("Maximum" + element + "Resistance");
            resists[element] = new ResistLine(element, raw, Math.Min(raw, cap), cap);
        }

        return new GearEvaluation
        {
            Totals = totals,
            Resists = resists,
            Sets = sets,
            RequirementFailure = Requirements(character, slots, totals, tables),
        };
    }

    /// <summary>
    /// What a worn armor piece adds to defense. The rolled base defense is not recorded, so a base with
    /// enhanced defense counts at its maximum, which is where the game puts it, and any other at the middle
    /// of its range; ethereal adds half.
    /// </summary>
    public static double ArmorDefense(GearItem item)
    {
        if (item.Base is not { Kind: BaseItemKind.Armor } armor)
        {
            return 0;
        }

        var enhanced = item.Stats.GetValueOrDefault("EnhancedDefense");
        var rolled = enhanced > 0 ? armor.MaxDefense : (armor.MinDefense + armor.MaxDefense) / 2.0;
        if (item.Ethereal)
        {
            rolled = Math.Floor(rolled * 1.5);
        }

        return Math.Floor(rolled * (1 + enhanced / 100)) + item.Stats.GetValueOrDefault("Defense");
    }

    /// <summary>A weapon with only two-handed damage; barbarian swords carry one-handed damage too and leave the other hand free.</summary>
    public static bool TakesBothHands(GearItem item)
        => item.Base is { Kind: BaseItemKind.Weapon } weapon && weapon.TwoHandMinDamage > 0 && weapon.MinDamage == 0;

    /// <summary>Totals under this prefix count the carried charms only, for weights that apply to the charm slot.</summary>
    public const string CharmsPrefix = "Charms ";

    public static int Penalty(bool expansion, Difficulty difficulty) => difficulty switch
    {
        Difficulty.Nightmare => expansion ? -40 : -20,
        Difficulty.Hell => expansion ? -100 : -50,
        _ => 0,
    };

    /// <summary>The amazon's bow and crossbow attack animations, 14 and 20 frames; other weapons and classes are not tabled yet.</summary>
    private static int? AttackAnimation(string characterClass, GearItem weapon) => (characterClass, weapon.Base?.Type) switch
    {
        ("Amazon", "bow" or "abow") => 14,
        ("Amazon", "xbow") => 20,
        _ => null,
    };

    /// <summary>
    /// Frames per attack: item IAS with diminishing returns, less the base's speed modifier, the
    /// effective value held to -85..75, as 1.10's formula has it on the same animation data.
    /// </summary>
    public static int AttackFrames(int animationLength, int weaponSpeed, double increasedAttackSpeed)
    {
        var effective = Math.Clamp(Math.Floor(120 * increasedAttackSpeed / (120 + increasedAttackSpeed)) - weaponSpeed, -85, 75);
        var speed = Math.Floor(256 * (100 + effective) / 100);
        return (int)Math.Ceiling(256 * animationLength / speed) - 1;
    }

    /// <summary>The frames an action takes at this percent: the first frame count less one per breakpoint reached past the first.</summary>
    public static int Frames(double percent, (int First, int[] Breakpoints) table)
        => table.First - (table.Breakpoints.Count(b => percent >= b) - 1);

    /// <summary>
    /// (shield block + increased blocking + class block factor) x (dexterity - 15) / (2 x level),
    /// at most 75. 1.10's formula, not yet checked on a 1.09 screen.
    /// </summary>
    private static double Block(GearCharacterDb character, IReadOnlyDictionary<string, GearItem> slots, Dictionary<string, double> totals)
    {
        var shield = slots.Values.FirstOrDefault(i => i.Base?.Block > 0);
        if (shield == null || character.Level <= 0)
        {
            return 0;
        }

        var block = shield.Base.Block + totals.GetValueOrDefault("IncreasedBlocking") + ClassTables.BlockFactor.GetValueOrDefault(character.Class ?? "");
        return Math.Clamp(Math.Floor(block * (totals.GetValueOrDefault("Dexterity") - 15) / (2 * character.Level)), 0, 75);
    }

    /// <summary>
    /// Strength and dexterity against every worn base, after reduced requirements and the ethereal
    /// ten, without the item's own bonus: the game checks an item against what the character has
    /// before it goes on. Then the level a unique or set needs, and a two-handed weapon with the
    /// other hand full.
    /// </summary>
    private static string Requirements(GearCharacterDb character, IReadOnlyDictionary<string, GearItem> slots, Dictionary<string, double> totals, GearTables tables)
    {
        if (slots.TryGetValue(GearSlots.RightHand, out var right) && TakesBothHands(right)
            && slots.TryGetValue(GearSlots.LeftHand, out var left) && left.Base?.Type is not ("bowq" or "xboq"))
        {
            return $"{right.Base.Name} takes both hands";
        }

        foreach (var (slot, item) in slots)
        {
            if (item.Base == null)
            {
                continue;
            }

            var reduction = Math.Abs(item.Stats.GetValueOrDefault("ReducedRequirements"));
            foreach (var (stat, required) in new[] { ("Strength", item.Base.RequiredStrength), ("Dexterity", item.Base.RequiredDexterity) })
            {
                var need = (int)(required * (100 - reduction) / 100) - (item.Ethereal ? 10 : 0);
                var has = totals.GetValueOrDefault(stat) - item.Stats.GetValueOrDefault(stat);
                if (need > 0 && has < need)
                {
                    return $"needs {need} {stat.ToLowerInvariant()}, has {has:0}";
                }
            }

            var level = tables?.RequiredLevel(item.Source);
            if (level > character.Level)
            {
                return $"needs level {level}, is {character.Level}";
            }
        }

        return null;
    }
}

/// <summary>Per class numbers from charstats and the published breakpoint tables.</summary>
public static class ClassTables
{
    /// <summary>Frames at 0% and the percent that buys each frame after it. The published 1.10 tables, which share 1.09's animation data.</summary>
    public static readonly Dictionary<string, (int First, int[] Breakpoints)> CastBreakpoints = new()
    {
        ["Amazon"] = (19, [0, 7, 14, 22, 32, 48, 68, 99, 152]),
        ["Sorceress"] = (13, [0, 9, 20, 37, 63, 105, 200]),
        ["Necromancer"] = (15, [0, 9, 18, 30, 48, 75, 125]),
        ["Paladin"] = (15, [0, 9, 18, 30, 48, 75, 125]),
        ["Barbarian"] = (13, [0, 9, 20, 37, 63, 105, 200]),
        ["Druid"] = (18, [0, 4, 10, 19, 30, 46, 68, 99, 163]),
        ["Assassin"] = (16, [0, 8, 16, 27, 42, 65, 102, 174]),
    };

    public static readonly Dictionary<string, (int First, int[] Breakpoints)> HitRecoveryBreakpoints = new()
    {
        ["Amazon"] = (11, [0, 6, 13, 20, 32, 52, 86, 174, 600]),
        ["Sorceress"] = (15, [0, 5, 9, 14, 20, 30, 42, 60, 86, 142, 280]),
        ["Necromancer"] = (13, [0, 5, 10, 16, 26, 39, 56, 86, 152, 377]),
        ["Paladin"] = (9, [0, 7, 15, 27, 48, 86, 200]),
        ["Barbarian"] = (9, [0, 7, 15, 27, 48, 86, 200]),
        ["Druid"] = (13, [0, 5, 10, 16, 26, 39, 56, 86, 152, 377]),
        ["Assassin"] = (9, [0, 7, 15, 27, 48, 86, 200]),
    };

    /// <summary>The prefix of a class's skill tab names; the game spells the assassin's tabs "Assasin".</summary>
    public static string TabPrefix(string characterClass) => characterClass == "Assassin" ? "Assasin" : characterClass ?? "\0";

    public static readonly Dictionary<string, int> BlockFactor = new()
    {
        ["Amazon"] = 25, ["Sorceress"] = 20, ["Necromancer"] = 20, ["Paladin"] = 30, ["Barbarian"] = 25, ["Druid"] = 20, ["Assassin"] = 25,
    };

    /// <summary>Life per point of vitality and mana per point of energy, as <c>Self</c> computes the maximums.</summary>
    public static (double LifePerVitality, double ManaPerEnergy) Conversion(string characterClass) => characterClass switch
    {
        "Amazon" => (3, 1.5),
        "Sorceress" => (2, 2),
        "Necromancer" => (2, 2),
        "Paladin" => (3, 2),
        "Barbarian" => (4, 2),
        "Druid" => (2, 2),
        "Assassin" => (3, 1.75),
        _ => (1, 1),
    };

    /// <summary>Items only one class can use, keyed by classification name.</summary>
    public static readonly Dictionary<string, string> ClassOnly = new()
    {
        ["SorceressOrb"] = "Sorceress", ["AssassinKatar"] = "Assassin", ["AmazonBow"] = "Amazon", ["AmazonJavelin"] = "Amazon",
        ["AmazonSpear"] = "Amazon", ["BarbarianHelm"] = "Barbarian", ["DruidPelt"] = "Druid", ["PaladinShield"] = "Paladin",
        ["NecromancerShrunkenHead"] = "Necromancer",
    };
}
