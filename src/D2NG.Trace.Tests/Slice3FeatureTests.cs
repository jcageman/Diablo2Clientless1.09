using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace D2NG.Trace.Tests;

internal static class Game1
{
    public static readonly DateTime Start = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    public static string Folder(TempRoot root, string name = "party1") => Path.Combine(HandTrace.DateFolder(root.Path, Start), name);

    public static string Date(TempRoot root) => HandTrace.DateFolder(root.Path, Start);

    /// <summary>Joined in the Rogue Encampment at (5057, 1902), then moved by the server into the Blood Moor at (5160, 2010) at the given time.</summary>
    public static HandTrace OutOfTown(this HandTrace file, double at, ushort x = 5160, ushort y = 2010, Area area = Area.BloodMoor)
        => file.Received(at, Packets.MapReveal(x, y, area)).Received(at + 0.01, SyntheticGame.Reassign(1, x, y));

    public static HandTrace Ticks(this HandTrace file, double from, double to, ushort x = 5160, ushort y = 2010, int life = 1000)
    {
        for (var at = from; at <= to; at += 1)
        {
            file.Received(at, SyntheticGame.LifeMana(life, 100, x, y));
        }

        return file;
    }

    public static (List<Incident> Found, List<string> Notes) Incidents(string folder, TraceHistory history = null, MapSource maps = null, string checks = "all")
        => Query.Incidents.Find(GameQuery.Open(folder), new IncidentOptions { Checks = checks, History = history ?? TraceHistory.InMemory(), Maps = maps });

    public static List<Incident> Replayed(string folder, string client, IncidentCheck check)
    {
        ClientReplay.Run(GameTrace.Open(folder), client, TimeSpan.MaxValue, [check])?.Replayer.Dispose();
        return [.. check.Found];
    }
}

public class ActivityTests
{
    [Fact]
    public void TheStatesFollowTheirPrecedenceAndSumExactlyToTheInterval()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(2)
            .Sent(5, SyntheticGame.Location(0x0C, 5165, 2012))
            .Sent(5.5, Packets.Pickup(0x40))
            .Sent(8, Packets.Interact(0x5))
            .Received(16, SyntheticGame.SetSkill(1, Hand.Right, Skill.Teleport))
            .Sent(16.5, SyntheticGame.Location(0x0C, 5190, 2030))
            .Received(16.7, SyntheticGame.Reassign(1, 5190, 2030))
            .Ticks(17, 20, 5190, 2030)
            .WriteTo(folder);
        var query = GameQuery.Open(folder);
        var activity = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => new ActivityObserver(), o => [o])[0].Observers;
        var to = TimeSpan.FromSeconds(25.05);

        var stretches = activity.Stretches(TimeSpan.Zero, to, [(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15))]);
        ActivityState At(double seconds) => stretches.Single(s => s.Start <= TimeSpan.FromSeconds(seconds) && TimeSpan.FromSeconds(seconds) < s.End).State;

        Assert.Equal(to, stretches.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration));
        Assert.Equal(TimeSpan.Zero, stretches[0].Start);
        Assert.All(stretches.Zip(stretches.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
        Assert.Equal(ActivityState.Town, At(1));
        Assert.Equal(ActivityState.Walking, At(2.5));
        Assert.Equal(ActivityState.StandingIdle, At(4));
        Assert.Equal(ActivityState.Fighting, At(6));
        Assert.Equal(ActivityState.Picking, At(6.8));
        Assert.Equal(ActivityState.Npc, At(9));
        Assert.Equal(ActivityState.StandingWaiting, At(13));
        Assert.Equal(ActivityState.Teleporting, At(17.2));
        Assert.Equal(ActivityState.StandingIdle, At(19));
        Assert.Equal(ActivityState.Out, At(22));
        var split = new ActivitySplit();
        split.Add(stretches, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(5), split.Total);
    }

    [Fact]
    public void StepsComeFromTypedEventsAndFromTimingLinesAndNest()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 120).WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(10, "step", "client1", "party1", ("name", "cross A->B"), ("attempt", 1), ("phase", "start"))
            .Typed(30, "step", "client1", "party1", ("name", "cross A->B"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"))
            .Typed(50, "step", "client1", "party1", ("name", "cross B->C"), ("attempt", 1), ("phase", "end"), ("outcome", "failed"))
            .Log(100, LogEventLevel.Information, "TIMING step {Step} {Outcome} in {Seconds:0}s", ("Step", "Khalim's Will"), ("Outcome", "ok"), ("Seconds", 95.0))
            .WriteTo(Game1.Date(root), "115900");

        var steps = Steps.Of(GameQuery.Open(folder));

        var party = Assert.Single(steps, s => s.Name == "Khalim's Will");
        Assert.Null(party.Client);
        Assert.Equal(TimeSpan.FromSeconds(5), party.Start);
        var measured = Assert.Single(steps, s => s.Name == "cross A->B");
        Assert.Same(party, measured.Parent);
        Assert.False(party.Leaf);
        var cross = Assert.Single(steps, s => s.Name == "cross B->C");
        Assert.True(cross.StartInferred);
        Assert.False(cross.StartUnknown);
        Assert.Equal(TimeSpan.FromSeconds(30), cross.Start);
        Assert.Same(party, cross.Parent);
        Assert.True(cross.Leaf);
    }

    [Fact]
    public void AStepAroundOnlyAnInferredChildStaysALeafAndTheChildsStartIsUnknown()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 120).WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(60, "step", "client1", "party1", ("name", "cross A->B"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"))
            .Log(100, LogEventLevel.Information, "TIMING step {Step} {Outcome} in {Seconds:0}s", ("Step", "Mephisto"), ("Outcome", "ok"), ("Seconds", 95.0))
            .WriteTo(Game1.Date(root), "115900");

        var steps = Steps.Of(GameQuery.Open(folder));

        var mephisto = Assert.Single(steps, s => s.Name == "Mephisto");
        Assert.True(mephisto.Leaf);
        var cross = Assert.Single(steps, s => s.Name == "cross A->B");
        Assert.True(cross.StartUnknown);
        Assert.Equal(TimeSpan.Zero, cross.Duration);
        Assert.Same(mephisto, cross.Parent);
    }
}

public class StateCheckTests
{
    [Fact]
    public void ACursorHeldWhileItemPacketsGoUnansweredIsFlagged()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).Received(1, SyntheticGame.Item(Core.D2GS.Items.Action.ToCursor, 0x40));
        for (var i = 0; i < 12; i++)
        {
            file.Sent(2 + i * 0.5, Packets.Insert(0x40));
        }

        file.WriteTo(folder);

        var found = Game1.Replayed(folder, "client1", new StuckCursorCheck());

        var stuck = Assert.Single(found);
        Assert.Contains("12 cursor packets went unanswered", stuck.Detail);
    }

    [Fact]
    public void ABriefHoldWhileMovingItemsIsNotFlagged()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1);
        for (var i = 0; i < 12; i++)
        {
            file.Received(1 + i, SyntheticGame.Item(Core.D2GS.Items.Action.ToCursor, 0x40))
                .Sent(1.2 + i, Packets.Insert(0x40))
                .Received(1.3 + i, SyntheticGame.Item(Core.D2GS.Items.Action.PutInContainer, 0x40));
        }

        file.WriteTo(folder);

        Assert.Empty(Game1.Replayed(folder, "client1", new StuckCursorCheck()));
    }

    [Fact]
    public void AnUnexplainedServerMoveIsAMismatchAndATeleportIsNot()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(5, SyntheticGame.Reassign(1, 5100, 1902))
            .Sent(20, SyntheticGame.Location(0x0C, 5130, 1902))
            .Received(20.3, SyntheticGame.Reassign(1, 5130, 1902))
            .WriteTo(folder);

        var found = Game1.Replayed(folder, "client1", new PositionMismatchCheck());

        var mismatch = Assert.Single(found);
        Assert.Equal(TimeSpan.FromSeconds(5), mismatch.Offset);
        Assert.Contains("(5100, 1902)", mismatch.Detail);
    }

    [Fact]
    public void AMonsterAttackedButSilentForHalfAMinuteIsStaleAndOneThatAnswersIsNot()
    {
        using var root = new TempRoot();
        string Write(string name, bool answered)
        {
            var folder = Game1.Folder(root, name);
            var file = new HandTrace("client1", "party1", Game1.Start).Join(1)
                .Received(1, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5070, 1910))
                .Sent(5, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30))
                .Sent(5.1, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30))
                .Sent(5.2, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
            if (answered)
            {
                file.Received(5.3, SyntheticGame.NpcHit(0x30, 50));
            }

            file.Ticks(6, 45, 5057, 1902).WriteTo(folder);
            return folder;
        }

        var stale = Assert.Single(Game1.Replayed(Write("stale", false), "client1", new StaleUnitCheck()));
        Assert.Contains("Zombie 0x30", stale.Detail);
        Assert.Empty(Game1.Replayed(Write("answered", true), "client1", new StaleUnitCheck()));
    }

    [Fact]
    public void LifeAboveMaxForSecondsIsFlaggedAndForAMomentIsNot()
    {
        using var root = new TempRoot();
        var held = Game1.Folder(root, "held");
        new HandTrace("client1", "party1", Game1.Start).Join(1).Received(1, SyntheticGame.LifeMana(1200, 100, 5057, 1902)).Received(4, SyntheticGame.LifeMana(1200, 100, 5057, 1902)).Received(5, SyntheticGame.LifeMana(900, 100, 5057, 1902)).WriteTo(held);
        var moment = Game1.Folder(root, "moment");
        new HandTrace("client1", "party1", Game1.Start).Join(1).Received(1, SyntheticGame.LifeMana(1200, 100, 5057, 1902)).Received(1.5, SyntheticGame.LifeMana(900, 100, 5057, 1902)).WriteTo(moment);

        Assert.Single(Game1.Replayed(held, "client1", new LifeAboveMaxCheck()));
        Assert.Empty(Game1.Replayed(moment, "client1", new LifeAboveMaxCheck()));
    }

    private static MapSource WallMap()
    {
        var rows = Enumerable.Range(0, 200).Select(_ => Enumerable.Range(0, 200).Select(x => x == 100 ? 1 : 0).ToArray()).ToArray();
        var json = JsonSerializer.Serialize(new { levelOrigin = new { x = 5100, y = 1950 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });
        return new MapSource("http://map.invalid", null, _ => json);
    }

    [Fact]
    public void StandingOnAThinWallIsNotWalkableOnlyWhenTheServerMovesTheClientOff()
    {
        using var root = new TempRoot();
        var corrected = Game1.Folder(root, "corrected");
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1, 5200, 2050).Ticks(2, 10, 5200, 2050)
            .Received(10.5, SyntheticGame.Reassign(1, 5195, 2050)).Ticks(11, 14, 5195, 2050).WriteTo(corrected);
        var lettingStand = Game1.Folder(root, "letting-stand");
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1, 5200, 2050).Ticks(2, 14, 5200, 2050).WriteTo(lettingStand);
        var beside = Game1.Folder(root, "beside");
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1, 5199, 2050).Ticks(2, 10, 5199, 2050).WriteTo(beside);

        var flagged = Assert.Single(Game1.Incidents(corrected, maps: WallMap(), checks: "state").Found, i => i.Kind == "not walkable");

        Assert.Contains("moved the client away to (5195, 2050)", flagged.Detail);
        Assert.DoesNotContain(Game1.Incidents(lettingStand, maps: WallMap(), checks: "state").Found, i => i.Kind == "not walkable");
        Assert.DoesNotContain(Game1.Incidents(beside, maps: WallMap(), checks: "state").Found, i => i.Kind == "not walkable");
    }

    [Fact]
    public void StandingWithNoGroundAroundIsNotWalkableWithoutACorrection()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1, 5160, 2400).Ticks(2, 10, 5160, 2400).WriteTo(folder);
        var rows = Enumerable.Range(0, 600).Select(y => Enumerable.Range(0, 200).Select(_ => y > 400 ? 1 : 0).ToArray()).ToArray();
        var json = JsonSerializer.Serialize(new { levelOrigin = new { x = 5100, y = 1950 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });

        var flagged = Assert.Single(Game1.Incidents(folder, maps: new MapSource("http://map.invalid", null, _ => json), checks: "state").Found, i => i.Kind == "not walkable");

        Assert.Contains("no walkable ground within 2 subtiles", flagged.Detail);
    }

    [Fact]
    public void WithoutAMapServiceTheWalkableCheckIsSkippedWithANote()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 10).WriteTo(folder);
        var down = new MapSource("http://127.0.0.1:9", null, _ => throw new System.Net.Http.HttpRequestException("connection refused"));

        var (found, notes) = Game1.Incidents(folder, maps: down, checks: "state");

        Assert.DoesNotContain(found, i => i.Kind == "not walkable");
        Assert.Contains(notes, n => n.Contains("unreachable", StringComparison.Ordinal));
    }
}

public class BotCheckTests
{
    [Fact]
    public void AttacksAtAUnitThatNeverGetsHitAreFlaggedAndHitsClearThem()
    {
        using var root = new TempRoot();
        string Write(string name, bool hits)
        {
            var folder = Game1.Folder(root, name);
            var file = new HandTrace("client1", "party1", Game1.Start).Join(1).Received(1, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5070, 1910));
            for (var at = 2.0; at < 25; at += 0.5)
            {
                file.Sent(at, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
                if (hits && (int)(at * 2) % 4 == 0)
                {
                    file.Received(at + 0.2, SyntheticGame.NpcHit(0x30, 90));
                }
            }

            file.WriteTo(folder);
            return folder;
        }

        Assert.Single(Game1.Replayed(Write("unanswered", false), "client1", new UnansweredAttackCheck()));
        Assert.Empty(Game1.Replayed(Write("answered", true), "client1", new UnansweredAttackCheck()));
    }

    [Fact]
    public void GoldHalvedInOneTownStayIsFlagged()
    {
        using var root = new TempRoot();
        string Write(string name, ushort after)
        {
            var folder = Game1.Folder(root, name);
            new HandTrace("client1", "party1", Game1.Start).Join(1)
                .Received(0.5, [0x1E, 0x0E, .. BitConverter.GetBytes((ushort)10000)])
                .Ticks(1, 4, 5057, 1902)
                .Received(5, [0x1E, 0x0E, .. BitConverter.GetBytes(after)])
                .Ticks(6, 8, 5057, 1902)
                .OutOfTown(9)
                .Ticks(10, 12)
                .WriteTo(folder);
            return folder;
        }

        Assert.Single(Game1.Replayed(Write("spent", 3000), "client1", new GoldDropCheck()));
        Assert.Empty(Game1.Replayed(Write("kept", 8000), "client1", new GoldDropCheck()));
    }

    [Fact]
    public void StandingIdleDuringAStepIsStuckButATypedWaitIsNot()
    {
        using var root = new TempRoot();
        string Write(string name, bool wait)
        {
            var folder = Game1.Folder(root, name);
            new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 50).WriteTo(folder);
            var sidecar = new SidecarFile(Game1.Start)
                .Typed(1, "step", "client1", "party1", ("name", "kill"), ("attempt", 1), ("phase", "start"))
                .Typed(50, "step", "client1", "party1", ("name", "kill"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"));
            if (wait)
            {
                sidecar.Typed(3, "wait", "client1", "party1", ("reason", "leader"), ("state", "entered")).Typed(49, "wait", "client1", "party1", ("reason", "leader"), ("state", "left"));
            }

            sidecar.WriteTo(Path.GetDirectoryName(folder), name == "idle" ? "115900" : "115901");
            return folder;
        }

        var idle = Write("idle", false);
        var stuck = Assert.Single(Game1.Incidents(idle, checks: "bot").Found, i => i.Kind == "stuck");
        Assert.Contains("during step kill", stuck.Detail);
        Assert.DoesNotContain(Game1.Incidents(Write("waiting", true), checks: "bot").Found, i => i.Kind == "stuck");
    }

    private static string WithSidecar(TempRoot root, Action<SidecarFile> fill, double length = 30, string process = "115900")
    {
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, length, 5057, 1902).WriteTo(folder);
        var sidecar = new SidecarFile(Game1.Start).Typed(0.5, "move", "client1", "party1", ("kind", "walk"), ("outcome", "arrived"));
        fill(sidecar);
        sidecar.WriteTo(Game1.Date(root), process);
        return folder;
    }

    [Fact]
    public void ThreeFailedMovesInOneSpotArePathFailuresAndTwoAreNot()
    {
        static Action<SidecarFile> Moves(int count) => s =>
        {
            for (var i = 0; i < count; i++)
            {
                s.Typed(2 + i, "move", "client1", "party1", ("from", new Point(5100, 1900)), ("to", new Point(5120, 1900)), ("kind", "walk"), ("outcome", "stuck"));
            }
        };

        using var three = new TempRoot();
        Assert.Single(Game1.Incidents(WithSidecar(three, Moves(3)), checks: "bot").Found, i => i.Kind == "path failures");
        using var two = new TempRoot();
        Assert.DoesNotContain(Game1.Incidents(WithSidecar(two, Moves(2)), checks: "bot").Found, i => i.Kind == "path failures");
    }

    [Fact]
    public void OneTemplateMoreThanTwentyTimesASecondIsAHotLoop()
    {
        static Action<SidecarFile> Lines(int count) => s =>
        {
            for (var i = 0; i < count; i++)
            {
                s.Log(5 + i * 0.03, LogEventLevel.Debug, "{Character} retries the portal", ("Character", "client1"));
            }
        };

        using var hot = new TempRoot();
        Assert.Single(Game1.Incidents(WithSidecar(hot, Lines(25)), checks: "bot").Found, i => i.Kind == "hot loop");
        using var calm = new TempRoot();
        Assert.DoesNotContain(Game1.Incidents(WithSidecar(calm, Lines(15)), checks: "bot").Found, i => i.Kind == "hot loop");
    }

    [Fact]
    public void AStepFailingWithinSecondsIsFlaggedAndOneFailingLaterIsNot()
    {
        static Action<SidecarFile> Step(double end) => s => s
            .Typed(2, "step", "client1", "party1", ("name", "kill"), ("attempt", 1), ("phase", "start"))
            .Typed(end, "step", "client1", "party1", ("name", "kill"), ("attempt", 1), ("phase", "end"), ("outcome", "failed"));

        using var quick = new TempRoot();
        Assert.Single(Game1.Incidents(WithSidecar(quick, Step(5)), checks: "bot").Found, i => i.Kind == "quick step failure");
        using var slow = new TempRoot();
        Assert.DoesNotContain(Game1.Incidents(WithSidecar(slow, Step(25)), checks: "bot").Found, i => i.Kind == "quick step failure");
    }

    /// <summary>
    /// Earlier games of the bot "party" on disk, party1..partyN two minutes apart, each with a kill step
    /// of the given seconds and the given warning, all from one process; then the game under test,
    /// party{N+1}, two minutes after the last, with its own step and lines. Returns its folder.
    /// </summary>
    internal static string GamesOfOneBot(TempRoot root, int earlier, double earlierSeconds, string earlierWarning, Action<SidecarFile, double> current, double currentLength = 45)
    {
        var sidecar = new SidecarFile(Game1.Start);
        for (var i = 1; i <= earlier + 1; i++)
        {
            var name = $"party{i}";
            var at = (i - 1) * 120.0;
            var length = i <= earlier ? 30 : currentLength;
            new HandTrace("client1", name, Game1.Start.AddSeconds(at)).Join(1, token: (ushort)(0x100 + i)).Ticks(1, length, 5057, 1902).WriteTo(Game1.Folder(root, name));
            sidecar.Typed(at + 0.5, "move", "client1", name, ("kind", "walk"), ("outcome", "arrived"));
            if (i <= earlier)
            {
                sidecar.Typed(at + 2, "step", "client1", name, ("name", "kill"), ("attempt", 1), ("phase", "start"))
                    .Typed(at + 2 + earlierSeconds, "step", "client1", name, ("name", "kill"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"))
                    .Log(at + 3, LogEventLevel.Warning, earlierWarning, ("Character", "client1"), ("Npc", "Ormus"));
            }
            else
            {
                current(sidecar, at);
            }
        }

        sidecar.WriteTo(Game1.Date(root), "115900");
        return Game1.Folder(root, $"party{earlier + 1}");
    }

    private static Action<SidecarFile, double> KillStep(double end) => (s, at) => s
        .Typed(at + 1, "step", "client1", $"party{(int)(at / 120) + 1}", ("name", "kill"), ("attempt", 1), ("phase", "start"))
        .Typed(at + end, "step", "client1", $"party{(int)(at / 120) + 1}", ("name", "kill"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"));

    [Fact]
    public void AStepOverThreeTimesItsMedianIsSlow()
    {
        using var slow = new TempRoot();
        var found = Game1.Incidents(GamesOfOneBot(slow, 10, 10, "{Character} cannot find {Npc} in town", KillStep(41)), checks: "bot").Found;
        Assert.Contains("4.0x the median 10s of 10 earlier runs", Assert.Single(found, i => i.Kind == "slow step").Detail);
        using var normal = new TempRoot();
        Assert.DoesNotContain(Game1.Incidents(GamesOfOneBot(normal, 10, 10, "{Character} cannot find {Npc} in town", KillStep(20)), checks: "bot").Found, i => i.Kind == "slow step");
    }

    [Fact]
    public void OnlyAWarningNoEarlierGameLoggedIsNew()
    {
        using var root = new TempRoot();
        var folder = GamesOfOneBot(root, 10, 10, "{Character} cannot find {Npc} in town", (s, at) => s
            .Log(at + 3, LogEventLevel.Warning, "{Character} cannot find {Npc} in town", ("Character", "client1"), ("Npc", "Ormus"))
            .Log(at + 4, LogEventLevel.Warning, "{Character} lost the party", ("Character", "client1")));

        var found = Game1.Incidents(folder, checks: "bot").Found.Where(i => i.Kind == "new warning").ToList();

        Assert.Contains("lost the party", Assert.Single(found).Detail);
        Assert.Contains("first time in 10 earlier games", found[0].Detail);
    }

    [Fact]
    public void MembersInDifferentAreasForAMinuteAreASplitParty()
    {
        using var root = new TempRoot();
        string Write(string name, Area second)
        {
            var folder = Game1.Folder(root, name);
            new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 80).WriteTo(folder);
            new HandTrace("client2", "party1", Game1.Start).Join(1).OutOfTown(1, 5300, 2200, second).Ticks(2, 80, 5300, 2200).WriteTo(folder);
            return folder;
        }

        Assert.Single(Game1.Incidents(Write("split", Area.ColdPlains), checks: "bot").Found, i => i.Kind == "party split");
        Assert.DoesNotContain(Game1.Incidents(Write("together", Area.BloodMoor), checks: "bot").Found, i => i.Kind == "party split");
    }
}

public class TileTests
{
    private static string CannedMap()
    {
        var rows = new int[6][];
        for (var y = 0; y < 6; y++)
        {
            rows[y] = [1, 0, 0, 7, 0, 0, -1];
        }

        return JsonSerializer.Serialize(new { levelOrigin = new { x = 100, y = 200 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new Dictionary<string, object> { ["13"] = new[] { new { x = 103, y = 202 } } } });
    }

    [Fact]
    public void TheGridShowsGroundDoorsAndMarks()
    {
        var map = new MapSource("http://map.invalid", null, _ => CannedMap()).Get(1, Difficulty.Normal, Area.BloodMoor);

        var lines = TileGrid.Render(map, new Point(103, 202), 2, [(new Point(102, 202), '@'), (new Point(104, 201), 'm')]);

        Assert.Equal(7, lines.Count);
        Assert.Equal("..#m.", lines[3][7..]);
        Assert.Equal(".@D..", lines[4][7..]);
        Assert.Equal(' ', TileGrid.Ground(map, 106, 202));
        Assert.Equal(' ', TileGrid.Ground(map, 99, 202));
        Assert.Equal('~', TileGrid.Ground(map, 100, 202));
    }

    [Fact]
    public void FetchedMapsAreCachedAndAnUnreachableServiceFailsInOneLine()
    {
        using var root = new TempRoot();
        var fetches = 0;
        var source = new MapSource("http://map.invalid", root.Path, _ =>
        {
            fetches++;
            return CannedMap();
        });
        source.Get(7, Difficulty.Normal, Area.BloodMoor);
        source.Get(7, Difficulty.Normal, Area.BloodMoor);
        new MapSource("http://map.invalid", root.Path, _ => throw new System.Net.Http.HttpRequestException("down")).Get(7, Difficulty.Normal, Area.BloodMoor);

        Assert.Equal(1, fetches);
        var down = new MapSource("http://127.0.0.1:9", null, _ => throw new System.Net.Http.HttpRequestException("No connection could be made\nmore"));
        var failure = Assert.Throws<MapUnavailableException>(() => down.Get(7, Difficulty.Normal, Area.ColdPlains));
        Assert.DoesNotContain('\n', failure.Message);
        Assert.Contains("http://127.0.0.1:9", failure.Message);
    }
}

public class BatchTests
{
    [Fact]
    public void EventFiltersMatchKindNameAndOutcome()
    {
        SidecarEntry Entry(string json) => new() { Kind = JsonDocument.Parse(json).RootElement.GetProperty("k").GetString(), Json = JsonDocument.Parse(json).RootElement };
        var cross = Entry("""{"k":"step","name":"cross A->B","phase":"end","outcome":"failed"}""");
        var stuck = Entry("""{"k":"move","kind":"walk","outcome":"stuck"}""");
        var chicken = Entry("""{"k":"decision","name":"chicken"}""");

        Assert.True(new EventFilter("step:cross*:failed").Matches(cross));
        Assert.False(new EventFilter("step:cross*:ok").Matches(cross));
        Assert.True(new EventFilter("move:stuck").Matches(stuck));
        Assert.True(new EventFilter("decision:chicken").Matches(chicken));
        Assert.False(new EventFilter("decision:death").Matches(chicken));
        Assert.True(new EventFilter("incident:stuck*").Matches(new Incident(TimeSpan.Zero, "c", "stuck cursor", "")));
    }

    [Fact]
    public void ASnapshotIsTakenAtEachStopInOnePass()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Received(5, SyntheticGame.LifeMana(400, 100, 5057, 1902)).Ticks(6, 10, 5057, 1902, 800).WriteTo(folder);
        var snapshots = new SnapshotObserver([(TimeSpan.FromSeconds(3), [("event", "a")]), (TimeSpan.FromSeconds(5.5), [("event", "b")])], StateQuery.ParseSelect("self"));

        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [snapshots])?.Replayer.Dispose();

        Assert.Equal(2, snapshots.Lines.Count);
        Assert.Contains("\"life\":1000", snapshots.Lines[0]);
        Assert.Contains("\"life\":400", snapshots.Lines[1]);
    }

    [Fact]
    public void SightingsAreCountedPerSpot()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(2, SyntheticGame.AssignObject(0x77, EntityCode.TownPortal, 5062, 1906))
            .Received(3, SyntheticGame.AssignObject(0x78, EntityCode.TownPortal, 5062, 1906))
            .WriteTo(folder);
        var seen = new SeenObserver("TownPortal", null);

        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [seen])?.Replayer.Dispose();

        Assert.Equal(2, seen.Sightings.Count);
        Assert.All(seen.Sightings, s => Assert.Equal(Area.RogueEncampment, s.Area));
    }

    [Fact]
    public void ExperiencePerMinuteCountsLossesAndLeavesOutLongGaps()
    {
        var start = Game1.Start;
        var rows = ExperienceKpi.Compute(
        [
            new("client1", "g1", start, start.AddMinutes(10), 1000, 2000, 0),
            new("client1", "g2", start.AddMinutes(11), start.AddMinutes(21), 1900, 3000, 0),
            new("client1", "g3", start.AddMinutes(60), start.AddMinutes(70), 3000, 4000, 1),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal(3000, row.Gained);
        Assert.Equal(100, row.Lost);
        Assert.Equal(TimeSpan.FromMinutes(31), row.Clock);
        Assert.Equal(1, row.ExcludedGaps);
        Assert.Equal(1, row.Deaths);
        Assert.Equal(1, row.UnexplainedFalls);
        Assert.Equal(3, row.Games);
    }

    [Fact]
    public void ExperienceFromPacketsStartsAtTheJoinTotal()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(1, [0x1C, .. BitConverter.GetBytes(50000u)])
            .Received(5, [0x1A, 200])
            .Received(6, [0x1B, .. BitConverter.GetBytes((ushort)1000)])
            .Received(60, SyntheticGame.LifeMana(1000, 100, 5057, 1902))
            .WriteTo(folder);
        var observer = new ExperienceObserver();

        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [observer])?.Replayer.Dispose();

        var file = Assert.Single(observer.Files);
        Assert.Equal(50000, file.First);
        Assert.Equal(51200, file.Last);
    }
}
