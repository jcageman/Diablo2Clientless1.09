using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class TeleportSpotTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    private static IEnumerable<NearbyCow> Pack(uint id, int x, int y)
        => [new(At(x - 2, y - 2), id), new(At(x + 2, y + 2), id), new(At(x - 2, y + 2), id), new(At(x + 2, y - 2), id)];

    private static SpotSearch Search(Point from, IEnumerable<NearbyCow> cows) => new()
    {
        From = from,
        Cows = cows.ToList(),
        Radius = 18,
        Clearance = 8,
    };

    [Fact]
    public void A_spot_between_two_packs_beats_the_near_side_of_one()
    {
        var choice = TeleportSpot.Choose(Search(At(60, 100), Pack(1, 100, 100).Concat(Pack(2, 130, 100))));

        Assert.NotNull(choice);
        Assert.Equal(8, choice.CowsInRange);
        Assert.Equal(2, choice.PacksInRange);
        Assert.InRange(choice.Spot.X, 104, 126);
    }

    [Fact]
    public void The_nearest_cow_is_never_inside_striking_reach_on_landing()
    {
        var choice = TeleportSpot.Choose(Search(At(70, 100), Pack(1, 100, 100)));

        Assert.NotNull(choice);
        Assert.Equal(4, choice.CowsInRange);
        Assert.True(choice.Nearest >= 8);
    }

    [Fact]
    public void Of_equally_good_spots_the_shorter_hop_wins()
    {
        var choice = TeleportSpot.Choose(Search(At(70, 100), Pack(1, 100, 100)));

        Assert.NotNull(choice);
        Assert.True(choice.Hop <= TeleportSpot.MaxHop);
        Assert.True(choice.Spot.X < 100);
    }

    [Fact]
    public void Walls_and_blocked_sight_rule_spots_out()
    {
        var search = Search(At(70, 100), Pack(1, 100, 100)) with
        {
            Walkable = point => point.Y >= 100,
            InSight = (from, _) => from.X >= 100,
        };

        var choice = TeleportSpot.Choose(search);

        Assert.NotNull(choice);
        Assert.True(choice.Spot.Y >= 100);
        Assert.True(choice.Spot.X >= 100);
    }

    [Fact]
    public void A_spot_beside_a_lightning_enchanted_cow_is_rejected_however_well_it_scores()
    {
        // Without the danger the midpoint between the packs wins; with a lightning enchanted cow there
        // nothing within its distance is taken, so only the far side of one pack is left.
        var cows = Pack(1, 100, 100).Concat(Pack(2, 130, 100)).ToList();
        var search = Search(At(60, 100), cows) with { Dangers = [At(130, 100)] };

        var choice = TeleportSpot.Choose(search);

        Assert.NotNull(choice);
        Assert.True(choice.Spot.Distance(At(130, 100)) >= TeleportSpot.DangerDistance);
        Assert.Equal(1, choice.PacksInRange);
    }

    [Fact]
    public void A_monster_she_cannot_hurt_still_counts_for_the_clearance()
    {
        var search = Search(At(70, 100), Pack(1, 100, 100)) with { Monsters = [At(88, 100)] };

        var choice = TeleportSpot.Choose(search);

        Assert.NotNull(choice);
        Assert.True(choice.Spot.Distance(At(88, 100)) >= 8);
    }

    [Fact]
    public void A_big_pack_is_given_more_room_than_a_small_one()
    {
        var big = new List<NearbyCow>();
        for (var x = 0; x < 7; x++)
        {
            for (var y = 0; y < 7; y++)
            {
                if (big.Count < 46)
                {
                    big.Add(new NearbyCow(At(97 + x, 97 + y), 1));
                }
            }
        }

        var choice = TeleportSpot.Choose(Search(At(60, 100), big) with { Radius = 25 });

        Assert.NotNull(choice);
        // Ten units in front of a 46-cow wave was not room for one cast.
        Assert.True(choice.Nearest >= 12);
        Assert.Equal(12.6, TeleportSpot.WaveClearance(8, 46), 1);
        Assert.Equal(13, TeleportSpot.WaveClearance(8, 200));
    }

    private static List<NearbyCow> Spread(int count, int x0, int y0, int step)
    {
        var cows = new List<NearbyCow>();
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        for (var i = 0; i < count; i++)
        {
            cows.Add(new NearbyCow(At(x0 + ((i % side) * step), y0 + ((i / side) * step)), 1));
        }

        return cows;
    }

    [Fact]
    public void A_spread_pack_still_gets_a_spot_at_static_range()
    {
        // Forty cows over thirty units: no ring spot clears the full wave clearance, so the base one is used.
        var choice = TeleportSpot.Choose(Search(At(40, 100), Spread(40, 85, 85, 5)));

        Assert.NotNull(choice);
        Assert.True(choice.CowsInRange > 0);
        Assert.True(choice.Nearest >= 8);
    }

    [Fact]
    public void Going_in_for_nova_on_a_big_pack_never_lands_beside_nobody()
    {
        var choice = TeleportSpot.Choose(Search(At(60, 100), Spread(25, 96, 96, 2)) with { Radius = 10 });

        Assert.NotNull(choice);
        Assert.True(choice.CowsInRange > 0);
    }

    [Fact]
    public void An_escape_asks_only_for_the_base_clearance()
    {
        var search = Search(At(100, 100), Spread(46, 94, 94, 2)) with { WaveSized = false };

        var choice = TeleportSpot.Choose(search);

        Assert.NotNull(choice);
        Assert.True(choice.Nearest >= 8);
        Assert.True(choice.CowsInRange > 0);
    }

    [Fact]
    public void An_escape_gets_clear_of_the_wave_and_away_from_it()
    {
        // She stands at the front of a pack coming from the east.
        var wave = Spread(30, 102, 94, 3);
        var search = Search(At(100, 100), wave) with { Clearance = 13.7, WaveSized = false };

        var choice = TeleportSpot.ChooseEscape(search, At(104, 100));

        Assert.NotNull(choice);
        Assert.True(choice.Nearest >= 13.7);
        Assert.InRange(choice.Hop, TeleportSpot.MinHop, TeleportSpot.MaxHop);
        Assert.True(choice.Spot.Distance(At(104, 100)) > At(100, 100).Distance(At(104, 100)));
    }

    [Fact]
    public void An_escape_never_lands_near_a_danger()
    {
        var wave = Spread(30, 102, 94, 3);
        var search = Search(At(100, 100), wave) with { Clearance = 13.7, Dangers = [At(80, 100)] };

        var choice = TeleportSpot.ChooseEscape(search, At(104, 100));

        Assert.True(choice == null || choice.Spot.Distance(At(80, 100)) >= TeleportSpot.DangerDistance);
    }

    [Fact]
    public void A_long_hop_lands_fifteen_to_thirty_away_with_nothing_near()
    {
        var wave = Spread(30, 102, 94, 3);
        var choice = TeleportSpot.ChooseLongHop(Search(At(100, 100), wave), At(104, 100));

        Assert.NotNull(choice);
        Assert.InRange(choice.Hop, TeleportSpot.LongHopMin, TeleportSpot.MaxHop);
        Assert.True(choice.Nearest >= TeleportSpot.LongHopClearance);
    }

    [Fact]
    public void Hemmed_in_she_still_gets_the_least_bad_spot()
    {
        // A wave on her and packs on every side: no spot clears 13.7 or offers a long hop.
        var around = new List<NearbyCow>();
        for (var angle = 0; angle < 360; angle += 15)
        {
            for (var radius = 6; radius <= 30; radius += 8)
            {
                around.Add(new NearbyCow(At(100 + (int)(radius * Math.Cos(angle * Math.PI / 180)), 100 + (int)(radius * Math.Sin(angle * Math.PI / 180))), (uint)(radius / 8)));
            }
        }

        var search = Search(At(100, 100), around) with { Clearance = 13.7 };

        Assert.Null(TeleportSpot.ChooseEscape(search, At(100, 100)));
        Assert.Null(TeleportSpot.ChooseLongHop(search, At(100, 100)));

        var choice = TeleportSpot.ChooseLeastBad(search);

        Assert.NotNull(choice);
        Assert.InRange(choice.Hop, TeleportSpot.MinHop, TeleportSpot.MaxHop);
        Assert.True(choice.Nearest > 0);
    }

    [Fact]
    public void A_danger_counts_against_a_least_bad_spot_but_does_not_rule_it_out()
    {
        var search = Search(At(100, 100), Spread(9, 96, 96, 4)) with { Dangers = [At(70, 100), At(130, 100), At(100, 70), At(100, 130)] };

        Assert.NotNull(TeleportSpot.ChooseLeastBad(search));
    }

    [Fact]
    public void A_long_hop_never_goes_past_one_teleport()
    {
        var choice = TeleportSpot.ChooseLongHop(Search(At(100, 100), Spread(30, 102, 94, 3)), At(104, 100));

        Assert.NotNull(choice);
        Assert.True(choice.Hop <= TeleportSpot.MaxHop);
    }

    [Fact]
    public void No_spot_in_sight_means_no_choice()
    {
        var search = Search(At(70, 100), Pack(1, 100, 100)) with { InSight = (_, _) => false };

        Assert.Null(TeleportSpot.Choose(search));
        Assert.Null(TeleportSpot.Choose(Search(At(70, 100), [])));
    }

    [Fact]
    public void The_clearance_leaves_room_for_the_teleport_and_one_cast()
    {
        Assert.Equal(7.86, TeleportSpot.Clearance(TimeSpan.FromMilliseconds(500)), 2);
    }
}
