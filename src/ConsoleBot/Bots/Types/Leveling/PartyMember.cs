using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Players;
using D2NG.Leveling.Plans;
using System.Collections.Generic;
using System.Linq;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Bots.Types.Leveling;

public enum FollowMode
{
    /// <summary>Fights what comes near and keeps up with the leader.</summary>
    Follow,

    /// <summary>Fights what comes near but stays put.</summary>
    Hold,

    /// <summary>Does nothing on its own: the coordinator is moving it.</summary>
    Off
}

public sealed class PartyMember(Client client, LevelingMember account, BuildPlan plan, SkillCatalog catalog)
{
    public Client Client { get; } = client;

    public LevelingMember Account { get; } = account;

    public BuildPlan Plan { get; } = plan;

    public SkillCatalog Catalog { get; } = catalog;

    public Game Game => Client.Game;

    public string Name => Account.Character;

    public PartyRole Role => Account.Role;

    public volatile FollowMode Mode = FollowMode.Follow;

    /// <summary>Set while the barbarian casts Battle Orders: his own loop holds its attacks, a cast during a whirl is dropped.</summary>
    public volatile bool Shouting;

    public bool IsLeader { get; set; }

    /// <summary>How far the character has come, from the realm's character list at login.</summary>
    public D2NG.Core.MCP.CharacterProgression Progression { get; set; }

    public int Level => Game.Me?.Attributes.GetValueOrDefault(Attribute.Level) ?? 0;

    public bool InGame => Client.Game.IsInGame() && Game.Me != null;

    /// <summary>The class's own skills with their hard points, which is what the plan counts.</summary>
    public Dictionary<Skill, int> HardSkills()
        => Game.Me.Skills
            .Where(pair => pair.Value > 0 && Catalog.TryGet(pair.Key, out var entry) && entry.Class == Plan.Class)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    public Dictionary<Attribute, int> HardStats()
        => new[] { Attribute.Strength, Attribute.Dexterity, Attribute.Vitality, Attribute.Energy }
            .ToDictionary(stat => stat, stat => Game.Me.Attributes.GetValueOrDefault(stat));

    /// <summary>Hard points plus what items add.</summary>
    public int SkillLevel(Skill skill)
        => Game.Me.Skills.GetValueOrDefault(skill) + Game.Me.ItemSkills.GetValueOrDefault(skill);

    public CombatStage Stage => Plan.Combat.LastOrDefault(stage => stage.FromLevel <= Level) ?? new CombatStage { Attack = Skill.Attack };

    public override string ToString() => Name;
}
