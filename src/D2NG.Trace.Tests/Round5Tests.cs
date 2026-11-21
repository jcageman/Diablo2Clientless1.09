using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>The fifth review round: one regression test per finding, each failing on the code before it.</summary>
public class AreaOutsideTheRevealedMapTests
{
    /// <summary>Joined in town, into the Blood Moor through a revealed tile, then moved by the server to a spot no revealed tile holds, and left standing there.</summary>
    internal static string OffTheMap(TempRoot root)
    {
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(2)
            .Received(5, SyntheticGame.Reassign(1, 5400, 2400))
            .Ticks(6, 50, 5400, 2400)
            .WriteTo(folder);
        return folder;
    }

    [Fact]
    public void AStandOutsideEveryRevealedTileIsNotTownButAStallTheChecksSee()
    {
        using var root = new TempRoot();
        var folder = OffTheMap(root);
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver(), Events: new GameEvents(25)), x => [x.Activity, x.Town, x.Events])[0];

        var stretches = o.Activity.Stretches(TimeSpan.Zero, end, []);
        var stays = TownStays.Of("client1", o.Activity, o.Town, [], [], TownStays.Chickens(query, "client1"), end);

        Assert.Equal(ActivityState.StandingIdle, stretches.Single(s => s.Start <= TimeSpan.FromSeconds(20) && TimeSpan.FromSeconds(20) < s.End).State);
        Assert.Equal(["game start"], stays.Select(s => s.Cause));
        Assert.DoesNotContain(o.Events.Entries, e => e.Text.Contains("-> RogueEncampment", StringComparison.Ordinal));
        Assert.Single(Game1.Incidents(folder, checks: "bot").Found, i => i.Kind == "stuck");
    }
}

public class NotWalkableEpisodeTests
{
    private static MapSource NoGroundBelow(int y)
    {
        var rows = Enumerable.Range(0, 600).Select(row => Enumerable.Range(0, 200).Select(_ => 1950 + row >= y ? 1 : 0).ToArray()).ToArray();
        var json = JsonSerializer.Serialize(new { levelOrigin = new { x = 5100, y = 1950 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });
        return new MapSource("http://map.invalid", null, _ => json);
    }

    [Fact]
    public void ThreeStandsInARowOnUnwalkableGroundAreOneIncidentWithTheirWholeLength()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1, 5160, 2400)
            .Ticks(2, 5, 5160, 2400).Ticks(5.2, 8.2, 5170, 2400).Ticks(8.4, 11.4, 5180, 2400)
            .WriteTo(folder);

        var found = Game1.Incidents(folder, maps: NoGroundBelow(2350), checks: "state").Found.Where(i => i.Kind == "not walkable").ToList();

        var episode = Assert.Single(found);
        Assert.True(episode.End - episode.Offset >= TimeSpan.FromSeconds(7), episode.Detail);
        Assert.Contains("spots", episode.Detail);
    }

    private static MapSource WallAt5200()
    {
        var rows = Enumerable.Range(0, 200).Select(_ => Enumerable.Range(0, 200).Select(x => x == 100 ? 1 : 0).ToArray()).ToArray();
        var json = JsonSerializer.Serialize(new { levelOrigin = new { x = 5100, y = 1950 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });
        return new MapSource("http://map.invalid", null, _ => json);
    }

    private static string OnTheWall(TempRoot root, string name, ushort toX, ushort toY, bool interact)
    {
        var folder = Game1.Folder(root, name);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1, 5200, 2050).Ticks(2, 10, 5200, 2050)
            .Received(10.05, SyntheticGame.AssignObject(0x50, EntityCode.TownPortal, 5201, 2050));
        if (interact)
        {
            file.Sent(10.2, SyntheticGame.Unit(0x13, EntityType.Object, 0x50));
        }

        file.Received(10.5, SyntheticGame.Reassign(1, toX, toY)).Ticks(11, 14, toX, toY).WriteTo(folder);
        return folder;
    }

    [Fact]
    public void ANudgeOfTwoSubtilesOrAWarpRightAfterAnInteractionIsNoServerCorrection()
    {
        using var root = new TempRoot();

        bool Flagged(string folder) => Game1.Incidents(folder, maps: WallAt5200(), checks: "state").Found.Any(i => i.Kind == "not walkable");

        Assert.False(Flagged(OnTheWall(root, "nudge", 5198, 2051, false)));
        Assert.False(Flagged(OnTheWall(root, "warp", 5195, 2050, true)));
        Assert.True(Flagged(OnTheWall(root, "corrected", 5195, 2050, false)));
    }
}

public class RejoinCauseTests
{
    private static string RejoinAfter(TempRoot root, bool sentLeave, bool sidecar)
    {
        var folder = Game1.Folder(root);
        var first = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(2).Ticks(3, 9).Received(10, SyntheticGame.LifeMana(270, 100, 5160, 2010));
        if (sentLeave)
        {
            first.Sent(10.002, [(byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame]);
        }

        first.WriteTo(folder);
        new HandTrace("client1", "party1", Game1.Start.AddSeconds(15)).Join(1).Ticks(1, 10, 5057, 1902).WriteTo(folder, "client1-2");
        if (sidecar)
        {
            new SidecarFile(Game1.Start).Typed(1, "move", "client1", "party1", ("kind", "walk"), ("outcome", "arrived")).WriteTo(Game1.Date(root), "115900");
        }

        return folder;
    }

    private static List<TownStay> Stays(string folder)
    {
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver()), x => [x.Activity, x.Town])[0];
        return TownStays.Of("client1", o.Activity, o.Town, [], [], TownStays.Chickens(query, "client1"), end);
    }

    [Fact]
    public void WithoutBotEventsARejoinIsOfUnknownCauseAndNotALeave()
    {
        using var root = new TempRoot();

        var rejoin = Stays(RejoinAfter(root, sentLeave: true, sidecar: false))[^1];

        Assert.Equal("rejoin (no bot events recorded: cause unknown)", rejoin.Cause);
    }

    [Fact]
    public void WithBotEventsAFileThatEndedWithoutLeaveGameIsARejoinAfterDisconnect()
    {
        using var root = new TempRoot();

        var rejoin = Stays(RejoinAfter(root, sentLeave: false, sidecar: true))[^1];

        Assert.Equal("rejoin after disconnect", rejoin.Cause);
    }
}

public class SiblingFolderTests
{
    [Fact]
    public void ARejoinAfterMidnightIntoAnotherNumberedFolderJoinsItsGameAndLeavesTheUnrelatedOneAlone()
    {
        using var root = new TempRoot();
        var start = new DateTime(2026, 11, 13, 23, 50, 0, DateTimeKind.Local).ToUniversalTime();
        var rejoin = new DateTime(2026, 11, 14, 0, 2, 0, DateTimeKind.Local).ToUniversalTime();
        var dayOne = HandTrace.DateFolder(root.Path, start);
        var dayTwo = HandTrace.DateFolder(root.Path, rejoin);
        new HandTrace("client1", "lvlp", start.AddHours(-2), "10.0.0.1#1").Join(1).Ticks(1, 60).WriteTo(Path.Combine(dayOne, "lvlp"));
        new HandTrace("client1", "lvlp", start, "10.0.0.1#5").Join(1).Ticks(1, 690).WriteTo(Path.Combine(dayOne, "lvlp~5"));
        new HandTrace("client1", "lvlp", rejoin, "10.0.0.1#5").Join(1).Ticks(1, 60).WriteTo(Path.Combine(dayTwo, "lvlp"));
        new HandTrace("client1", "lvlp", rejoin.AddSeconds(20), "10.0.0.1#6").Join(1, token: 6).Ticks(1, 60).WriteTo(Path.Combine(dayTwo, "lvlp~2"));

        var game = Assert.Single(GameTrace.OpenAll(Path.Combine(dayOne, "lvlp~5")));
        var nextDay = GameTrace.GamesUnder(dayTwo);

        Assert.Equal(2, game.Files.Count);
        Assert.Equal(2, game.FilesOf("client1").Last().Part);
        Assert.Equal("10.0.0.1#6", Assert.Single(nextDay).Files.Single().ServerGame);
    }

    [Fact]
    public void AGameWhoseFilesLandedInTheFallbackFolderTooIsOneGameNumberedAcrossItsSiblings()
    {
        using var root = new TempRoot();
        var date = Game1.Date(root);
        new HandTrace("client1", "party1", Game1.Start, "10.0.0.1#5").Join(1).Ticks(1, 60).WriteTo(Path.Combine(date, "party1"));
        new HandTrace("client2", "party1", Game1.Start.AddSeconds(1), "10.0.0.1#5").Join(2).Ticks(1, 60).WriteTo(Path.Combine(date, "party1~1a2b3c4d"));
        new HandTrace("client1", "party1", Game1.Start.AddMinutes(5), "10.0.0.1#6").Join(1, token: 6).Ticks(1, 60).WriteTo(Path.Combine(date, "party1~2"));

        var games = GameTrace.GamesUnder(date);

        Assert.Equal(2, games.Count);
        Assert.Equal(["client1", "client2"], games[0].Labels);
        Assert.Equal(games[1].StartUtc, GameTrace.Open(Path.Combine(date, "party1~2") + "#2").StartUtc);
        Assert.Throws<ArgumentException>(() => GameTrace.Open(Path.Combine(date, "party1") + "#2"));
        Assert.Equal(games[1].StartUtc, GameTrace.Open(Path.Combine(date, "party1~2")).StartUtc);
        Assert.Equal(games[0].StartUtc, GameTrace.Open(Path.Combine(date, "party1~1a2b3c4d")).StartUtc);
    }
}

public class FolderClaimRound5Tests
{
    private sealed class Collect : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
            {
                Events.Add(logEvent);
            }
        }
    }

    [Fact]
    public void AFallbackFolderIsNamedInOneWarning()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var blocked = Path.Combine(root.Path, "party2");
        File.WriteAllText(blocked, "a file where the folder should be");
        var sink = new Collect();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        string own;
        try
        {
            own = TraceChannel.GameDirectory(blocked, "10.0.0.1#6");
        }
        finally
        {
            Log.Logger = previous;
        }

        List<LogEvent> mine;
        lock (sink.Events)
        {
            mine = [.. sink.Events.Where(e => e.RenderMessage().Contains(blocked, StringComparison.Ordinal))];
        }

        var warning = Assert.Single(mine);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Contains(own, warning.RenderMessage());
    }

    private static string OldMark(TempRoot root, string name, TimeSpan traceAge)
    {
        var folder = Path.Combine(root.Path, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TraceChannel.ServerGameFile), "10.0.0.1#5");
        var trace = Path.Combine(folder, "client1" + TraceFormat.Extension);
        File.WriteAllText(trace, "x");
        File.SetLastWriteTimeUtc(trace, DateTime.UtcNow - traceAge);
        return folder;
    }

    [Fact]
    public void AMarkWithoutAJoinTimeCountsFromTheNewestTraceInItsFolder()
    {
        using var root = new TempRoot();
        var stale = OldMark(root, "party1", TimeSpan.FromHours(4));
        var recent = OldMark(root, "party2", TimeSpan.FromHours(1));

        Assert.Equal(stale + "~2", TraceChannel.GameDirectory(stale, "10.0.0.1#5", DateTime.UtcNow));
        Assert.Equal(recent, TraceChannel.GameDirectory(recent, "10.0.0.1#5", DateTime.UtcNow));
    }

    [Fact]
    public void AnEmptyMarkLeftByAKilledProcessIsTakenOverAndAFreshOneWaitedForBriefly()
    {
        using var root = new TempRoot();
        var abandoned = Path.Combine(root.Path, "party1");
        Directory.CreateDirectory(abandoned);
        var mark = Path.Combine(abandoned, TraceChannel.ServerGameFile);
        File.WriteAllText(mark, "");
        File.SetLastWriteTimeUtc(mark, DateTime.UtcNow.AddSeconds(-10));
        var fresh = Path.Combine(root.Path, "party2");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, TraceChannel.ServerGameFile), "");

        var timer = Stopwatch.StartNew();
        var takenOver = TraceChannel.GameDirectory(abandoned, "10.0.0.1#5");
        var tookOver = timer.Elapsed;
        timer.Restart();
        var next = TraceChannel.GameDirectory(fresh, "10.0.0.1#6");
        var waited = timer.Elapsed;

        Assert.Equal(abandoned, takenOver);
        Assert.True(tookOver < TimeSpan.FromMilliseconds(200), $"took {tookOver.TotalMilliseconds:0} ms");
        Assert.Equal(fresh + "~2", next);
        Assert.True(waited < TimeSpan.FromMilliseconds(700), $"waited {waited.TotalMilliseconds:0} ms");
    }
}

public class BuffAndBurrowTests
{
    [Fact]
    public void ABuffIsNotFightingAndTheStatesStillSumToTheInterval()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: CharacterClass.Paladin)
            .Received(0.2, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5170, 2015))
            .OutOfTown(0.5)
            .Received(1, SyntheticGame.SetSkill(1, Hand.Right, Skill.HolyShield))
            .Sent(2, SyntheticGame.Location(0x0C, 5160, 2010))
            .Received(4, SyntheticGame.SetSkill(1, Hand.Right, Skill.BlessedHammer));
        for (var at = 5.0; at < 8; at += 0.5)
        {
            file.Sent(at, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
        }

        file.Ticks(8, 12).WriteTo(folder);
        var query = GameQuery.Open(folder);
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Fights: new FightObserver()), x => [x.Activity, x.Fights])[0];
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var stretches = o.Activity.Stretches(TimeSpan.Zero, end, []);

        var skills = new SkillSplit();
        skills.Add(o.Fights, stretches, TimeSpan.Zero, end);

        Assert.NotEqual(ActivityState.Fighting, stretches.Single(s => s.Start <= TimeSpan.FromSeconds(2.5) && TimeSpan.FromSeconds(2.5) < s.End).State);
        Assert.False(skills.Skills.ContainsKey("HolyShield"));
        Assert.Equal(end, stretches.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration));
    }

    [Fact]
    public void ADeathMaulerThatWentUnderIsAttackingASubmergedUnit()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(1, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5070, 1910))
            .Received(2, SyntheticGame.SkillOnPoint(0x30, Skill.Deathmaul, 5070, 1910));
        for (var i = 0; i < 3; i++)
        {
            file.Sent(3 + i, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
        }

        file.Ticks(6, 45, 5057, 1902).WriteTo(folder);

        var found = Assert.Single(Game1.Replayed(folder, "client1", new StaleUnitCheck()));

        Assert.Equal(StaleUnitCheck.SubmergedKind, found.Kind);
        Assert.Contains("went under (Deathmaul)", found.Detail);
    }
}

public class SendRateTests
{
    private static string Requests(TempRoot root, string name, double every, double from, double to)
    {
        var folder = Game1.Folder(root, name);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1);
        for (var at = from; at < to; at += every)
        {
            file.Sent(at, SyntheticGame.Unit(0x4B, EntityType.Player, 1));
        }

        for (var at = 2.0; at < 290; at += 0.25)
        {
            file.Sent(at, SyntheticGame.Location(0x03, 5160, 2010));
        }

        file.Ticks(2, 300).WriteTo(folder);
        return folder;
    }

    [Fact]
    public void ARareRequestSentEveryTwoSecondsForFortySecondsIsReportedAndNormalPlayIsNot()
    {
        using var root = new TempRoot();

        var burst = Game1.Incidents(Requests(root, "burst", 2, 100, 146), checks: "bot").Found.Where(i => i.Kind == "unusual send rate").ToList();
        var steady = Game1.Incidents(Requests(root, "steady", 30, 10, 290), checks: "bot").Found.Where(i => i.Kind == "unusual send rate").ToList();

        var line = Assert.Single(burst);
        Assert.Contains("RequestEntityUpdate (0x4B) sent 23 times", line.Detail);
        Assert.Empty(steady);
    }
}

public class StuckCursorBoundaryTests
{
    private static string Unanswered(TempRoot root, string name, params double[] at)
    {
        var folder = Game1.Folder(root, name);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).Received(1, SyntheticGame.Item(Core.D2GS.Items.Action.ToCursor, 0x40));
        foreach (var time in at)
        {
            file.Sent(time, Packets.Insert(0x40));
        }

        file.Received(12, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        return folder;
    }

    [Fact]
    public void ThreeUnansweredCursorPacketsOverFiveSecondsAreAStuckCursor()
    {
        using var root = new TempRoot();

        var stuck = Assert.Single(Game1.Replayed(Unanswered(root, "three", 2, 4.5, 7.1), "client1", new StuckCursorCheck()));

        Assert.Contains("3 cursor packets went unanswered", stuck.Detail);
    }

    [Fact]
    public void TwoUnansweredCursorPacketsOverFiveSecondsAreNot()
    {
        using var root = new TempRoot();

        Assert.Empty(Game1.Replayed(Unanswered(root, "two", 2, 9), "client1", new StuckCursorCheck()));
    }
}
