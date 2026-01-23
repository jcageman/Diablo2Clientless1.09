namespace D2NG.Core.D2GS.Act;

public class Level
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; }
    public int Type { get; set; }

    public Level(int w, int h, int x, int y, string s, int t)
    {
        Width = w;
        Height = h;
        X = x;
        Y = y;
        Name = s;
        Type = t;
    }
}
