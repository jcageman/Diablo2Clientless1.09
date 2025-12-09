using D2NG.Core.D2GS.Items;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule;

public class MuleFilter
{
    public bool? NotFilter { get; set; }

    [Required]
    public ItemName? ItemName { get; set; }

    [Required]
    public QualityType? QualityType { get; set; }

    [Required]
    public ClassificationType? ClassificationType { get; set; }
}
