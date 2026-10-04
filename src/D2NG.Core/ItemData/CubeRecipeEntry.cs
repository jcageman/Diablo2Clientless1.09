using D2NG.Core.D2GS.Enums;
using System;
using System.Collections.Generic;

namespace D2NG.Core.ItemData;

/// <summary>
/// One row of cubemain. Inputs and output are kept as the game's own strings, such as
/// <c>ring,mag</c> or <c>usetype,crf</c>.
/// </summary>
public sealed class CubeRecipeEntry
{
    public int Index { get; set; }
    public bool Enabled { get; set; }
    public int Version { get; set; }
    public CharacterClass? Class { get; set; }
    public List<string> Inputs { get; set; } = [];
    public string Output { get; set; }

    /// <summary>Share of the player's level in the output's item level.</summary>
    public int PlayerLevelPercent { get; set; }

    /// <summary>Share of the first input's item level in the output's item level.</summary>
    public int ItemLevelPercent { get; set; }

    /// <summary>Fixed mods the output always gets, on top of any affixes it rolls.</summary>
    public List<PropertyRange> Mods { get; set; } = [];

    public bool IsCraft => Output != null && Output.Contains("crf", StringComparison.OrdinalIgnoreCase);
}
