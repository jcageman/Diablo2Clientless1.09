using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class LeaveEarlyTests
{
    private static readonly DateTime Night = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Set up for 20 s, then 100 xp a second for 100 s, then 10 a second for 100 s more; the next game 20 s after.</summary>
    private static List<GameExperience> RichThenThin(int games)
        => Enumerable.Range(0, games).Select(i =>
        {
            var points = new List<(TimeSpan, long)> { (TimeSpan.FromSeconds(5), 0) };
            for (var s = 1; s <= 200; s++)
            {
                points.Add((TimeSpan.FromSeconds(20 + s), s <= 100 ? s * 100L : 10_000 + (s - 100) * 10L));
            }

            return new GameExperience($"g{i}", "sorc", Night.AddSeconds(i * 240), TimeSpan.FromSeconds(220), points);
        }).ToList();

    [Fact]
    public void Leaving_when_the_rate_falls_below_the_average_beats_playing_on()
    {
        var result = LeaveEarly.Compute(RichThenThin(6), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

        Assert.Equal(6, result.Games);
        Assert.Equal(TimeSpan.FromSeconds(20), result.Between);
        Assert.Equal(TimeSpan.FromSeconds(21), result.FirstGain);
        Assert.Equal(11_000 / 240.0 * 60, result.FullPerMinute, 3);
        Assert.Equal(TimeSpan.FromSeconds(100), result.Best.After);
        Assert.True(result.Best.Change > 0.5);
    }

    [Fact]
    public void The_marginal_rate_drops_where_the_thin_stretch_starts()
    {
        var result = LeaveEarly.Compute(RichThenThin(6), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

        Assert.Equal(6000, result.Marginal[0].PerMinute, 3);
        Assert.Equal(600, result.Marginal[^1].PerMinute, 3);
        Assert.All(result.Marginal, b => Assert.Equal(6, b.Games));
    }

    [Fact]
    public void Games_without_a_gain_and_parties_too_small_are_left_out()
    {
        var games = RichThenThin(3);
        games.Add(new GameExperience("mule", "sorc", Night.AddHours(1), TimeSpan.FromSeconds(60), [(TimeSpan.FromSeconds(3), 0)]));

        var result = LeaveEarly.Compute(games, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), minGames: 5);

        Assert.Equal(3, result.Games);
        Assert.Empty(result.Marginal);
    }

    [Fact]
    public void A_rejoin_joins_its_files_and_counts_from_the_games_first_total()
    {
        var files = new List<ExperienceCurveObserver.FileCurve>
        {
            new("sorc", "g1", Night, TimeSpan.FromSeconds(50), [(TimeSpan.FromSeconds(10), 1_000), (TimeSpan.FromSeconds(40), 1_500)]),
            new("sorc", "g1", Night, TimeSpan.FromSeconds(120), [(TimeSpan.FromSeconds(70), 1_500), (TimeSpan.FromSeconds(100), 2_200)]),
        };

        var game = Assert.Single(ExperienceCurveObserver.PerGame(files));

        Assert.Equal(TimeSpan.FromSeconds(120), game.Length);
        Assert.Equal(1_200, game.Gained);
        Assert.Equal(500, game.GainedBy(TimeSpan.FromSeconds(69)));
        Assert.Equal(TimeSpan.FromSeconds(40), game.FirstGain);
    }
}
