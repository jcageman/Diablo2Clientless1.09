using D2NG.Core.D2GS.Items;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule;

/// <summary>
/// One item filter inside a <see cref="MuleRule"/>. Every property that is set must match; the
/// ones left unset are ignored, so a filter with nothing set matches every item.
/// </summary>
public class MuleFilter
{
    /// <summary>
    /// Set to <see langword="true"/> to invert the filter, accepting exactly the items the other
    /// properties would have rejected.
    /// </summary>
    public bool? NotFilter { get; set; }

    /// <summary>
    /// Item name the item must have. Leave unset to match any name.
    /// </summary>
    [Required]
    public ItemName? ItemName { get; set; }

    /// <summary>
    /// Quality the item must have, for example <c>Unique</c> or <c>Rare</c>. Leave unset to match
    /// any quality.
    /// </summary>
    [Required]
    public QualityType? QualityType { get; set; }

    /// <summary>
    /// Classification the item must have, for example <c>Ring</c> or <c>Gem</c>. Leave unset to
    /// match any classification.
    /// </summary>
    [Required]
    public ClassificationType? ClassificationType { get; set; }
}
