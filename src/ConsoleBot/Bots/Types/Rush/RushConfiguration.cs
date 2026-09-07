using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
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
