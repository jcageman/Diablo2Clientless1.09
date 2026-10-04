using D2NG.Core.D2GS;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// A knot of monsters the killers work as one piece of the level. There is only one kind: whatever
/// is standing there is what gets killed, and which character can hurt it is decided from its
/// resistances rather than from a list of monster types kept by hand.
/// </summary>
public sealed class MonsterCluster
{
    public uint Id { get; init; }

    public Point Location { get; init; }

    /// <summary>Whether a client has taken this cluster and is on its way.</summary>
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
    /// Done because the party gave up on it with monsters still standing, not because they died.
    /// Counting these as cleared made the progress line report a full sweep - "54/54" - on a level
    /// that still had living packs in it, so a change that quietly abandoned more ground still read
    /// as perfect.
    /// </summary>
    public bool Abandoned { get; set; }

    public override string ToString() => $"cluster at {Location}";
}
