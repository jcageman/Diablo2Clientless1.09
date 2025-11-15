using D2NG.Core.D2GS;
using System.Collections.Generic;

namespace D2NG.Navigation.Services.Pathing;

public struct Path
{
    public bool Found { get; set; }
    public List<Point> Points { get; set; }
}
