using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>
/// Game.ResolvedArea and the server confirmed position against packet sequences built with the real packet
/// layouts. Every test also asserts what Game.Area and IsInTown() answer, which this change leaves alone:
/// where they are wrong the old value is asserted, so the test documents the difference.
/// </summary>
public class ResolvedAreaTests
{
    private const uint SelfId = 1;
    private const string Name = "tester";

    // Wilderness and town tiles are 8x8 tiles of 5 subtiles: the tile at (1000, 1000) holds x and y 5000 to 5039.
    private const ushort TownTileX = 1000;
    private const ushort FieldTileX = 1008;
    private const ushort LaterTileX = 1016;
    private const ushort TileY = 1000;

    private sealed class ManualClock : TimeProvider
    {
        public long Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref Ticks);

        public void Advance(double seconds) => Interlocked.Add(ref Ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private sealed class Session : IDisposable
    {
        public GameServer Server { get; } = new();

        public ManualClock Clock { get; } = new();

        public Game Game { get; }

        public Session(Action<GameServer> beforeGame = null)
        {
            beforeGame?.Invoke(Server);
            Game = new Game(Server, Clock);
            var stats = new byte[32];
            stats[13] = (byte)(CharacterClass.Sorceress + 1);
            Game.SelectCharacter(new Character(Name, stats));
            Receive([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        }

        public void Receive(byte[] packet) => Server.Replay(new D2gsPacket(packet), sent: false);

        public void LoadAct(Act act, Area area) => Receive([0x03, (byte)act, 0x73, 0x31, 0x1B, 0x5C, .. BitConverter.GetBytes((ushort)area), 0x00, 0x00, 0x00, 0x00]);

        public void Reveal(ushort tileX, ushort tileY, Area area) => Receive(SyntheticGame.MapReveal(tileX, tileY, area));

        public void Assign() => Receive(SyntheticGame.AssignPlayer(SelfId, CharacterClass.Sorceress, Name, 0, 0));

        public void Reassign(ushort x, ushort y) => Receive(SyntheticGame.Reassign(SelfId, x, y));

        public void Dispose() => Server.Dispose();
    }

    /// <summary>Act 1 loaded into the Rogue Encampment, a town tile and a Blood Moor tile beside it revealed, the character in the town tile.</summary>
    private static Session InTown(Action<GameServer> beforeGame = null)
    {
        var session = new Session(beforeGame);
        session.LoadAct(Act.Act1, Area.RogueEncampment);
        session.Assign();
        session.Reveal(TownTileX, TileY, Area.RogueEncampment);
        session.Reveal(FieldTileX, TileY, Area.BloodMoor);
        session.Reassign(5010, 5010);
        return session;
    }

    [Fact]
    public void BeforeTheGameStartsNothingIsKnown()
    {
        using var server = new GameServer();
        var game = new Game(server, new ManualClock());
        Assert.Null(game.ResolvedArea);
        Assert.Null(game.LastResolvedArea);
        Assert.Equal(AreaState.Unknown, game.AreaState);
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());
        Assert.Null(game.ServerConfirmedLocation);
        Assert.Equal(TimeSpan.MaxValue, game.ServerConfirmedAge);
        Assert.Null(game.ServerConfirmedDistance);
    }

    [Fact]
    public void InsideARevealedTileTheAreaIsThatTiles()
    {
        using var session = InTown();
        var game = session.Game;

        Assert.Equal(Area.RogueEncampment, game.ResolvedArea);
        Assert.Equal(AreaState.Resolved, game.AreaState);
        Assert.True(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());
        Assert.Equal(Area.RogueEncampment, game.Area);
        Assert.True(game.IsInTown());

        session.Reassign(5050, 5010);
        Assert.Equal(Area.BloodMoor, game.ResolvedArea);
        Assert.True(game.IsOutOfTownConfirmed());
        Assert.False(game.IsInTownConfirmed());
        Assert.Equal(Area.BloodMoor, game.Area);
        Assert.False(game.IsInTown());
    }

    [Theory]
    [InlineData(5000, Area.RogueEncampment)]
    [InlineData(5039, Area.RogueEncampment)]
    [InlineData(5040, Area.BloodMoor)]
    [InlineData(5079, Area.BloodMoor)]
    public void TheBorderBetweenTwoTilesFallsWhereTileContainsPutsIt(ushort x, Area expected)
    {
        using var session = InTown();
        session.Reassign(x, 5039);
        Assert.Equal(expected, session.Game.ResolvedArea);
        Assert.Equal(expected, session.Game.Area);
    }

    [Fact]
    public void OutsideEveryTileTheAreaIsUnknownWhereGameAreaAnswersTheTown()
    {
        using var session = InTown();
        var game = session.Game;
        session.Clock.Advance(1);
        session.Reassign(5100, 5010);

        Assert.Null(game.ResolvedArea);
        Assert.Equal(AreaState.Unknown, game.AreaState);
        Assert.Equal(Area.RogueEncampment, game.LastResolvedArea);
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());
        Assert.Equal(TimeSpan.Zero, game.AreaUnknownFor);
        session.Clock.Advance(2.5);
        Assert.Equal(TimeSpan.FromSeconds(2.5), game.AreaUnknownFor);

        // The old answer, wrong: the act's town, because no revealed tile holds the position.
        Assert.Equal(Area.RogueEncampment, game.Area);
        Assert.True(game.IsInTown());
    }

    [Fact]
    public void ATileRevealedLaterResolvesThePosition()
    {
        using var session = InTown();
        var game = session.Game;
        session.Reassign(5100, 5010);
        Assert.Null(game.ResolvedArea);

        session.Reveal(LaterTileX, TileY, Area.BloodMoor);

        Assert.Equal(Area.BloodMoor, game.ResolvedArea);
        Assert.Equal(TimeSpan.Zero, game.AreaUnknownFor);
        Assert.Equal(Area.BloodMoor, game.Area);
        Assert.False(game.IsInTown());
    }

    [Fact]
    public void AnActLoadHoldsItsTownUntilATileHoldsAPositionSetAfterIt()
    {
        using var session = InTown();
        var game = session.Game;
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5100, 5010)));
        session.LoadAct(Act.Act2, Area.LutGholein);

        Assert.Equal(Area.LutGholein, game.ResolvedArea);
        Assert.True(game.ResolvedAreaIsActLoad);
        Assert.Equal((Area.LutGholein, AreaSource.ActLoad), game.KnownArea);
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());
        Assert.Equal(Area.LutGholein, game.Area);

        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(6000, 6000)));
        Assert.Equal(Area.LutGholein, game.ResolvedArea);

        session.Reveal(1200, 1200, Area.LutGholein);
        session.Reveal(1208, 1200, Area.RockyWaste);
        Assert.Equal(Area.LutGholein, game.ResolvedArea);
        Assert.False(game.ResolvedAreaIsActLoad);
        Assert.Equal((Area.LutGholein, AreaSource.Believed), game.KnownArea);

        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(6045, 6010)));
        Assert.Equal(Area.RockyWaste, game.ResolvedArea);
        Assert.Equal(Area.RockyWaste, game.Area);

        // Once a tile has held the position the load no longer does: off the tiles the area is unknown.
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(6200, 6200)));
        Assert.Null(game.ResolvedArea);
        Assert.Equal(Area.RockyWaste, game.LastResolvedArea);
        Assert.Equal(Area.LutGholein, game.Area);
        Assert.True(game.IsInTown());
        Assert.False(game.IsInTownConfirmed());
    }

    [Fact]
    public void TheServersFirstPositionAfterAnActLoadEndsTheHold()
    {
        using var session = InTown();
        var game = session.Game;
        session.LoadAct(Act.Act3, Area.KurastDocks);
        session.Reassign(5110, 5010);

        Assert.Null(game.ResolvedArea);
        Assert.False(game.ResolvedAreaIsActLoad);
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());

        session.Reveal(LaterTileX, TileY, Area.Travincal);
        Assert.Equal(Area.Travincal, game.ResolvedArea);
        Assert.Equal(Area.Travincal, game.ServerConfirmedArea);
        Assert.True(game.IsOutOfTownConfirmed());
        Assert.False(game.IsInTownConfirmed());
    }

    [Fact]
    public void AWaypointIntoAnotherActsFieldIsNeverConfirmedTownOnTheActLoadsWord()
    {
        using var session = InTown();
        var game = session.Game;
        session.LoadAct(Act.Act3, Area.KurastDocks);

        Assert.Equal(Area.KurastDocks, game.ResolvedArea);
        Assert.False(game.IsInTownConfirmed());

        // The arrival statement before its tile: the old resolver held the town until the tile came.
        session.Reassign(5110, 5010);
        Assert.NotEqual(Area.KurastDocks, game.ResolvedArea);
        Assert.False(game.IsInTownConfirmed());
    }

    [Fact]
    public void ABelievedTownWithNoServerPositionIsNotConfirmed()
    {
        using var session = InTown();
        var game = session.Game;
        session.LoadAct(Act.Act2, Area.LutGholein);
        session.Reveal(1200, 1200, Area.LutGholein);
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(6010, 6010)));

        Assert.Equal(Area.LutGholein, game.ResolvedArea);
        Assert.Null(game.ServerConfirmedArea);
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());

        session.Reassign(6010, 6010);
        Assert.True(game.IsInTownConfirmed());
    }

    [Fact]
    public void BackInAnEarlierActThePreviousActsCoordinatesDoNotResolve()
    {
        using var session = InTown();
        var game = session.Game;
        session.LoadAct(Act.Act2, Area.LutGholein);
        session.Reveal(TownTileX, TileY, Area.LutGholein);
        session.Reveal(FieldTileX, TileY, Area.RockyWaste);
        session.Reassign(5050, 5010);
        Assert.Equal(Area.RockyWaste, game.ResolvedArea);

        session.LoadAct(Act.Act1, Area.RogueEncampment);

        Assert.Equal(Area.RogueEncampment, game.ResolvedArea);
        Assert.NotEqual(Area.BloodMoor, game.LastResolvedArea);

        session.Reveal(TownTileX, TileY, Area.RogueEncampment);
        Assert.Equal(Area.RogueEncampment, game.ResolvedArea);

        session.Reassign(5010, 5010);
        Assert.Equal(Area.RogueEncampment, game.ResolvedArea);
        Assert.True(game.IsInTownConfirmed());
    }

    [Fact]
    public void AnActLoadForgetsTheServerPositionWhole()
    {
        using var session = InTown();
        var game = session.Game;
        session.Reassign(5050, 5010);
        session.Clock.Advance(2);
        session.LoadAct(Act.Act2, Area.LutGholein);

        Assert.Null(game.ServerConfirmedArea);
        Assert.Null(game.ServerConfirmedLocation);
        Assert.Equal(TimeSpan.MaxValue, game.ServerConfirmedAge);
        Assert.Null(game.ServerConfirmedDistance);
    }

    [Fact]
    public void TilesOfAnotherActDoNotResolveAPosition()
    {
        using var session = InTown();
        session.LoadAct(Act.Act2, Area.LutGholein);
        session.Reveal(1200, 1200, Area.LutGholein);
        session.Reassign(6010, 6010);
        Assert.Equal(Area.LutGholein, session.Game.ResolvedArea);

        session.Reassign(5010, 5010);
        Assert.Null(session.Game.ResolvedArea);
        Assert.Equal(Area.LutGholein, session.Game.Area);
    }

    [Fact]
    public void TheClientsOwnStepsAreFollowed()
    {
        using var session = InTown();
        var game = session.Game;
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5100, 5010)));
        Assert.Null(game.ResolvedArea);
        Assert.Equal(Area.RogueEncampment, game.Area);

        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5045, 5010)));
        Assert.Equal(Area.BloodMoor, game.ResolvedArea);
    }

    [Fact]
    public void OffItsMapTheServerConfirmedPositionStillResolves()
    {
        using var session = InTown();
        var game = session.Game;
        session.Reassign(5050, 5010);
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5100, 5010)));

        Assert.Null(game.ResolvedArea);
        Assert.Equal(Area.BloodMoor, game.ServerConfirmedArea);
        Assert.Equal((Area.BloodMoor, AreaSource.ServerConfirmed), game.KnownArea);
        Assert.True(game.IsOutOfTownConfirmed());
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.ServerDisagreesOnTown);

        // The old answer, wrong: the act's town.
        Assert.Equal(Area.RogueEncampment, game.Area);
        Assert.True(game.IsInTown());

        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5055, 5010)));
        Assert.Equal((Area.BloodMoor, AreaSource.Believed), game.KnownArea);
    }

    [Fact]
    public void WhenTheBelievedAndTheServerPositionDisagreeAboutTownNeitherHelperAnswers()
    {
        using var session = InTown();
        var game = session.Game;
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5045, 5010)));

        Assert.Equal(Area.BloodMoor, game.ResolvedArea);
        Assert.Equal(Area.RogueEncampment, game.ServerConfirmedArea);
        Assert.Equal((Area.BloodMoor, AreaSource.Believed), game.KnownArea);
        Assert.True(game.ServerDisagreesOnTown);
        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());
        Assert.Equal(Area.BloodMoor, game.Area);
        Assert.False(game.IsInTown());

        session.Receive(SyntheticGame.WalkVerify(5045, 5010));
        Assert.False(game.ServerDisagreesOnTown);
        Assert.True(game.IsOutOfTownConfirmed());
    }

    [Fact]
    public void TheServerConfirmedAreaFollowsTilesAndForgetsAtAnActLoad()
    {
        using var session = InTown();
        var game = session.Game;
        session.Reassign(5100, 5010);
        Assert.Null(game.ServerConfirmedArea);
        Assert.Equal((null, AreaSource.Unknown), game.KnownArea);

        session.Reveal(LaterTileX, TileY, Area.BloodMoor);
        Assert.Equal(Area.BloodMoor, game.ServerConfirmedArea);

        session.LoadAct(Act.Act2, Area.LutGholein);
        Assert.Null(game.ServerConfirmedArea);
        Assert.Null(game.ServerConfirmedLocation);
        Assert.Equal((Area.LutGholein, AreaSource.ActLoad), game.KnownArea);
        Assert.False(game.IsInTownConfirmed());
    }

    [Fact]
    public void EveryPacketThatStatesTheOwnPositionConfirmsIt()
    {
        using var session = InTown();
        var game = session.Game;
        var packets = new (InComingPacket Type, byte[] Packet, Point Location)[]
        {
            (InComingPacket.ReassignPlayer, SyntheticGame.Reassign(SelfId, 5011, 5012), new Point(5011, 5012)),
            (InComingPacket.WalkVerify, SyntheticGame.WalkVerify(5013, 5014), new Point(5013, 5014)),
            (InComingPacket.LifeManaUpdate, SyntheticGame.LifeMana(100, 50, 5015, 5016), new Point(5015, 5016)),
            (InComingPacket.LifeManaUpdatePot, LifeManaPot(5017, 5018), new Point(5017, 5018)),
            (InComingPacket.PlayerStop, SyntheticGame.PlayerStop(SelfId, 5019, 5020), new Point(5019, 5020)),
            (InComingPacket.EntityMove, SyntheticGame.EntityMove(SelfId, 5030, 5030, 5021, 5022), new Point(5021, 5022)),
        };

        foreach (var (type, packet, location) in packets)
        {
            session.Clock.Advance(4);
            Assert.Equal(TimeSpan.FromSeconds(4), game.ServerConfirmedAge);
            session.Receive(packet);
            Assert.Equal(TimeSpan.Zero, game.ServerConfirmedAge);
            Assert.Equal(location, game.ServerConfirmedLocation);
            Assert.Equal(type, game.PositionConfirmation.Packet);
        }

        // 0x0F moves the client on to the destination; the server stated where the walk starts.
        Assert.Equal(new Point(5030, 5030), game.Me.Location);
        Assert.Equal(new Point(5030, 5030).Distance(new Point(5021, 5022)), game.ServerConfirmedDistance);
    }

    [Fact]
    public void OtherPlayersAndTheJoinsZeroPositionConfirmNothing()
    {
        using var session = InTown();
        var game = session.Game;
        session.Receive(SyntheticGame.AssignPlayer(7, CharacterClass.Amazon, "other", 5000, 5000));
        session.Clock.Advance(3);

        session.Receive(SyntheticGame.Reassign(7, 5001, 5001));
        session.Receive(SyntheticGame.PlayerStop(7, 5002, 5002));
        session.Receive(SyntheticGame.EntityMove(7, 5003, 5003, 5004, 5004));
        session.Receive(SyntheticGame.LifeMana(100, 50, 0, 0));

        Assert.Equal(TimeSpan.FromSeconds(3), game.ServerConfirmedAge);
        Assert.Equal(new Point(5010, 5010), game.ServerConfirmedLocation);
    }

    [Fact]
    public void TheDistanceIsBetweenTheClientsBeliefAndTheServersWord()
    {
        using var session = InTown();
        var game = session.Game;
        Assert.Equal(0.0, game.ServerConfirmedDistance);

        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5013, 5014)));
        Assert.Equal(5.0, game.ServerConfirmedDistance);
        Assert.Equal(new Point(5010, 5010), game.ServerConfirmedLocation);

        session.Reassign(5010, 5010);
        Assert.Equal(new Point(5013, 5014), game.PositionConfirmation.Believed);
        Assert.Equal(0.0, game.ServerConfirmedDistance);
    }

    [Fact]
    public void ReadersSeeOnlyWholeAnswersWhileTheReceiveAndBotThreadsWrite()
    {
        using var session = InTown();
        var game = session.Game;
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var allowed = new HashSet<Area?> { Area.RogueEncampment, Area.BloodMoor, null };
        var failures = 0;
        long reads = 0;

        void Read()
        {
            while (!stop.IsCancellationRequested)
            {
                var area = game.ResolvedArea;
                if (!allowed.Contains(area) || game.LastResolvedArea is not (Area.RogueEncampment or Area.BloodMoor)
                    || game.ServerConfirmedLocation == null
                    || game.ServerConfirmedAge < TimeSpan.Zero || game.AreaUnknownFor < TimeSpan.Zero)
                {
                    Interlocked.Increment(ref failures);
                }

                Interlocked.Increment(ref reads);
            }
        }

        var receive = Task.Run(() =>
        {
            ushort[] xs = [5010, 5050, 5100];
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                session.Reassign(xs[i % xs.Length], 5010);
                session.Receive(SyntheticGame.WalkVerify(xs[(i + 1) % xs.Length], 5010));
                session.Clock.Advance(0.001);
            }
        });
        var bot = Task.Run(() =>
        {
            ushort[] xs = [5020, 5060, 5110];
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                game.Apply(ClientAction.Move, ClientActions.Pack(new Point(xs[i % xs.Length], 5020)));
            }
        });
        var readers = new[] { Task.Run(Read), Task.Run(Read), Task.Run(Read) };
        Task.WaitAll([receive, bot, .. readers]);

        Assert.Equal(0, failures);
        Assert.True(reads > 1000);
    }

    /// <summary>
    /// A walk verify on the receive thread races the bot's own steps. Both threads pause together at a barrier many
    /// times during the race, and at every pause the published answer must describe the position the client holds:
    /// before, the receive thread read the position, the bot then moved and resolved, and the receive thread resolved
    /// the position it had read, so the answer described a place the client had left. Checking only after the race
    /// hid it, because the receive thread usually resolved once more after the bot's last step.
    /// </summary>
    [Fact]
    public async Task AtEveryPauseInTheRaceOfTheReceiveAndBotThreadsTheAnswerDescribesThePositionHeld()
    {
        ushort[] xs = [5010, 5050, 5100];
        static Area? Expected(Point position) => position.X < 5040 ? Area.RogueEncampment : position.X < 5080 ? Area.BloodMoor : null;
        const int Pauses = 4000;
        using var session = InTown();
        var game = session.Game;
        var stale = 0;
        var checks = 0;
        using var pause = new Barrier(2, _ =>
        {
            checks++;
            if (game.ResolvedArea != Expected(game.Me.Location))
            {
                stale++;
            }
        });
        var receive = Task.Run(() =>
        {
            for (var round = 0; round < Pauses; round++)
            {
                for (var i = 0; i <= round % 4; i++)
                {
                    session.Receive(SyntheticGame.WalkVerify(xs[(round + i) % xs.Length], 5010));
                }

                pause.SignalAndWait();
            }
        }, TestContext.Current.CancellationToken);
        var bot = Task.Run(() =>
        {
            for (var round = 0; round < Pauses; round++)
            {
                for (var i = 0; i <= round % 3; i++)
                {
                    game.Apply(ClientAction.Move, ClientActions.Pack(new Point(xs[((round * 7) + i) % xs.Length], 5020)));
                }

                pause.SignalAndWait();
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(receive, bot);

        Assert.Equal(Pauses, checks);
        Assert.Equal(0, stale);
    }

    [Fact]
    public void TheServerHoldingThePositionWhileTheClientWalksOnIsReported()
    {
        using var session = InTown();
        var game = session.Game;
        Assert.Null(game.ServerHoldsPosition);
        Assert.Equal(TimeSpan.Zero, game.ServerHeldFor);
        var start = session.Clock.GetTimestamp();

        for (var i = 0; i < 6; i++)
        {
            if (i > 0)
            {
                session.Clock.Advance(1.1);
            }

            game.Apply(ClientAction.Move, ClientActions.Pack(new Point((ushort)(5030 + (i * 10)), 5010)));
            session.Receive(SyntheticGame.WalkVerify((ushort)(5010 + (i % 2)), 5010));
            if (i < 5)
            {
                Assert.Null(game.ServerHoldsPosition);
            }
        }

        var hold = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);
        Assert.Equal(new Point(5010, 5010), hold.Location);
        Assert.Equal(start, hold.Since);
        Assert.Equal(6, hold.Statements);
        Assert.Equal(new Point(5080, 5010).Distance(new Point(5010, 5010)), hold.LargestBelievedDistance);
        Assert.True(game.ServerHeldFor >= TimeSpan.FromSeconds(5.5));

        session.Reassign(5050, 5010);
        Assert.Null(game.ServerHoldsPosition);
    }

    /// <summary>
    /// One far statement early in a stay, a minute standing in sync, then one statement a little off: before, the early
    /// divergence was kept for the whole stay and the late statement set the hold at once with 202 statements.
    /// </summary>
    [Fact]
    public void AnOldDivergenceDoesNotMakeALaterStatementAHold()
    {
        using var session = InTown();
        var game = session.Game;
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5045, 5010)));
        session.Receive(SyntheticGame.WalkVerify(5010, 5010));
        for (var i = 0; i < 200; i++)
        {
            session.Clock.Advance(0.3);
            session.Receive(SyntheticGame.LifeMana(100, 50, 5010, 5010));
        }

        session.Clock.Advance(0.3);
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5022, 5010)));
        session.Receive(SyntheticGame.WalkVerify(5010, 5010));

        Assert.Null(game.ServerHoldsPosition);
    }

    /// <summary>Held, then the server goes quiet: the flag clears once the span holds too few statements, without one arriving.</summary>
    [Fact]
    public void TheHoldClearsWhenTheStatementsStop()
    {
        using var session = InTown();
        var game = session.Game;
        HoldFor(session, 7);
        Assert.NotNull(game.ServerHoldsPosition);

        session.Clock.Advance(AreaResolver.HoldSpan.TotalSeconds);
        Assert.Null(game.ServerHoldsPosition);
        Assert.Equal(TimeSpan.Zero, game.ServerHeldFor);
    }

    /// <summary>
    /// A reassign onto the held point is the snap back a held client gets several times a second: the episode goes on,
    /// with the same identity, and only the momentary fact says the client is on the point. The reassign is itself a
    /// statement that found the client away, so the episode ends the span after it.
    /// </summary>
    [Fact]
    public void AReassignOntoTheHeldPointKeepsTheEpisodeAndReportsTheClientOnIt()
    {
        using var session = InTown();
        var game = session.Game;
        HoldFor(session, 7);
        var hold = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);
        Assert.False(hold.ClientIsOnHeldPoint);

        session.Clock.Advance(0.1);
        session.Reassign(5011, 5010);
        var after = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);
        Assert.Equal(hold.Since, after.Since);
        Assert.True(after.ClientIsOnHeldPoint);
        Assert.Equal(1, after.BelievedDistance);

        session.Clock.Ticks += AreaResolver.HoldSpan.Ticks - 1;
        Assert.NotNull(game.ServerHoldsPosition);
        session.Clock.Ticks++;
        Assert.Null(game.ServerHoldsPosition);
    }

    /// <summary>A statement that finds the client in sync neither ends the episode nor extends it.</summary>
    [Fact]
    public void AStatementFindingTheClientInSyncNeitherEndsNorExtendsTheEpisode()
    {
        using var session = InTown();
        var game = session.Game;
        HoldFor(session, 7);
        var hold = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);

        session.Clock.Advance(0.1);
        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5015, 5010)));
        session.Receive(SyntheticGame.WalkVerify(5010, 5010));
        var after = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);
        Assert.Equal(hold.Since, after.Since);
        Assert.True(after.ClientIsOnHeldPoint);

        session.Clock.Advance(AreaResolver.HoldSpan.TotalSeconds - 0.1);
        Assert.Null(game.ServerHoldsPosition);
    }

    [Fact]
    public void TheHoldClearsAtOnceOnAStatementElsewhere()
    {
        using var session = InTown();
        var game = session.Game;
        HoldFor(session, 7);
        Assert.NotNull(game.ServerHoldsPosition);

        session.Clock.Advance(0.1);
        session.Receive(SyntheticGame.WalkVerify(5014, 5010));
        Assert.Null(game.ServerHoldsPosition);
        Assert.Equal(TimeSpan.Zero, game.ServerHeldFor);
    }

    /// <summary>
    /// A held client is snapped back onto the held point and walks off again every 150 ms, as recorded games show: read
    /// every 10 ms, the flag is set without a gap from the moment the rule is met until the span after the last
    /// statement that found the client away, with one identity and a time held that only grows.
    /// </summary>
    [Fact]
    public void AHoldWithSnapBacksReadsSetForTheWholeEpisode()
    {
        using var session = InTown();
        var game = session.Game;
        const int Cycles = 70;
        const int Step = 10;
        var span = AreaResolver.HoldSpan.Ticks;
        long? firstSet = null;
        long? since = null;
        long lastFar = 0;
        var heldFor = TimeSpan.Zero;
        var offInside = 0;
        var onPoint = 0;
        var offPoint = 0;
        for (var ms = 0; ms < (Cycles * 150) + 6000; ms += Step)
        {
            var phase = ms % 150;
            if (ms < Cycles * 150 && phase == 0)
            {
                game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5060, 5010)));
                session.Receive(SyntheticGame.WalkVerify(5010, 5010));
                lastFar = session.Clock.GetTimestamp();
                session.Reassign(5010, 5010);
            }
            else if (ms < Cycles * 150 && phase == 50)
            {
                game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5060, 5010)));
            }

            var now = session.Clock.GetTimestamp();
            var hold = game.ServerHoldsPosition;
            if (hold != null)
            {
                firstSet ??= now;
                since ??= hold.Since;
                Assert.Equal(since, hold.Since);
                Assert.True(game.ServerHeldFor >= heldFor);
                heldFor = game.ServerHeldFor;
                if (hold.ClientIsOnHeldPoint)
                {
                    onPoint++;
                }
                else
                {
                    offPoint++;
                }
            }
            else if (firstSet != null && now < lastFar + span)
            {
                offInside++;
            }

            if (firstSet != null && now >= lastFar + span)
            {
                Assert.Null(hold);
            }

            session.Clock.Advance(Step / 1000.0);
        }

        Assert.NotNull(firstSet);
        Assert.Equal(0, offInside);
        Assert.Equal(TimeSpan.FromMilliseconds(34 * 150).Ticks, firstSet.Value - since.Value);
        Assert.True(onPoint > 0 && offPoint > 0);
        Assert.Null(game.ServerHoldsPosition);
    }

    /// <summary>
    /// Two holds at one point, apart by more than the span, are two episodes; a statement away exactly the span after the
    /// last one starts the second rather than bringing the first back.
    /// </summary>
    [Fact]
    public void TwoHoldsAtOnePointAreTwoEpisodes()
    {
        using var session = InTown();
        var game = session.Game;
        HoldFor(session, 7);
        var first = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);

        session.Clock.Advance(AreaResolver.HoldSpan.TotalSeconds);
        Assert.Null(game.ServerHoldsPosition);
        HoldFor(session, 5);
        Assert.Null(game.ServerHoldsPosition);
        session.Clock.Advance(1);
        HoldFor(session, 2);
        var second = Assert.IsType<ServerHeldPosition>(game.ServerHoldsPosition);
        Assert.NotEqual(first.Since, second.Since);
        Assert.Equal(first.Location, second.Location);
        Assert.Equal(TimeSpan.FromSeconds(6), game.ServerHeldFor);
    }

    /// <summary>A walk away and a walk verify at (5010, 5010) every second, <paramref name="statements"/> times; the clock stops at the last.</summary>
    private static void HoldFor(Session session, int statements)
    {
        for (var i = 0; i < statements; i++)
        {
            if (i > 0)
            {
                session.Clock.Advance(1);
            }

            session.Game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5060, 5010)));
            session.Receive(SyntheticGame.WalkVerify(5010, 5010));
        }
    }

    [Fact]
    public void StandingWhereTheServerSaysIsNoHold()
    {
        using var session = InTown();
        var game = session.Game;
        for (var i = 0; i < 10; i++)
        {
            session.Clock.Advance(1);
            session.Receive(SyntheticGame.LifeMana(100, 50, 5010, 5010));
            game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5015, 5010)));
        }

        Assert.Null(game.ServerHoldsPosition);
    }

    [Fact]
    public void StandingThenSettingOffIsNoHold()
    {
        using var session = InTown();
        var game = session.Game;
        for (var i = 0; i < 10; i++)
        {
            session.Clock.Advance(1);
            session.Receive(SyntheticGame.LifeMana(100, 50, 5010, 5010));
        }

        game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5045, 5010)));
        session.Receive(SyntheticGame.WalkVerify(5012, 5010));

        Assert.Null(game.ServerHoldsPosition);
    }

    [Fact]
    public void TheHoldEndsWhenTheClientStopsWalkingAwayAndAtAnActLoad()
    {
        using var session = InTown();
        var game = session.Game;
        HoldFor(session, 7);
        Assert.NotNull(game.ServerHoldsPosition);
        for (var i = 0; i < 7; i++)
        {
            session.Clock.Advance(1);
            session.Receive(SyntheticGame.LifeMana(100, 50, 5010, 5010));
        }

        Assert.Null(game.ServerHoldsPosition);

        session.Clock.Advance(1);
        for (var i = 0; i < 2; i++)
        {
            game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5060, 5010)));
            session.Receive(SyntheticGame.WalkVerify(5010, 5010));
        }

        Assert.Null(game.ServerHoldsPosition);
        session.Clock.Advance(1);
        HoldFor(session, 7);
        Assert.NotNull(game.ServerHoldsPosition);
        session.LoadAct(Act.Act2, Area.LutGholein);
        Assert.Null(game.ServerHoldsPosition);
    }

    /// <summary>
    /// After leaving a game the data stays the last game's until the next game's first packet; the confirmed answers
    /// must not describe it. Game.Area and IsInTown() are left as they were.
    /// </summary>
    [Fact]
    public void OutOfAGameTheConfirmedAnswersAreUnknown()
    {
        using var session = InTown();
        var game = session.Game;
        Assert.True(game.IsInTownConfirmed());
        HoldFor(session, 7);
        Assert.NotNull(game.ServerHoldsPosition);
        Assert.True(game.ServerDisagreesOnTown);
        Assert.Equal((Area.BloodMoor, AreaSource.Believed), game.KnownArea);
        var (area, inTown) = (game.Area, game.IsInTown());

        session.Server.GameLeft();

        Assert.False(game.IsInTownConfirmed());
        Assert.False(game.IsOutOfTownConfirmed());
        Assert.False(game.ServerDisagreesOnTown);
        Assert.False(game.ResolvedAreaIsActLoad);
        Assert.Null(game.ServerHoldsPosition);
        Assert.Equal(TimeSpan.Zero, game.ServerHeldFor);
        Assert.Equal((null, AreaSource.Unknown), game.KnownArea);
        Assert.Null(game.ResolvedArea);
        Assert.Null(game.LastResolvedArea);
        Assert.Null(game.ServerConfirmedArea);
        Assert.Null(game.ServerConfirmedLocation);
        Assert.Null(game.ServerConfirmedDistance);
        Assert.Equal(TimeSpan.MaxValue, game.ServerConfirmedAge);
        Assert.Equal(AreaState.Unknown, game.AreaState);
        Assert.Equal(area, game.Area);
        Assert.Equal(inTown, game.IsInTown());

        session.Receive([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Assert.False(game.IsInTownConfirmed());
    }

    /// <summary>
    /// A new game's flags clear "left the game" only once its data is installed: a core handler run before that must not
    /// be served the previous game's answers.
    /// </summary>
    [Fact]
    public void ANewGamesFlagsDoNotServeThePreviousGamesAnswers()
    {
        Session session = null;
        bool? duringFlags = null;
        using var owned = session = InTown(server => server.OnCoreReceivedPacketEvent(InComingPacket.GameFlags, _ =>
        {
            if (session != null)
            {
                duringFlags = session.Game.IsInTownConfirmed();
            }
        }));
        Assert.True(session.Game.IsInTownConfirmed());

        session.Server.GameLeft();
        session.Server.OnPacketReceived(new D2gsPacket([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));

        Assert.False(duringFlags);
        Assert.False(session.Server.LeftGame);
        Assert.False(session.Game.IsInTownConfirmed());
    }

    /// <summary>0x18: life, mana and stamina in 15 bits each, 14 more, then x and y, least significant bit first.</summary>
    private static byte[] LifeManaPot(ushort x, ushort y)
    {
        var packet = new byte[13];
        packet[0] = 0x18;
        var bit = 8 + (3 * 15) + 14;
        foreach (var value in new[] { x, y })
        {
            for (var i = 0; i < 16; i++, bit++)
            {
                if (((value >> i) & 1) != 0)
                {
                    packet[bit / 8] |= (byte)(1 << (bit % 8));
                }
            }
        }

        return packet;
    }
}
