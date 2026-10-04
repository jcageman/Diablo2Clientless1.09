using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;

namespace D2NG.Core.ObjectData;

/// <summary>A shrine placed by the level's preset data, so its position is fixed for a map seed.</summary>
public sealed class ShrineLocation
{
    public ShrineLocation(EntityCode code, ShrineKind kind, Point location)
    {
        Code = code;
        Kind = kind;
        Location = location;
    }

    public EntityCode Code { get; }

    public ShrineKind Kind { get; }

    public Point Location { get; }

    public override string ToString() => $"{Kind} shrine (object {(int)Code}) at {Location}";
}
