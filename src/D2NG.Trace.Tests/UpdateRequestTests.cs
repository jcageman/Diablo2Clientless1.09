using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MCP;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>
/// A position request answered as soon as the server's reassign of that player is in, rather than after the fixed
/// 400 ms <see cref="Game.RequestUpdate"/> sleeps.
/// </summary>
public class UpdateRequestTests
{
    private const uint Self = 0x1;
    private const uint Other = 0x7;
    private const uint Third = 0x9;

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

    private static void Reassign(GameServer server, uint id, ushort x, ushort y)
        => server.Replay(new D2gsPacket(SyntheticGame.Reassign(id, x, y)), sent: false);

    [Fact]
    public async Task It_returns_once_the_server_reassigns_the_player_and_the_position_is_the_servers()
    {
        var (game, server) = Joined();
        var waited = Stopwatch.StartNew();
        var wait = game.RequestUpdateAndWaitAsync(Self, TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        Reassign(server, Self, 5070, 1910);

        Assert.True(await wait);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(new Point(5070, 1910), game.Me.Location);
    }

    [Fact]
    public async Task Without_an_answer_it_gives_up_at_the_timeout()
    {
        var (game, _) = Joined();

        Assert.False(await game.RequestUpdateAndWaitAsync(Other, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task A_reassign_from_before_the_request_is_not_its_answer()
    {
        var (game, server) = Joined();
        Reassign(server, Other, 5070, 1910);

        Assert.False(await game.RequestUpdateAndWaitAsync(Other, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Another_players_reassign_is_not_its_answer()
    {
        var (game, server) = Joined();
        var wait = game.RequestUpdateAndWaitAsync(Other, TimeSpan.FromMilliseconds(300));
        Reassign(server, Third, 5070, 1910);

        Assert.False(await wait);
    }

    [Fact]
    public async Task Several_requests_wait_for_every_answer()
    {
        var (game, server) = Joined();
        var wait = game.RequestUpdatesAndWaitAsync([Other, Third], TimeSpan.FromSeconds(5));
        Reassign(server, Other, 5070, 1910);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        Reassign(server, Third, 5072, 1912);

        Assert.True(await wait);
    }
}
