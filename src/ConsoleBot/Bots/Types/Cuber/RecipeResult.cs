using D2NG.Core.D2GS.Items;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// Describes what a cube recipe produces. The filters that are set must all match; at least one of
/// them is required.
/// </summary>
public class RecipeResult
{
    /// <summary>
    /// Expected quality of the produced item. Leave unset to accept any quality.
    /// </summary>
    public QualityType? Quality { get; set; }

    /// <summary>
    /// Expected names of the produced item. Leave unset to accept any name that passes the other
    /// filters.
    /// </summary>
    public List<ItemName> ItemNames { get; set; }

    /// <summary>
    /// Expected classification of the produced item. Leave unset to accept any classification.
    /// </summary>
    public ClassificationType? Classification { get; set; }

    /// <summary>
    /// Validates that at least one of <see cref="Classification"/>, <see cref="ItemNames"/> or
    /// <see cref="Quality"/> is set.
    /// </summary>
    public void Validate()
    {
        if (Classification == null && ItemNames == null && Quality == null)
        {
            throw new ValidationException($"at least one of {nameof(Classification)}, {nameof(ItemNames)} or {nameof(Quality)} is required on cube configuration recipe result");
        }
    }
}
