namespace D2NG.Core.D2GS.Objects;

public abstract class Entity
{
    public EntityType Type { get; internal set; }
    public uint Id { get; protected set; }
    public Point Location { get; internal set; }
}