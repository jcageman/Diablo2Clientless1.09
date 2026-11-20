using ConsoleBot.Bots.Types.Cows;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;

namespace ConsoleBot.Tests;

public class KitingTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    private static CloseCow Plain(double distance) => new(distance);

    private static CloseCow Champion(double distance) => new(distance, Enchanted: true);

    private static CloseCow Lightning(double distance) => new(distance, Enchanted: true, LightningEnchanted: true);

    [Fact]
    public void Ranged_and_caster_walkers_and_thin_ones_kite()
    {
        Assert.True(Kiting.IsWeakWalker(CharacterClass.Amazon, 3000, whirls: false));
        Assert.True(Kiting.IsWeakWalker(CharacterClass.Necromancer, 3000, whirls: false));
        Assert.True(Kiting.IsWeakWalker(CharacterClass.Paladin, 1400, whirls: false));
        Assert.False(Kiting.IsWeakWalker(CharacterClass.Paladin, 2400, whirls: false));
    }

    [Fact]
    public void Sorceresses_and_whirlwind_barbarians_do_not()
    {
        Assert.False(Kiting.IsWeakWalker(CharacterClass.Sorceress, 900, whirls: false));
        Assert.False(Kiting.IsWeakWalker(CharacterClass.Barbarian, 1200, whirls: true));
        Assert.True(Kiting.IsWeakWalker(CharacterClass.Barbarian, 1200, whirls: false));
    }

    [Fact]
    public void One_plain_cow_close_is_attacked_head_on()
    {
        Assert.False(Kiting.ShouldStepBack([Plain(2)]));
        Assert.False(Kiting.ShouldStepBack([Plain(2), Plain(9), Plain(11)]));
    }

    [Fact]
    public void Two_plain_cows_close_make_it_step_back()
    {
        Assert.True(Kiting.ShouldStepBack([Plain(3), Plain(4.5)]));
    }

    [Fact]
    public void One_enchanted_cow_close_makes_it_step_back_alone()
    {
        Assert.True(Kiting.ShouldStepBack([Champion(4)]));
        Assert.False(Kiting.ShouldStepBack([Champion(7)]));
    }

    [Fact]
    public void Anything_lightning_enchanted_within_its_bolts_makes_it_step_back()
    {
        Assert.True(Kiting.ShouldStepBack([Lightning(12)]));
        Assert.True(Kiting.TooCloseToLightning([Lightning(12)]));
        Assert.False(Kiting.ShouldStepBack([Lightning(16)]));
    }

    [Fact]
    public void The_amazon_death_situation_steps_back_away_from_the_lightning_enchanted_boss()
    {
        // 03:20.5 of the dead amazon's game: the King, lightning enchanted, at 5.1 to her west, plain
        // cows at 4.2 and 6.7, nothing east for 29 units.
        var here = At(100, 100);
        var boss = At(95, 101);
        var cows = new[] { boss, At(96, 98), At(94, 105) };

        Assert.True(Kiting.ShouldStepBack([Lightning(5.1), Plain(4.2), Plain(6.7)]));

        var step = Kiting.ChooseStep(new StepSearch { From = here, Cows = cows, LightningEnchanted = [boss] });

        Assert.NotNull(step);
        Assert.True(step.Spot.X > here.X);
        Assert.True(step.Spot.Distance(boss) >= Kiting.LightningEnchantedDistance);
    }

    [Fact]
    public void A_short_step_goes_away_from_the_cows_and_towards_the_lead()
    {
        var cows = new[] { At(104, 100), At(104, 103), At(105, 98) };
        var step = Kiting.ChooseStep(new StepSearch { From = At(100, 100), Cows = cows, Lead = At(80, 100) });

        Assert.NotNull(step);
        Assert.False(step.Long);
        Assert.InRange(step.Hop, Kiting.ShortStepRadii.Min() - 0.5, Kiting.ShortStepRadii.Max() + 0.5);
        Assert.True(step.Spot.X < 100);
        Assert.True(step.Nearest > Kiting.NearestDistance(At(100, 100), cows));
    }

    [Fact]
    public void A_step_never_walks_past_a_cow()
    {
        // Cows close on the east, and one standing just west of it on the way out that way.
        var cows = new[] { At(103, 100), At(103, 102), At(96, 100) };
        var step = Kiting.ChooseStep(new StepSearch { From = At(100, 100), Cows = cows });

        Assert.NotNull(step);
        Assert.True(step.Spot.Distance(At(96, 100)) > 2);
        Assert.True(step.Spot.Y != 100 || step.Spot.X > 96);
    }

    [Fact]
    public void Hemmed_in_close_it_takes_the_longer_walk_out_or_none()
    {
        var cows = new List<Point>();
        for (var angle = 0; angle < 360; angle += 30)
        {
            cows.Add(At(100 + (int)Math.Round(4 * Math.Cos(angle * Math.PI / 180)), 100 + (int)Math.Round(4 * Math.Sin(angle * Math.PI / 180))));
        }

        var walls = new Func<Point, bool>(point => point.Distance(At(100, 100)) >= 7);
        var step = Kiting.ChooseStep(new StepSearch { From = At(100, 100), Cows = cows, Walkable = walls });

        Assert.True(step == null || step.Long);
    }

    [Fact]
    public void Only_spots_in_sight_of_a_cow_are_taken()
    {
        var cows = new[] { At(104, 100), At(104, 103) };
        var step = Kiting.ChooseStep(new StepSearch { From = At(100, 100), Cows = cows, InSight = (_, _) => false });

        Assert.Null(step);
    }

    [Fact]
    public void With_a_ledge_behind_it_the_step_keeps_off_the_ledge_and_keeps_room()
    {
        var cows = new[] { At(103, 100), At(103, 103), At(103, 97), At(106, 100) };
        var search = new StepSearch { From = At(100, 100), Cows = cows, Walkable = point => point.X > 92 };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.False(step.DeadEnd);
        Assert.True(step.Spot.X >= 96, $"stepped to {step.Spot.X} against the ledge at 92");
        Assert.True(Kiting.Room(search, step.Spot) >= 2, $"stepped to {step.Spot} with one way on");
        Assert.Equal(0, Kiting.Room(search, At(94, 100)));
        Assert.Equal(1, Kiting.Room(search, At(94, 102)));
    }

    [Fact]
    public void The_amazon_in_a_pack_beside_a_ledge_is_told_her_only_step_is_a_dead_end()
    {
        // 00:27.6 of the game she left surrounded at full life: landed in a pack, the ledge eight to her
        // west, the party east, a portal four away. She stepped six west and stood two from the ledge, cows
        // on three sides. That step is still the only one that gains distance; flagged a dead end, the
        // portal goes first.
        var cows = new[]
        {
            At(102, 102), At(100, 97), At(103, 99), At(104, 100), At(101, 104), At(103, 103), At(104, 102), At(98, 96),
            At(97, 104), At(100, 105), At(99, 95), At(101, 95), At(98, 105), At(106, 101), At(99, 106), At(101, 106),
            At(102, 94), At(105, 96), At(97, 106), At(105, 95), At(102, 93), At(107, 103), At(102, 92), At(104, 92),
            At(101, 109), At(109, 98), At(98, 109), At(102, 109), At(95, 108), At(105, 108), At(101, 90), At(98, 110), At(96, 110),
        };
        var search = new StepSearch { From = At(100, 100), Cows = cows, Lead = At(107, 96), Walkable = point => point.X > 92 };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.True(step.DeadEnd || step.Spot.X >= 96, $"stepped to {step.Spot.X} against the ledge at 92 without a dead end flag");
        Assert.Equal(0, Kiting.Room(search, At(94, 100)));
        Assert.True(PortalRetreat.ShouldRetreat(weakWalkerInDanger: true, belowRetreatLine: false, hostileNear: true, portalDistance: 4.5));
    }

    [Fact]
    public void In_a_corridor_the_step_goes_along_it()
    {
        var cows = new[] { At(104, 100), At(105, 101), At(104, 98) };
        var search = new StepSearch { From = At(100, 100), Cows = cows, Walkable = point => point.Y is >= 98 and <= 102 };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.True(step.Spot.X < 100);
        Assert.InRange(step.Spot.Y, 98, 102);
    }

    [Fact]
    public void When_every_step_ends_in_a_dead_end_it_still_takes_the_best_one()
    {
        // A corridor closed four behind her: each step back gains distance and leaves none to gain after.
        var cows = new[] { At(104, 100), At(105, 101), At(104, 98) };
        var search = new StepSearch { From = At(100, 100), Cows = cows, Walkable = point => point.Y is >= 98 and <= 102 && point.X >= 96 };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.True(step.DeadEnd);
        Assert.Equal(0, Kiting.Room(search, step.Spot));
        Assert.True(step.Nearest > Kiting.NearestDistance(At(100, 100), cows));
    }

    [Fact]
    public void A_dead_end_is_taken_only_when_no_step_has_room()
    {
        var cows = new[] { At(104, 100), At(105, 101), At(104, 98) };
        var search = new StepSearch { From = At(100, 100), Cows = cows, Walkable = point => point.X >= 96 || point.Y > 101 };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.False(step.DeadEnd);
        Assert.True(Kiting.Room(search, step.Spot) > 0);
    }

    [Fact]
    public void Open_ground_between_cows_thirteen_away_on_two_sides_has_room()
    {
        var search = new StepSearch { From = At(100, 100), Cows = [At(87, 100), At(113, 100)] };

        Assert.True(Kiting.Room(search, At(100, 100)) > 0);
    }

    [Fact]
    public void A_step_towards_the_party_with_room_beats_open_ground_away_from_it()
    {
        // Cows to the north, the lead east beyond a wall five units on: steps towards him leave two ways
        // on, steps south and west five.
        var cows = new[] { At(99, 96), At(100, 96), At(101, 96) };
        var lead = At(130, 100);
        var search = new StepSearch { From = At(100, 100), Cows = cows, Lead = lead, Walkable = point => point.X <= 105 };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.True(Kiting.Room(search, step.Spot) >= 2);
        Assert.True(step.Spot.Distance(lead) < At(100, 100).Distance(lead), $"stepped to {step.Spot}, away from the lead");
    }

    [Fact]
    public void A_lightning_enchanted_cow_at_two_on_open_ground_gets_a_step_not_a_leave()
    {
        var lightning = At(102, 100);
        var search = new StepSearch { From = At(100, 100), Cows = [lightning], LightningEnchanted = [lightning] };

        var step = Kiting.ChooseStep(search);

        Assert.NotNull(step);
        Assert.True(step.Spot.Distance(lightning) > 3);
        Assert.False(Kiting.Surrounded([Lightning(2)], Kiting.HasWayOut(search)));
    }

    [Fact]
    public void With_another_cow_beside_it_there_is_still_a_way_out_on_open_ground()
    {
        var lightning = At(102, 100);
        var search = new StepSearch { From = At(100, 100), Cows = [lightning, At(100, 103)], LightningEnchanted = [lightning] };

        Assert.NotNull(Kiting.ChooseStep(search));
        Assert.True(Kiting.HasWayOut(search));
        Assert.False(Kiting.Surrounded([Lightning(2), Plain(3)], Kiting.HasWayOut(search)));
    }

    [Fact]
    public void A_way_out_does_not_need_sight_of_a_cow()
    {
        var search = new StepSearch { From = At(100, 100), Cows = [At(104, 100), At(104, 103)], InSight = (_, _) => false };

        Assert.Null(Kiting.ChooseStep(search));
        Assert.True(Kiting.HasWayOut(search));
    }

    [Fact]
    public void A_way_out_lands_clear_of_the_pack_and_no_nearer_the_lightning_enchanted_whatever_the_lead()
    {
        // Three cows on her, a lightning enchanted one ten to the east and the lead beyond it.
        var lightning = At(110, 100);
        var cows = new[] { At(102, 101), At(101, 98), At(98, 102), lightning };
        var search = new StepSearch { From = At(100, 100), Cows = cows, LightningEnchanted = [lightning], Lead = At(125, 100) };

        var wayOut = Kiting.WayOut(search);

        Assert.NotNull(wayOut);
        Assert.False(wayOut.Long);
        Assert.True(Kiting.NearestDistance(wayOut.Spot, cows) > Kiting.CrowdRadius, $"landed at {wayOut.Spot} beside a cow");
        Assert.True(wayOut.Spot.Distance(lightning) >= At(100, 100).Distance(lightning), $"landed at {wayOut.Spot}, nearer the lightning enchanted");
    }

    [Fact]
    public void Walled_in_there_is_no_way_out()
    {
        var search = new StepSearch { From = At(100, 100), Cows = [At(102, 100), At(100, 102), At(98, 100)], Walkable = point => point.Distance(At(100, 100)) < 1 };

        Assert.False(Kiting.HasWayOut(search));
        Assert.True(Kiting.Surrounded([Plain(2), Plain(2), Plain(2)], Kiting.HasWayOut(search)));
    }

    [Fact]
    public void A_follow_stop_keeps_further_from_the_lightning_enchanted()
    {
        var lightning = At(132, 100);

        var stop = Kiting.FollowDestination(At(130, 100), At(90, 100), [lightning], [lightning]);

        Assert.True(stop.Distance(lightning) >= Kiting.LightningEnchantedDistance + Kiting.LightningEnchantedMargin);
    }

    [Fact]
    public void Three_cows_on_it_with_no_way_out_is_surrounded()
    {
        Assert.True(Kiting.Surrounded([Plain(2), Plain(3), Plain(4)], wayOut: false));
        Assert.False(Kiting.Surrounded([Plain(2), Plain(3), Plain(4)], wayOut: true));
    }

    [Fact]
    public void An_enchanted_cow_and_another_with_no_way_out_is_surrounded()
    {
        Assert.True(Kiting.Surrounded([Champion(2), Plain(3)], wayOut: false));
        Assert.False(Kiting.Surrounded([Champion(2)], wayOut: false));
    }

    [Fact]
    public void One_lightning_enchanted_cow_alone_with_no_way_out_is_fought_not_left()
    {
        // Walled in with it on her: she shoots it, now that nothing is left out of her targets.
        Assert.False(Kiting.Surrounded([Lightning(3)], wayOut: false));
        Assert.True(Kiting.Surrounded([Lightning(3), Plain(4)], wayOut: false));
    }

    [Fact]
    public void One_plain_cow_or_two_never_make_it_leave()
    {
        Assert.False(Kiting.Surrounded([Plain(1)], wayOut: false));
        Assert.False(Kiting.Surrounded([Plain(1), Plain(2)], wayOut: false));
        Assert.False(Kiting.Surrounded([Plain(1), Plain(2), Plain(9)], wayOut: false));
    }

    [Fact]
    public void A_follow_beside_a_clear_lead_ends_at_the_lead()
    {
        Assert.Equal(At(130, 100), Kiting.FollowDestination(At(130, 100), At(100, 100), [At(170, 100)]));
    }

    [Fact]
    public void A_follow_to_a_lead_among_cows_stops_at_shooting_range()
    {
        var cows = new[] { At(133, 100), At(132, 104), At(135, 97) };

        var stop = Kiting.FollowDestination(At(130, 100), At(100, 100), cows);

        Assert.True(Kiting.NearestDistance(stop, cows) >= Kiting.ShootingRange);
        Assert.True(stop.X > 100 && stop.X < 130);
    }

    [Fact]
    public void A_follow_with_no_clear_point_stays_put()
    {
        var cows = new[] { At(105, 100), At(115, 100), At(125, 100) };

        Assert.Equal(At(100, 100), Kiting.FollowDestination(At(130, 100), At(100, 100), cows));
    }

    [Fact]
    public void A_stop_keeps_the_cows_at_arms_length()
    {
        Assert.True(Kiting.KeepsDistance(At(100, 100), [At(110, 100)]));
        Assert.False(Kiting.KeepsDistance(At(100, 100), [At(105, 100)]));
    }

    [Fact]
    public void The_cow_king_is_a_super_unique_hell_bovine()
    {
        Assert.True(CowKing.IsCowKing(NPCCode.HellBovine, new HashSet<MonsterEnchantment> { MonsterEnchantment.IsSuperUnique, MonsterEnchantment.LightningEnchanted }, isBossMinion: false));
        Assert.False(CowKing.IsCowKing(NPCCode.HellBovine, new HashSet<MonsterEnchantment> { MonsterEnchantment.LightningEnchanted }, isBossMinion: false));
        // His guards are boss minions and are not him.
        Assert.False(CowKing.IsCowKing(NPCCode.HellBovine, new HashSet<MonsterEnchantment> { MonsterEnchantment.IsSuperUnique }, isBossMinion: true));
    }
}
