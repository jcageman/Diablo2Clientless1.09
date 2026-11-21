using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MCP;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>
/// A monster's life from every packet that states it. Before, Game took it from NPCHit and NPCState only: the stops,
/// which state it too and arrive far more often, moved the unit and left its life as it was, and an assign left 100%.
/// </summary>
public class MonsterLifeTests
{
    private const uint Monster = 0x40;

    private static (Game Game, GameServer Server) Joined()
    {
        var server = new GameServer();
        var game = new Game(server);
        var stats = new byte[32];
        stats[13] = 3;
        game.SelectCharacter(new Character(SyntheticTrace.CharacterName, stats));
        foreach (var packet in SyntheticTrace.Joining())
        {
            server.Replay(new D2gsPacket(packet), sent: false);
        }

        return (game, server);
    }

    private static byte[] Assign(byte life)
    {
        var packet = SyntheticGame.AssignNpc(Monster, (NPCCode)82, 5100, 5100);
        packet[11] = life;
        return packet;
    }

    private static byte[] Stop(byte life)
    {
        var packet = SyntheticTrace.MonsterStop(Monster, 5101, 5102);
        packet[9] = life;
        return packet;
    }

    private static double LifeOf(Game game) => game.WorldObjects[(Monster, EntityType.NPC)].LifePercentage;

    [Fact]
    public void AStopAndAnAssignSetTheLifeTheyState()
    {
        var (game, server) = Joined();
        server.Replay(new D2gsPacket(Assign(96)), sent: false);
        Assert.Equal(96 / 1.28, LifeOf(game));

        server.Replay(new D2gsPacket(Stop(66)), sent: false);
        Assert.Equal(66 / 1.28, LifeOf(game));
        Assert.Equal(new Point(5101, 5102), game.WorldObjects[(Monster, EntityType.NPC)].Location);

        server.Replay(new D2gsPacket(Stop(128)), sent: false);
        Assert.Equal(100, LifeOf(game));
    }

    /// <summary>Town folk and other units that take no damage stop and are assigned with 0 while alive: that states no life.</summary>
    [Fact]
    public void AStopOrAssignStatingNothingLeavesTheLifeAndTheUnitAlive()
    {
        var (game, server) = Joined();
        server.Replay(new D2gsPacket(Assign(0)), sent: false);
        Assert.Equal(100, LifeOf(game));

        server.Replay(new D2gsPacket(Stop(64)), sent: false);
        server.Replay(new D2gsPacket(Stop(0)), sent: false);
        Assert.Equal(50, LifeOf(game));
        Assert.Equal(EntityState.Alive, game.WorldObjects[(Monster, EntityType.NPC)].State);
    }

    [Fact]
    public void AStopAfterDeathLeavesTheUnitDead()
    {
        var (game, server) = Joined();
        server.Replay(new D2gsPacket(Assign(128)), sent: false);
        server.Replay(new D2gsPacket(SyntheticGame.NpcState(Monster, EntityState.Dead, 5100, 5100, 12)), sent: false);
        server.Replay(new D2gsPacket(Stop(40)), sent: false);

        Assert.Equal(0, LifeOf(game));
        Assert.Equal(EntityState.Dead, game.WorldObjects[(Monster, EntityType.NPC)].State);
    }
}
