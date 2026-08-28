using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// Configuration for the cube bot, bound from <c>bot:cube</c>. The bot logs on with a single
/// account, collects the configured ingredients from the stash and transmutes them until it runs
/// out of material.
/// </summary>
public class CubeConfiguration : AccountConfig
{
    /// <summary>
    /// The ingredients of the recipe to run, one entry per distinct ingredient, each with the
    /// number of items needed per transmute.
    /// </summary>
    [Required]
    public List<RecipeRequirement> RecipeRequirements { get; set; }

    /// <summary>
    /// What the recipe produces. Used to recognise the output in the cube so it can be moved out
    /// and, when it is itself an ingredient, fed back in.
    /// </summary>
    [Required]
    public RecipeResult RecipeResult { get; set; }

    /// <summary>
    /// Validates every configured recipe requirement.
    /// </summary>
    public override void Validate()
    {
        RecipeRequirements.ForEach(x => x.Validate());
    }
}
