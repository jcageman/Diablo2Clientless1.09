using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber;

public class CubeConfiguration : AccountConfig
{
    [Required]
    public List<RecipeRequirement> RecipeRequirements { get; set; }

    [Required]
    public RecipeResult RecipeResult { get; set; }

    public override void Validate()
    {
        RecipeRequirements.ForEach(x => x.Validate());
    }
}
