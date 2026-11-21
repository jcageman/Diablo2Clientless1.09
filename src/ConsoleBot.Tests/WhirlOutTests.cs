using ConsoleBot.Attack;

namespace ConsoleBot.Tests;

public class WhirlOutTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    // The hunt lead at 02:44.5 of the game he chickened in: a dozen cows within three of him, walking
    // fourteen east towards the party. The server held both walks and his life went from 75% to 16%.
    private static readonly Point[] LeadInPack =
    [
        At(101, 100), At(100, 99), At(100, 102), At(102, 99), At(102, 101), At(101, 98), At(98, 101), At(99, 98),
        At(98, 99), At(103, 100), At(98, 103), At(104, 99), At(96, 101), At(104, 97), At(97, 96), At(102, 95), At(105, 96),
    ];

    private static Point StraightEnd(Point from, Point to) => WhirlOut.Ends(from, [to])[0];

    [Fact]
    public void Cows_on_the_way_out_make_him_whirl_it()
    {
        var inTheWay = WhirlOut.InTheWay(At(100, 100), StraightEnd(At(100, 100), At(114, 99)), LeadInPack);

        Assert.True(inTheWay >= 5, $"{inTheWay} in the way");
        Assert.True(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay, lastWalkHeld: false));
    }

    [Fact]
    public void One_or_two_cows_in_the_way_are_walked_past_a_pack_is_whirled()
    {
        Assert.False(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: 1, lastWalkHeld: false));
        Assert.False(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: 2, lastWalkHeld: false));
        Assert.True(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: WhirlOut.PackInTheWay, lastWalkHeld: false));
        Assert.True(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: 1, lastWalkHeld: true));
    }

    [Fact]
    public void A_monster_attacking_him_makes_him_whirl_out_whatever_is_in_the_way()
    {
        Assert.True(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: 0, lastWalkHeld: false, underAttack: true));
        Assert.False(WhirlOut.ShouldWhirl(whirls: true, mana: WhirlOut.AttackedManaReserve, inTheWay: 0, lastWalkHeld: false, underAttack: true));
        Assert.True(WhirlOut.ShouldWhirl(whirls: true, mana: WhirlOut.AttackedManaReserve, inTheWay: WhirlOut.PackInTheWay, lastWalkHeld: false, underAttack: true));
        Assert.False(WhirlOut.ShouldWhirl(whirls: false, mana: 221, inTheWay: 0, lastWalkHeld: false, underAttack: true));
    }

    [Fact]
    public void A_monster_behind_him_is_not_in_the_way()
    {
        Assert.Equal(0, WhirlOut.InTheWay(At(100, 100), At(112, 100), [At(98, 100), At(99, 101), At(100, 103)]));
        Assert.Equal(1, WhirlOut.InTheWay(At(100, 100), At(112, 100), [At(101, 100)]));
    }

    [Fact]
    public void A_walk_the_server_held_makes_him_whirl_the_next_even_on_a_clear_way()
    {
        Assert.True(WhirlOut.WalkHeld(At(100, 100), believedEnd: At(114, 99), confirmed: At(99, 100)));
        Assert.True(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: 0, lastWalkHeld: true));
    }

    [Fact]
    public void A_walk_the_server_followed_was_not_held()
    {
        Assert.False(WhirlOut.WalkHeld(At(100, 100), believedEnd: At(114, 99), confirmed: At(112, 99)));
        Assert.False(WhirlOut.WalkHeld(At(100, 100), believedEnd: At(103, 100), confirmed: At(100, 100)));
        Assert.False(WhirlOut.WalkHeld(At(100, 100), believedEnd: At(114, 99), confirmed: null));
    }

    [Fact]
    public void A_move_of_his_own_after_the_walk_is_not_a_held_walk()
    {
        // A whirl or a pickup after the walk puts the server's position far from the walk's end, but not back at its start.
        Assert.False(WhirlOut.WalkHeld(At(100, 100), believedEnd: At(114, 99), confirmed: At(124, 108)));
        Assert.False(WhirlOut.WalkHeld(At(100, 100), believedEnd: At(114, 99), confirmed: At(114, 110)));
    }

    [Fact]
    public void A_clear_way_is_walked()
    {
        var behindAndAside = new[] { At(95, 100), At(96, 97), At(106, 105), At(110, 95), At(130, 100) };

        Assert.Equal(0, WhirlOut.InTheWay(At(100, 100), StraightEnd(At(100, 100), At(150, 100)), behindAndAside));
        Assert.False(WhirlOut.ShouldWhirl(whirls: true, mana: 221, inTheWay: 0, lastWalkHeld: false));
    }

    [Fact]
    public void Without_whirlwind_the_level_for_it_or_the_mana_he_walks()
    {
        Assert.False(WhirlOut.ShouldWhirl(whirls: false, mana: 221, inTheWay: 5, lastWalkHeld: true));
        Assert.False(WhirlOut.ShouldWhirl(whirls: true, mana: WhirlOut.ManaFloor, inTheWay: 5, lastWalkHeld: true));
        Assert.True(WhirlOut.Whirls(hasWhirlwind: true, level: 34, normal: false));
        Assert.False(WhirlOut.Whirls(hasWhirlwind: true, level: 33, normal: false));
        Assert.False(WhirlOut.Whirls(hasWhirlwind: true, level: 40, normal: true));
        Assert.True(WhirlOut.Whirls(hasWhirlwind: true, level: 41, normal: true));
        Assert.False(WhirlOut.Whirls(hasWhirlwind: false, level: 95, normal: false));
    }

    [Fact]
    public void One_whirl_goes_twelve_along_the_way_or_to_its_end()
    {
        Assert.Equal(At(112, 100), StraightEnd(At(100, 100), At(150, 100)));
        Assert.Equal(At(100, 88), StraightEnd(At(100, 100), At(100, 60)));
        Assert.Equal(At(108, 100), StraightEnd(At(100, 100), At(108, 100)));
    }

    [Fact]
    public void The_whirl_follows_the_walking_path_round_a_fence()
    {
        // The walk goes four east to a gap, then south; the straight line south-east ends against the fence.
        var ends = WhirlOut.Ends(At(100, 100), [At(104, 100), At(104, 108), At(104, 120)]);

        Assert.Equal([At(104, 108), At(104, 100)], ends);
        Assert.Equal(At(102, 112), StraightEnd(At(100, 100), At(104, 120)));
    }

    [Fact]
    public void Only_the_stretch_one_whirl_covers_counts()
    {
        Assert.Equal(1, WhirlOut.InTheWay(At(100, 100), StraightEnd(At(100, 100), At(150, 100)), [At(110, 102), At(125, 100)]));
    }

    [Fact]
    public async Task Whirls_stop_at_three_or_at_the_first_that_does_not_happen()
    {
        Assert.Equal(WhirlOut.MaxWhirls, await WhirlOut.WhirlAlong(() => false, () => false, () => Task.FromResult(true)));

        var calls = 0;
        Assert.Equal(1, await WhirlOut.WhirlAlong(() => false, () => false, () => Task.FromResult(++calls == 1)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_stop_check_comes_before_every_whirl()
    {
        var whirls = 0;
        Assert.Equal(1, await WhirlOut.WhirlAlong(() => whirls >= 1, () => false, () => Task.FromResult(++whirls > 0)));

        whirls = 0;
        Assert.Equal(0, await WhirlOut.WhirlAlong(() => true, () => false, () => Task.FromResult(++whirls > 0)));
        Assert.Equal(0, await WhirlOut.WhirlAlong(() => false, () => true, () => Task.FromResult(++whirls > 0)));
        Assert.Equal(0, whirls);
    }

    [Fact]
    public async Task The_caller_gets_control_back_before_the_first_whirl()
    {
        // A whirl blocks: without the yield the party guard's retreat check could not run until all three were done.
        using var gate = new ManualResetEventSlim();
        var task = WhirlOut.WhirlAlong(() => !gate.Wait(TimeSpan.FromSeconds(5)), () => false, () => Task.FromResult(false));

        Assert.False(task.IsCompleted);
        gate.Set();
        Assert.Equal(0, await task);
    }
}
