using D2NG.Core.D2GS;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class AreaSpellsTests
{
    private static AttackCast Cast(double at, string skill, ushort x = 100, ushort y = 100)
        => new(TimeSpan.FromSeconds(at), skill, true, new Point(x, y), new Point(x, y), 0, null, null);

    private static MonsterLifeDrop Drop(double at, uint id, double before, double after, ushort x, ushort y)
        => new(TimeSpan.FromSeconds(at), id, before, after, new Point(x, y));

    [Fact]
    public void A_drop_goes_to_the_latest_cast_whose_radius_holds_the_monster()
    {
        var casts = new List<AttackCast> { Cast(1.0, "StaticField"), Cast(1.4, "Nova") };
        var drops = new List<MonsterLifeDrop>
        {
            Drop(1.2, 1, 100, 75, 115, 100),
            Drop(1.5, 2, 60, 20, 105, 100),
            Drop(1.6, 3, 80, 0, 115, 100),
        };

        var totals = AreaSpells.Measure(casts, drops).ToDictionary(t => t.Skill);

        Assert.Equal(25, totals["StaticField"].LifeRemoved, 3);
        Assert.Equal(40, totals["Nova"].LifeRemoved, 3);
        Assert.Equal(0, totals["Nova"].Kills);
        Assert.Equal(1, totals["Nova"].CastsWithEffect);
    }

    [Fact]
    public void The_distance_profile_keeps_only_static_shaped_drops_for_static()
    {
        var casts = new List<AttackCast> { Cast(1.0, "StaticField") };
        var drops = new List<MonsterLifeDrop> { Drop(1.1, 1, 80, 60, 103, 100), Drop(1.2, 2, 80, 20, 103, 100), Drop(1.3, 3, 40, 30, 118, 100) };

        var bins = AreaSpells.ByDistance(casts, drops, "StaticField");

        Assert.Equal(1, bins[1].Drops);
        Assert.Equal(1, bins[7].Drops);
        Assert.Equal(2, bins.Sum(b => b.Drops));
    }

    [Fact]
    public void A_drop_after_the_window_or_away_from_the_caster_is_nobodys()
    {
        var casts = new List<AttackCast> { Cast(1.0, "Nova") };
        var drops = new List<MonsterLifeDrop> { Drop(1.0 + AreaSpells.Window.TotalSeconds + 0.1, 1, 100, 50, 101, 100), Drop(1.1, 2, 100, 50, 140, 100) };

        var nova = Assert.Single(AreaSpells.Measure(casts, drops));

        Assert.Equal(0, nova.LifeRemoved);
        Assert.Equal(0, nova.CastsWithEffect);
        Assert.Equal(AreaSpells.Window, nova.CastTime);
    }
}
