using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

internal static class ClientMoves
{
    /// <summary>The bot's own step: the client action that sets the believed position, as the recorder writes it.</summary>
    public static HandTrace Believe(this HandTrace file, double at, ushort x, ushort y)
        => file.Add(TraceRecordKind.ClientAction, at, [(byte)ClientAction.Move, .. BitConverter.GetBytes(ClientActions.Pack(new Point(x, y)))]);

    public static HandTrace RunTo(this HandTrace file, double at, ushort x, ushort y)
        => file.Sent(at, SyntheticGame.Location(0x03, x, y)).Believe(at, x, y);
}

public class SelfPositionCheckTests
{
    [Fact]
    public void TheServerAnsweringEveryStepWithOnePositionIsAnIncident()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1);
        for (var i = 0; i < 8; i++)
        {
            file.RunTo(2 + i, 5160, (ushort)(2010 + (5 * (i + 1))))
                .Received(2.2 + i, SyntheticGame.WalkVerify(5160, 2010));
        }

        file.Ticks(10.5, 12.5, 5161, 2010).WriteTo(folder);

        var incident = Assert.Single(Game1.Replayed(folder, "client1", new ServerHoldsPositionCheck()));
        Assert.Equal(ServerHoldsPositionCheck.Kind, incident.Kind);
        Assert.Equal(TimeSpan.FromSeconds(4), incident.Offset);
        Assert.Contains("(5160, 2010)", incident.Detail);
        Assert.Contains("6 walk/run packets", incident.Detail);
        Assert.Contains("up to 40 away", incident.Detail);
        Assert.Contains("Game.ServerHoldsPosition was set", incident.Detail);
    }

    [Fact]
    public void AnActLoadEndsTheEpisode()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1);
        for (var i = 0; i < 8; i++)
        {
            file.RunTo(2 + i, 5160, (ushort)(2010 + (5 * (i + 1))))
                .Received(2.2 + i, SyntheticGame.WalkVerify(5160, 2010));
        }

        file.Received(10.5, [0x03, 0x01, 0x73, 0x31, 0x1B, 0x5C, 0x28, 0x00, 0xB0, 0x00, 0x50, 0x24])
            .Believe(10.6, 5100, 5100)
            .RunTo(10.7, 5130, 5100)
            .Received(11, SyntheticGame.Reassign(1, 5100, 5100))
            .WriteTo(folder);

        var incident = Assert.Single(Game1.Replayed(folder, "client1", new ServerHoldsPositionCheck()));
        Assert.Contains("ended by an act load", incident.Detail);
    }

    [Fact]
    public void StandingWhereTheServerSaysThenSettingOffIsNoIncident()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1)
            .Ticks(2, 12)
            .RunTo(12.5, 5160, 2045)
            .Received(12.7, SyntheticGame.WalkVerify(5161, 2010))
            .Received(13.5, SyntheticGame.Reassign(1, 5160, 2030))
            .Ticks(14, 16, 5160, 2030)
            .WriteTo(folder);

        Assert.Empty(Game1.Replayed(folder, "client1", new ServerHoldsPositionCheck()));
    }

    [Fact]
    public void ATeleportExplainsTheNextPositionAndTalkingToAnNpcDoesNot()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1)
            .Received(1.5, SyntheticGame.SetSkill(1, Hand.Right, Skill.Teleport))
            .Sent(5, SyntheticGame.Location(0x0C, 5190, 2010))
            .Received(5.3, SyntheticGame.Reassign(1, 5190, 2010))
            .Received(6, SyntheticGame.AssignNpc(0x30, NPCCode.Akara, 5192, 2010))
            .Sent(8, SyntheticGame.Unit(0x13, EntityType.NPC, 0x30))
            .Received(8.5, SyntheticGame.Reassign(1, 5160, 2010))
            .Received(10, SyntheticGame.AssignObject(0x50, EntityCode.WaypointAct1, 5162, 2012))
            .Sent(12, SyntheticGame.Unit(0x13, EntityType.Object, 0x50))
            .Received(13, SyntheticGame.Reassign(1, 5190, 2040))
            .Ticks(14, 15, 5190, 2040)
            .WriteTo(folder);

        var self = new SelfAccuracy();
        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [self])?.Replayer.Dispose();

        var far = self.Corrections.Where(c => c.Distance > 20).Skip(1).ToList();
        Assert.Equal([true, false, true], far.Select(c => c.Warp));
    }

    [Fact]
    public void TheTownTableSplitsUnknownByCause()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 3, 5057, 1902).WriteTo(folder);

        var self = new SelfAccuracy();
        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [self])?.Replayer.Dispose();

        Assert.Equal(0.03, self.TownUnknown[(int)TownUnknownCause.Join].Seconds, 3);
        Assert.Equal(1, self.TownUnknown[(int)TownUnknownCause.Join].Count);
        Assert.Equal(0.03, self.InTownUnconfirmed.Seconds, 3);
        Assert.Equal(TimeSpan.FromSeconds(0.06), Assert.Single(self.FirstStatementAfter));
    }
}
