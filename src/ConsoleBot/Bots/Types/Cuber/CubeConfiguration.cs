using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// Configuration for the cube bot, bound from <c>bot:cube</c>. The account here is the cube
/// character itself; the mules that hold the ingredients come from the mule configuration. Run one
/// instance per game mode: a classic cube character only sees the classic mules' items, and an
/// expansion one the expansion mules'.
/// </summary>
public class CubeConfiguration : AccountConfig
{
    /// <summary>
    /// The recipes to run, in order of preference: when one run cannot collect everything, the
    /// recipes listed first get the room.
    /// </summary>
    [Required]
    public List<CubeRecipe> Recipes { get; set; }

    /// <summary>
    /// How long to wait before asking the mule database again when nothing could be cubed, or after
    /// a run failed. Defaults to an hour.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Most ingredients one run collects. The cube character stashes what it is handed, so this is
    /// bounded by its stash and inventory; leave room for the cube and the transmute results.
    /// Defaults to 30.
    /// </summary>
    public int MaxItemsPerRun { get; set; } = 30;

    /// <summary>
    /// Validates every configured recipe.
    /// </summary>
    public override void Validate()
    {
        base.Validate();
        if (Recipes == null || Recipes.Count == 0)
        {
            throw new ValidationException("the cube bot needs at least one recipe");
        }

        Recipes.ForEach(r => r.Validate());
        if (MaxItemsPerRun < 1)
        {
            throw new ValidationException($"{nameof(MaxItemsPerRun)} must be at least 1");
        }
    }
}
