namespace D2NG.Core.D2GS.Motion;

/// <summary>What <see cref="Objects.WorldObject.Location"/> returns for a monster. Every packet is tracked in every mode; this only picks the reading.</summary>
public enum MonsterMotion
{
    /// <summary>The behaviour before motion tracking: 0x67 puts the monster on its destination, 0x6D and 0x69 on their position, nothing else moves it.</summary>
    Legacy,

    /// <summary>The last exact position any packet carried; no motion between packets.</summary>
    Anchored,

    /// <summary>The last exact position moved along the current walk or chase at the monster's speed.</summary>
    Simulated,
}

public enum MotionMode : byte
{
    Standing,
    MovingToPoint,
    MovingToUnit,
    Acting,
    Dead,
}
