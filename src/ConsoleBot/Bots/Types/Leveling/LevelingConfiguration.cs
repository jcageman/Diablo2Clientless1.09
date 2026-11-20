using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Leveling;

public enum PartyRole
{
    /// <summary>Engages first and holds the front.</summary>
    Melee,

    /// <summary>Stays behind the front and attacks from range.</summary>
    Ranged
}

/// <summary>
/// Where a session stops, reached when every party member has done it. The act goals are Normal's; Normal,
/// Nightmare and Hell mean Diablo dead on that difficulty, with every member at the target level.
/// </summary>
public enum LevelingGoal
{
    DenOfEvil,
    ActOne,
    ActTwo,
    ActThree,
    Normal,
    Nightmare,
    Hell
}

public class LevelingMember : AccountConfig
{
    /// <summary>The build plan's file name in the plans folder, without .json.</summary>
    public string Plan { get; set; }

    public PartyRole Role { get; set; }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(Plan))
        {
            throw new InvalidOperationException($"{Character} has no build plan");
        }
    }
}

public class LevelingConfiguration
{
    public List<LevelingMember> Party { get; set; } = [];

    /// <summary>The character that creates the games, picks the route and that the others follow. Required.</summary>
    public string Leader { get; set; }

    /// <summary>Where the build plans live; next to the data folder when left out.</summary>
    public string PlansFolder { get; set; }

    public LevelingGoal Until { get; set; } = LevelingGoal.Normal;

    /// <summary>The level every member reaches before the goal counts; the party farms for it after the last quest.</summary>
    public int TargetLevel { get; set; }

    /// <summary>A safety valve for a game that stops making progress, not a schedule.</summary>
    public int MaxGameMinutes { get; set; } = 45;

    /// <summary>
    /// Diablo kills in the goal's difficulty to make again once the quests are done, to try out a change on the hardest
    /// fight the party knows; a single one becomes two once an attempt fails or sends the party home. 0, the default, makes none.
    /// </summary>
    public int DiabloKills { get; set; }

    public int StartingGameNumber { get; set; } = 1;

    /// <summary>An existing game the party joins for its first game instead of creating one - a player's game with a portal open, say. Later games are created as usual.</summary>
    public string JoinGame { get; set; }

    public string JoinPassword { get; set; }

    public LevelingMember LeaderMember => Party.FirstOrDefault(member => member.Character.Equals(Leader, StringComparison.OrdinalIgnoreCase));

    public void Validate()
    {
        if (Party.Count == 0)
        {
            throw new InvalidOperationException("The leveling party is empty");
        }

        if (string.IsNullOrWhiteSpace(Leader) || LeaderMember == null)
        {
            throw new InvalidOperationException("The leveling party needs a leader, and the leader must be one of its members");
        }

        foreach (var member in Party)
        {
            member.Validate();
        }

        var accounts = Party.GroupBy(member => member.Username, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (accounts.Count > 0)
        {
            throw new InvalidOperationException($"Each member needs its own account, a second login drops the first: {string.Join(", ", accounts)}");
        }
    }
}
