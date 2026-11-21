using ConsoleBot.Bots.Types.Cows;
using D2NG.Core.D2GS.Objects;

namespace ConsoleBot.Tests;

public class MonsterTrackingTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    [Fact]
    public void A_corpse_assign_is_not_a_live_monster()
    {
        Assert.Equal(AssignOutcome.Ignore, MonsterTracking.OnAssign(false, false, tracked: false, lifePercentage: 0));
    }

    [Fact]
    public void A_tracked_monster_assigned_as_a_corpse_is_dead()
    {
        Assert.Equal(AssignOutcome.RemoveDead, MonsterTracking.OnAssign(false, false, tracked: true, lifePercentage: 0));
    }

    [Fact]
    public void A_tracked_monster_assigned_again_moves_to_where_it_now_stands()
    {
        Assert.Equal(AssignOutcome.Move, MonsterTracking.OnAssign(false, false, tracked: true, lifePercentage: 80));
    }

    [Fact]
    public void A_new_live_monster_is_tracked_and_known_dead_or_friendly_ones_are_not()
    {
        Assert.Equal(AssignOutcome.Track, MonsterTracking.OnAssign(false, false, tracked: false, lifePercentage: 100));
        Assert.Equal(AssignOutcome.Ignore, MonsterTracking.OnAssign(true, false, tracked: false, lifePercentage: 100));
        Assert.Equal(AssignOutcome.Ignore, MonsterTracking.OnAssign(false, true, tracked: false, lifePercentage: 100));
    }

    [Fact]
    public void Prune_drops_the_dead_as_well_as_the_unseen()
    {
        Assert.Equal(PruneOutcome.OutOfSight, MonsterTracking.OnPrune(false, EntityState.Alive));
        Assert.Equal(PruneOutcome.Dead, MonsterTracking.OnPrune(true, EntityState.Dead));
        Assert.Equal(PruneOutcome.Dead, MonsterTracking.OnPrune(true, EntityState.Dieing));
        Assert.Equal(PruneOutcome.Keep, MonsterTracking.OnPrune(true, EntityState.BeingHit));
    }

    [Fact]
    public void The_freshest_sighting_wins()
    {
        var position = MonsterTracking.Freshest([(At(10, 10), TimeSpan.FromSeconds(2)), (At(20, 20), TimeSpan.FromMilliseconds(100))]);

        Assert.Equal(At(20, 20), position);
        Assert.Null(MonsterTracking.Freshest([]));
    }

    [Fact]
    public void A_cluster_follows_its_live_members()
    {
        var registry = new ClusterRegistry();
        var cluster = registry.FindOrRegister(At(100, 100), out _);

        registry.Follow([(cluster.Id, At(120, 110)), (cluster.Id, At(130, 120))]);

        Assert.Equal(At(125, 115), cluster.Location);
        Assert.Equal(At(100, 100), cluster.FirstSeen);
    }

    [Fact]
    public void A_cluster_with_no_live_member_tracked_goes_back_to_where_it_was_first_seen()
    {
        var registry = new ClusterRegistry();
        var cluster = registry.FindOrRegister(At(100, 100), out _);
        registry.Follow([(cluster.Id, At(140, 100))]);

        registry.Follow([]);

        Assert.Equal(At(100, 100), cluster.Location);
    }

    [Fact]
    public void A_monster_seen_near_where_its_pack_moved_to_joins_that_pack()
    {
        var registry = new ClusterRegistry();
        var cluster = registry.FindOrRegister(At(100, 100), out _);
        registry.Follow([(cluster.Id, At(150, 100))]);

        var joined = registry.FindOrRegister(At(170, 100), out var created);

        Assert.False(created);
        Assert.Same(cluster, joined);
    }
}
