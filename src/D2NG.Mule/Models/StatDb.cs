namespace D2NG.Mule.Models;

public class StatDb
{
    public string Type { get; set; }
    public int Value { get; set; }

    /// <summary>A per level stat's rate in eighths per character level; its <see cref="Value"/> is then 0.</summary>
    public int PerLevel { get; set; }
}
