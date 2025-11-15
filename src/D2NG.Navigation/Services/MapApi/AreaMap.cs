using D2NG.Core.D2GS;
using System.Collections.Generic;
using D2NG.Core.D2GS.Act;

namespace D2NG.Navigation.Services.MapApi;

public class AreaMap
{
    public Point LevelOrigin { get; set; }
    public int[][] Map { get; set; }
    public Dictionary<Area, AdjacentLevel> AdjacentLevels { get; set; }
    public Dictionary<int, List<Point>> Npcs { get; set; }
    public Dictionary<int, List<Point>> Objects { get; set; }
}
