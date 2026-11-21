using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Trace.Query;
using Serilog.Events;
using System;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>The fifth review round's tests of what the fixes added: the unknown area's display, the town reconciliation, the rejoin evidence, the buffing state, the between-games bucket and TIMING steps as events.</summary>
public class Round5AdditionTests
{
    [Fact]
    public void AnUnknownAreaCarriesTheLastKnownOneForDisplay()
    {
        using var root = new TempRoot();
        var query = GameQuery.Open(AreaOutsideTheRevealedMapTests.OffTheMap(root));
        var activity = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => new ActivityObserver(), o => [o])[0].Observers;

        Assert.Null(activity.AreaAt(TimeSpan.FromSeconds(20)));
        Assert.Equal("unknown (last known: BloodMoor)", activity.AreaName(TimeSpan.FromSeconds(20)));
        Assert.Equal(Core.D2GS.Act.Area.BloodMoor, activity.AreaAt(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void TimeInTownAreasIsTheHeadlineTownPlusTheStatesThatOutrankItThere()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(0.2, SyntheticGame.AssignNpc(0x10, NPCCode.Akara, 5060, 1905))
            .Sent(2, [0x2F, .. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(0x10u)]).Sent(4, [0x30, .. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(0x10u)])
            .Ticks(5, 10, 5057, 1902)
            .OutOfTown(12)
            .Ticks(13, 20)
            .WriteTo(folder);
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver()), x => [x.Activity, x.Town])[0];
        var stretches = o.Activity.Stretches(TimeSpan.Zero, end, []);
        var split = new ActivitySplit();
        split.Add(stretches, TimeSpan.Zero, end);

        var town = new TownSummary();
        town.Add(TownStays.Of("client1", o.Activity, o.Town, [], [], TownStays.Chickens(query, "client1"), end), stretches);

        Assert.Equal(split.Totals[ActivityState.Town], town.States[ActivityState.Town]);
        Assert.True(town.States[ActivityState.Npc] > TimeSpan.FromSeconds(3));
        Assert.Equal(town.Total, town.States.Values.Aggregate(TimeSpan.Zero, (sum, t) => sum + t));
        Assert.Equal(town.Total.TotalSeconds, town.ByCause.Values.Sum(v => v.Sum()), 3);
        Assert.StartsWith($"time in town areas: {town.Total.TotalSeconds:0}s (headline town {split.Totals[ActivityState.Town].TotalSeconds:0}s + npc", town.Reconcile());
    }

    [Fact]
    public void ARejoinStayCarriesTheLifeBeforeLeavingAndWhetherLeaveGameWasSent()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(2).Ticks(3, 9).Received(10, SyntheticGame.LifeMana(270, 100, 5160, 2010))
            .Sent(10.002, [(byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame]).WriteTo(folder);
        new HandTrace("client1", "party1", Game1.Start.AddSeconds(15)).Join(1).Ticks(1, 10, 5057, 1902).WriteTo(folder, "client1-2");
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver()), x => [x.Activity, x.Town])[0];

        var rejoin = TownStays.Of("client1", o.Activity, o.Town, [], [], TownStays.Chickens(query, "client1"), end)[^1];

        Assert.Equal(TownStays.UnknownRejoin, rejoin.Cause);
        Assert.Equal("life 27% 0.002s before leaving; the client sent leave-game", rejoin.Evidence);
    }

    [Fact]
    public void ABuffCastIsTheBuffingStateAndItsOwnGroupOfSkills()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: CharacterClass.Paladin)
            .OutOfTown(0.5)
            .Received(1, SyntheticGame.SetSkill(1, Hand.Right, Skill.HolyShield))
            .Sent(2, SyntheticGame.Location(0x0C, 5160, 2010))
            .Ticks(3, 8)
            .WriteTo(folder);
        var query = GameQuery.Open(folder);
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Fights: new FightObserver()), x => [x.Activity, x.Fights])[0];
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var stretches = o.Activity.Stretches(TimeSpan.Zero, end, []);

        var skills = new SkillSplit();
        skills.Add(o.Fights, stretches, TimeSpan.Zero, end);

        Assert.Equal(ActivityState.Buffing, stretches.Single(s => s.Start <= TimeSpan.FromSeconds(2.5) && TimeSpan.FromSeconds(2.5) < s.End).State);
        Assert.Equal(1, skills.Buffs["HolyShield"].Casts);
        Assert.Equal(skills.Buffing, skills.Buffs["HolyShield"].Time);
        Assert.True(skills.Buffing >= ActivityObserver.BuffMemory && skills.Fighting == TimeSpan.Zero);
        Assert.Contains(skills.Lines(), l => l.StartsWith("buffs (not fighting", StringComparison.Ordinal));
    }

    [Fact]
    public void LinesBetweenTwoGamesOfAProcessGoToABucketWithTheirWarnings()
    {
        using var root = new TempRoot();
        var first = Game1.Folder(root, "partyA1");
        var second = Game1.Folder(root, "partyB1");
        new HandTrace("client1", "partyA1", Game1.Start).Join(1).Ticks(1, 60).WriteTo(first);
        new HandTrace("client1", "partyB1", Game1.Start.AddSeconds(120)).Join(1, token: 0x0203).Ticks(1, 60).WriteTo(second);
        new SidecarFile(Game1.Start)
            .Typed(2, "move", "client1", "partyA1", ("kind", "walk"), ("outcome", "arrived"))
            .Log(90, LogEventLevel.Warning, "Mule detour found nothing to carry")
            .Log(95, LogEventLevel.Information, "Creating the next game")
            .Typed(122, "move", "client1", "partyB1", ("kind", "walk"), ("outcome", "arrived"))
            .WriteTo(Game1.Date(root), "115900");
        var sources = new TraceSources();
        var games = GameTrace.GamesUnder(Game1.Date(root));

        var bucket = Assert.Single(BetweenGames.Of(games, sources));

        Assert.Equal("partyA1", bucket.Before.GameName);
        Assert.Equal("partyB1", bucket.After.GameName);
        Assert.Equal(2, bucket.Lines.Count);
        Assert.Equal(1, bucket.Warnings);
        Assert.DoesNotContain(games.SelectMany(g => sources.QueryOf(g).GameEntries()), e => e.IsLog);
    }

    [Fact]
    public void AStepKnownOnlyFromATimingLineIsAStepEvent()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 120).WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(1, "move", "client1", "party1", ("kind", "walk"), ("outcome", "arrived"))
            .Log(100, LogEventLevel.Information, "TIMING step {Step} {Outcome} in {Seconds:0}s", ("Step", "Mephisto"), ("Outcome", "ok"), ("Seconds", 95.0))
            .WriteTo(Game1.Date(root), "115900");

        var events = new EventFilter("step:Mephisto").Events(GameQuery.Open(folder)).ToList();

        var (client, at, description) = Assert.Single(events);
        Assert.Null(client);
        Assert.Equal(TimeSpan.FromSeconds(100), at);
        Assert.Contains("Mephisto ok", description);
        Assert.Empty(new EventFilter("step:Mephisto:failed").Events(GameQuery.Open(folder)));
    }
}
