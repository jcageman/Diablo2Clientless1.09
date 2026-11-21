using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>Monster packets copied out of recorded 1.09 games; ids are the recorded ones.</summary>
public class MonsterMotionPacketTests
{
    internal static D2gsPacket Packet(string hex) => new(Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal)));

    [Fact]
    public void NpcMoveCarriesTheDestinationPathTypeAndRunBonus()
    {
        var walk = new NPCMovePacket(Packet("67 a7 01 00 00 01 5b 15 5b 08 01 00 0d 00 05"));
        Assert.Equal(0x1A7u, walk.EntityId);
        Assert.Equal(1, walk.MoveType);
        Assert.Equal(new Point(0x155B, 0x085B), walk.Location);
        Assert.Equal(13, walk.PathType);
        Assert.Equal(0, walk.RunBonus);
        Assert.Equal(5, walk.MaxDistance);

        var run = new NPCMovePacket(Packet("67 39 01 00 00 17 01 15 9f 0a 01 00 0d 53 08"));
        Assert.Equal(0x17, run.MoveType);
        Assert.Equal(83, run.RunBonus);
        Assert.Equal(8, run.MaxDistance);

        Assert.Equal(7, new NPCMovePacket(Packet("67 03 00 00 00 01 fa 13 2c 14 01 00 07 00 05")).PathType);
    }

    [Fact]
    public void NpcMoveToTargetCarriesTheCurrentPositionAndTheChasedUnit()
    {
        var chase = new NPCMoveToTargetPacket(Packet("68 a7 01 00 00 00 59 15 4f 08 00 03 00 00 00 01 00 0d 00 05"));
        Assert.Equal(0x1A7u, chase.EntityId);
        Assert.Equal(new Point(0x1559, 0x084F), chase.Location);
        Assert.Equal(EntityType.Player, chase.TargetType);
        Assert.Equal(3u, chase.TargetId);
        Assert.Equal(13, chase.PathType);
        Assert.Equal(0, chase.RunBonus);

        var run = new NPCMoveToTargetPacket(Packet("68 39 01 00 00 18 10 15 90 0a 00 03 00 00 00 01 00 0d 53 05"));
        Assert.Equal(0x18, run.MoveType);
        Assert.Equal(83, run.RunBonus);
        Assert.Equal(15, new NPCMoveToTargetPacket(Packet("68 52 00 00 00 18 25 14 fa 06 00 01 00 00 00 01 00 0f 53 05")).PathType);
    }

    [Fact]
    public void NpcActionCarriesItsPointAndTheCurrentPositionAtTwelve()
    {
        var aimed = new NPCActionPacket(Packet("6b 18 00 00 00 0c 61 15 22 0c 00 00 66 15 1e 0c"));
        Assert.Equal(0x18u, aimed.EntityId);
        Assert.Equal(12, aimed.Action);
        Assert.Equal(new Point(0x1561, 0x0C22), aimed.TargetLocation);
        Assert.Equal(new Point(0x1566, 0x0C1E), aimed.Location);

        var plain = new NPCActionPacket(Packet("6b 18 00 00 00 0e 00 00 00 00 00 00 62 15 28 0c"));
        Assert.Null(plain.TargetLocation);
        Assert.Equal(new Point(0x1562, 0x0C28), plain.Location);
    }

    [Fact]
    public void NpcAttackNamesItsTargetAndTheCurrentPositionAtTwelve()
    {
        var attack = new NPCAttackPacket(Packet("6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08"));
        Assert.Equal(0x1A7u, attack.EntityId);
        Assert.Equal(10, attack.AttackType);
        Assert.Equal(EntityType.Player, attack.TargetType);
        Assert.Equal(3u, attack.TargetId);
        Assert.Equal(new Point(0x155F, 0x086A), attack.Location);
    }

    [Fact]
    public void DyingAndDeadCarryADirectionWhereGetHitCarriesLife()
    {
        var hit = new NpcStatePacket(Packet("69 0c 00 00 00 06 03 15 34 0c 6d 1a"));
        Assert.Equal(EntityState.BeingHit, hit.EntityState);
        Assert.Equal(0x6D / 1.28, hit.LifePercentage, 6);
        Assert.Equal(new Point(0x1503, 0x0C34), hit.Location);

        var dying = new NpcStatePacket(Packet("69 a7 01 00 00 08 58 15 68 08 3f 1a"));
        Assert.Equal(EntityState.Dieing, dying.EntityState);
        Assert.Equal(63, dying.Direction);
        Assert.Equal(0, dying.LifePercentage);

        var dead = new NpcStatePacket(Packet("69 a7 01 00 00 09 5e 15 6e 08 3f 00"));
        Assert.Equal(EntityState.Dead, dead.EntityState);
        Assert.Equal(new Point(0x155E, 0x086E), dead.Location);
        Assert.Equal(0, dead.LifePercentage);
    }

    [Fact]
    public void AssignCarriesMinionAndChampionFlagsAndExtraFast()
    {
        var minion = new AssignNpcPacket(Packet("56 a7 01 00 00 59 01 59 15 4f 08 80 01 02 00 ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 06 07 00 00 00 00 00 00 00"));
        Assert.Equal(NPCCode.CouncilMember1, minion.UniqueCode);
        Assert.True(minion.IsBossMinion);
        Assert.False(minion.IsChampion);
        Assert.Contains(MonsterEnchantment.ExtraFast, minion.MonsterEnchantments);

        var champion = new AssignNpcPacket(Packet("56 b9 00 00 00 d7 00 5b 31 96 14 80 01 05 00 ff ff ff ff 78 6d 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 10 00 00 00 00 00 00 00 00"));
        Assert.True(champion.IsChampion);
        Assert.True(champion.IsBoss);
    }

    [Fact]
    public void ReadTurnsEachPacketIntoItsMotionUpdate()
    {
        Assert.Equal(MotionUpdateKind.MoveToPoint, MotionUpdate.Read(Packet("67 a7 01 00 00 01 5b 15 5b 08 01 00 0d 00 05").Raw).Kind);
        var chase = MotionUpdate.Read(Packet("68 39 01 00 00 18 10 15 90 0a 00 03 00 00 00 01 00 0d 53 05").Raw);
        Assert.Equal(MotionUpdateKind.MoveToUnit, chase.Kind);
        Assert.Equal((byte)83, chase.RunBonus);
        Assert.Equal(3u, chase.TargetId);
        Assert.True(chase.HasExactPosition);
        Assert.Equal(MotionUpdateKind.Action, MotionUpdate.Read(Packet("6b 18 00 00 00 0e 00 00 00 00 00 00 62 15 28 0c").Raw).Kind);
        Assert.Equal(MotionUpdateKind.Attack, MotionUpdate.Read(Packet("6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08").Raw).Kind);
        Assert.Equal(MotionUpdateKind.Stop, MotionUpdate.Read(Packet("6d a7 01 00 00 5f 15 6a 08 7f").Raw).Kind);
        Assert.Equal(MotionUpdateKind.Hit, MotionUpdate.Read(Packet("69 0c 00 00 00 06 03 15 34 0c 6d 1a").Raw).Kind);
        var dying = MotionUpdate.Read(Packet("69 a7 01 00 00 08 58 15 68 08 3f 1a").Raw);
        Assert.Equal(MotionUpdateKind.Dying, dying.Kind);
        Assert.False(dying.HasExactPosition);
        Assert.Equal(MotionUpdateKind.Dead, MotionUpdate.Read(Packet("69 a7 01 00 00 09 5e 15 6e 08 3f 00").Raw).Kind);

        var snap = MotionUpdate.Read(Packet("15 01 18 00 00 00 62 15 28 0c 01").Raw);
        Assert.Equal(MotionUpdateKind.Snap, snap.Kind);
        Assert.Equal(0x18u, snap.EntityId);
        Assert.Equal(new Point(0x1562, 0x0C28), snap.Position);

        var player = Convert.FromHexString("1500180000006215280C01");
        Assert.Equal(MotionUpdateKind.None, MotionUpdate.Read(player).Kind);
        Assert.Equal(MotionUpdateKind.None, MotionUpdate.Read([0x0C, 0x01]).Kind);
    }

    [Theory]
    [InlineData("67 a7 01 00 00 01 5b 15 5b 08 01 00 0d 00 05")]
    [InlineData("67 39 01 00 00 17 01 15 9f 0a 01 00 0d 53 08")]
    [InlineData("68 39 01 00 00 18 10 15 90 0a 00 03 00 00 00 01 00 0d 53 05")]
    [InlineData("68 52 00 00 00 18 25 14 fa 06 00 01 00 00 00 01 00 0f 53 05")]
    [InlineData("6b 18 00 00 00 0c 61 15 22 0c 00 00 66 15 1e 0c")]
    [InlineData("6c a7 01 00 00 0a 00 03 00 00 00 00 5f 15 6a 08")]
    [InlineData("6d a7 01 00 00 5f 15 6a 08 7f")]
    [InlineData("69 0c 00 00 00 06 03 15 34 0c 6d 1a")]
    [InlineData("69 a7 01 00 00 08 58 15 68 08 3f 1a")]
    [InlineData("69 a7 01 00 00 09 5e 15 6e 08 3f 00")]
    [InlineData("56 a7 01 00 00 59 01 59 15 4f 08 80 01 02 00 ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 06 07 00 00 00 00 00 00 00")]
    [InlineData("15 01 18 00 00 00 62 15 28 0c 01")]
    public void ReadOnTheRawBytesMatchesThePacketParsers(string hex)
    {
        var packet = Packet(hex);
        MotionUpdate parsed = packet.Raw[0] switch
        {
            0x67 => MotionUpdate.From(new NPCMovePacket(packet)),
            0x68 => MotionUpdate.From(new NPCMoveToTargetPacket(packet)),
            0x6B => MotionUpdate.From(new NPCActionPacket(packet)),
            0x6C => MotionUpdate.From(new NPCAttackPacket(packet)),
            0x6D => MotionUpdate.From(new NPCStopPacket(packet)),
            0x69 => MotionUpdate.From(new NpcStatePacket(packet)),
            0x56 => MotionUpdate.From(new AssignNpcPacket(packet)),
            _ => MotionUpdate.From(new ReassignPlayerPacket(packet)),
        };

        Assert.Equal(parsed, MotionUpdate.Read(packet.Raw));
    }
}
