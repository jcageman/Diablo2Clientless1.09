using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class SorceressRotationTests
{
    private static double[] Cows(int count, double life) => Enumerable.Repeat(life, count).ToArray();

    /// <summary>A level 20 nova with level 20 lightning mastery in a five player classic game.</summary>
    private static RotationInput Classic(double[] inStatic, double[] inNova) => new()
    {
        StaticRangeLives = inStatic,
        NovaRadiusLives = inNova,
        StaticFloor = 0,
        NovaDamagePercent = SorceressRotation.NovaDamagePercent(20, 20, 5),
        Mana = 1000,
        MaxMana = 1000,
        NovaManaCost = SorceressRotation.NovaManaCost(20),
        FrostNovaManaCost = SorceressRotation.FrostNovaManaCost(1),
        Classic = true,
        HasFrostNova = true,
        PackAlive = inStatic.Length,
    };

    [Fact]
    public void Nova_damage_follows_the_skill_data_and_the_players_in_the_game()
    {
        // 1-20 lightning plus 149 by level 20: 159.5 on average, against 2171.5 life tripled for five players.
        Assert.Equal(2.45, SorceressRotation.NovaDamagePercent(20, 0, 5), 2);
        Assert.Equal(9.25, SorceressRotation.NovaDamagePercent(20, 20, 5), 2);
        Assert.Equal(0, SorceressRotation.NovaDamagePercent(0, 20, 5));
    }

    [Fact]
    public void A_fresh_pack_keeps_static()
    {
        var input = Classic(Cows(6, 100), Cows(3, 100));

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input));
    }

    [Fact]
    public void A_worn_down_pack_inside_the_nova_radius_gets_nova()
    {
        var input = Classic(Cows(6, 10), Cows(6, 10));

        Assert.Equal(RotationChoice.Nova, SorceressRotation.Decide(input));
    }

    [Fact]
    public void Few_cows_in_the_nova_radius_keep_static_on_the_many_around_it()
    {
        // Two cows at 25% inside nova; eight at 25% in static range. Nova takes 18.5, static 50.
        var input = Classic(Cows(8, 25), Cows(2, 25));

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input));
    }

    [Fact]
    public void A_pack_arriving_together_in_classic_gets_one_frost_nova_then_nova()
    {
        var arriving = Classic(Cows(6, 10), Cows(4, 10)) with { CowsWithinReach = 1, CowsInFrostNovaRadius = 4 };

        Assert.Equal(RotationChoice.FrostNova, SorceressRotation.Decide(arriving));

        var chilled = arriving with { ChilledInFrostNovaRadius = 4, SinceFrostNova = TimeSpan.FromSeconds(2), Current = RotationChoice.FrostNova };
        Assert.Equal(RotationChoice.Nova, SorceressRotation.Decide(chilled));
    }

    [Fact]
    public void Expansion_goes_static_to_nova_without_frost_nova()
    {
        var arriving = Classic(Cows(6, 10), Cows(4, 10)) with { Classic = false, CowsWithinReach = 1, CowsInFrostNovaRadius = 4 };

        Assert.Equal(RotationChoice.Nova, SorceressRotation.Decide(arriving));
    }

    [Fact]
    public void Expansion_static_stops_at_its_floor()
    {
        var input = Classic(Cows(6, 55), Cows(3, 55)) with { Classic = false, StaticFloor = 60 };

        Assert.Equal(RotationChoice.Nova, SorceressRotation.Decide(input));
        Assert.Equal(0, SorceressRotation.StaticDamage(55, 60));
        Assert.Equal(5, SorceressRotation.StaticDamage(65, 60));
    }

    [Fact]
    public void Close_but_too_few_of_the_pack_yet_waits_for_the_rest()
    {
        var input = Classic(Cows(10, 100), Cows(3, 100)) with { CowsWithinReach = 2, CowsInFrostNovaRadius = 3 };

        Assert.NotEqual(RotationChoice.FrostNova, SorceressRotation.Decide(input));
    }

    [Fact]
    public void Enough_of_the_pack_close_gets_the_frost_nova()
    {
        var input = Classic(Cows(10, 100), Cows(5, 100)) with { CowsWithinReach = 2, CowsInFrostNovaRadius = 5 };

        Assert.Equal(RotationChoice.FrostNova, SorceressRotation.Decide(input));
    }

    [Fact]
    public void One_cow_running_ahead_of_the_pack_does_not_get_it()
    {
        var input = Classic(Cows(8, 100), Cows(1, 100)) with { CowsWithinReach = 1, CowsInFrostNovaRadius = 1 };

        Assert.False(SorceressRotation.FrostNovaDue(input));
    }

    [Fact]
    public void A_pack_inside_the_radius_but_not_yet_in_reach_is_left_to_walk_in_at_full_speed()
    {
        var input = Classic(Cows(6, 100), Cows(5, 100)) with { CowsWithinReach = 0, CowsInFrostNovaRadius = 5 };

        Assert.False(SorceressRotation.FrostNovaDue(input));
    }

    [Fact]
    public void A_chilled_pack_is_not_frozen_again()
    {
        var input = Classic(Cows(6, 100), Cows(4, 100)) with { CowsWithinReach = 2, CowsInFrostNovaRadius = 4, ChilledInFrostNovaRadius = 3 };

        Assert.False(SorceressRotation.FrostNovaDue(input));
        Assert.True(SorceressRotation.FrostNovaDue(input with { ChilledInFrostNovaRadius = 1 }));
    }

    [Fact]
    public void Frost_nova_is_not_cast_twice_before_its_chill_shows()
    {
        var input = Classic(Cows(6, 100), Cows(4, 100)) with { CowsWithinReach = 2, CowsInFrostNovaRadius = 4, SinceFrostNova = TimeSpan.FromMilliseconds(500) };

        Assert.False(SorceressRotation.FrostNovaDue(input));
    }

    [Fact]
    public void A_sorceress_without_frost_nova_or_mana_for_it_and_a_nova_skips_it()
    {
        var input = Classic(Cows(6, 100), Cows(4, 100)) with { CowsWithinReach = 2, CowsInFrostNovaRadius = 4 };

        Assert.False(SorceressRotation.FrostNovaDue(input with { HasFrostNova = false }));
        Assert.False(SorceressRotation.FrostNovaDue(input with { Mana = 40 }));
    }

    [Fact]
    public void Short_of_mana_static_and_nova_are_weighed_per_point_of_mana()
    {
        // Static takes 30 for 9 mana, nova 55.6 for 34: nova per cast, static per point of mana.
        var input = Classic(Cows(6, 20), Cows(6, 20));

        Assert.Equal(RotationChoice.Nova, SorceressRotation.Decide(input));
        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input with { Mana = 200 }));
    }

    [Fact]
    public void Cows_static_no_longer_moves_and_none_in_the_nova_radius_mean_going_in()
    {
        var input = Classic(Cows(6, 5), []);

        Assert.Equal(RotationChoice.Approach, SorceressRotation.Decide(input));
    }

    [Fact]
    public void Out_of_mana_nothing_is_cast()
    {
        var input = Classic(Cows(6, 100), Cows(3, 100)) with { Mana = 5 };

        Assert.Equal(RotationChoice.Nothing, SorceressRotation.Decide(input));
    }

    [Fact]
    public void A_close_call_does_not_flap_between_static_and_nova()
    {
        // Static takes 35.2, nova 37.0: not enough to leave static, not too little to leave nova.
        var input = Classic(Cows(4, 35.2), Cows(4, 35.2)) with { NovaDamagePercent = 9.25 };

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input with { Current = RotationChoice.Static }));
        Assert.Equal(RotationChoice.Nova, SorceressRotation.Decide(input with { Current = RotationChoice.Nova }));
    }

    [Fact]
    public void A_big_wave_gets_frost_nova_once_half_of_it_is_in_the_radius()
    {
        // Capping the quorum at six cast half as many Frost Novas again for no gain; a wave already on her
        // is the escape's job.
        var wave = Classic(Cows(44, 100), Cows(25, 100)) with { PackAlive = 49, Nearest = 5, CowsWithinReach = 0, CowsInFrostNovaRadius = 25 };

        Assert.Equal(RotationChoice.FrostNova, SorceressRotation.Decide(wave));
        Assert.NotEqual(RotationChoice.FrostNova, SorceressRotation.Decide(wave with { CowsInFrostNovaRadius = 6 }));
    }

    [Fact]
    public void A_wave_still_beyond_one_cast_of_her_is_left_to_come()
    {
        var wave = Classic(Cows(44, 100), Cows(6, 100)) with { PackAlive = 49, Nearest = 9, CowsWithinReach = 0, CowsInFrostNovaRadius = 6 };

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(wave));
    }

    [Fact]
    public void A_few_cows_in_reach_of_a_big_pack_do_not_take_her_off_static()
    {
        // Turning static off whenever three stood in reach took static from 62% of her casting to 45% and
        // kills per minute fell by 7%; a wave on her is the escape's business.
        var input = Classic(Cows(44, 100), Cows(4, 100)) with { PackAlive = 49, Nearest = 2, CowsWithinReach = 3, CowsInFrostNovaRadius = 4 };

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input));
    }

    [Fact]
    public void Chilled_cows_in_reach_get_static_while_it_pays_more()
    {
        var input = Classic(Cows(20, 100), Cows(4, 100)) with
        {
            PackAlive = 20,
            Nearest = 2,
            CowsWithinReach = 3,
            ChilledWithinReach = 3,
            CowsInFrostNovaRadius = 4,
            ChilledInFrostNovaRadius = 4,
            SinceFrostNova = TimeSpan.FromSeconds(3),
        };

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input));
    }

    [Fact]
    public void In_expansion_cows_in_reach_do_not_force_a_change()
    {
        var input = Classic(Cows(20, 100), Cows(4, 100)) with { Classic = false, PackAlive = 20, Nearest = 2, CowsWithinReach = 3, CowsInFrostNovaRadius = 4 };

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input));
    }

    [Fact]
    public void A_thirteen_cow_pack_still_waits_for_enough_of_itself()
    {
        var input = Classic(Cows(13, 100), Cows(6, 100)) with { PackAlive = 13, Nearest = 6, CowsWithinReach = 0, CowsInFrostNovaRadius = 6 };

        Assert.NotEqual(RotationChoice.FrostNova, SorceressRotation.Decide(input));
        Assert.Equal(RotationChoice.FrostNova, SorceressRotation.Decide(input with { CowsInFrostNovaRadius = 7 }));
    }

    [Fact]
    public void A_wave_on_her_makes_her_escape()
    {
        // The second live chicken: 46 cows, fifteen of them within reach, the nearest 1.4 away.
        var onHer = Classic(Cows(30, 100), Cows(20, 100)) with { PackAlive = 46, Nearest = 1.4, CowsWithinReach = 15, CowsInFrostNovaRadius = 22, LifeFraction = 0.61 };

        Assert.Equal(RotationChoice.Escape, SorceressRotation.Decide(onHer));
        Assert.Equal(RotationChoice.Escape, SorceressRotation.Decide(onHer with { ChilledWithinReach = 15 }));
    }

    [Fact]
    public void Six_cows_on_her_at_the_life_she_hovers_at_are_fought()
    {
        // The second tuned run escaped thirty times a game at 80% life with six chilled cows in reach.
        var input = Classic(Cows(30, 100), Cows(10, 100)) with { PackAlive = 30, Nearest = 1, CowsWithinReach = 6, ChilledWithinReach = 6, LifeFraction = 0.8 };

        Assert.False(SorceressRotation.ShouldEscape(input));
        Assert.True(SorceressRotation.ShouldEscape(input with { LifeFraction = 0.6 }));
        // Ten on her at the life she hovers at is the ordinary fight with static on; twenty is a whole pack.
        Assert.False(SorceressRotation.ShouldEscape(input with { CowsWithinReach = 10, ChilledWithinReach = 10 }));
        Assert.True(SorceressRotation.ShouldEscape(input with { CowsWithinReach = 20, ChilledWithinReach = 20 }));
        // Healthy, a whole pack on her is still fought: half of 4.5 escapes a game came from this alone.
        Assert.False(SorceressRotation.ShouldEscape(input with { CowsWithinReach = 20, ChilledWithinReach = 20, LifeFraction = 0.9 }));
        // Unchilled ones are met with a fresh frost nova, not an escape: escaping them cost more than the chickens.
        Assert.False(SorceressRotation.ShouldEscape(input with { ChilledWithinReach = 2 }));
    }

    [Fact]
    public void The_first_live_sorceress_chicken_escapes()
    {
        // Ten cows on her at 44% life, then 486 lost in 104 ms.
        var input = Classic(Cows(30, 100), Cows(20, 100)) with { PackAlive = 49, Nearest = 2.2, CowsWithinReach = 10, CowsInFrostNovaRadius = 25, LifeFraction = 0.44 };

        Assert.True(SorceressRotation.ShouldEscape(input));
    }

    [Fact]
    public void An_escape_lands_beyond_what_the_cows_walk_before_the_next_one()
    {
        // Reach 4.9 at half a second a cast, plus 1.5 s of walking at 5.86 a second.
        Assert.Equal(13.72, SorceressRotation.EscapeClearance(TimeSpan.FromMilliseconds(500)), 2);
    }

    [Fact]
    public void Two_cows_in_reach_are_fought_not_fled()
    {
        var input = Classic(Cows(30, 100), Cows(5, 100)) with { PackAlive = 30, Nearest = 2, CowsWithinReach = 2, CowsInFrostNovaRadius = 5, LifeLostLastSecond = 0.3 };

        Assert.NotEqual(RotationChoice.Escape, SorceressRotation.Decide(input));
    }

    [Fact]
    public void Three_in_reach_tearing_into_her_life_make_her_escape()
    {
        var input = Classic(Cows(30, 100), Cows(5, 100)) with { PackAlive = 30, Nearest = 2, CowsWithinReach = 3, CowsInFrostNovaRadius = 5 };

        Assert.Equal(RotationChoice.Escape, SorceressRotation.Decide(input with { LifeLostLastSecond = 0.25 }));
        Assert.NotEqual(RotationChoice.Escape, SorceressRotation.Decide(input with { LifeLostLastSecond = 0.1 }));
    }

    [Fact]
    public void One_escape_at_a_time()
    {
        var onHer = Classic(Cows(30, 100), Cows(20, 100)) with { PackAlive = 46, Nearest = 1.4, CowsWithinReach = 15, CowsInFrostNovaRadius = 22, SinceEscape = TimeSpan.FromSeconds(1) };

        Assert.NotEqual(RotationChoice.Escape, SorceressRotation.Decide(onHer));
    }

    [Fact]
    public void Right_after_a_frost_nova_static_runs_while_it_pays_more()
    {
        var input = Classic(Cows(20, 100), Cows(4, 100)) with
        {
            PackAlive = 20,
            Nearest = 2,
            CowsWithinReach = 3,
            ChilledWithinReach = 3,
            CowsInFrostNovaRadius = 4,
            ChilledInFrostNovaRadius = 4,
            SinceFrostNova = TimeSpan.FromMilliseconds(500),
        };

        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input));
        Assert.Equal(RotationChoice.Static, SorceressRotation.Decide(input with { Mana = 40 }));
    }

    [Fact]
    public void Six_in_reach_mostly_unchilled_below_the_low_life_line_escape()
    {
        // The third live chicken: six on her, four just walked in unchilled, at 66% life, then 1176 lost in 0.4 s.
        var input = Classic(Cows(30, 100), Cows(6, 100)) with { PackAlive = 30, Nearest = 1, CowsWithinReach = 6, ChilledWithinReach = 2, LifeFraction = 0.64 };

        Assert.True(SorceressRotation.ShouldEscape(input));
        Assert.False(SorceressRotation.ShouldEscape(input with { LifeFraction = 0.66 }));
    }

    [Fact]
    public void Six_in_reach_all_chilled_at_eighty_percent_stay()
    {
        var input = Classic(Cows(30, 100), Cows(6, 100)) with { PackAlive = 30, Nearest = 1, CowsWithinReach = 6, ChilledWithinReach = 6, LifeFraction = 0.8 };

        Assert.False(SorceressRotation.ShouldEscape(input));
    }

    [Fact]
    public void No_second_frost_nova_inside_the_recast_gap()
    {
        var input = Classic(Cows(30, 100), Cows(6, 100)) with
        {
            PackAlive = 30,
            Nearest = 1,
            CowsWithinReach = 4,
            ChilledWithinReach = 0,
            CowsInFrostNovaRadius = 15,
            ChilledInFrostNovaRadius = 2,
            SinceFrostNova = TimeSpan.FromMilliseconds(600),
        };

        Assert.NotEqual(RotationChoice.FrostNova, SorceressRotation.Decide(input));
        Assert.Equal(RotationChoice.FrostNova, SorceressRotation.Decide(input with { SinceFrostNova = TimeSpan.FromSeconds(2) }));
        Assert.NotEqual(RotationChoice.FrostNova, SorceressRotation.Decide(input with { SinceFrostNova = TimeSpan.FromSeconds(2), Classic = false }));
    }

    [Fact]
    public void Frost_nova_waits_until_a_cow_is_one_cast_from_its_swing()
    {
        // Two units of melee reach plus 5.86 units a second for half a second of cast.
        Assert.Equal(4.93, SorceressRotation.AttackReach(TimeSpan.FromMilliseconds(500)), 2);
        Assert.Equal(7.86, SorceressRotation.FrostNovaTrigger(TimeSpan.FromMilliseconds(500)), 2);
        Assert.Equal(3, SorceressRotation.FrostNovaQuorum(4));
        Assert.Equal(5, SorceressRotation.FrostNovaQuorum(10));
        Assert.Equal(25, SorceressRotation.FrostNovaQuorum(49));
    }
}
