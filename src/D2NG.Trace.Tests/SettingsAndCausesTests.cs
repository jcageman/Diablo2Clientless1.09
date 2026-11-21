using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>The bot's settings in the trace, and the causes of slow games and failed steps.</summary>
public class SettingsAndCausesTests
{
    [Fact]
    public void TheRecorderWritesTheBotsSettingsWhenAClientEntersAGame()
    {
        using var root = new TempRoot();
        string folder;
        using (var recorder = root.Recorder())
        {
            recorder.Settings = [("botType", "travincal"), ("monsterMotion", "Simulated"), ("wallAwareWhirl", "true")];
            var channel = recorder.OpenGame("party1", "client1");
            channel.Write(TraceRecordKind.D2gsReceived, [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            folder = channel.Directory;
            channel.Close();
            recorder.FlushNow();
        }

        var settings = GameSettings.Of(GameQuery.Open(folder));

        Assert.Equal("travincal", settings["botType"]);
        Assert.Equal("Simulated", settings["monsterMotion"]);
        Assert.Equal("true", settings["wallAwareWhirl"]);
        Assert.True(GameSettings.Filter("wallAwareWhirl=TRUE,botType=travincal")(GameQuery.Open(folder)));
        Assert.False(GameSettings.Filter("wallAwareWhirl=false")(GameQuery.Open(folder)));
    }

    [Fact]
    public void AnOlderGameTakesItsMotionModelFromItsProcessStartupLineAndHasNoOtherSetting()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 3, 5057, 1902).WriteTo(folder);
        new SidecarFile(Game1.Start.AddMinutes(-5))
            .Log(0, LogEventLevel.Information, "Monster motion: configured {Configured}, effective {Effective}", ("Configured", "Simulated"), ("Effective", "Legacy"))
            .Typed(301, "step", "client1", "party1", ("name", "run"), ("phase", "start"))
            .WriteTo(Game1.Date(root), "115500");
        var query = GameQuery.Open(folder);

        new SidecarFile(Game1.Start.AddMinutes(-5))
            .Log(0, LogEventLevel.Information, "Monster motion: configured {Configured}, effective {Effective}", ("Configured", "Legacy"), ("Effective", "Legacy"))
            .WriteTo(Game1.Date(root), "115500-2");
        var settings = GameSettings.Of(query);

        Assert.Equal("Legacy|Simulated", settings["monsterMotion"]);
        Assert.Equal("Legacy", settings["monsterMotionEffective"]);
        Assert.Equal(GameSettings.NotRecorded, GameSettings.ValueOf(query, "wallAwareWhirl"));
        Assert.True(GameSettings.Filter("wallAwareWhirl=none")(query));
        Assert.Throws<ArgumentException>(() => GameSettings.Filter("wallAwareWhirl"));
    }

    private static readonly Dictionary<string, TimeSpan> SlowPhases = new()
    {
        ["out"] = TimeSpan.FromSeconds(0.5), ["town"] = TimeSpan.FromSeconds(180), ["travel"] = TimeSpan.FromSeconds(12),
        ["kill"] = TimeSpan.FromSeconds(16), ["loot"] = TimeSpan.FromSeconds(17), ["return"] = TimeSpan.FromSeconds(2),
    };

    private static readonly Dictionary<string, double> Median = new() { ["out"] = 0.5, ["town"] = 10, ["travel"] = 12, ["kill"] = 16, ["loot"] = 17, ["return"] = 2 };

    private static ItemMoves.Summary Items(int gambled = 0, int bought = 0, int traded = 0)
        => new("client1", 0, 0, 0, 0, 0, 0, 0, bought, gambled, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, traded, 0, 0, 0);

    [Fact]
    public void ADeathComesBeforeAChickenAndOnlyTheBotsOwnFailureLinesAreARefusedPortal()
    {
        Incident[] incidents =
        [
            new(TimeSpan.FromSeconds(10), "client1", "chicken", "out of potions", "sidecar"),
            new(TimeSpan.FromSeconds(58), "client1", "death", "slain"),
            new(TimeSpan.FromSeconds(30), "client1", "error", "Moving tome of town portal to cube failed", "sidecar"),
            new(TimeSpan.FromSeconds(40), "client1", "trace gap", "12 records dropped"),
        ];

        var causes = SlowCauses.Of("client1", incidents, TimeSpan.Zero, TimeSpan.FromSeconds(60), null, null, null, failed: true);

        Assert.Equal(SlowCauses.Death, causes[0].Label);
        Assert.StartsWith("ended it; death at 00:58.000", causes[0].Detail, StringComparison.Ordinal);
        Assert.Equal("chicken / out of potions", causes[1].Label);
        Assert.DoesNotContain(causes, c => c.Label == "waypoint or portal refused");
        Assert.Equal(1, causes.Single(c => c.Label == "records dropped").Seconds);
        Assert.True(SlowCauses.IsPortalFailure(new Incident(TimeSpan.Zero, "client1", "error", "Taking waypoint to to StonyFields waypoint failed")));
        Assert.True(SlowCauses.IsPortalFailure(new Incident(TimeSpan.Zero, "client1", "error", "Client \"client1\" Moving to town failed with area Travincal")));
    }

    [Fact]
    public void EveryCauseIsCappedAtTheWindowAndAStepGetsNoPhaseCauses()
    {
        Incident[] incidents = [new(TimeSpan.FromSeconds(5), "client1", "connection closed", "closed the connection mid-game", "sidecar")];

        var causes = SlowCauses.Of("client1", incidents, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), null, null, null);

        Assert.Equal("connection lost", Assert.Single(causes).Label);
        Assert.Equal(2, causes[0].Seconds);
        Assert.Empty(SlowCauses.Of("client1", [], TimeSpan.Zero, TimeSpan.FromSeconds(30), null, null, Items(gambled: 9)));
    }

    [Fact]
    public void ALongTownPhaseIsGamblingMulingOrAVendorTripByWhatTheClientDidWithItems()
    {
        static string Top(ItemMoves.Summary items) => SlowCauses.Of("client1", [], TimeSpan.Zero, TimeSpan.FromSeconds(228), SlowPhases, Median, items)[0].Label;

        Assert.Equal("gambling", Top(Items(gambled: 40, bought: 2)));
        Assert.Equal("muling", Top(Items(traded: 12)));
        Assert.Equal("vendor trip", Top(Items(bought: 3)));
        Assert.Equal("long town stay", Top(Items()));
        var gambling = SlowCauses.Of("client1", [], TimeSpan.Zero, TimeSpan.FromSeconds(228), SlowPhases, Median, Items(gambled: 40))[0];
        Assert.Equal(170, gambling.Seconds);
    }

    [Fact]
    public void SkillMovesThatWentNowhereExplainTheirSpanAndWhatEndedAFailedStepComesFirst()
    {
        Incident[] incidents =
        [
            new(TimeSpan.FromSeconds(20), "client1", CombatCheck.NowhereKind, "22 Whirlwind moves ...; 506 mana spent on them, 1320 in the span") { End = TimeSpan.FromSeconds(80) },
            new(TimeSpan.FromSeconds(70), "client1", CombatCheck.NothingKind, "... 400 mana spent in the span") { End = TimeSpan.FromSeconds(100) },
            new(TimeSpan.FromSeconds(134), "client1", "error", "Client \"client1\" Moving to town failed with area Travincal", "sidecar"),
            new(TimeSpan.FromSeconds(50), "client2", "death", "someone else"),
        ];

        var slow = SlowCauses.Of("client1", incidents, TimeSpan.Zero, TimeSpan.FromSeconds(136), null, null, null);
        Assert.Equal(SlowCauses.Wall, slow[0].Label);
        Assert.Equal(80, slow[0].Seconds);
        Assert.Contains("mana up to 1320 spent in one span", slow[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(slow, c => c.Label == "death");

        var failed = SlowCauses.Of("client1", incidents, TimeSpan.Zero, TimeSpan.FromSeconds(136), null, null, null, failed: true);
        Assert.Equal("waypoint or portal refused", failed[0].Label);
        Assert.StartsWith("ended it;", failed[0].Detail, StringComparison.Ordinal);
        Assert.Equal(SlowCauses.Wall, failed[1].Label);
        Assert.StartsWith("cause: waypoint or portal refused", SlowCauses.Render(failed), StringComparison.Ordinal);
    }
}
