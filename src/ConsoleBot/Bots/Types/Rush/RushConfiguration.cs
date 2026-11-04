using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace ConsoleBot.Bots.Types.Rush;

public enum RushMilestone
{
    Enter,
    Complete
}

public class RushDestination
{
    public Difficulty Difficulty { get; set; }

    [Range(1, 5)]
    public int Act { get; set; } = 1;

    public RushMilestone Milestone { get; set; } = RushMilestone.Enter;

    public D2NG.Core.D2GS.Act.Act ActValue => (D2NG.Core.D2GS.Act.Act)(Act - 1);
}

public enum RushSideQuest
{
    DenOfEvil,
    CharsiImbue,
    Radament,
    GoldenBird,
    LamEsen,
    Izual,
    Shenk,
    Anya,
    Ancients
}

public class RushConfiguration
{
    [Range(1, int.MaxValue)]
    public int StartingGameNumber { get; set; } = 1;

    [Required]
    public AccountConfig Rusher { get; set; }

    [Required]
    public List<AccountConfig> Rushees { get; set; }

    /// <summary>
    /// Extra fighters that join, party up and add damage to boss fights. The <see cref="Rusher"/>
    /// still leads - it takes the waypoints, opens the portals and does the traversal - so the crew
    /// only has to arrive and attack. Exists because a single-element damage type cannot handle
    /// immunities: a cold-only sorceress does literally nothing to Nightmare Duriel.
    /// </summary>
    public List<AccountConfig> Crew { get; set; }

    [Required]
    public RushDestination Destination { get; set; }

    /// <summary>
    /// Replays the chain on this difficulty for rushees that already satisfy the destination, so a
    /// step can be tested again without a fresh character. The quest-state gates still skip finished
    /// acts and the Chaos Sanctuary is fought every game, so on Nightmare this rehearses Diablo. No
    /// destination game is proven afterwards.
    /// </summary>
    public Difficulty? RehearsalDifficulty { get; set; }

    /// <summary>
    /// "all" has the rusher hand every rushee the field waypoints of the acts it can reach, once the
    /// rush itself is done; "none" (the default) hands out nothing. The rusher collects any it lacks
    /// first, in a game of its own.
    /// </summary>
    public string GiveWaypoints { get; set; } = "none";

    /// <summary>Waypoints left out of <see cref="GiveWaypoints"/>.</summary>
    public List<Waypoint> SkipWaypoints { get; set; } = [];

    public bool GivesWaypoints => GiveWaypoints?.ToLowerInvariant() == "all";

    /// <summary>
    /// "all" does the rushees' open side quests - skill, stat, socket and the rest - along with the rush,
    /// or on their own for a rushee already at the destination; "none" (the default) leaves them.
    /// </summary>
    public string SideQuests { get; set; } = "none";

    /// <summary>
    /// The difficulties the side work is done on. A rushee already at the destination is otherwise played on
    /// the difficulty it has reached; for now one game per run, on the first listed difficulty reached.
    /// </summary>
    public List<Difficulty> QuestDifficulties { get; set; } = [];

    /// <summary>Side quests left out of <see cref="SideQuests"/>.</summary>
    public List<RushSideQuest> SkipQuests { get; set; } = [];

    /// <summary>
    /// Lets the rushee fight. Needed for the Golden Bird, whose figurine only drops for a killer that has
    /// not finished the quest, and for a rushee strong enough to do its own killing.
    /// </summary>
    public bool RusheeFights { get; set; }

    /// <summary>How long a rushee gets to land a kill a quest needs from it before the quest is skipped.</summary>
    [Range(5, 3600)]
    public int RusheeKillLimitSeconds { get; set; } = 60;

    public bool DoesSideQuest(RushSideQuest quest) =>
        SideQuests?.ToLowerInvariant() == "all" && !(SkipQuests ?? []).Contains(quest);

    public void Validate()
    {
        if (Rusher == null)
        {
            throw new ValidationException($"{nameof(Rusher)} is required");
        }

        Rusher.Validate();

        if (Rushees == null || Rushees.Count == 0)
        {
            throw new ValidationException($"{nameof(Rushees)} must contain at least one existing character");
        }

        foreach (var rushee in Rushees)
        {
            rushee.Validate();
        }

        if (Destination == null)
        {
            throw new ValidationException($"{nameof(Destination)} is required");
        }

        if (SideQuests?.ToLowerInvariant() is not ("none" or "all"))
        {
            throw new ValidationException($"{nameof(SideQuests)} must be \"none\" or \"all\", not \"{SideQuests}\"");
        }

        if (GiveWaypoints?.ToLowerInvariant() is not ("none" or "all"))
        {
            throw new ValidationException($"{nameof(GiveWaypoints)} must be \"none\" or \"all\", not \"{GiveWaypoints}\"");
        }

        Validator.ValidateObject(Destination, new ValidationContext(Destination), validateAllProperties: true);

        foreach (var member in Crew ?? [])
        {
            member.Validate();
        }

        var duplicateCharacters = Rushees
            .Concat(Crew ?? [])
            .Append(Rusher)
            .GroupBy(a => a.Character, System.StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateCharacters.Count > 0)
        {
            throw new ValidationException(
                $"Every configured client must use a distinct character: {string.Join(", ", duplicateCharacters)}");
        }
    }
}
