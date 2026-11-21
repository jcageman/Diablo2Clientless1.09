using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace D2NG.Trace.Tests;

internal static class Round6Packets
{
    /// <summary>0x5A type 6: "name1 was slain by"; the dword is the killer's class, byte 7 its unit type, name2 a killing player.</summary>
    public static byte[] Slain(string victim, uint killerClass, EntityType killerType = EntityType.NPC, string killerPlayer = "")
    {
        var packet = new byte[40];
        packet[0] = 0x5A;
        packet[1] = 6;
        packet[2] = 4;
        BitConverter.GetBytes(killerClass).CopyTo(packet, 3);
        packet[7] = (byte)killerType;
        Encoding.ASCII.GetBytes(victim).CopyTo(packet, 8);
        Encoding.ASCII.GetBytes(killerPlayer).CopyTo(packet, 24);
        return packet;
    }

    /// <summary>0x56 with the boss flag and the given enchantments in the nine enchantment bytes at 37.</summary>
    public static byte[] Boss(uint id, NPCCode code, ushort x, ushort y, params MonsterEnchantment[] enchantments)
    {
        var packet = SyntheticGame.AssignNpc(id, code, x, y);
        packet[13] = 0x01;
        for (var i = 0; i < enchantments.Length; i++)
        {
            packet[37 + i] = (byte)enchantments[i];
        }

        return packet;
    }

    public static byte[] Leave => [(byte)OutGoingPacket.LeaveGame];
}

public class DeathSignalTests
{
    private static string DiedAndLeft(TempRoot root)
    {
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 5)
            .Received(5.1, SyntheticGame.AssignNpc(0x30, NPCCode.BurningSoul, 5163, 2010))
            .Received(5.5, SyntheticGame.NpcAttack(0x30, 1, 5160, 2010))
            .Received(5.6, SyntheticGame.LifeMana(400, 100, 5160, 2010))
            .Received(5.8, Round6Packets.Slain("client1", (uint)NPCCode.BurningSoul))
            .Received(6.05, SyntheticGame.LifeMana(0, 100, 5160, 2010))
            .Received(6.051, SyntheticGame.Attribute(0x0E, 0))
            .Sent(6.053, Round6Packets.Leave)
            .WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(6.07, "decision", "client1", "party1", ("name", "chicken"), ("f", new (string, object)[] { ("life", 0), ("maxLife", 1000), ("reason", "lost 400 with 0 left") }))
            .WriteTo(Game1.Date(root), "115900");
        return folder;
    }

    [Fact]
    public void ADeathTheClientLeftBeforeItsCorpseCameIsADeathNamingItsKillerAndNotAChicken()
    {
        using var root = new TempRoot();

        var found = Game1.Incidents(DiedAndLeft(root)).Found;

        var death = Assert.Single(found, i => i.Kind == "death");
        Assert.Equal(TimeSpan.FromSeconds(5.8), death.Offset);
        Assert.Contains("slain by BurningSoul", death.Detail);
        Assert.Contains("\"was slain by\" message", death.Detail);
        Assert.Contains("gold on the person set to 0", death.Detail);
        Assert.Contains("the bot left 253 ms after dying", death.Detail);
        Assert.Contains("chicken", death.Detail);
        Assert.DoesNotContain(found, i => i.Kind == "chicken");
    }

    [Fact]
    public void ExperienceFallingBetweenTwoFilesIsReportedButNotCountedAsADeath()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 5)
            .Received(2.5, [0x1C, .. BitConverter.GetBytes(5_000_000u)])
            .Sent(6, Round6Packets.Leave)
            .WriteTo(folder);
        new HandTrace("client1", "party1", Game1.Start.AddSeconds(20)).Join(1).Ticks(1, 5, 5057, 1902)
            .Received(1.5, [0x1C, .. BitConverter.GetBytes(4_900_000u)])
            .WriteTo(folder, "client1-2");

        var found = Game1.Incidents(folder).Found;
        var observers = GameQuery.Open(folder).ReplayAll(["client1"], TimeSpan.MaxValue, () => new ExperienceObserver(), o => [o]);
        var row = Assert.Single(ExperienceKpi.Compute(observers.SelectMany(o => o.Observers.Files)));

        Assert.Contains("100,000 under the running total", Assert.Single(found, i => i.Kind == DeathCheck.ExperienceFellKind).Detail);
        Assert.DoesNotContain(found, i => i.Kind == "death");
        Assert.Equal(0, row.Deaths);
        Assert.Equal(1, row.UnexplainedFalls);
    }
}

public class DamageInferenceTests
{
    private static List<string> Damage(string folder, double from, double to)
    {
        var damage = new DamageObserver(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to));
        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [damage])?.Replayer.Dispose();
        return [.. damage.Report("client1")];
    }

    [Fact]
    public void ADropRightAfterAFireEnchantedBossDiedOnTheClientNamesItsDeathExplosionAsInference()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1, life: 352).OutOfTown(1).Ticks(2, 5, life: 266)
            .Received(5.1, Round6Packets.Boss(0x1CA, NPCCode.CouncilMember2, 5160, 2011, MonsterEnchantment.ExtraStrong, MonsterEnchantment.FireEnchanted, MonsterEnchantment.IsSuperUnique))
            .Received(6.04, SyntheticGame.NpcState(0x1CA, EntityState.Dieing, 0, 0, 0))
            .Received(6.2, SyntheticGame.LifeMana(101, 100, 5160, 2010))
            .WriteTo(folder);

        var report = Damage(folder, 6, 7);

        Assert.Contains(report, l => l.Contains("unattributed") && l.Contains("165"));
        Assert.Contains(report, l => l.Contains("INFERENCE") && l.Contains("likely: death explosion of FireEnchanted CouncilMember2 0x1CA, which died 0.16s before at distance 1"));
    }

    [Fact]
    public void ADropCreditedToAnAttackShowsTheAttackPacketsOwnTime()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 5)
            .Received(5.1, SyntheticGame.AssignNpc(0x30, NPCCode.BurningSoul, 5163, 2010))
            .Received(5.76, SyntheticGame.NpcAttack(0x30, 1, 5160, 2010))
            .Received(6.0, SyntheticGame.LifeMana(800, 100, 5160, 2010))
            .WriteTo(folder);

        var report = Damage(folder, 5.5, 7);

        Assert.Contains(report, l => l.Contains("00:06.000 -200") && l.Contains("at 00:05.760, 0.24s before"));
    }
}

public class MoveEventTests
{
    private static string WalkTheServerNeverMade(TempRoot root)
    {
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 10).WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(5, "move", "client1", "party1", ("from", new Point(5160, 2010)), ("to", new Point(5175, 2010)), ("area", "BloodMoor"), ("kind", "walk"), ("outcome", "arrived"))
            .Typed(6, "move", "client1", "party1", ("from", new Point(5160, 2010)), ("to", new Point(5162, 2011)), ("area", "BloodMoor"), ("kind", "walk"), ("outcome", "arrived"))
            .Typed(7, "move", "client1", "party1", ("from", new Point(5160, 2010)), ("to", new Point(0, 0)), ("area", "BloodMoor"), ("kind", "path-request"), ("outcome", "empty-path"), ("fallback", "direct move"))
            .WriteTo(Game1.Date(root), "115900");
        return folder;
    }

    [Fact]
    public void AnArrivedWalkTheServerPositionDoesNotConfirmIsMarkedAndAnIncident()
    {
        using var root = new TempRoot();
        var folder = WalkTheServerNeverMade(root);

        var lines = Timeline.Build(GameQuery.Open(folder), new TimelineOptions { Client = "client1" }).Select(Timeline.Format).ToList();
        var found = Game1.Incidents(folder, checks: "state").Found;

        Assert.Contains(lines, l => l.Contains("-> (5175, 2010)") && l.Contains("NOT CONFIRMED") && l.Contains("server-confirmed position (5160, 2010)") && l.Contains("15 away"));
        Assert.Contains(lines, l => l.Contains("-> (5162, 2011)") && !l.Contains("NOT CONFIRMED"));
        var incident = Assert.Single(found, i => i.Kind == MoveNotConfirmedCheck.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), incident.Offset);
    }

    [Fact]
    public void APathRequestToTheOriginSaysTheDestinationWasNotSet()
    {
        using var root = new TempRoot();

        var lines = Timeline.Build(GameQuery.Open(WalkTheServerNeverMade(root)), new TimelineOptions { Client = "client1" }).Select(Timeline.Format).ToList();

        Assert.Contains(lines, l => l.Contains("path-request") && l.Contains("(0, 0) (destination not set"));
    }
}

public class TimelineGapTests
{
    [Fact]
    public void IdleInTownIsListedAsTownIdle()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Received(0.07, Packets.MapReveal(5057, 1902, Area.RogueEncampment)).Ticks(1, 20, 5057, 1902).WriteTo(folder);

        var lines = Timeline.Build(GameQuery.Open(folder), new TimelineOptions { Client = "client1", Idle = true }).Select(Timeline.Format).ToList();

        Assert.Contains(lines, l => l.Contains("town idle") && l.Contains("RogueEncampment"));
    }

    [Fact]
    public void DebugOnAGameWithoutDebugLinesSaysSoAndASpanBegunEarlierSaysSince()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Received(1.5, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5162, 2010)).Ticks(2, 40);
        for (var at = 2.0; at <= 30; at += 0.5)
        {
            file.Sent(at, SyntheticGame.Unit(0x06, EntityType.NPC, 0x30));
        }

        file.WriteTo(folder);
        new SidecarFile(Game1.Start).Log(3, Serilog.Events.LogEventLevel.Information, "Fighting").WriteTo(Game1.Date(root), "115900");
        var query = GameQuery.Open(folder);
        var options = new TimelineOptions { Client = "client1", From = TimeSpan.FromSeconds(20), To = TimeSpan.FromSeconds(40), Debug = true };

        var lines = Timeline.Build(query, options).Select(Timeline.Format).ToList();

        Assert.Contains(Timeline.Notes(query, options), n => n.Contains("no Debug lines were recorded"));
        Assert.Contains(lines, l => l.Contains("since 00:02.000"));
    }
}

public class IncidentSummaryTests
{
    [Fact]
    public void TheSummaryPutsDeathsFirstAndTheChickensWithLeastLifeLeft()
    {
        Incident Chicken(double at, int life) => new(TimeSpan.FromSeconds(at), "client1", "chicken", $"low (life {life}/500)", "sidecar");
        var lines = IncidentSummary.Lines(
        [
            ("g#1", Chicken(10, 200)),
            ("g#2", new Incident(TimeSpan.FromSeconds(30), "client2", "death", "slain by Zombie at (1, 1); signals: x")),
            ("g#1", Chicken(20, 50)),
            ("g#3", Chicken(5, 120)),
            ("g#3", Chicken(6, 400)),
            ("g#1", new Incident(TimeSpan.FromSeconds(40), "client1", "stuck", "stood") { End = TimeSpan.FromSeconds(127) }),
        ]);

        Assert.StartsWith("deaths: 1: g#2 00:30.000 client2 slain by Zombie", lines[0]);
        Assert.StartsWith("chickens: 4, worst 3: g#1 00:20.000 client1 at 10% life; g#3 00:05.000 client1 at 24% life; g#1 00:10.000", lines[1]);
        Assert.Contains("stuck 87s", lines[2]);
    }

    [Fact]
    public void AGameWhoseTraceWasJustWrittenIsInProgress()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var path = new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 5).WriteTo(folder);

        var running = GameTrace.Open(folder).InProgress;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        Assert.True(running);
        Assert.False(GameTrace.Open(folder).InProgress);
    }
}

public class WaitReasonTests
{
    [Fact]
    public void StandingWaitingSplitsByTheTypedWaitsReason()
    {
        var at = new Point(1, 1);
        List<Stretch> stretches =
        [
            new(ActivityState.StandingWaiting, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10), at, "BloodMoor"),
            new(ActivityState.Walking, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), at, "BloodMoor"),
            new(ActivityState.StandingWaiting, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15), at, "BloodMoor"),
        ];
        List<(TimeSpan, TimeSpan, string)> waits = [(TimeSpan.Zero, TimeSpan.FromSeconds(10), "party"), (TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15), "low life")];

        var split = ActivityObserver.WaitingByReason(stretches, waits, TimeSpan.Zero, TimeSpan.FromSeconds(15));

        Assert.Equal("party 10s, low life 3s", ActivityObserver.RenderWaiting(split));
    }
}

public class AccuracyModelTests
{
    [Fact]
    public void WithoutSpeedsOnlyLegacyAndAnchoredAreScored()
    {
        Assert.Equal(["legacy", "anchored"], MotionAccuracy.ModelNames("all", speeds: false));
        Assert.Equal(["legacy", "anchored"], MotionAccuracy.ModelNames("legacy,anchored,simulated", speeds: false));
        Assert.Equal(["legacy", "anchored", "simulated"], MotionAccuracy.ModelNames("legacy,anchored,simulated", speeds: true));
        Assert.Null(MotionAccuracy.ModelNames("legacy,nonsense", speeds: true));
    }

    [Fact]
    public void AMonsterLegacyHoldsAtTheOriginIsLeftOutAndCounted()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var npcMoveNowhere = SyntheticGame.NpcMove(0x30, 0, 0);
        npcMoveNowhere[5] = 20;
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1)
            .Received(2, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5170, 2010))
            .Received(3, SyntheticGame.MonsterStop(0x30, 5170, 2010))
            .Received(4, npcMoveNowhere)
            .Received(5, SyntheticGame.MonsterStop(0x30, 5172, 2011))
            .WriteTo(folder);
        var accuracy = new MotionAccuracy(MotionAccuracy.StandardModels);

        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [accuracy])?.Replayer.Dispose();

        Assert.Equal(1, accuracy.Unplaced[0]);
        Assert.DoesNotContain(accuracy.Observations, o => o.Errors[0] > 1000);
    }
}

public class PlayerStopDecodingTests
{
    [Fact]
    public void PlayerStopShowsItsBytesNeutrallyAndTheLifeShare()
    {
        byte[] stop = [0x0D, 0x00, 0x01, 0x00, 0x00, 0x00, 0x13, 0xA1, 0x14, 0xB2, 0x14, 0x40, 0x00];

        var fields = PacketDecoder.Fields(Core.Tracing.TraceRecordKind.D2gsReceived, stop);

        Assert.DoesNotContain("Weaken", fields);
        Assert.Contains("byte6=19", fields);
        Assert.Contains("location=(5281, 5298)", fields);
        Assert.Contains("life=0%", fields);
    }
}

public class ImmuneUnitTests
{
    private static byte[] PartySkillOn(uint player, uint npc) => [0x4C, 0x00, .. BitConverter.GetBytes(player), 0x06, 0x00, 0x00, 0x01, .. BitConverter.GetBytes(npc), 0x00, 0x00];

    [Fact]
    public void AttacksTheServerCarriesOutWithLifeUnchangedNameTheUnitImmuneOrInvulnerable()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Received(1.5, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5161, 2010)).Ticks(2, 30);
        for (var at = 2.0; at <= 26; at += 0.5)
        {
            file.Sent(at, SyntheticGame.Unit(0x06, EntityType.NPC, 0x30));
            file.Received(at + 0.1, PartySkillOn(2, 0x30));
            file.Received(at + 0.2, SyntheticGame.MonsterStop(0x30, 5161, 2010));
        }

        file.WriteTo(folder);

        var found = Game1.Incidents(folder, checks: "bot").Found;

        var unanswered = Assert.Single(found, i => i.Kind == "unanswered attacks");
        Assert.Contains("life stayed at 100% throughout: immune or invulnerable?", unanswered.Detail);
    }
}

public class SendRateBaselineTests
{
    private static byte[] Resync => SyntheticGame.Unit(0x4B, EntityType.Player, 1);

    [Fact]
    public void ABurstOfATypeSentEveryMinuteIsFoundAgainstItsOwnMedian()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 600);
        for (var minute = 0; minute < 10; minute++)
        {
            foreach (var second in new[] { 5, 25, 45 })
            {
                file.Sent(minute * 60 + second, Resync);
            }
        }

        for (var at = 300.25; at < 340; at += 1)
        {
            file.Sent(at, Resync);
        }

        file.WriteTo(folder);

        var found = Game1.Incidents(folder, checks: "bot").Found;

        Assert.Single(found, i => i.Kind == "unusual send rate" && i.Detail.Contains("0x4B"));
    }

    [Fact]
    public void TwoBurstsLessThanAMinuteApartAreOneIncidentCountingEachSendOnce()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 600);
        for (var i = 0; i < 25; i++)
        {
            file.Sent(100 + i, Resync);
            file.Sent(169.5 + i, Resync);
        }

        file.WriteTo(folder);

        var found = Game1.Incidents(folder, checks: "bot").Found.Where(i => i.Kind == "unusual send rate").ToList();

        var burst = Assert.Single(found);
        Assert.Contains("sent 50 times", burst.Detail);
    }

    [Fact]
    public void NoHistoryComparesWithNothingAndSaysSo()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 30).WriteTo(folder);

        var (_, notes) = Query.Incidents.Find(GameQuery.Open(folder), new IncidentOptions { History = null });

        Assert.Contains(notes, n => n.Contains("--no-history"));
    }
}

public class TownEdgeTests
{
    [Fact]
    public void SteppingOutOfTownOnFootForAMomentKeepsTheStaysCause()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Received(0.07, Packets.MapReveal(5057, 1902, Area.RogueEncampment)).Ticks(1, 9, 5057, 1902)
            .OutOfTown(10).Ticks(10.5, 11.5)
            .Received(12, SyntheticGame.Reassign(1, 5057, 1902)).Ticks(12.5, 30, 5057, 1902)
            .WriteTo(folder);
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver()), x => [x.Activity, x.Town])[0];

        var stays = TownStays.Of("client1", o.Activity, o.Town, [], [], TownStays.Chickens(query, "client1"), end);

        Assert.Equal(2, stays.Count);
        Assert.All(stays, s => Assert.Equal("game start", s.Cause));
    }

    [Fact]
    public void TheLifeBeforeARejoinIsTimedToTheLeaveGameNotTheFileEnd()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(2).Ticks(3, 9)
            .Received(10, SyntheticGame.LifeMana(270, 100, 5160, 2010))
            .Sent(10.002, Round6Packets.Leave)
            .Received(10.5, SyntheticGame.LifeMana(260, 100, 5160, 2010))
            .WriteTo(folder);
        new HandTrace("client1", "party1", Game1.Start.AddSeconds(15)).Join(1).Ticks(1, 10, 5057, 1902).WriteTo(folder, "client1-2");
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver()), x => [x.Activity, x.Town])[0];

        var rejoin = TownStays.Of("client1", o.Activity, o.Town, [], [], null, end)[^1];

        Assert.Contains("life 27% 0.002s before leaving", rejoin.Evidence);
    }
}

public class FolderTakeoverTests
{
    [Fact]
    public void ATakeoverWhoseMarkCouldNotBeWrittenMovesOnToTheNextFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an open file only refuses another writer on Windows");
        using var root = new TempRoot();
        var abandoned = Path.Combine(root.Path, "party1");
        Directory.CreateDirectory(abandoned);
        var mark = Path.Combine(abandoned, Core.Tracing.TraceChannel.ServerGameFile);
        File.WriteAllText(mark, "");
        File.SetLastWriteTimeUtc(mark, DateTime.UtcNow.AddSeconds(-10));
        string claimed;
        using (new FileStream(mark, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            claimed = Core.Tracing.TraceChannel.GameDirectory(abandoned, "10.0.0.1#5");
        }

        Assert.Equal(abandoned + "~2", claimed);
    }
}
