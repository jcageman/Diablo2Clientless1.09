using D2NG.MuleManager.Contracts;
using System.Globalization;

namespace D2NG.MuleManager.UI.Services;

public static class Format
{
    public static readonly string[] Slots = ["head", "amulet", "body", "right hand", "left hand", "right ring", "left ring", "belt", "boots", "gloves"];

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Title(string slot) => string.Join(' ', slot.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    public static string Number(double value) => value.ToString("#,0.#", Culture);

    private static readonly (string Group, string[] Stats)[] Groups =
    [
        ("Resists", ["FireResistance", "ColdResistance", "LightningResistance", "PoisonResistance", "MaximumFireResistance", "MaximumColdResistance", "MaximumLightningResistance", "MaximumPoisonResistance"]),
        ("Life and mana", ["Life", "Mana", "EnhancedLife", "EnhancedMana", "MinimumLifeStolenPerHit", "MinimumManaStolenPerHit", "ReplenishLife", "ManaRecoveryBonus"]),
        ("Attributes", ["Strength", "Dexterity", "Vitality", "Energy"]),
        ("Speed", ["FasterCastRate", "FasterHitRecovery", "FasterRunWalk", "IncreasedAttackSpeed", "FasterBlockRate"]),
        ("Damage", ["EnhancedMaximumDamage", "MinimumDamage", "MaximumDamage", "AttackRating", "CrushingBlow", "DeadlyStrike", "OpenWounds"]),
        ("Defense", ["Defense", "EnhancedDefense", "DamageReduction", "MagicalDamageReduction", "IncreasedBlocking"]),
        ("Find", ["BetterChanceOfGettingMagicItem", "ExtraGold"]),
    ];

    /// <summary>The stats an item list carries, grouped the way a player looks for them: resists first, everything unusual last.</summary>
    public static List<(string Group, List<string> Stats)> GroupStats(IEnumerable<string> stats)
    {
        var present = stats.ToHashSet();
        var grouped = Groups.Select(g => (g.Group, g.Stats.Where(present.Contains).ToList())).ToList();
        var skills = present.Where(s => s.EndsWith("Skills", StringComparison.Ordinal) || s.StartsWith("SkillTab", StringComparison.Ordinal)).OrderBy(s => s == "AllSkills" ? 0 : 1).ThenBy(s => s).ToList();
        grouped.Insert(3, ("Skills", skills));
        var placed = grouped.SelectMany(g => g.Item2).ToHashSet();
        grouped.Add(("Other", [.. present.Where(s => !placed.Contains(s)).OrderBy(D2NG.Mule.Upgrades.StatText.Short)]));
        return [.. grouped.Where(g => g.Item2.Count > 0)];
    }

    public static string Socket(int sockets) => sockets == 1 ? "1 socket" : $"{sockets} sockets";

    public static string Signed(double value) => value > 0 ? "+" + Number(value) : value < 0 ? "−" + Number(-value) : "·";

    public static string DeltaClass(double value, bool lowerIsBetter = false)
    {
        var better = lowerIsBetter ? value < 0 : value > 0;
        return value == 0 ? "same" : better ? "gain" : "loss";
    }

    public static string Ago(DateTimeOffset? when)
    {
        if (when == null)
        {
            return "never";
        }

        var span = DateTimeOffset.UtcNow - when.Value;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 60 ? $"{(int)span.TotalDays} days ago"
            : $"{(int)(span.TotalDays / 30)} months ago";
    }

    public static bool IsStale(DateTimeOffset? when, double days) => when == null || DateTimeOffset.UtcNow - when.Value > TimeSpan.FromDays(days);

    public static string Where(ItemDto item)
    {
        var place = item.Container switch
        {
            "Stash" or "Stash2" => "stash",
            "Inventory" => "inventory",
            "Cube" => "cube",
            _ => null,
        };
        return place == null ? $"{item.Account} / {item.Character}" : $"{item.Account} / {item.Character} · {place}";
    }

    /// <summary>A whole number with a true minus sign.</summary>
    public static string Whole(int value) => value < 0 ? "−" + (-value).ToString(Culture) : value.ToString(Culture);

    /// <summary>A unique or set item from before level requirements: the realm still carries these, and they need no level.</summary>
    public static bool IsPre(ItemDto item) => item.RequiredLevel == 0 && item.Quality is "Unique" or "Set";

    /// <summary>An item named by its first stat line, for charms that all share one name.</summary>
    public static string Brief(ItemDto item) => item.Stats.Count == 0 ? item.Name : $"{item.Name.ToLowerInvariant()} ({item.Stats[0].Text})";

    public static string Mode(GameMode mode) => mode == GameMode.Expansion ? "expansion" : "classic";

    public static string Quality(string quality) => quality == "Magical" ? "Magic" : quality;

    /// <summary>
    /// Where a roll sits in its range, 0 to 100, best to the right; above the range reads as the far end. A range
    /// wholly below zero, such as reduced requirements, is best at its most negative.
    /// </summary>
    public static string RangePosition(StatLineDto line)
    {
        if (line.Min == null || line.Max == null)
        {
            return null;
        }

        var span = line.Max.Value - line.Min.Value;
        var gained = line.Max.Value <= 0 ? line.Max.Value - line.Value : line.Value - line.Min.Value;
        var at = span <= 0 ? 100 : Math.Clamp(gained * 100 / span, 0, 100);
        return at.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }
}
