using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>The game's rules the gear score rests on, apart from any game.</summary>
public static class GearRules
{
    /// <summary>
    /// Classic takes 20 and 50 off, the expansion 40 and 100. On the expansion's figures the classic party stood 30 to 120
    /// over the cap in Hell while the score still counted every point of resistance in full, and the amazon wore a Full
    /// Plate Mail of resistances and no attack speed.
    /// </summary>
    public static int ResistPenalty(Difficulty difficulty, bool expansion) => difficulty switch
    {
        Difficulty.Nightmare => expansion ? 40 : 20,
        Difficulty.Hell => expansion ? 100 : 50,
        _ => 0
    };

    /// <summary>
    /// The barbarian's Natural Resistance, on its curve from skills.bin: min 0, max 80, 110 x level / (level + 6). Left out,
    /// level 5 hid 40 to every resistance and the barbarian kept choosing resistances over strength and leech.
    /// </summary>
    public static int NaturalResistance(int level) => level > 0 ? 110 * level / (level + 6) * 80 / 100 : 0;

    /// <summary>Increased attack speed after the diminishing returns of the 1.10 formula, 120 x IAS / (120 + IAS); not yet checked on 1.09.</summary>
    public static double EffectiveAttackSpeed(int increased) => increased <= 0 ? 0 : 120.0 * increased / (120 + increased);

    /// <summary>The percent more attacks <paramref name="add"/> attack speed gives on top of <paramref name="have"/>.</summary>
    public static double AttackRateGain(int have, int add)
    {
        var before = EffectiveAttackSpeed(have);
        return (EffectiveAttackSpeed(have + add) - before) / (100 + before) * 100;
    }

    /// <summary>
    /// The set properties that switch on when <paramref name="piece"/> joins the other pieces of its set in
    /// <paramref name="held"/>, one of each: its own set properties for each other piece, the next property of every piece
    /// already held, the set's bonus for the new count and its full bonus once complete. <paramref name="later"/> counts one
    /// more piece when none is held, as if it turns up.
    /// </summary>
    public static List<PropertyRange> SetPropertiesAdded(SetItemEntry piece, IReadOnlyList<SetItemEntry> held, SetEntry set, bool later = false)
    {
        var count = later ? Math.Max(held.Count + 1, 2) : held.Count + 1;
        if (count < 2)
        {
            return [];
        }

        return piece.SetProperties.Take(count - 1)
            .Concat(held.SelectMany(other => other.SetProperties.Skip(held.Count - 1).Take(1)))
            .Concat(set.Partial.GetValueOrDefault(count) ?? [])
            .Concat(count >= set.ItemCount ? set.Full : [])
            .ToList();
    }

    /// <summary>
    /// What a set property adds at a character level. A per level one ("att/lvl", "ac/lvl", "hp/lvl") carries it in its
    /// parameter, attack rating in halves and the rest in eighths, as the item packets do: Sigon's Visor's 16 is 8 attack
    /// rating a level, 656 at level 82, and was not counted at all.
    /// </summary>
    public static int PropertyAmount(PropertyRange property, int level)
        => !IsPerLevel(property.Property) ? property.Max : property.Param * level / (BaseProperty(property.Property) == "att" ? 2 : 8);

    /// <summary>The property a per level one raises: "ac" for "ac/lvl".</summary>
    public static string BaseProperty(string code) => IsPerLevel(code) ? code[..^"/lvl".Length] : code;

    private static bool IsPerLevel(string code) => code.EndsWith("/lvl", StringComparison.Ordinal);

    /// <summary>The set properties switched on among <paramref name="pieces"/> of one set worn together, one of each.</summary>
    public static IEnumerable<PropertyRange> ActiveSetProperties(IReadOnlyList<SetItemEntry> pieces, SetEntry set)
    {
        if (pieces.Count < 2)
        {
            return [];
        }

        return pieces.SelectMany(piece => piece.SetProperties.Take(pieces.Count - 1))
            .Concat(set.Partial.Where(pair => pair.Key <= pieces.Count).SelectMany(pair => pair.Value))
            .Concat(pieces.Count >= set.ItemCount ? set.Full : []);
    }

    /// <summary>
    /// Which of its three columns a gem takes its properties from in an item of this kind, or null for a kind that takes no
    /// socket (MaxSock 0 in itemtypes): belts, gloves, boots, jewellery, javelins and throwing weapons, where a Standard of
    /// Heroes would be spent for nothing.
    /// </summary>
    public static SocketKind? SocketKindOf(ClassificationType classification) => classification switch
    {
        ClassificationType.Shield or ClassificationType.PaladinShield or ClassificationType.NecromancerShrunkenHead => SocketKind.Shield,
        ClassificationType.Helm or ClassificationType.Armor or ClassificationType.Circlet or ClassificationType.BarbarianHelm
            or ClassificationType.DruidPelt => SocketKind.Armor,
        ClassificationType.Axe or ClassificationType.Club or ClassificationType.Dagger or ClassificationType.Hammer or ClassificationType.Mace
            or ClassificationType.Polearm or ClassificationType.Scepter or ClassificationType.Spear or ClassificationType.Staff
            or ClassificationType.Sword or ClassificationType.Wand or ClassificationType.Bow or ClassificationType.AmazonBow
            or ClassificationType.Crossbow or ClassificationType.AmazonSpear or ClassificationType.SorceressOrb
            or ClassificationType.AssassinKatar => SocketKind.Weapon,
        _ => null
    };

    /// <summary>A gem's properties in an item of <paramref name="kind"/>; none for anything but a gem.</summary>
    public static IReadOnlyList<PropertyRange> GemProperties(ItemName gem, SocketKind kind) => Gems.GetValueOrDefault((gem, kind)) ?? [];

    /// <summary>
    /// Every gem's properties per column, chipped to perfect, from gems.txt (the patch's gems.bin was not decoded; the five
    /// diamond grades in a shield match what the party's diamond shield showed). Left out: poison damage, which comes in
    /// 256ths a frame, and cold duration; no score reads them.
    /// </summary>
    private static readonly Dictionary<(ItemName Gem, SocketKind Kind), PropertyRange[]> Gems = BuildGems();

    private static Dictionary<(ItemName, SocketKind), PropertyRange[]> BuildGems()
    {
        var table = new Dictionary<(ItemName, SocketKind), PropertyRange[]>();
        void Add(ItemName[] grades, SocketKind kind, string code, params int[] byGrade)
        {
            for (var grade = 0; grade < grades.Length; grade++)
            {
                table[(grades[grade], kind)] = [.. table.GetValueOrDefault((grades[grade], kind)) ?? [],
                    new PropertyRange { Property = code, Min = byGrade[grade], Max = byGrade[grade] }];
            }
        }

        ItemName[] amethyst = [ItemName.ChippedAmethyst, ItemName.FlawedAmethyst, ItemName.Amethyst, ItemName.FlawlessAmethyst, ItemName.PerfectAmethyst];
        Add(amethyst, SocketKind.Weapon, "att", 40, 60, 80, 100, 150);
        Add(amethyst, SocketKind.Armor, "str", 3, 4, 6, 8, 10);
        Add(amethyst, SocketKind.Shield, "ac", 8, 12, 18, 24, 30);

        ItemName[] sapphire = [ItemName.ChippedSapphire, ItemName.FlawedSapphire, ItemName.Sapphire, ItemName.FlawlessSapphire, ItemName.PerfectSapphire];
        Add(sapphire, SocketKind.Weapon, "cold-min", 1, 3, 4, 6, 10);
        Add(sapphire, SocketKind.Weapon, "cold-max", 3, 5, 7, 10, 14);
        Add(sapphire, SocketKind.Armor, "mana", 10, 17, 24, 31, 38);
        Add(sapphire, SocketKind.Shield, "res-cold", 12, 16, 22, 28, 40);

        ItemName[] emerald = [ItemName.ChippedEmerald, ItemName.FlawedEmerald, ItemName.Emerald, ItemName.FlawlessEmerald, ItemName.PerfectEmerald];
        Add(emerald, SocketKind.Armor, "dex", 3, 4, 6, 8, 10);
        Add(emerald, SocketKind.Shield, "res-pois", 12, 16, 22, 28, 40);

        ItemName[] ruby = [ItemName.ChippedRuby, ItemName.FlawedRuby, ItemName.Ruby, ItemName.FlawlessRuby, ItemName.PerfectRuby];
        Add(ruby, SocketKind.Weapon, "fire-min", 3, 5, 8, 10, 15);
        Add(ruby, SocketKind.Weapon, "fire-max", 4, 8, 12, 16, 20);
        Add(ruby, SocketKind.Armor, "hp", 10, 17, 24, 31, 38);
        Add(ruby, SocketKind.Shield, "res-fire", 12, 16, 22, 28, 40);

        ItemName[] diamond = [ItemName.ChippedDiamond, ItemName.FlawedDiamond, ItemName.Diamond, ItemName.FlawlessDiamond, ItemName.PerfectDiamond];
        Add(diamond, SocketKind.Weapon, "dmg-undead", 28, 34, 44, 54, 68);
        Add(diamond, SocketKind.Armor, "att", 20, 40, 60, 80, 100);
        Add(diamond, SocketKind.Shield, "res-all", 6, 8, 11, 14, 19);

        ItemName[] topaz = [ItemName.ChippedTopaz, ItemName.FlawedTopaz, ItemName.Topaz, ItemName.FlawlessTopaz, ItemName.PerfectTopaz];
        Add(topaz, SocketKind.Weapon, "ltng-min", 1, 1, 1, 1, 1);
        Add(topaz, SocketKind.Weapon, "ltng-max", 8, 14, 22, 30, 40);
        Add(topaz, SocketKind.Armor, "mag%", 9, 13, 16, 20, 24);
        Add(topaz, SocketKind.Shield, "res-ltng", 12, 16, 22, 28, 40);

        ItemName[] skull = [ItemName.ChippedSkull, ItemName.FlawedSkull, ItemName.Skull, ItemName.FlawlessSkull, ItemName.PerfectSkull];
        Add(skull, SocketKind.Weapon, "manasteal", 1, 2, 2, 3, 3);
        Add(skull, SocketKind.Weapon, "lifesteal", 2, 2, 3, 3, 4);
        Add(skull, SocketKind.Armor, "regen", 2, 3, 3, 4, 5);
        Add(skull, SocketKind.Armor, "regen-mana", 8, 8, 12, 12, 19);
        Add(skull, SocketKind.Shield, "thorns", 4, 8, 12, 16, 20);
        return table;
    }
}

/// <summary>The gem table's three columns: weapons, helms and body armor, shields.</summary>
public enum SocketKind
{
    Weapon,
    Armor,
    Shield
}
