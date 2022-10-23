using D2NG.Core.D2GS.Items;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber
{
    public class RecipeResult
    {
        public QualityType? Quality { get; set; }

        public List<ItemName> ItemNames { get; set; }

        public ClassificationType? Classification { get; set; }

        public void Validate()
        {
            if (Classification == null && ItemNames == null && Quality == null)
            {
                throw new ValidationException($"at least one of {nameof(Classification)}, {nameof(ItemNames)} or {nameof(Quality)} is required on cube configuration recipe result");
            }
        }
    }
}
