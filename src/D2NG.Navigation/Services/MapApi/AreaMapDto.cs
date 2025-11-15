using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace D2NG.Navigation.Services.MapApi;

public class AreaMapDto
{
    public PointDto LevelOrigin { get; set; }

    [JsonProperty("mapRows")]
    public List<List<int>> Map { get; set; }
    public Dictionary<string, AdjacentLevel> AdjacentLevels { get; set; }
    public Dictionary<string, List<PointDto>> Npcs { get; set; }
    public Dictionary<string, List<PointDto>> Objects { get; set; }
}
