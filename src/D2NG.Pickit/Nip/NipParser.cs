using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;

namespace D2NG.Pickit.Nip;

public static class NipParser
{
    public static IReadOnlyList<NipRule> ParseDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var rules = new List<NipRule>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.nip", SearchOption.AllDirectories))
        {
            rules.AddRange(ParseFile(file));
        }

        return rules;
    }

    public static IReadOnlyList<NipRule> ParseFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var rules = new List<NipRule>();
        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = StripComments(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var sections = trimmed.Split('#');
            var propertyText = sections.Length > 0 ? sections[0].Trim() : string.Empty;
            var statText = sections.Length > 1 ? sections[1].Trim() : string.Empty;
            var maxText = sections.Length > 2 ? sections[2].Trim() : string.Empty;

            var propertyExpr = string.IsNullOrEmpty(propertyText) ? null : ParseExpression(propertyText, filePath, i + 1);
            var statExpr = string.IsNullOrEmpty(statText) ? null : ParseExpression(statText, filePath, i + 1);
            var maxQuantity = ParseMaxQuantity(maxText);

            if (propertyExpr == null && statExpr == null)
            {
                continue;
            }

            var pickupOnly = string.Equals(Path.GetFileName(filePath), "GoldItems.nip", StringComparison.OrdinalIgnoreCase);
            var typeFilters = ExtractFilters(propertyText, "type");
            var itemTypeFilters = ExtractFilters(propertyText, "itemtype");
            var qualityFilters = ExtractFilters(propertyText, "quality");
            rules.Add(new NipRule(propertyExpr, statExpr, maxQuantity, filePath, i + 1, rawLine, pickupOnly, typeFilters, itemTypeFilters, qualityFilters));
        }

        return rules;
    }

    private static string[] ExtractFilters(string propertyText, string field)
    {
        return Regex.Matches(propertyText, $@"\[{field}\]\s*==\s*([a-z0-9_*]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static NipExpression ParseExpression(string text, string source, int line)
    {
        var tokenizer = new NipTokenizer(text, source, line);
        var tokens = tokenizer.Tokenize();
        var parser = new NipExpressionParser(tokens, source, line);
        return parser.Parse();
    }

    private static int? ParseMaxQuantity(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        if (text.StartsWith("[maxquantity]", StringComparison.OrdinalIgnoreCase))
        {
            var eq = text.IndexOf("==", StringComparison.Ordinal);
            var tail = eq >= 0 ? text[(eq + 2)..].Trim() : text[13..].Trim(' ', '=');
            if (int.TryParse(tail, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
        }

        return null;
    }

    private static string StripComments(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }
}

public sealed record NipRule(
    NipExpression? PropertyCondition,
    NipExpression? StatCondition,
    int? MaxQuantity,
    string Source,
    int Line,
    string Raw,
    bool PickupOnly,
    string[] TypeFilters,
    string[] ItemTypeFilters,
    string[] QualityFilters)
{
    public bool MatchesPickup(NipEvaluationContext context)
    {
        if (PropertyCondition == null)
        {
            return false;
        }

        if (context.Item.Quality == QualityType.Set && QualityFilters.Length == 0)
        {
            return false;
        }

        if (!MatchesFilters(context))
        {
            return false;
        }

        // Before identification, only the cheap item/property side is available.
        if (!PropertyCondition.Evaluate(context))
        {
            return false;
        }

        return !context.Item.IsGold || StatCondition == null || StatCondition.Evaluate(context);
    }

    public bool MatchesKeep(NipEvaluationContext context)
    {
        if (PropertyCondition == null || StatCondition == null || !MatchesFilters(context))
        {
            return false;
        }

        return PropertyCondition.Evaluate(context) && StatCondition.Evaluate(context);
    }

    private bool MatchesFilters(NipEvaluationContext context)
    {
        if (!MatchesFilter(TypeFilters, context.NipType)
            || !MatchesFilter(ItemTypeFilters, context.Item.Type)
            || !MatchesFilter(QualityFilters, context.NipQuality))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesFilter(string[] filters, string? value)
    {
        if (filters.Length == 0)
        {
            return true;
        }

        value ??= string.Empty;

        foreach (var filter in filters)
        {
            if (filter.EndsWith('*'))
            {
                if (value.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(filter, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public readonly struct NipEvaluationContext
{
    private static readonly (string DisplayName, Skill Skill)[] SkillDisplayNames =
    [
        ("poisoncreeper", Skill.PlaguePoppy),
        ("werewolf", Skill.Wearwolf),
        ("lycanthropy", Skill.ShapeShifting),
        ("werebear", Skill.Wearbear),
        ("carrionvine", Skill.CycleOfLife),
        ("fissure", Skill.Eruption),
        ("summondirewolf", Skill.SummonFenris),
        ("direwolf", Skill.SummonFenris),
        ("solarcreeper", Skill.Vines),
        ("fireblast", Skill.FireTrauma),
        ("shockweb", Skill.ShockField),
        ("burstofspeed", Skill.Quickness)
    ];

    private static readonly Dictionary<string, CharacterClass> FixedClassLife =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "amazonlife", CharacterClass.Amazon },
            { "assassinlife", CharacterClass.Assassin },
            { "barbarianlife", CharacterClass.Barbarian },
            { "druidlife", CharacterClass.Druid },
            { "necromancerlife", CharacterClass.Necromancer },
            { "paladinlife", CharacterClass.Paladin },
            { "sorceresslife", CharacterClass.Sorceress },
        };

    private static readonly Dictionary<string, Skill> SkillAliases = BuildSkillAliases();
    private static readonly Dictionary<string, Func<Item, double>> NumericResolvers = BuildNumericResolvers();
    private static readonly Dictionary<string, Func<Item, string>> TextResolvers = BuildTextResolvers();
    private readonly string _nipType;
    private readonly string _nipQuality;

    public NipEvaluationContext(Item item, CharacterClass characterClass)
    {
        Item = item;
        CharacterClass = characterClass;
        _nipType = NipFieldMaps.ClassificationToType(item.Classification);
        _nipQuality = NipFieldMaps.QualityToText(item.Quality);
    }

    public Item Item { get; }
    public CharacterClass CharacterClass { get; }
    public string NipType => _nipType;
    public string NipQuality => _nipQuality;

    public bool TryResolve(string identifier, out NipValue value)
    {
        if (int.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statId)
            && Enum.IsDefined(typeof(StatType), statId))
        {
            value = NipValue.FromNumber(Item.GetValueOfStatType((StatType)statId));
            return true;
        }

        if (NumericResolvers.TryGetValue(identifier, out var number))
        {
            value = NipValue.FromNumber(number(Item));
            return true;
        }

        if (FixedClassLife.TryGetValue(identifier, out var lifeClass))
        {
            value = NipValue.FromNumber(Item.GetTotalLifeFromStats(lifeClass));
            return true;
        }

        if (TextResolvers.TryGetValue(identifier, out var text))
        {
            value = NipValue.FromText(text(Item));
            return true;
        }

        if (TryResolveSkillAlias(identifier, out var skillValue))
        {
            value = NipValue.FromNumber(skillValue);
            return true;
        }

        // Treat unknown identifiers as text literals so bare values like 'amulet' can participate in comparisons
        value = NipValue.FromText(identifier);
        return true;
    }

    private bool TryResolveSkillAlias(string identifier, out int value)
    {
        value = 0;
        if (!identifier.StartsWith("skill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryGetSkill(identifier, out var skill))
        {
            value = Item.GetValueToSkill(skill);
            return true;
        }

        return false;
    }

    private static bool TryGetSkill(string identifier, out Skill skill)
    {
        if (SkillAliases.TryGetValue(identifier, out skill))
        {
            return true;
        }

        var normalized = NormalizeSkillKey(identifier);
        return SkillAliases.TryGetValue(normalized, out skill);
    }

    private static Dictionary<string, Skill> BuildSkillAliases()
    {
        var map = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in Enum.GetValues<Skill>())
        {
            var name = skill.ToString();
            var baseKey = "skill" + name.ToLowerInvariant();
            map[baseKey] = skill;

            var normalized = "skill" + NormalizeSkillKey(name);
            map[normalized] = skill;
        }

        foreach (var (displayName, skill) in SkillDisplayNames)
        {
            map["skill" + NormalizeSkillKey(displayName)] = skill;
        }

        return map;
    }

    private static string NormalizeSkillKey(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, Func<Item, double>> BuildNumericResolvers()
    {
        return new Dictionary<string, Func<Item, double>>(StringComparer.OrdinalIgnoreCase)
        {
            { "allskills", item => item.GetValueOfStatType(StatType.AllSkills) },
            { "amazonskills", item => item.GetValueOfStatType(StatType.AmazonSkills) },
            { "assassinskills", item => item.GetValueOfStatType(StatType.AssassinSkills) },
            { "barbarianskills", item => item.GetValueOfStatType(StatType.BarbarianSkills) },
            { "druidskills", item => item.GetValueOfStatType(StatType.DruidSkills) },
            { "necromancerskills", item => item.GetValueOfStatType(StatType.NecromancerSkills) },
            { "paladinskills", item => item.GetValueOfStatType(StatType.PaladinSkills) },
            { "sorceressskills", item => item.GetValueOfStatType(StatType.SorceressSkills) },
            { "strength", item => item.GetValueOfStatType(StatType.Strength) },
            { "dexterity", item => item.GetValueOfStatType(StatType.Dexterity) },
            { "vitality", item => item.GetValueOfStatType(StatType.Vitality) },
            { "energy", item => item.GetValueOfStatType(StatType.Energy) },
            { "defense", item => item.Defense },
            { "maxhp", item => item.GetValueOfStatType(StatType.Life) },
            { "cannotbefrozen", item => item.GetValueOfStatType(StatType.CannotBeFrozen) },
            { "damagetomana", item => item.GetValueOfStatType(StatType.DamageToMana) },
            { "maxmana", item => item.GetValueOfStatType(StatType.Mana) },
            { "hpregen", item => item.GetValueOfStatType(StatType.ReplenishLife) },
            { "lifereplenish", item => item.GetValueOfStatType(StatType.ReplenishLife) },
            { "lifesteal", item => item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) },
            { "manasteal", item => item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) },
            { "lifeleech", item => item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) },
            { "manaleech", item => item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) },
            { "sockets", item => item.Sockets },
            { "level", item => item.Level },
            { "amount", item => item.Amount },
            { "frw", item => item.GetValueOfStatType(StatType.FasterRunWalk) },
            { "fhr", item => item.GetValueOfStatType(StatType.FasterHitRecovery) },
            { "fcr", item => item.GetValueOfStatType(StatType.FasterCastRate) },
            { "fbr", item => item.GetValueOfStatType(StatType.FasterBlockRate) },
            { "increasedblocking", item => item.GetValueOfStatType(StatType.IncreasedBlocking) },
            { "itemfasterblockrate", item => item.GetValueOfStatType(StatType.FasterBlockRate) },
            { "ias", item => item.GetValueOfStatType(StatType.IncreasedAttackSpeed) },
            { "ed", item => item.GetValueOfStatType(StatType.EnhancedDamage) + item.GetValueOfStatType(StatType.EnhancedMaximumDamage) + item.GetValueOfStatType(StatType.EnhancedMinimumDamage) },
            { "enhanceddamage", item => item.GetValueOfStatType(StatType.EnhancedDamage) + item.GetValueOfStatType(StatType.EnhancedMaximumDamage) + item.GetValueOfStatType(StatType.EnhancedMinimumDamage) },
            { "enhanceddefense", item => item.GetValueOfStatType(StatType.EnhancedDefense) },
            { "fireresist", item => item.GetValueOfStatType(StatType.FireResistance) },
            { "coldresist", item => item.GetValueOfStatType(StatType.ColdResistance) },
            { "lightresist", item => item.GetValueOfStatType(StatType.LightningResistance) },
            { "poisonresist", item => item.GetValueOfStatType(StatType.PoisonResistance) },
            { "itemgoldbonus", item => item.GetValueOfStatType(StatType.ExtraGold) },
            { "goldfind", item => item.GetValueOfStatType(StatType.ExtraGold) },
            { "magicfind", item => item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) },
            { "itemmagicbonus", item => item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) },
            { "attackrating", item => item.GetValueOfStatType(StatType.AttackRating) },
            { "tohit", item => item.GetValueOfStatType(StatType.ToHitPercent) },
            { "maxhpplusmana", item => item.GetValueOfStatType(StatType.Life) + item.GetValueOfStatType(StatType.Mana) },
            { "resall", item => item.GetTotalResistFrLrCr() + item.GetValueOfStatType(StatType.PoisonResistance) },

            { "crlrfr", item => item.GetTotalResistFrLrCr() },
            { "crfrlr", item => item.GetTotalResistFrLrCr() },
            { "lrcrfr", item => item.GetTotalResistFrLrCr() },
            { "lrfrcr", item => item.GetTotalResistFrLrCr() },
            { "frcrlr", item => item.GetTotalResistFrLrCr() },
            { "frlrcr", item => item.GetTotalResistFrLrCr() },
            { "poisonlength", item => item.GetValueOfStatType(StatType.PoisonDamageLength) },
            { "minpoison", item => item.GetValueOfStatType(StatType.MinimumPoisonDamage) },
            { "poisonmindmg", item => item.GetValueOfStatType(StatType.MinimumPoisonDamage) },
            { "maxpoison", item => item.GetValueOfStatType(StatType.MaximumPoisonDamage) },
            { "poisonmaxdmg", item => item.GetValueOfStatType(StatType.MaximumPoisonDamage) },
            { "minlightdmg", item => item.GetValueOfStatType(StatType.MinimumLightningDamage) },
            { "maxlightdmg", item => item.GetValueOfStatType(StatType.MaximumLightningDamage) },
            { "minfiredmg", item => item.GetValueOfStatType(StatType.MinimumFireDamage) },
            { "maxfiredmg", item => item.GetValueOfStatType(StatType.MaximumFireDamage) },
            { "mincolddmg", item => item.GetValueOfStatType(StatType.MinimumColdDamage) },
            { "maxcolddmg", item => item.GetValueOfStatType(StatType.MaximumColdDamage) },
            { "itempoisonlengthresist", item => item.GetValueOfStatType(StatType.PoisonLengthReduction) },
            { "itemreplenishdurability", item => item.GetValueOfStatType(StatType.RepairsDurability) },
            { "itemreplenishquantity", item => item.GetValueOfStatType(StatType.ReplenishesQuantity) },
            { "itemreqpercent", item => item.GetValueOfStatType(StatType.ReducedRequirements) },
            { "itemreq", item => item.GetValueOfStatType(StatType.ReducedRequirements) },
            { "mindamage", item => item.GetValueOfStatType(StatType.MinimumDamage) },
            { "maxdamage", item => item.GetValueOfStatType(StatType.MaximumDamage) },
            { "knockback", item => item.GetValueOfStatType(StatType.Knockback) },
            { "mindmg", item => item.GetValueOfStatType(StatType.MinimumDamage) },
            { "maxdmg", item => item.GetValueOfStatType(StatType.MaximumDamage) },
            { "itemmaxdamageperlevel", item => item.GetValueOfStatType(StatType.MaximumDamagePerLevel) },
            { "itemtohitperlevel", item => item.GetValueOfStatType(StatType.AttackRatingPerLevel) },
            { "itemtohitpercentperlevel", item => item.GetValueOfStatType(StatType.BonusToAttackRatingPerLevel) },
            { "itemaddskilltab", item => item.TotalToSkillTabs() },
            { "itemaddclassskills", item => item.GetToClassSkills() },
            { "itemskillonhit", item => item.GetValueOfStatType(StatType.SkillOnHit) },
            { "itemskillonattack", item => item.GetValueOfStatType(StatType.SkillOnStriking) },
            { "itemskillondeath", item => item.GetValueOfStatType(StatType.SkillOnDeath) },
            { "itemskillonkill", item => item.GetValueOfStatType(StatType.SkillOnKill) },
            { "itemskillonlevelup", item => item.GetValueOfStatType(StatType.SkillOnLevelUp) },
            { "itemskillongethit", item => item.GetValueOfStatType(StatType.SkillWhenStruck1) },
            { "prefix", item => item.Prefix },
            { "suffix", item => item.Suffix },
            { "bowandcrossbowskilltab", item => item.GetValueToSkillTab(SkillTab.AmazonBowAndCrossbowSkills) },
            { "javelinandspearskilltab", item => item.GetValueToSkillTab(SkillTab.AmazonJavelinAndSpearSkills) },
            { "passiveandmagicskilltab", item => item.GetValueToSkillTab(SkillTab.AmazonPassiveAndMagicSkills) },
            { "itemaddbowandcrossbowskilltab", item => item.GetValueToSkillTab(SkillTab.AmazonBowAndCrossbowSkills) },
            { "itemaddpassiveandmagicskilltab", item => item.GetValueToSkillTab(SkillTab.AmazonPassiveAndMagicSkills) },
            { "itemaddjavelinandspearskilltab", item => item.GetValueToSkillTab(SkillTab.AmazonJavelinAndSpearSkills) },
            { "palicombatskilltab", item => item.GetValueToSkillTab(SkillTab.PaladinCombatSkills) },
            { "paladincombat", item => item.GetValueToSkillTab(SkillTab.PaladinCombatSkills) },
            { "paladinoffensiveauras", item => item.GetValueToSkillTab(SkillTab.PaladinOffensiveAuras) },
            { "paladindefensiveauras", item => item.GetValueToSkillTab(SkillTab.PaladinDefensiveAuras) },
            { "offensiveaurasskilltab", item => item.GetValueToSkillTab(SkillTab.PaladinOffensiveAuras) },
            { "defensiveaurasskilltab", item => item.GetValueToSkillTab(SkillTab.PaladinDefensiveAuras) },
            { "sorceresslightningskilltab", item => item.GetValueToSkillTab(SkillTab.SorceressLightningSpells) },
            { "sorceressfireskilltab", item => item.GetValueToSkillTab(SkillTab.SorceressFireSpells) },
            { "sorceresscoldskilltab", item => item.GetValueToSkillTab(SkillTab.SorceressColdSpells) },
            { "lightningskilltab", item => item.GetValueToSkillTab(SkillTab.SorceressLightningSpells) },
            { "fireskilltab", item => item.GetValueToSkillTab(SkillTab.SorceressFireSpells) },
            { "coldskilltab", item => item.GetValueToSkillTab(SkillTab.SorceressColdSpells) },
            { "necromancercurses", item => item.GetValueToSkillTab(SkillTab.NecromancerCurses) },
            { "necromancerpoisonandbonespells", item => item.GetValueToSkillTab(SkillTab.NecromancerPoisonAndBoneSpells) },
            { "necromancersummoningspells", item => item.GetValueToSkillTab(SkillTab.NecromancerSummoningSpells) },
            { "poisonandboneskilltab", item => item.GetValueToSkillTab(SkillTab.NecromancerPoisonAndBoneSpells) },
            { "necromancersummoningskilltab", item => item.GetValueToSkillTab(SkillTab.NecromancerSummoningSpells) },
            { "cursesskilltab", item => item.GetValueToSkillTab(SkillTab.NecromancerCurses) },
            { "barbarianwarcriesskilltab", item => item.GetValueToSkillTab(SkillTab.BarbarianWarcries) },
            { "barbariancombatmasteries", item => item.GetValueToSkillTab(SkillTab.BarbarianCombatMasteries) },
            { "barbariancombatskills", item => item.GetValueToSkillTab(SkillTab.BarbarianCombatSkills) },
            { "barbcombatskilltab", item => item.GetValueToSkillTab(SkillTab.BarbarianCombatSkills) },
            { "masteriesskilltab", item => item.GetValueToSkillTab(SkillTab.BarbarianCombatMasteries) },
            { "warcriesskilltab", item => item.GetValueToSkillTab(SkillTab.BarbarianWarcries) },
            { "druidsummoning", item => item.GetValueToSkillTab(SkillTab.DruidSummoning) },
            { "druidshapeshifting", item => item.GetValueToSkillTab(SkillTab.DruidShapeShifting) },
            { "druidelemental", item => item.GetValueToSkillTab(SkillTab.DruidElemental) },
            { "druidsummoningskilltab", item => item.GetValueToSkillTab(SkillTab.DruidSummoning) },
            { "shapeshiftingskilltab", item => item.GetValueToSkillTab(SkillTab.DruidShapeShifting) },
            { "elementalskilltab", item => item.GetValueToSkillTab(SkillTab.DruidElemental) },
            { "assassintraps", item => item.GetValueToSkillTab(SkillTab.AssasinTraps) },
            { "assassinshadowdisciplines", item => item.GetValueToSkillTab(SkillTab.AssasinShadowDisciplines) },
            { "assassinmartialarts", item => item.GetValueToSkillTab(SkillTab.AssasinMartialArts) },
            { "itemaddtrapsskilltab", item => item.GetValueToSkillTab(SkillTab.AssasinTraps) },
            { "trapsskilltab", item => item.GetValueToSkillTab(SkillTab.AssasinTraps) },
            { "shadowdisciplinesskilltab", item => item.GetValueToSkillTab(SkillTab.AssasinShadowDisciplines) },
            { "martialartsskilltab", item => item.GetValueToSkillTab(SkillTab.AssasinMartialArts) }
        };
    }

    private static Dictionary<string, Func<Item, string>> BuildTextResolvers()
    {
        return new Dictionary<string, Func<Item, string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "type", item => NipFieldMaps.ClassificationToType(item.Classification) },
            { "itemtype", item => item.Type ?? string.Empty },
            { "name", item => NipFieldMaps.NormalizeName(item.Name) },
            { "quality", item => NipFieldMaps.QualityToText(item.Quality) },
            { "class", item => NipFieldMaps.ClassTier(item) },
            { "flag", NipFieldMaps.FlagsFor }
        };
    }
}

public abstract class NipExpression
{
    public abstract NipValue EvaluateValue(NipEvaluationContext context);
    public bool Evaluate(NipEvaluationContext context) => EvaluateValue(context).ToBoolean();
}

public sealed class LiteralExpression : NipExpression
{
    public LiteralExpression(NipValue value)
    {
        Value = value;
    }

    public NipValue Value { get; }
    public override NipValue EvaluateValue(NipEvaluationContext context) => Value;
}

public sealed class IdentifierExpression : NipExpression
{
    public IdentifierExpression(string identifier)
    {
        Identifier = identifier;
    }

    public string Identifier { get; }

    public override NipValue EvaluateValue(NipEvaluationContext context)
    {
        return context.TryResolve(Identifier, out var value) ? value : NipValue.Zero;
    }
}

public sealed class UnaryExpression : NipExpression
{
    public UnaryExpression(NipExpression operand, NipUnaryOperator op)
    {
        Operand = operand;
        Operator = op;
    }

    public NipExpression Operand { get; }
    public NipUnaryOperator Operator { get; }

    public override NipValue EvaluateValue(NipEvaluationContext context)
    {
        var value = Operand.EvaluateValue(context);
        return Operator switch
        {
            NipUnaryOperator.Negate => NipValue.FromNumber(-value.AsNumber()),
            _ => value
        };
    }
}

public sealed class BinaryExpression : NipExpression
{
    public BinaryExpression(NipExpression left, NipExpression right, NipBinaryOperator op)
    {
        Left = left;
        Right = right;
        Operator = op;
    }

    public NipExpression Left { get; }
    public NipExpression Right { get; }
    public NipBinaryOperator Operator { get; }

    public override NipValue EvaluateValue(NipEvaluationContext context)
    {
        return Operator switch
        {
            NipBinaryOperator.And => NipValue.FromBool(Left.Evaluate(context) && Right.Evaluate(context)),
            NipBinaryOperator.Or => NipValue.FromBool(Left.Evaluate(context) || Right.Evaluate(context)),
            NipBinaryOperator.Equal => Compare(context, (a, b) => a.CompareTo(b) == 0),
            NipBinaryOperator.NotEqual => Compare(context, (a, b) => a.CompareTo(b) != 0),
            NipBinaryOperator.GreaterThan => Compare(context, (a, b) => a.CompareTo(b) > 0),
            NipBinaryOperator.GreaterThanOrEqual => Compare(context, (a, b) => a.CompareTo(b) >= 0),
            NipBinaryOperator.LessThan => Compare(context, (a, b) => a.CompareTo(b) < 0),
            NipBinaryOperator.LessThanOrEqual => Compare(context, (a, b) => a.CompareTo(b) <= 0),
            NipBinaryOperator.Add => NipValue.FromNumber(Left.EvaluateValue(context).AsNumber() + Right.EvaluateValue(context).AsNumber()),
            NipBinaryOperator.Subtract => NipValue.FromNumber(Left.EvaluateValue(context).AsNumber() - Right.EvaluateValue(context).AsNumber()),
            NipBinaryOperator.Multiply => NipValue.FromNumber(Left.EvaluateValue(context).AsNumber() * Right.EvaluateValue(context).AsNumber()),
            NipBinaryOperator.Divide => NipValue.FromNumber(Right.EvaluateValue(context).AsNumber() == 0 ? 0 : Left.EvaluateValue(context).AsNumber() / Right.EvaluateValue(context).AsNumber()),
            _ => NipValue.Zero
        };
    }

    private NipValue Compare(NipEvaluationContext context, Func<NipComparableValue, NipComparableValue, bool> predicate)
    {
        var leftVal = NipComparableValue.From(Left.EvaluateValue(context));
        var rightVal = NipComparableValue.From(Right.EvaluateValue(context));
        return NipValue.FromBool(predicate(leftVal, rightVal));
    }
}

public enum NipUnaryOperator
{
    Negate
}

public enum NipBinaryOperator
{
    And,
    Or,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Add,
    Subtract,
    Multiply,
    Divide
}

public readonly record struct NipValue(double? Number, string? Text)
{
    public static NipValue Zero => new(0, null);

    public static NipValue FromNumber(double value) => new(value, null);
    public static NipValue FromText(string value) => new(null, value);
    public static NipValue FromBool(bool value) => new(value ? 1 : 0, null);

    public double AsNumber()
    {
        if (Number.HasValue)
        {
            return Number.Value;
        }

        if (!string.IsNullOrEmpty(Text) && double.TryParse(Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    public bool ToBoolean()
    {
        if (Number.HasValue)
        {
            return Math.Abs(Number.Value) > double.Epsilon;
        }

        if (!string.IsNullOrEmpty(Text))
        {
            return !string.Equals(Text, "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(Text, "0", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

internal readonly record struct NipComparableValue(double Number, string? Text)
{
    public static NipComparableValue From(NipValue value)
    {
        if (!string.IsNullOrEmpty(value.Text))
        {
            return new NipComparableValue(0, value.Text.ToLowerInvariant());
        }

        return new NipComparableValue(value.AsNumber(), null);
    }

    public int CompareTo(NipComparableValue other)
    {
        if (Text != null && other.Text == null)
        {
            return 0d.CompareTo(other.Number);
        }

        if (Text == null && other.Text != null)
        {
            return Number.CompareTo(0d);
        }

        if (Text != null || other.Text != null)
        {
            if (Text != null && other.Text != null && other.Text.EndsWith('*'))
            {
                return Text.StartsWith(other.Text[..^1], StringComparison.OrdinalIgnoreCase) ? 0 : -1;
            }

            return string.Compare(Text ?? string.Empty, other.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return Number.CompareTo(other.Number);
    }
}

internal static class NipFieldMaps
{
    public static string ClassificationToType(ClassificationType classification)
    {
        return classification switch
        {
            ClassificationType.Amulet => "amulet",
            ClassificationType.Armor => "armor",
            ClassificationType.Belt => "belt",
            ClassificationType.Boots => "boots",
            ClassificationType.Bow => "bow",
            ClassificationType.Circlet => "circlet",
            ClassificationType.Gloves => "gloves",
            ClassificationType.Helm => "helm",
            ClassificationType.BarbarianHelm => "primalhelm",
            ClassificationType.NecromancerShrunkenHead => "voodooheads",
            ClassificationType.SorceressOrb => "orb",
            ClassificationType.Scepter => "scepter",
            ClassificationType.Polearm => "polearm",
            ClassificationType.Spear => "spear",
            ClassificationType.AmazonJavelin => "javelin",
            ClassificationType.AmazonSpear => "spear",
            ClassificationType.AmazonBow => "bow",
            ClassificationType.Jewel => "jewel",
            ClassificationType.Ring => "ring",
            ClassificationType.Rune => "rune",
            ClassificationType.Gem => "gem",
            ClassificationType.Shield => "shield",
            ClassificationType.PaladinShield => "shield",
            ClassificationType.DruidPelt => "pelt",
            ClassificationType.Staff => "staff",
            ClassificationType.Wand => "wand",
            ClassificationType.Token => "token",
            ClassificationType.Gold => "gold",
            ClassificationType.SmallCharm => "smallcharm",
            ClassificationType.LargeCharm => "largecharm",
            ClassificationType.GrandCharm => "grandcharm",
            _ => classification.ToString().ToLowerInvariant()
        };
    }

    public static string NormalizeName(ItemName name)
    {
        return name.ToString().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    public static string QualityToText(QualityType quality)
    {
        return quality switch
        {
            QualityType.Inferior => "inferior",
            QualityType.Normal => "normal",
            QualityType.Superior => "superior",
            QualityType.Magical => "magic",
            QualityType.Set => "set",
            QualityType.Rare => "rare",
            QualityType.Unique => "unique",
            QualityType.Crafted => "crafted",
            _ => "unknown"
        };
    }

    public static string FlagsFor(Item item)
    {
        if (item.Ethereal)
        {
            return "ethereal";
        }

        if (item.IsIdentified)
        {
            return "identified";
        }

        return "unidentified";
    }

    public static string ClassTier(Item item)
    {
        if (string.IsNullOrWhiteSpace(item.Type))
        {
            return "normal";
        }

        var code = item.Type.ToLowerInvariant();
        if (code.StartsWith('u'))
        {
            return "elite";
        }

        if (code.StartsWith('x'))
        {
            return "exceptional";
        }

        return "normal";
    }
}

internal sealed class NipTokenizer
{
    private readonly string _text;
    private readonly string _source;
    private readonly int _line;

    public NipTokenizer(string text, string source, int line)
    {
        _text = text;
        _source = source;
        _line = line;
    }

    public IReadOnlyList<NipToken> Tokenize()
    {
        var tokens = new List<NipToken>();
        var i = 0;
        while (i < _text.Length)
        {
            var c = _text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < _text.Length && (char.IsLetterOrDigit(_text[i]) || _text[i] == '_'))
                {
                    i++;
                }
                if (i < _text.Length && _text[i] == '*')
                {
                    i++;
                }

                tokens.Add(new NipToken(NipTokenType.Identifier, _text[start..i]));
                continue;
            }

            if (c == '[')
            {
                var end = _text.IndexOf(']', i + 1);
                if (end < 0)
                {
                    throw new NipParserException(_source, _line, "Unclosed identifier");
                }

                var ident = _text.Substring(i + 1, end - i - 1).Trim();
                tokens.Add(new NipToken(NipTokenType.Identifier, ident));
                i = end + 1;
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < _text.Length && (char.IsDigit(_text[i]) || _text[i] == '.'))
                {
                    i++;
                }
                tokens.Add(new NipToken(NipTokenType.Number, _text[start..i]));
                continue;
            }

            switch (c)
            {
                case '&' when Peek(i, '&'):
                    tokens.Add(new NipToken(NipTokenType.And, "&&"));
                    i += 2;
                    continue;
                case '|' when Peek(i, '|'):
                    tokens.Add(new NipToken(NipTokenType.Or, "||"));
                    i += 2;
                    continue;
                case '=' when Peek(i, '='):
                    tokens.Add(new NipToken(NipTokenType.Equal, "=="));
                    i += 2;
                    continue;
                case '!' when Peek(i, '='):
                    tokens.Add(new NipToken(NipTokenType.NotEqual, "!="));
                    i += 2;
                    continue;
                case '>' when Peek(i, '='):
                    tokens.Add(new NipToken(NipTokenType.GreaterThanOrEqual, ">="));
                    i += 2;
                    continue;
                case '<' when Peek(i, '='):
                    tokens.Add(new NipToken(NipTokenType.LessThanOrEqual, "<="));
                    i += 2;
                    continue;
                case '>':
                    tokens.Add(new NipToken(NipTokenType.GreaterThan, ">"));
                    i++;
                    continue;
                case '<':
                    tokens.Add(new NipToken(NipTokenType.LessThan, "<"));
                    i++;
                    continue;
                case '+':
                    tokens.Add(new NipToken(NipTokenType.Plus, "+"));
                    i++;
                    continue;
                case '-':
                    tokens.Add(new NipToken(NipTokenType.Minus, "-"));
                    i++;
                    continue;
                case '*':
                    tokens.Add(new NipToken(NipTokenType.Star, "*"));
                    i++;
                    continue;
                case '/':
                    tokens.Add(new NipToken(NipTokenType.Slash, "/"));
                    i++;
                    continue;
                case '(':
                    tokens.Add(new NipToken(NipTokenType.LeftParen, "("));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new NipToken(NipTokenType.RightParen, ")"));
                    i++;
                    continue;
                default:
                    throw new NipParserException(_source, _line, $"Unexpected character '{c}'");
            }
        }

        tokens.Add(new NipToken(NipTokenType.End, string.Empty));
        return tokens;
    }

    private bool Peek(int index, char expected)
    {
        return index + 1 < _text.Length && _text[index + 1] == expected;
    }
}

internal sealed record NipToken(NipTokenType Type, string Value);

internal enum NipTokenType
{
    Identifier,
    Number,
    And,
    Or,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Plus,
    Minus,
    Star,
    Slash,
    LeftParen,
    RightParen,
    End
}

internal sealed class NipExpressionParser
{
    private readonly IReadOnlyList<NipToken> _tokens;
    private readonly string _source;
    private readonly int _line;
    private int _position;

    public NipExpressionParser(IReadOnlyList<NipToken> tokens, string source, int line)
    {
        _tokens = tokens;
        _source = source;
        _line = line;
    }

    public NipExpression Parse()
    {
        var expr = ParseOr();
        Expect(NipTokenType.End);
        return expr;
    }

    private NipExpression ParseOr()
    {
        var expr = ParseAnd();
        while (Match(NipTokenType.Or))
        {
            expr = new BinaryExpression(expr, ParseAnd(), NipBinaryOperator.Or);
        }
        return expr;
    }

    private NipExpression ParseAnd()
    {
        var expr = ParseEquality();
        while (Match(NipTokenType.And))
        {
            expr = new BinaryExpression(expr, ParseEquality(), NipBinaryOperator.And);
        }
        return expr;
    }

    private NipExpression ParseEquality()
    {
        var expr = ParseComparison();
        while (true)
        {
            if (Match(NipTokenType.Equal))
            {
                expr = new BinaryExpression(expr, ParseComparison(), NipBinaryOperator.Equal);
            }
            else if (Match(NipTokenType.NotEqual))
            {
                expr = new BinaryExpression(expr, ParseComparison(), NipBinaryOperator.NotEqual);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private NipExpression ParseComparison()
    {
        var expr = ParseTerm();
        while (true)
        {
            if (Match(NipTokenType.GreaterThan))
            {
                expr = new BinaryExpression(expr, ParseTerm(), NipBinaryOperator.GreaterThan);
            }
            else if (Match(NipTokenType.GreaterThanOrEqual))
            {
                expr = new BinaryExpression(expr, ParseTerm(), NipBinaryOperator.GreaterThanOrEqual);
            }
            else if (Match(NipTokenType.LessThan))
            {
                expr = new BinaryExpression(expr, ParseTerm(), NipBinaryOperator.LessThan);
            }
            else if (Match(NipTokenType.LessThanOrEqual))
            {
                expr = new BinaryExpression(expr, ParseTerm(), NipBinaryOperator.LessThanOrEqual);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private NipExpression ParseTerm()
    {
        var expr = ParseFactor();
        while (true)
        {
            if (Match(NipTokenType.Plus))
            {
                expr = new BinaryExpression(expr, ParseFactor(), NipBinaryOperator.Add);
            }
            else if (Match(NipTokenType.Minus))
            {
                expr = new BinaryExpression(expr, ParseFactor(), NipBinaryOperator.Subtract);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private NipExpression ParseFactor()
    {
        var expr = ParseUnary();
        while (true)
        {
            if (Match(NipTokenType.Star))
            {
                expr = new BinaryExpression(expr, ParseUnary(), NipBinaryOperator.Multiply);
            }
            else if (Match(NipTokenType.Slash))
            {
                expr = new BinaryExpression(expr, ParseUnary(), NipBinaryOperator.Divide);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private NipExpression ParseUnary()
    {
        if (Match(NipTokenType.Minus))
        {
            return new UnaryExpression(ParseUnary(), NipUnaryOperator.Negate);
        }

        return ParsePrimary();
    }

    private NipExpression ParsePrimary()
    {
        if (Match(NipTokenType.Number, out var numberToken))
        {
            if (!double.TryParse(numberToken.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                throw new NipParserException(_source, _line, $"Invalid number '{numberToken.Value}'");
            }

            return new LiteralExpression(NipValue.FromNumber(value));
        }

        if (Match(NipTokenType.Identifier, out var ident))
        {
            return new IdentifierExpression(ident.Value);
        }

        if (Match(NipTokenType.LeftParen))
        {
            var expr = ParseOr();
            Expect(NipTokenType.RightParen);
            return expr;
        }

        throw new NipParserException(_source, _line, "Unexpected token");
    }

    private bool Match(NipTokenType type)
    {
        if (Current.Type == type)
        {
            _position++;
            return true;
        }
        return false;
    }

    private bool Match(NipTokenType type, out NipToken token)
    {
        token = Current;
        if (Current.Type == type)
        {
            _position++;
            return true;
        }
        return false;
    }

    private void Expect(NipTokenType type)
    {
        if (!Match(type))
        {
            throw new NipParserException(_source, _line, $"Expected {type} but found {Current.Type}");
        }
    }

    private NipToken Current => _tokens[_position];
}

public sealed class NipParserException : Exception
{
    public NipParserException(string source, int line, string message) : base($"{source}:{line} - {message}")
    {
    }
}
