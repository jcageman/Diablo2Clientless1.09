using D2NG.Core.D2GS.Objects;

namespace D2NG.Core.D2GS.Motion;

/// <summary>
/// Everything known about a monster's movement after one packet. Never changed once made: a handler
/// publishes a new one with a single reference write, so a reader on another thread sees either the old
/// or the new state, never a mix. Times are timestamps of the game's clock.
/// </summary>
/// <param name="Exact">The last position a packet said the monster was at.</param>
/// <param name="AnchorX">Where the current move started; a 0x67 starts from the position predicted when it arrived.</param>
/// <param name="Destination">The 0x67 destination while moving to a point, otherwise null.</param>
/// <param name="Speed">Subtiles per second.</param>
/// <param name="LastPacket">The packet that made this snapshot.</param>
public sealed record MotionSnapshot(
    Point Exact,
    long ExactAt,
    double AnchorX,
    double AnchorY,
    long AnchorAt,
    MotionMode Mode,
    Point Destination,
    EntityType TargetType,
    uint TargetId,
    double Speed,
    byte PathType,
    byte RunBonus,
    byte LastPacket)
{
    public bool IsMoving => Mode is MotionMode.MovingToPoint or MotionMode.MovingToUnit;
}
