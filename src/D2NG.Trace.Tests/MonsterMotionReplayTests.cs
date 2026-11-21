using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MCP;
using D2NG.Core.MonsterData;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class MonsterMotionReplayTests
{
    /// <summary>
    /// One Extra Fast Council Member minion of a recorded Normal act 3 game, seconds from its assign: it
    /// stands, chases a player, walks, attacks from a fixed spot, is hit, walks, dies. The 0x56, the first
    /// 44 packets after it and the last six up to the corpse; the chased player id is the recorded one.
    /// </summary>
    private const string CouncilMember = """
        0.000 56 a7 01 00 00 59 01 59 15 4f 08 80 01 02 00 ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 06 07 00 00 00 00 00 00 00
        0.000 6d a7 01 00 00 59 15 4f 08 80
        79.159 6d a7 01 00 00 59 15 4f 08 80
        89.518 6d a7 01 00 00 59 15 4f 08 80
        104.718 68 a7 01 00 00 00 59 15 4f 08 00 03 00 00 00 01 00 0d 00 05
        104.879 68 a7 01 00 00 00 59 15 51 08 00 03 00 00 00 01 00 0d 00 05
        105.038 68 a7 01 00 00 00 59 15 53 08 00 03 00 00 00 01 00 0d 00 05
        105.478 68 a7 01 00 00 00 59 15 59 08 00 03 00 00 00 01 00 0d 00 05
        105.998 68 a7 01 00 00 00 58 15 5e 08 00 03 00 00 00 01 00 0d 00 05
        106.118 67 a7 01 00 00 01 5b 15 5b 08 01 00 0d 00 05
        106.477 6d a7 01 00 00 5b 15 5b 08 80
        106.878 68 a7 01 00 00 00 5b 15 5b 08 00 03 00 00 00 01 00 0d 00 05
        107.118 68 a7 01 00 00 00 5b 15 5d 08 00 03 00 00 00 01 00 0d 00 05
        107.318 68 a7 01 00 00 00 5a 15 5f 08 00 03 00 00 00 01 00 0f 00 05
        107.798 68 a7 01 00 00 00 5c 15 5b 08 00 03 00 00 00 01 00 0d 00 05
        107.998 6d a7 01 00 00 5c 15 5d 08 80
        108.958 68 a7 01 00 00 00 5c 15 5d 08 00 03 00 00 00 01 00 0d 00 05
        109.078 68 a7 01 00 00 00 5b 15 5e 08 00 03 00 00 00 01 00 0f 00 05
        110.838 68 a7 01 00 00 00 61 15 60 08 00 03 00 00 00 01 00 0d 00 05
        111.237 68 a7 01 00 00 00 61 15 65 08 00 03 00 00 00 01 00 0f 00 05
        111.798 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        112.359 6d a7 01 00 00 5f 15 6a 08 80
        112.997 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        113.558 6d a7 01 00 00 5f 15 6a 08 7f
        114.158 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        114.797 6d a7 01 00 00 5f 15 6a 08 7f
        115.838 6c a7 01 00 00 0d 00 03 00 00 00 00 5f 15 6a 08
        116.437 6d a7 01 00 00 5f 15 6a 08 7f
        117.037 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        117.638 6d a7 01 00 00 5f 15 6a 08 7f
        118.239 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        118.837 6d a7 01 00 00 5f 15 6a 08 7f
        119.477 6c a7 01 00 00 0d 00 03 00 00 00 00 5f 15 6a 08
        120.037 6d a7 01 00 00 5f 15 6a 08 7f
        120.638 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        121.237 6d a7 01 00 00 5f 15 6a 08 7f
        121.838 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        122.437 6d a7 01 00 00 5f 15 6a 08 7f
        123.037 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        123.637 6d a7 01 00 00 5f 15 6a 08 7f
        124.718 6c a7 01 00 00 0d 00 03 00 00 00 00 5f 15 6a 08
        125.357 6d a7 01 00 00 5f 15 6a 08 7f
        126.397 6c a7 01 00 00 0d 00 03 00 00 00 00 5f 15 6a 08
        127.037 6d a7 01 00 00 5f 15 6a 08 7f
        127.597 6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08
        266.075 6d a7 01 00 00 5e 15 6e 08 16
        266.555 69 a7 01 00 00 06 5e 15 6e 08 00 00
        266.675 6d a7 01 00 00 5e 15 6e 08 00
        266.994 67 a7 01 00 00 01 58 15 68 08 01 00 0d 00 06
        267.035 69 a7 01 00 00 08 58 15 68 08 3f 1a
        267.875 69 a7 01 00 00 09 5e 15 6e 08 3f 00
        """;

    private static IEnumerable<(double At, byte[] Packet)> Sequence() => CouncilMember
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(' ', 2))
        .Select(parts => (double.Parse(parts[0], CultureInfo.InvariantCulture), Convert.FromHexString(parts[1].Replace(" ", "", StringComparison.Ordinal))));

    private sealed class ManualClock : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;
    }

    private static (Game Game, GameServer Server, ManualClock Clock) NewGame(MonsterMotion mode)
    {
        var server = new GameServer();
        var clock = new ManualClock();
        var game = new Game(server, clock) { MonsterMotion = mode };
        var stats = new byte[32];
        stats[13] = 3;
        game.SelectCharacter(new Character(SyntheticTrace.CharacterName, stats));
        foreach (var packet in SyntheticTrace.Joining())
        {
            server.Replay(new D2gsPacket(packet), sent: false);
        }

        return (game, server, clock);
    }

    private static MonsterSpeedTable CouncilMemberSpeeds
        => MonsterSpeedTable.From(MonsterResistTable.FromEntries([new MonsterResistEntry { ClassId = (int)NPCCode.CouncilMember1, Velocity = 6, Run = 12 }], null));

    /// <summary>The position handling before motion tracking, written out on its own: assign, 0x67, 0x6D, 0x69 get-hit and dead set it.</summary>
    private static Point OldHandler(Point current, byte[] packet) => packet[0] switch
    {
        0x55 or 0x56 => new Point(BitConverter.ToUInt16(packet, 7), BitConverter.ToUInt16(packet, 9)),
        0x67 => new Point(BitConverter.ToUInt16(packet, 6), BitConverter.ToUInt16(packet, 8)),
        0x6D => new Point(BitConverter.ToUInt16(packet, 5), BitConverter.ToUInt16(packet, 7)),
        0x69 when packet[5] is 6 or 9 => new Point(BitConverter.ToUInt16(packet, 6), BitConverter.ToUInt16(packet, 8)),
        _ => current,
    };

    [Theory]
    [InlineData(MonsterMotion.Legacy)]
    [InlineData(MonsterMotion.Anchored)]
    [InlineData(MonsterMotion.Simulated)]
    public void TheLegacyPositionIsWhatTheOldHandlersHeldInEveryMode(MonsterMotion mode)
    {
        var (game, server, clock) = NewGame(mode);
        game.MonsterMotionSettings.Rules = new MotionRules { Speeds = CouncilMemberSpeeds };
        Point expected = null;
        var differed = false;
        foreach (var (at, packet) in Sequence())
        {
            clock.Ticks = TimeSpan.FromSeconds(at).Ticks;
            server.Replay(new D2gsPacket(packet), sent: false);
            expected = OldHandler(expected, packet);
            var npc = game.WorldObjects[(0x1A7, EntityType.NPC)];
            Assert.Equal(expected, npc.LegacyLocation);
            if (mode == MonsterMotion.Legacy)
            {
                Assert.Equal(expected, npc.Location);
            }
            else
            {
                differed |= npc.Location != expected;
            }
        }

        Assert.Equal(mode != MonsterMotion.Legacy, differed);
        server.Dispose();
    }

    [Fact]
    public void TheSimulatedCouncilMemberWalksFromWhereItWasToward0x67()
    {
        var (game, server, clock) = NewGame(MonsterMotion.Simulated);
        game.MonsterMotionSettings.Rules = new MotionRules { Speeds = CouncilMemberSpeeds };
        foreach (var (at, packet) in Sequence().TakeWhile(p => p.At <= 106.118))
        {
            clock.Ticks = TimeSpan.FromSeconds(at).Ticks;
            server.Replay(new D2gsPacket(packet), sent: false);
        }

        var npc = game.WorldObjects[(0x1A7, EntityType.NPC)];
        Assert.Equal(MotionMode.MovingToPoint, npc.MotionMode);
        Assert.Equal(new Point(0x1558, 0x085E), npc.LastKnownLocation);
        Assert.Equal(new Point(0x155B, 0x085B), npc.Destination);
        clock.Ticks = TimeSpan.FromSeconds(106.118 + 0.1).Ticks;
        Assert.Equal(new Point(0x1559, 0x085D), npc.Location);
        clock.Ticks = TimeSpan.FromSeconds(110).Ticks;
        Assert.Equal(new Point(0x155B, 0x085B), npc.Location);
        server.Dispose();
    }

    [Fact]
    public void AFailingMotionHandlerNeitherThrowsNorSkipsTheHandlersAfterIt()
    {
        var (game, server, clock) = NewGame(MonsterMotion.Simulated);
        game.MonsterMotionSettings.Rules = new MotionRules { Speeds = CouncilMemberSpeeds, SpeedFactor = _ => throw new InvalidOperationException("speed") };
        var botHandlerCalls = 0;
        server.OnReceivedPacketEvent(InComingPacket.NPCMoveToTarget, _ => botHandlerCalls++);
        var failuresBefore = MotionFailures.Count;
        foreach (var (at, packet) in Sequence().TakeWhile(p => p.At <= 105.038))
        {
            clock.Ticks = TimeSpan.FromSeconds(at).Ticks;
            server.Replay(new D2gsPacket(packet), sent: false);
        }

        Assert.Equal(3, botHandlerCalls);
        Assert.Equal(3, MotionFailures.Count - failuresBefore);
        Assert.Equal(new Point(0x1559, 0x084F), game.WorldObjects[(0x1A7, EntityType.NPC)].LegacyLocation);
        server.Dispose();
    }

    [Fact]
    public void SimulatedWithoutSpeedDataReadsTheLegacyPosition()
    {
        var (game, server, clock) = NewGame(MonsterMotion.Simulated);
        game.MonsterMotionSettings.Rules = new MotionRules { Speeds = MonsterSpeedTable.Empty };
        Assert.Equal(MonsterMotion.Simulated, game.MonsterMotion);
        Assert.Equal(MonsterMotion.Legacy, game.EffectiveMonsterMotion);
        foreach (var (at, packet) in Sequence())
        {
            clock.Ticks = TimeSpan.FromSeconds(at).Ticks;
            server.Replay(new D2gsPacket(packet), sent: false);
            var npc = game.WorldObjects[(0x1A7, EntityType.NPC)];
            clock.Ticks += TimeSpan.FromSeconds(0.3).Ticks;
            Assert.Equal(npc.LegacyLocation, npc.Location);
        }

        game.MonsterMotionSettings.Rules = new MotionRules { Speeds = CouncilMemberSpeeds };
        Assert.Equal(MonsterMotion.Simulated, game.EffectiveMonsterMotion);
        server.Dispose();
    }

    [Fact]
    public void TheClockIsFixedWhenTheGameIsMade()
    {
        using var server = new GameServer();
        var clock = new ManualClock();
        Assert.Same(clock, new Game(server, clock).Clock);
        Assert.Same(TimeProvider.System, new Game(server).Clock);
        Assert.Null(typeof(Game).GetProperty(nameof(Game.Clock))!.SetMethod);
        Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
            typeof(MonsterMotionSettings).GetProperty(nameof(MonsterMotionSettings.Clock))!.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
    }

    [Fact]
    public void SamplingScoresBetweenTheEndsOfAWalkAgainstTheirInterpolation()
    {
        using var root = new TempRoot();
        var game = GameTrace.Open(SyntheticGame.WriteWalkScenario(root.Path));
        var accuracy = new MotionAccuracy([GameMotionModel.Legacy, GameMotionModel.Anchored], TimeSpan.FromSeconds(0.5));
        ClientReplay.Run(game, SyntheticGame.Sorceress, TimeSpan.MaxValue, [accuracy]);

        var zombie = accuracy.Samples.Where(s => s.Unit == SyntheticGame.Zombie).ToList();
        Assert.Equal([0.5, 1.0, 1.5], zombie.Select(s => Math.Round(s.SinceStart, 6)));
        Assert.Equal([7.5, 5.0, 2.5], zombie.Select(s => Math.Round(s.Errors[0], 6)));
        Assert.Equal([2.5, 5.0, 7.5], zombie.Select(s => Math.Round(s.Errors[1], 6)));
        Assert.All(zombie, s => Assert.Equal("walk, path 0", s.Segment));
        Assert.All(zombie, s => Assert.False(s.Friendly));

        var ormus = accuracy.Samples.Where(s => s.Unit == SyntheticGame.Ormus).ToList();
        Assert.Equal(3, ormus.Count);
        Assert.All(ormus, s => Assert.True(s.Friendly));
        Assert.True(Assert.Single(accuracy.Observations, o => o.Unit == SyntheticGame.Ormus && o.Moving).Friendly);
        Assert.Contains(NPCCode.Zombie, accuracy.Classes);

        var unsampled = new MotionAccuracy([GameMotionModel.Legacy]);
        ClientReplay.Run(game, SyntheticGame.Sorceress, TimeSpan.MaxValue, [unsampled]);
        Assert.Empty(unsampled.Samples);
    }

    private sealed class FixedModel(string name) : IMotionModel
    {
        public string Name => name;

        public Point Predict(ReplayContext context, uint id, WorldObject current) => new(0, 0);
    }

    [Fact]
    public void ReplicaDisagreementsDoNotDependOnModelOrder()
    {
        using var root = new TempRoot();
        var game = GameTrace.Open(SyntheticGame.WriteScenario(root.Path));
        var counts = new List<int>();
        foreach (var models in new IMotionModel[][] { [GameMotionModel.Simulated, new FixedModel(MotionAccuracy.ReplicaName)], [new FixedModel(MotionAccuracy.ReplicaName), GameMotionModel.Simulated] })
        {
            var accuracy = new MotionAccuracy(models);
            ClientReplay.Run(game, SyntheticGame.Sorceress, TimeSpan.MaxValue, [accuracy]);
            counts.Add(accuracy.Disagreements);
        }

        Assert.True(counts[0] > 0);
        Assert.Equal(counts[0], counts[1]);
    }

    [Fact]
    public void ReplayTimesMotionByRecordTime()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var path = SyntheticTrace.Record(recorder, "game-1", playingPackets: 30);

        using var replayer = new Replayer(path);
        Assert.Same(replayer.Clock, replayer.Game.Clock);
        var checkedRecords = 0;
        replayer.Applied = record =>
        {
            Assert.Equal(record.Elapsed.Ticks, replayer.Game.Clock.GetTimestamp());
            checkedRecords++;
        };
        replayer.ReplayToEnd();
        Assert.True(checkedRecords > 30);
    }

    [Fact]
    public void AccuracyScoresEveryModelAtTheAttackThatEndsAWalk()
    {
        using var root = new TempRoot();
        var folder = SyntheticGame.WriteScenario(root.Path);
        var game = GameTrace.Open(folder);
        var accuracy = new MotionAccuracy([.. MotionAccuracy.StandardModels]);
        ClientReplay.Run(game, SyntheticGame.Sorceress, TimeSpan.MaxValue, [accuracy]);

        var attack = Assert.Single(accuracy.Observations, o => o.Moving);
        Assert.Equal("6C", attack.Packet);
        Assert.Equal(SyntheticGame.Zombie, attack.Unit);
        Assert.Equal(MotionMode.MovingToPoint, attack.ModeBefore);
        Assert.Equal(0, attack.Errors[0]);
        Assert.Equal(new Point(5172, 2016).Distance(new Point(5166, 2012)), attack.Errors[1], 6);
        Assert.All(accuracy.Observations.Where(o => !o.Moving), o => Assert.Equal(0, o.Errors[1]));
    }
}
