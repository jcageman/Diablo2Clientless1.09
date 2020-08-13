using D2NG.Core.D2GS;
using System.Collections.Generic;

namespace D2NG.Navigation.Services.MapApi
{
    public class AdjacentLevel
    {
        public List<Point> Exits { get; set; }
        public Point LevelOrigin { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
