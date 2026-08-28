using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace D2NG.Navigation.Services.MapApi;

public class AreaMapDto
{
    public PointDto LevelOrigin { get; set; }

    [JsonPropertyName("mapRows")]
    public List<List<int>> Map { get; set; }
    public Dictionary<string, AdjacentLevel> AdjacentLevels { get; set; }
    public Dictionary<string, List<PointDto>> Npcs { get; set; }
    public Dictionary<string, List<PointDto>> Objects { get; set; }

    /// <summary>
    /// For the canyon of the magi, which of the seven tombs is Tal Rasha's real one. Null everywhere
    /// else. Saves a rush from opening six wrong tombs to find the orifice.
    /// </summary>
    public string TombArea { get; set; }
}
