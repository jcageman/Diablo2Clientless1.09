using D2NG.Core.D2GS;

namespace ConsoleBot.Bots.Types.Cows;

public enum ClusterKind
{
    /// <summary>A pack of hell bovines, worked by the killing sorceresses.</summary>
    Cow,

    /// <summary>A pack of the monsters the party actively hunts in active mode.</summary>
    Hunted
}

/// <summary>
/// A pack of monsters at a fixed location. Clusters are discovered as monsters are assigned and
/// live until they are released, which is what marks them done.
/// </summary>
public sealed class MonsterCluster
{
    public uint Id { get; init; }

    public Point Location { get; init; }

    public ClusterKind Kind { get; init; }

    /// <summary>Whether a client is currently working this cluster.</summary>
    public bool Claimed { get; set; }

    /// <summary>
    /// How far along the sweep route this cluster sits. Work is handed out in this order so the
    /// killers advance as one line rather than each chasing whatever is nearest to itself.
    /// </summary>
    public int SweepIndex { get; set; } = int.MaxValue;

    /// <summary>
    /// Monsters assigned to this cluster that are still alive. Counted by entity rather than by
    /// what is standing near <see cref="Location"/>, because a pack that chases the party leaves
    /// its own cluster and would otherwise read as dead.
    /// </summary>
    public int AliveMembers { get; set; }

    /// <summary>
    /// Every monster ever assigned to this cluster. Tells a cluster whose members have all died
    /// apart from one that has not been populated yet, which look identical on
    /// <see cref="AliveMembers"/> alone.
    /// </summary>
    public int TotalMembers { get; set; }

    /// <summary>
    /// Members confirmed dead, as opposed to merely out of sight. A monster that leaves view is
    /// removed from the cluster too, so <see cref="AliveMembers"/> reaching zero does not mean the
    /// pack is dead - it may be one nobody has walked up to yet.
    /// </summary>
    public int KilledMembers { get; set; }

    /// <summary>
    /// Set when the cluster is released. A released cluster counts as done whatever the outcome,
    /// including a timeout or a client giving up on it.
    /// </summary>
    public bool Done { get; set; }

    /// <summary>
    /// Hunted clusters only: whether the nearest cow clusters have been done. Sticky, so the party
    /// never turns around halfway to a cluster that was eligible when it set off.
    /// </summary>
    public bool Eligible { get; set; }

    public override string ToString() => $"{Kind} cluster at {Location}";
}
