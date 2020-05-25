using D2NG.D2GS.Objects;

namespace D2NG.D2GS
{
    public abstract class Entity
    {
        public EntityType Type { get; internal set; }
        public uint Id { get; protected set; }
        public Point Location { get; internal set; }
    }
}