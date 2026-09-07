namespace D2NG.Mule.Packing;

public readonly record struct Shape(int Width, int Height)
{
    public int Area => Width * Height;

    public override string ToString() => $"{Width}x{Height}";
}

public readonly record struct Cell(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}
