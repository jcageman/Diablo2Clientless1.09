using D2NG.Core.D2GS;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class PartySpreadTests
{
    private static List<PartySpread.Sample> Path(params (int From, int To, ushort X, ushort Y, string Area)[] legs)
        => legs.SelectMany(l => Enumerable.Range(l.From, l.To - l.From + 1)
            .Select(s => new PartySpread.Sample(TimeSpan.FromSeconds(s), new Point(l.X, l.Y), l.Area))).ToList();

    [Fact]
    public void A_walker_left_behind_is_alone_while_the_others_stay_together()
    {
        var samples = new Dictionary<string, IReadOnlyList<PartySpread.Sample>>
        {
            ["lead"] = Path((0, 4, 100, 100, "CowLevel"), (5, 10, 140, 100, "CowLevel")),
            ["barb"] = Path((0, 4, 105, 100, "CowLevel"), (5, 10, 145, 100, "CowLevel")),
            ["amazon"] = Path((0, 9, 100, 105, "CowLevel"), (10, 10, 135, 100, "CowLevel")),
        };

        var result = PartySpread.Compute(samples, aloneDistance: 30, tick: TimeSpan.FromSeconds(1));

        Assert.Equal("CowLevel", result.Area);
        var amazon = result.Clients.Single(c => c.Client == "amazon");
        Assert.Equal(TimeSpan.FromSeconds(11), amazon.Present);
        Assert.Equal(TimeSpan.FromSeconds(5), amazon.Alone);
        Assert.Equal(TimeSpan.Zero, result.Clients.Single(c => c.Client == "lead").Alone);
    }

    [Fact]
    public void Leaving_the_area_or_a_stale_position_ends_presence()
    {
        var samples = new Dictionary<string, IReadOnlyList<PartySpread.Sample>>
        {
            ["lead"] = Path((0, 5, 100, 100, "CowLevel")),
            ["sorc"] = Path((0, 0, 103, 100, "CowLevel"), (1, 5, 9000, 9000, "RogueEncampment")),
            ["barb"] = Path((0, 0, 104, 100, "CowLevel"), (5, 5, 104, 100, "CowLevel")),
        };

        var result = PartySpread.Compute(samples, aloneDistance: 30, tick: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), result.Clients.Single(c => c.Client == "sorc").Present);
        Assert.Equal(TimeSpan.FromSeconds(4), result.Clients.Single(c => c.Client == "barb").Present);
    }
}
