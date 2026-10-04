namespace D2NG.Core.ItemData;

/// <summary>
/// A property code from properties.bin with the values it rolls between. What Param, Min and Max
/// mean is up to the property: for a skill-on-event property Param is the skill, Min the chance and
/// Max the skill level.
/// </summary>
public sealed class PropertyRange
{
    public string Property { get; set; }
    public int Param { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }

    /// <summary>Percent chance a cube output gets this mod at all; 0 means always. Unused on affixes.</summary>
    public int Chance { get; set; }

    public override string ToString()
    {
        var range = Min == Max ? $"{Min}" : $"{Min}-{Max}";
        var param = Param != 0 ? $"({Param})" : "";
        var chance = Chance != 0 ? $" {Chance}%" : "";
        return $"{Property}{param} {range}{chance}";
    }
}
