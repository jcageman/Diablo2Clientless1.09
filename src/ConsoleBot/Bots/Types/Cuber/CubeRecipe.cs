using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// One cube recipe: the ingredients one transmute consumes and what comes out.
/// </summary>
public class CubeRecipe
{
    /// <summary>
    /// Name used in the log, for example <c>Ko</c> or <c>Perfect Ruby</c>.
    /// </summary>
    [Required]
    public string Name { get; set; }

    /// <summary>
    /// The ingredients, one entry per distinct ingredient with the number one transmute consumes.
    /// </summary>
    [Required]
    public List<RecipeRequirement> Requirements { get; set; }

    /// <summary>
    /// What the transmute produces; checked against what the cube holds afterwards.
    /// </summary>
    [Required]
    public RecipeResult Result { get; set; }

    /// <summary>
    /// The first requirement is one item that is transmuted again and again, each time with fresh
    /// other ingredients, until a keep rule outside CraftBases.nip keeps the result - the sword
    /// reroll with chipped gems. Such a kept item is never fed back in; stock kept only by
    /// CraftBases.nip (white bases, junk magic Colossus Blades) is.
    /// </summary>
    public bool RerollUntilKept { get; set; }

    /// <summary>
    /// For <see cref="RerollUntilKept"/>: most rolls one run collects ingredients for, so the next
    /// reroll recipe in the list gets room in the same run. Defaults to 9.
    /// </summary>
    public int MaxRerollsPerRun { get; set; } = 9;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ValidationException("every cube recipe needs a name");
        }

        if (Requirements == null || Requirements.Count == 0)
        {
            throw new ValidationException($"cube recipe {Name} has no requirements");
        }

        foreach (var requirement in Requirements)
        {
            requirement.Validate();
            if (requirement.Amount < 1)
            {
                throw new ValidationException($"cube recipe {Name} has a requirement with an amount below 1");
            }
        }

        if (RerollUntilKept && (Requirements.Count < 2 || Requirements[0].Amount != 1))
        {
            throw new ValidationException($"cube recipe {Name} rerolls one item: its first requirement needs amount 1 and at least one other requirement");
        }

        if (Result == null)
        {
            throw new ValidationException($"cube recipe {Name} has no result");
        }

        Result.Validate();
    }
}
