using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class ClusterRegistryTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    private static MonsterCluster Register(ClusterRegistry registry, Point location)
        => registry.FindOrRegister(location, out _);

    private static bool CreatedNew(ClusterRegistry registry, Point location)
    {
        registry.FindOrRegister(location, out var created);
        return created;
    }

    [Fact]
    public void A_monster_inside_an_existing_cluster_does_not_start_a_new_one()
    {
        var registry = new ClusterRegistry();

        Assert.True(CreatedNew(registry, At(100, 100)));
        Assert.False(CreatedNew(registry, At(120, 100)));
        Assert.True(CreatedNew(registry, At(140, 100)));

        Assert.Equal(2, registry.Count());
    }

    [Fact]
    public void A_pack_the_claimant_cannot_hurt_is_left_for_someone_who_can()
    {
        var registry = new ClusterRegistry();
        var immune = Register(registry, At(100, 100));
        var killable = Register(registry, At(400, 100));

        // The nearer cluster is the one this claimant cannot damage, so it takes the far one.
        var claimed = registry.ClaimNearest(At(110, 100), canWork: cluster => cluster != immune);
        Assert.Same(killable, claimed);

        // And the one it skipped is still on offer, rather than having been retired behind it.
        Assert.Same(immune, registry.ClaimNearest(At(110, 100)));
    }

    [Fact]
    public void ClaimNearest_hands_out_the_closest_pending_cluster_once()
    {
        var registry = new ClusterRegistry();
        Register(registry, At(100, 100));
        var near = Register(registry, At(400, 100));

        var claimed = registry.ClaimNearest(At(390, 100));

        Assert.Same(near, claimed);
        Assert.NotSame(near, registry.ClaimNearest(At(390, 100)));
    }

    [Fact]
    public void Release_marks_a_cluster_done_whatever_the_outcome()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));

        registry.Release(registry.ClaimNearest(At(100, 100)));

        Assert.True(cluster.Done);
        Assert.True(registry.AllDone());
        Assert.Equal(1, registry.DoneCount());
    }

    [Fact]
    public void GiveUp_returns_a_cluster_to_the_pool_without_finishing_it()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));

        registry.GiveUp(registry.ClaimNearest(At(100, 100)));

        Assert.False(cluster.Done);
        Assert.False(registry.AllDone());
        Assert.Same(cluster, registry.ClaimNearest(At(100, 100)));
    }

    [Fact]
    public void A_client_with_nothing_pending_falls_back_to_a_cluster_someone_else_is_on()
    {
        var registry = new ClusterRegistry();
        var only = Register(registry, At(100, 100));
        registry.ClaimNearest(At(100, 100));

        Assert.Same(only, registry.ClaimNearest(At(100, 100)));
    }

    [Fact]
    public void A_cluster_is_only_cleared_once_every_member_is_dead()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));
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
        var cluster = Register(registry, At(100, 100));
        registry.AddMember(cluster);

        Assert.False(registry.IsCleared(cluster));
    }

    [Fact]
    public void Members_seen_later_join_the_cluster_that_already_covers_them()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));
        registry.AddMember(cluster);

        var same = registry.FindOrRegister(At(120, 100), out var created);

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
        var cluster = Register(registry, At(100, 100));
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
        Assert.True(registry.AllDone());

        Register(registry, At(100, 100));

        Assert.True(registry.AnyDiscovered);
        Assert.False(registry.AllDone());
    }

    [Fact]
    public void A_cluster_whose_monsters_went_out_of_sight_is_still_worth_claiming()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));
        registry.AddMember(cluster);
        registry.AddMember(cluster);

        // Out of sight, not dead. Monsters are dropped from their cluster when they leave view and
        // added back when they return, so a pack nobody has walked up to yet reads as empty.
        registry.RemoveMember(cluster.Id, killed: false);
        registry.RemoveMember(cluster.Id, killed: false);

        Assert.NotNull(registry.ClaimNearest(At(100, 100)));
    }

    [Fact]
    public void A_cluster_whose_monsters_were_all_killed_is_not_claimed_again()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));
        registry.AddMember(cluster);
        registry.AddMember(cluster);

        registry.RemoveMember(cluster.Id, killed: true);
        registry.RemoveMember(cluster.Id, killed: true);

        // Walking to a pack something else already killed was half of the party's trips.
        Assert.Null(registry.ClaimNearest(At(100, 100)));
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
        var farBack = Register(registry, At(10, 100));
        registry.AddMember(farBack);
        var justBehind = Register(registry, At(290, 100));
        registry.AddMember(justBehind);
        registry.SetSweep(sweep);

        // The party is at the far end with nothing ahead of it.
        var claimed = registry.ClaimNearest(At(340, 100), fromSweepIndex: 8);

        // Taking the earliest instead walked the whole width of the level, through everything alive
        // on the way, and then swept forward again over ground already covered.
        Assert.Same(justBehind, claimed);
    }

    [Fact]
    public void A_cluster_whose_members_were_all_killed_counts_as_done()
    {
        var registry = new ClusterRegistry();
        var cluster = Register(registry, At(100, 100));
        registry.AddMember(cluster);
        registry.RemoveMember(cluster.Id, killed: true);

        registry.ClaimNearest(At(100, 100));

        // Not merely unclaimable: left pending it blocks AllDone for the rest of the game, and the
        // killers stand still with nothing to claim.
        Assert.True(registry.AllDone());
    }
}
