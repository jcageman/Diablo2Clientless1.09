using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using System.Text.Json;
using System.Text.Json.Serialization;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Leveling.Plans;

/// <summary>
/// How one build spends its points from level 1 on. 1.09 has no respec, so the plan is the only
/// thing that decides a point and it is checked before anything is spent.
/// </summary>
public sealed class BuildPlan
{
    public string Name { get; set; } = "";

    public CharacterClass Class { get; set; }

    public List<StatTarget> Stats { get; set; } = [];

    /// <summary>In order: the first step that can take a point now gets it.</summary>
    public List<SkillStep> Skills { get; set; } = [];

    public List<CombatStage> Combat { get; set; } = [];

    /// <summary>The weapon kinds this build fights with, the one it prefers first.</summary>
    public List<D2NG.Core.D2GS.Items.ClassificationType> Weapons { get; set; } = [];

    /// <summary>Whether it carries a shield beside a one-handed weapon.</summary>
    public bool Shield { get; set; }

    public static BuildPlan Load(string path) => Parse(File.ReadAllText(path));

    public static BuildPlan Parse(string json)
        => JsonSerializer.Deserialize<BuildPlan>(json, SerializerOptions)
           ?? throw new InvalidDataException("A build plan cannot be empty");

    public static IReadOnlyList<BuildPlan> LoadFolder(string folder)
        => [.. Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).Select(Load)];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// <see cref="Base"/> is always spent, ahead of anything else. Between base and <see cref="Cap"/>
/// points go only to the requirement of an item the character wants to wear. The one stat marked
/// <see cref="Rest"/> takes every point nothing else claims.
/// </summary>
public sealed class StatTarget
{
    public Attribute Stat { get; set; }

    public int? Base { get; set; }

    public int? Cap { get; set; }

    public bool Rest { get; set; }
}

/// <summary>Either raise <see cref="Skill"/> to <see cref="Level"/> hard points, or hold every point until <see cref="UntilLevel"/>.</summary>
public sealed class SkillStep
{
    public Skill? Skill { get; set; }

    public int Level { get; set; } = 1;

    public bool Save { get; set; }

    public int? UntilLevel { get; set; }

    public override string ToString() => Save ? $"save until {UntilLevel}" : $"{Skill} {Level}";
}

public sealed class CombatStage
{
    public int FromLevel { get; set; } = 1;

    public Skill Attack { get; set; }

    public Skill? Secondary { get; set; }

    public Skill? Aura { get; set; }
}
