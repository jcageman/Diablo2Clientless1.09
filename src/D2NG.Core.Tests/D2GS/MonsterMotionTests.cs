using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.MonsterData;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class MonsterMotionTests
{
    private const double Walk6 = 6 * 0.75 * 25 / 16.0;

    private static readonly MonsterSpeedTable Speeds = MonsterSpeedTable.From(MonsterResistTable.FromEntries(
    [
        new MonsterResistEntry { ClassId = (int)NPCCode.Zealot, Class = "Zealot", Velocity = 6, Run = 11, MeleeRange = 1 },
        new MonsterResistEntry { ClassId = (int)NPCCode.CouncilMember1, Class = "Council Member", Velocity = 6, Run = 12 },
        new MonsterResistEntry { ClassId = (int)NPCCode.CouncilMember2, Class = "Council Member", Velocity = 10, Run = 12 },
    ], null));

    private static readonly MotionRules Rules = new() { Speeds = Speeds };

    private static readonly MotionUnit Zealot = new(NPCCode.Zealot, false, false, false, false);

    private static MotionUpdate Update(MotionUpdateKind kind, ushort x, ushort y, byte runBonus = 0, uint target = 0)
        => new(kind, 0, 1, new Point(x, y), EntityType.Player, target, runBonus);

    private static (double X, double Y) Walk(MotionSnapshot start, MotionUpdate move, double seconds, MotionRules rules = null)
    {
        rules ??= Rules;
        var moving = rules.Next(start, move, 0, (start.AnchorX, start.AnchorY), Zealot);
        return rules.PositionAt(moving, seconds, null, Zealot);
    }

    [Fact]
    public void SpeedIsVelocityAtSeventyFivePercentInSubtilesPerSecond()
    {
        Assert.Equal(Walk6, Rules.Speed(Zealot, 0), 9);
        Assert.Equal(Walk6 * 1.83, Rules.Speed(Zealot, 83), 9);
        Assert.Equal(6 * 1.58 * 25 / 16.0, new MotionRules { Speeds = Speeds, RunBonus = RunBonusFormula.Added }.Speed(Zealot, 83), 9);
        Assert.Equal(6 * 1.25 * 25 / 16.0, Rules.Speed(new MotionUnit(NPCCode.CouncilMember1, true, false, true, false), 0), 9);
        Assert.Equal(0, Rules.Speed(new MotionUnit(NPCCode.Skeleton, false, false, false, false), 0));
    }

    [Fact]
    public void TheRealmOverrideReplacesTheBinVelocity()
    {
        var council = new MotionUnit(NPCCode.CouncilMember2, false, false, true, false);
        Assert.Equal(8 * 0.75 * 25 / 16.0, Rules.Speed(council, 0), 9);
        Assert.Equal(10 * 0.75 * 25 / 16.0, new MotionRules { Speeds = Speeds.WithOverrides(null) }.Speed(council, 0), 9);
        Assert.True(Speeds.TryGetFileValue(NPCCode.CouncilMember2, out var file));
        Assert.Equal(10, file.Velocity);
    }

    [Fact]
    public void AWalkStartsAtTheAnchorMovesAlongTheLineAndStopsOnTheDestination()
    {
        var start = MotionRules.Start(new Point(100, 100), 0, 0x56);
        var move = Update(MotionUpdateKind.MoveToPoint, 130, 140);

        Assert.Equal((100, 100), Walk(start, move, 0));
        var travelled = Walk6 * 2;
        var (x, y) = Walk(start, move, 2);
        Assert.Equal(100 + 30 * travelled / 50, x, 9);
        Assert.Equal(100 + 40 * travelled / 50, y, 9);
        Assert.Equal((130, 140), Walk(start, move, 60));
    }

    [Fact]
    public void ANewWalkStartsFromWhereThePreviousOneHadGot()
    {
        var start = MotionRules.Start(new Point(100, 100), 0, 0x56);
        var first = Rules.Next(start, Update(MotionUpdateKind.MoveToPoint, 100, 150), 0, (100, 100), Zealot);
        var midway = Rules.PositionAt(first, 2, null, Zealot);
        var second = Rules.Next(first, Update(MotionUpdateKind.MoveToPoint, 50, 114), 2 * TimeSpan.TicksPerSecond, midway, Zealot);

        Assert.Equal(midway.X, second.AnchorX);
        Assert.Equal(midway.Y, second.AnchorY);
        Assert.Equal(new Point(100, 100), second.Exact);
        Assert.Equal(new Point(50, 114), second.Destination);
    }

    [Fact]
    public void AChaseHeadsForItsTargetAndStopsShortOfIt()
    {
        var start = MotionRules.Start(new Point(100, 100), 0, 0x56);
        var chase = Rules.Next(start, Update(MotionUpdateKind.MoveToUnit, 100, 100, target: 1), 0, (100, 100), Zealot);
        Assert.Equal(MotionMode.MovingToUnit, chase.Mode);

        Assert.Equal(100 + Walk6, Rules.PositionAt(chase, 1, new Point(100, 120), Zealot).Y, 9);
        Assert.Equal((100, 118), Rules.PositionAt(chase, 60, new Point(100, 120), Zealot));
        Assert.Equal((100, 117), new MotionRules { Speeds = Speeds, ChaseStopsAtMeleeRange = true }.PositionAt(chase, 60, new Point(100, 120), Zealot));
        Assert.Equal((100, 100), Rules.PositionAt(chase, 60, null, Zealot));
        Assert.Equal((100, 100), Rules.PositionAt(chase, 60, new Point(101, 101), Zealot));
    }

    [Fact]
    public void RunBonusAndExtraFastSpeedTheMoveUp()
    {
        var start = MotionRules.Start(new Point(100, 100), 0, 0x56);
        Assert.Equal(100 + Walk6 * 1.83, Walk(start, Update(MotionUpdateKind.MoveToPoint, 100, 200, runBonus: 83), 1).Y, 9);

        var fast = new MotionUnit(NPCCode.CouncilMember1, true, false, true, false);
        var moving = Rules.Next(start, Update(MotionUpdateKind.MoveToPoint, 100, 200), 0, (100, 100), fast);
        Assert.Equal(100 + 6 * 1.25 * 25 / 16.0, Rules.PositionAt(moving, 1, null, fast).Y, 9);
    }

    [Fact]
    public void ExactPacketsReanchorAndDyingFreezesWhereTheMonsterHadGot()
    {
        var start = MotionRules.Start(new Point(100, 100), 0, 0x56);
        var moving = Rules.Next(start, Update(MotionUpdateKind.MoveToPoint, 100, 200), 0, (100, 100), Zealot);
        var attacking = Rules.Next(moving, Update(MotionUpdateKind.Attack, 100, 105), 10, (100, 107), Zealot);
        Assert.Equal(MotionMode.Acting, attacking.Mode);
        Assert.Equal(new Point(100, 105), attacking.Exact);
        Assert.Equal((100, 105), Rules.PositionAt(attacking, 5, null, Zealot));

        var dying = Rules.Next(moving, new MotionUpdate(MotionUpdateKind.Dying, 0x69, 1, null), 10, (100, 107), Zealot);
        Assert.Equal(MotionMode.Dead, dying.Mode);
        Assert.Equal((100, 107), Rules.PositionAt(dying, 5, null, Zealot));
        Assert.Equal(new Point(100, 100), dying.Exact);
    }

    [Fact]
    public void WithoutSpeedDataAMonsterStaysWhereItWasLastSeen()
    {
        var oldFile = Path.Combine(Path.GetTempPath(), "monster-resists-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(oldFile, """{ "Monsters": [ { "ClassId": 237, "Class": "Zealot" } ] }""");
        try
        {
            Assert.False(MonsterSpeedTable.Load(oldFile).HasData);
        }
        finally
        {
            File.Delete(oldFile);
        }

        Assert.False(MonsterSpeedTable.Load(null).HasData);
        var rules = new MotionRules { Speeds = MonsterSpeedTable.Empty };
        var start = MotionRules.Start(new Point(100, 100), 0, 0x56);
        Assert.Equal((100, 100), Walk(start, Update(MotionUpdateKind.MoveToPoint, 100, 200), 5, rules));
    }

    [Fact]
    public void ConfidenceSaysWhichSpeedsWereMeasuredOnTheRealm()
    {
        var table = MonsterSpeedTable.From(MonsterResistTable.FromEntries(
            [.. new[] { NPCCode.Zealot, NPCCode.CouncilMember2, NPCCode.CouncilMember3 }.Select(c => new MonsterResistEntry { ClassId = (int)c, Velocity = 6 })], null));
        Assert.Equal(SpeedConfidence.Measured, table.Confidence(NPCCode.Zealot));
        Assert.Equal(SpeedConfidence.RealmOverride, table.Confidence(NPCCode.CouncilMember2));
        Assert.Equal(SpeedConfidence.FileOnly, table.Confidence(NPCCode.CouncilMember3));
        Assert.Equal(SpeedConfidence.Unknown, table.Confidence(NPCCode.Mauler));
    }

    [Theory]
    [InlineData(NPCCode.Ormus, null, true)]
    [InlineData(NPCCode.IronWolf, null, true)]
    [InlineData(NPCCode.Zombie, "Zombie", false)]
    [InlineData((NPCCode)294, "Dummy", true)]
    [InlineData((NPCCode)149, "dummy", true)]
    [InlineData((NPCCode)560, "Act 5 Hireling 1hs", true)]
    public void FriendlyUnitsAreTownFolkHirelingsSummonsAndDummies(NPCCode code, string fileClass, bool friendly)
        => Assert.Equal(friendly, FriendlyUnits.IsFriendly(code, fileClass));

    [Theory]
    [InlineData("""{ "Monsters": null }""")]
    [InlineData("""{ "Monsters": [ null ] }""")]
    [InlineData("null")]
    [InlineData("not json")]
    public void AnUnreadableSpeedFileGivesTheEmptyTable(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), "monster-resists-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, content);
        try
        {
            Assert.Same(MonsterSpeedTable.Empty, MonsterSpeedTable.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref Ticks);

        public void Advance(double seconds) => Interlocked.Add(ref Ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }

    private static (ActData Act, MonsterMotionSettings Settings, ManualClock Clock) NewAct(Func<EntityType, uint, Point> targets = null)
    {
        var clock = new ManualClock();
        var settings = new MonsterMotionSettings { Clock = clock, Rules = Rules };
        return (new ActData { Motion = new MotionContext(settings, targets ?? ((_, _) => null)) }, settings, clock);
    }

    private const string Assign = "56 a7 01 00 00 59 01 59 15 4f 08 80 01 02 00 ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 06 07 00 00 00 00 00 00 00";

    private static void Apply(ActData act, string hex) => act.ApplyMotion(MotionUpdate.Read(MonsterMotionPacketTests.Packet(hex).Raw));

    [Fact]
    public void TheSwitchPicksWhichPositionLocationReturns()
    {
        var (act, settings, clock) = NewAct();
        act.AddNPC(new AssignNpcPacket(MonsterMotionPacketTests.Packet(Assign)));
        var npc = act.WorldObjects[(0x1A7, EntityType.NPC)];
        var start = new Point(0x1559, 0x084F);
        var destination = new Point(0x1559, 0x084F + 40);

        npc.Location = destination;
        npc.ApplyMotion(new MotionUpdate(MotionUpdateKind.MoveToPoint, 0x67, 0x1A7, destination));
        clock.Advance(1);

        Assert.Equal(MonsterMotion.Legacy, settings.Mode);
        Assert.Equal(destination, npc.Location);
        settings.Mode = MonsterMotion.Anchored;
        Assert.Equal(start, npc.Location);
        settings.Mode = MonsterMotion.Simulated;
        var extraFast = 6 * 1.25 * 25 / 16.0;
        Assert.Equal(new Point(start.X, (ushort)Math.Round(start.Y + extraFast)), npc.Location);

        Assert.Equal(destination, npc.Destination);
        Assert.Equal(MotionMode.MovingToPoint, npc.MotionMode);
        Assert.Equal(extraFast, npc.Speed, 9);
        Assert.Equal(TimeSpan.FromSeconds(1), npc.LocationAge);
        Assert.Equal(start, npc.LastKnownLocation);
        Assert.Equal(destination, npc.LegacyLocation);
    }

    [Fact]
    public void AChaserFollowsItsTargetAsTheClientKnowsIt()
    {
        var target = new Point(0x1559, 0x084F + 30);
        var (act, settings, clock) = NewAct((type, id) => type == EntityType.Player && id == 3 ? target : null);
        settings.Mode = MonsterMotion.Simulated;
        act.AddNPC(new AssignNpcPacket(MonsterMotionPacketTests.Packet(Assign)));
        Apply(act, "68 a7 01 00 00 00 59 15 4f 08 00 03 00 00 00 01 00 0d 00 05");
        var npc = act.WorldObjects[(0x1A7, EntityType.NPC)];

        Assert.Equal(3u, npc.ChaseTargetId);
        clock.Advance(60);
        Assert.Equal(new Point(0x1559, 0x084F + 28), npc.Location);
        Assert.Equal(new Point(0x1559, 0x084F), npc.LegacyLocation);
    }

    [Fact]
    public void AReaderNeverSeesHalfAnUpdate()
    {
        var (act, settings, clock) = NewAct();
        settings.Mode = MonsterMotion.Simulated;
        act.AddNPC(new AssignNpcPacket(MonsterMotionPacketTests.Packet(Assign)));
        var npc = act.WorldObjects[(0x1A7, EntityType.NPC)];
        var a = new Point(1000, 1000);
        var b = new Point(2000, 3000);
        // Counts, not a time window: under a loaded test run either thread can be starved for the whole of a
        // short window. Both keep going until enough reads have overlapped writes.
        const int Overlapping = 20_000;
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var torn = 0;
        var reads = 0;
        var writes = 0;
        var readerDone = false;

        var writer = new Thread(() =>
        {
            var i = 0;
            while (!Volatile.Read(ref readerDone) && !giveUp.IsCancellationRequested)
            {
                npc.ApplyMotion(new MotionUpdate(MotionUpdateKind.Stop, 0x6D, 0x1A7, i++ % 2 == 0 ? a : b));
                clock.Advance(0.001);
                Interlocked.Increment(ref writes);
            }
        });
        writer.Start();
        var readsWhileWriting = 0;
        while ((readsWhileWriting < Overlapping || Volatile.Read(ref writes) < Overlapping) && !giveUp.IsCancellationRequested)
        {
            var writesBefore = Volatile.Read(ref writes);
            var motion = npc.Motion;
            var location = npc.Location;
            reads++;
            if (Volatile.Read(ref writes) != writesBefore)
            {
                readsWhileWriting++;
            }

            if (motion.AnchorX != motion.Exact.X || motion.AnchorY != motion.Exact.Y || motion.AnchorAt != motion.ExactAt
                || (location != a && location != b && location != new Point(0x1559, 0x084F)))
            {
                torn++;
            }
        }

        Volatile.Write(ref readerDone, true);
        writer.Join();
        Assert.False(giveUp.IsCancellationRequested, $"only {readsWhileWriting} reads overlapped a write in 60s");
        Assert.True(reads >= Overlapping);
        Assert.Equal(0, torn);
    }
}
