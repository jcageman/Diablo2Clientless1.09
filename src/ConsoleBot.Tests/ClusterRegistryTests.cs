using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class ClusterRegistryTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    private static MonsterCluster Register(ClusterRegistry registry, Point location, ClusterKind kind)
        => registry.FindOrRegister(location, kind, out _);

    private static bool CreatedNew(ClusterRegistry registry, Point location, ClusterKind kind)
    {
        registry.FindOrRegister(location, kind, out var created);
        return created;
    }

    /// <summary>
    /// Four cow clusters spread far enough apart to be distinct, plus one hunted cluster sitting
    /// among them. Returns the cow clusters in the order they were registered.
    /// </summary>
    private static (ClusterRegistry Registry, List<MonsterCluster> Cows, MonsterCluster Hunted) WithFourCowsAroundOneHunted()
    {
        var registry = new ClusterRegistry();
        List<MonsterCluster> cows =
        [
            Register(registry, At(100, 100), ClusterKind.Cow),
            Register(registry, At(200, 100), ClusterKind.Cow),
            Register(registry, At(100, 200), ClusterKind.Cow),
            Register(registry, At(200, 200), ClusterKind.Cow),
        ];
        var hunted = Register(registry, At(150, 150), ClusterKind.Hunted);
        return (registry, cows, hunted);
    }

    [Fact]
    public void A_monster_inside_an_existing_cluster_does_not_start_a_new_one()
    {
        var registry = new ClusterRegistry();

        Assert.True(CreatedNew(registry, At(100, 100), ClusterKind.Cow));
        Assert.False(CreatedNew(registry, At(120, 100), ClusterKind.Cow));
        Assert.True(CreatedNew(registry, At(140, 100), ClusterKind.Cow));

        Assert.Equal(2, registry.CountOf(ClusterKind.Cow));
    }

    [Fact]
    public void Cow_and_hunted_clusters_are_tracked_separately_at_the_same_spot()
    {
        var registry = new ClusterRegistry();

        Assert.True(CreatedNew(registry, At(100, 100), ClusterKind.Cow));
        Assert.True(CreatedNew(registry, At(100, 100), ClusterKind.Hunted));

        Assert.Equal(1, registry.CountOf(ClusterKind.Cow));
        Assert.Equal(1, registry.CountOf(ClusterKind.Hunted));
    }

    [Fact]
    public void ClaimNearest_hands_out_the_closest_pending_cluster_once()
    {
        var registry = new ClusterRegistry();
        Register(registry, At(100, 100), ClusterKind.Cow);
        var near = Register(registry, At(400, 100), ClusterKind.Cow);

        var claimed = registry.ClaimNearest(At(390, 100), ClusterKind.Cow);

        Assert.Same(near, claimed);
        Assert.NotSame(near, registry.ClaimNearest(At(390, 100), ClusterKind.Cow));
    }

    [Fact]
    public void Release_marks_a_cluster_done_whatever_the_outcome()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Cow);

        registry.Release(registry.ClaimNearest(At(100, 100), ClusterKind.Cow));

        Assert.True(cluster.Done);
        Assert.True(registry.AllDone(ClusterKind.Cow));
        Assert.Equal(1, registry.DoneCountOf(ClusterKind.Cow));
    }

    [Fact]
    public void GiveUp_returns_a_cluster_to_the_pool_without_finishing_it()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Cow);

        registry.GiveUp(registry.ClaimNearest(At(100, 100), ClusterKind.Cow));

        Assert.False(cluster.Done);
        Assert.False(registry.AllDone(ClusterKind.Cow));
        Assert.Same(cluster, registry.ClaimNearest(At(100, 100), ClusterKind.Cow));
    }

    [Fact]
    public void A_client_with_nothing_pending_falls_back_to_a_cluster_someone_else_is_on()
    {
        var registry = new ClusterRegistry();
        var only = Register(registry, At(100, 100), ClusterKind.Cow);
        registry.ClaimNearest(At(100, 100), ClusterKind.Cow);

        Assert.Same(only, registry.ClaimNearest(At(100, 100), ClusterKind.Cow));
    }

    [Fact]
    public void A_hunted_cluster_stays_shut_until_its_four_nearest_cow_clusters_are_done()
    {
        var (registry, cows, hunted) = WithFourCowsAroundOneHunted();

        Assert.False(hunted.Eligible);
        Assert.Null(registry.ClaimNearest(At(150, 150), ClusterKind.Hunted));

        for (var i = 0; i < 3; i++)
        {
            registry.Release(cows[i]);
            Assert.False(hunted.Eligible);
        }

        registry.Release(cows[3]);

        Assert.True(hunted.Eligible);
        Assert.Same(hunted, registry.ClaimNearest(At(150, 150), ClusterKind.Hunted));
    }

    [Fact]
    public void Fewer_than_four_cow_clusters_never_opens_a_hunted_cluster()
    {
        var registry = new ClusterRegistry();
        var hunted = Register(registry, At(150, 150), ClusterKind.Hunted);
        for (var i = 0; i < 3; i++)
        {
            registry.Release(Register(registry, At(100 + (i * 100), 100), ClusterKind.Cow));
        }

        Assert.False(hunted.Eligible);
        Assert.Equal(0, registry.EligibleHuntedCount());
    }

    [Fact]
    public void Only_the_four_nearest_cow_clusters_count_not_all_of_them()
    {
        var (registry, cows, hunted) = WithFourCowsAroundOneHunted();
        // A fifth cow cluster far away must not hold the hunted cluster shut.
        Register(registry, At(2000, 2000), ClusterKind.Cow);

        foreach (var cow in cows)
        {
            registry.Release(cow);
        }

        Assert.True(hunted.Eligible);
        Assert.False(registry.AllDone(ClusterKind.Cow));
    }

    [Fact]
    public void Eligibility_is_sticky_once_granted()
    {
        var (registry, cows, hunted) = WithFourCowsAroundOneHunted();
        foreach (var cow in cows)
        {
            registry.Release(cow);
        }

        Assert.True(hunted.Eligible);

        // A newly discovered, undone cow cluster right next to it would otherwise displace one of
        // the four and shut the cluster again halfway through the approach.
        Register(registry, At(155, 190), ClusterKind.Cow);

        Assert.True(hunted.Eligible);
    }

    [Fact]
    public void A_hunted_cluster_found_after_the_cows_are_done_opens_immediately()
    {
        var registry = new ClusterRegistry();
        for (var i = 0; i < 4; i++)
        {
            registry.Release(Register(registry, At(100 + (i * 100), 100), ClusterKind.Cow));
        }

        var hunted = Register(registry, At(250, 100), ClusterKind.Hunted);

        Assert.True(hunted.Eligible);
    }

    [Fact]
    public void A_cluster_is_only_cleared_once_every_member_is_dead()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Hunted);
        registry.AddMember(cluster);
        registry.AddMember(cluster);

        Assert.False(registry.IsCleared(cluster));

        registry.RemoveMember(cluster.Id, killed: true);
        Assert.False(registry.IsCleared(cluster));

        registry.RemoveMember(cluster.Id, killed: true);
        Assert.True(registry.IsCleared(cluster));
    }

    [Fact]
    public void A_pack_that_chases_the_party_keeps_its_own_cluster_open()
    {
        // The whole point of counting members by entity: the monsters walk away from the spot they
        // were first seen at, and the cluster must not read as cleared because that spot is empty.
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Hunted);
        registry.AddMember(cluster);

        Assert.False(registry.IsCleared(cluster));
    }

    [Fact]
    public void Members_seen_later_join_the_cluster_that_already_covers_them()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Hunted);
        registry.AddMember(cluster);

        var same = registry.FindOrRegister(At(120, 100), ClusterKind.Hunted, out var created);

        Assert.False(created);
        Assert.Same(cluster, same);

        registry.AddMember(same);
        registry.RemoveMember(cluster.Id, killed: true);

        Assert.False(registry.IsCleared(cluster));
    }

    [Fact]
    public void Removing_more_members_than_were_added_does_not_go_negative()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Hunted);
        registry.AddMember(cluster);

        registry.RemoveMember(cluster.Id, killed: true);
        registry.RemoveMember(cluster.Id, killed: true);

        Assert.True(registry.IsCleared(cluster));
        Assert.Equal(0, cluster.AliveMembers);
    }

    [Fact]
    public void An_empty_registry_has_discovered_nothing()
    {
        var registry = new ClusterRegistry();

        Assert.False(registry.AnyDiscovered);
        Assert.True(registry.AllDone(ClusterKind.Cow));

        Register(registry, At(100, 100), ClusterKind.Cow);

        Assert.True(registry.AnyDiscovered);
        Assert.False(registry.AllDone(ClusterKind.Cow));
    }

    [Fact]
    public void A_cluster_whose_monsters_went_out_of_sight_is_still_worth_claiming()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Cow);
        registry.AddMember(cluster);
        registry.AddMember(cluster);

        // Out of sight, not dead. Monsters are dropped from their cluster when they leave view and
        // added back when they return, so a pack nobody has walked up to yet reads as empty.
        registry.RemoveMember(cluster.Id, killed: false);
        registry.RemoveMember(cluster.Id, killed: false);

        Assert.NotNull(registry.ClaimNearest(At(100, 100), ClusterKind.Cow));
    }

    [Fact]
    public void A_cluster_whose_monsters_were_all_killed_is_not_claimed_again()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Cow);
        registry.AddMember(cluster);
        registry.AddMember(cluster);

        registry.RemoveMember(cluster.Id, killed: true);
        registry.RemoveMember(cluster.Id, killed: true);

        // Walking to a pack something else already killed was half of the party's trips.
        Assert.Null(registry.ClaimNearest(At(100, 100), ClusterKind.Cow));
    }

    [Fact]
    public void Clusters_left_behind_are_collected_nearest_first_not_earliest_first()
    {
        var registry = new ClusterRegistry();
        var sweep = new List<Point>();
        for (var x = 0; x < 400; x += 40)
        {
            sweep.Add(At(x, 100));
        }

        // Two left behind: one at the very start of the route, one just behind the party.
        var farBack = Register(registry, At(10, 100), ClusterKind.Cow);
        registry.AddMember(farBack);
        var justBehind = Register(registry, At(290, 100), ClusterKind.Cow);
        registry.AddMember(justBehind);
        registry.SetSweep(sweep);

        // The party is at the far end with nothing ahead of it.
        var claimed = registry.ClaimNearest(At(340, 100), ClusterKind.Cow, fromSweepIndex: 8);

        // Taking the earliest instead walked the whole width of the level, through everything alive
        // on the way, and then swept forward again over ground already covered.
        Assert.Same(justBehind, claimed);
    }

    [Fact]
    public void A_cluster_whose_members_were_all_killed_counts_as_done()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100), ClusterKind.Cow);
        registry.AddMember(cluster);
        registry.RemoveMember(cluster.Id, killed: true);

        registry.ClaimNearest(At(100, 100), ClusterKind.Cow);

        // Not merely unclaimable: left pending it blocks AllDone for the rest of the game, and the
        // killers stand still with nothing to claim.
        Assert.True(registry.AllDone(ClusterKind.Cow));
    }
}
