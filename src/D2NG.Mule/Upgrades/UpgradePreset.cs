using System.Collections.Generic;

namespace D2NG.Mule.Upgrades;

public enum ResistMode
{
    /// <summary>Fire, cold and lightning never drop below where they stand; cold over the cap still counts; poison does not.</summary>
    PvP,

    /// <summary>The resists the preset weighs count up to the cap, nothing over it.</summary>
    PvM,
}

/// <summary>One stat a preset values: its weight per point, and optionally the point it stops counting at.</summary>
public class StatWeight
{
    /// <summary>
    /// A <c>StatType</c> name, a total the evaluation derives (<c>Life</c>, <c>Mana</c>, <c>Cast Frames</c>,
    /// <c>Hit Recovery Frames</c>, <c>Block %</c>), or <c>ClassSkills</c> for the character's own class's skills.
    /// </summary>
    public string Stat { get; set; }

    public double Weight { get; set; }

    /// <summary>The total above which more of the stat is worth nothing; null for no cap.</summary>
    public double? Cap { get; set; }

    /// <summary>The slot the weight applies to, such as faster run/walk on boots only; null for every slot.</summary>
    public string Slot { get; set; }
}

/// <summary>
/// What "better" means for one build: the stats it lives on and how it weighs resists. Presets come
/// from the mule manager's configuration, with <see cref="UpgradePresets.Defaults"/> when it names none.
/// </summary>
public class UpgradePreset
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    /// <summary>Character classes this preset is the default for.</summary>
    public List<string> DefaultFor { get; set; } = [];

    public ResistMode Mode { get; set; } = ResistMode.PvP;

    public List<StatWeight> Weights { get; set; } = [];

    /// <summary>
    /// The item classifications the build fights with, such as Bow and AmazonBow. Empty keeps the right hand to the
    /// classification the character wields now.
    /// </summary>
    public List<string> Weapons { get; set; } = [];

    /// <summary>Score per resist point under the cap, keyed Fire, Cold, Lightning, Poison.</summary>
    public Dictionary<string, double> ResistWeights { get; set; } = [];

    /// <summary>Score per cold resist point over the cap in <see cref="ResistMode.PvP"/>.</summary>
    public double ColdOverCap { get; set; } = 0.3;
}
