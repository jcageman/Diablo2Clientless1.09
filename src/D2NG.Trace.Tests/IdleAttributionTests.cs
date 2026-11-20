using D2NG.Core.D2GS.Objects;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class IdleAttributionTests
{
    private static (ActivityObserver Activity, RequestObserver Requests) Replay(string folder)
    {
        var query = GameQuery.Open(folder);
        return query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (new ActivityObserver(), new RequestObserver()), o => [o.Item1, o.Item2])[0].Observers;
    }

    private static byte[] EntityUpdate() => [0x4B, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];

    [Fact]
    public void ARequestIsPairedWithTheFirstAnswerOfItsKindAndOneNeverAnsweredIsCountedAsSuch()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Sent(2, Packets.Interact(0x5))
            .Received(2.13, [0x27, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00])
            .Sent(3, Packets.Pickup(0x40))
            .Sent(3.05, Packets.Pickup(0x41))
            .Received(3.12, SyntheticGame.Remove(EntityType.Item, 0x40))
            .Sent(4, EntityUpdate())
            .Received(4.25, SyntheticGame.Reassign(1, 5057, 1902))
            .Ticks(5, 12, 5057, 1902)
            .WriteTo(folder);

        var (_, requests) = Replay(folder);
        var table = new LatencyTable();
        table.Add(requests);
        var rows = table.Rows().ToDictionary(r => r.Kind);

        Assert.Equal(130, rows[RequestObserver.NpcInteract].Median, 0);
        Assert.Equal(250, rows[RequestObserver.EntityUpdate].Median, 0);
        Assert.Equal((1, 1), (rows[RequestObserver.PickUp].Answered, rows[RequestObserver.PickUp].Unanswered));
        Assert.Equal(120, rows[RequestObserver.PickUp].Median, 0);
    }

    private static byte[] Cast(ushort x, ushort y) => SyntheticGame.Location(0x0C, x, y);

    [Fact]
    public void TeleportRecastUntilTheReassignIsOneRequestTimedFromItsFirstCast()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var trace = new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(1)
            .Received(1.5, SyntheticGame.SetSkill(1, Core.D2GS.Players.Hand.Right, Core.D2GS.Players.Skill.Teleport));
        foreach (var hop in new[] { 2.0, 3.0, 4.0 })
        {
            for (var cast = 0; cast < 5; cast++)
            {
                trace.Sent(hop + cast * 0.065, Cast(5180, 2010));
            }

            trace.Received(hop + 0.29, SyntheticGame.Reassign(1, 5180, 2010));
        }

        trace.Ticks(5, 12, 5180, 2010).WriteTo(folder);

        var (activity, requests) = Replay(folder);
        var table = new LatencyTable();
        table.Add(requests);
        var teleport = table.Rows().Single(r => r.Kind == RequestObserver.Teleport);

        Assert.Equal((3, 0), (teleport.Answered, teleport.Unanswered));
        Assert.Equal(290, teleport.Max, 0);
        var stretches = activity.Stretches(TimeSpan.Zero, TimeSpan.FromSeconds(12), []);
        var stills = IdleAttribution.Of(activity, stretches, requests, [], TimeSpan.FromSeconds(1));
        var teleporting = stills.SelectMany(s => s.Parts).Where(p => p.What.StartsWith(RequestObserver.Teleport, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(teleporting, p => p.What.EndsWith("(unanswered)", StringComparison.Ordinal));
        Assert.InRange(teleporting.Aggregate(TimeSpan.Zero, (a, p) => a + p.Time).TotalSeconds, 0, 0.9);
    }

    private static byte[] Waypoint(uint id, uint level) => [0x49, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(level)];

    [Fact]
    public void ClosingTheWaypointMenuAsksNothingAndTheNextWaypointIsTimedOnItsOwn()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Sent(2, Waypoint(0x20, 0))
            .Ticks(3, 7, 5057, 1902)
            .Sent(7.5, Waypoint(0x20, 0x03))
            .Received(7.55, SyntheticGame.Reassign(1, 5057, 1902))
            .Ticks(8, 9, 5057, 1902)
            .WriteTo(folder);

        var (activity, requests) = Replay(folder);
        var waypoint = Assert.Single(requests.Requests, r => r.Kind == RequestObserver.Waypoint);
        Assert.Equal(TimeSpan.FromSeconds(7.5), waypoint.At);
        Assert.Equal(50, waypoint.Latency.Value.TotalMilliseconds, 0);
        Assert.Contains(requests.Actions, a => a.Text == "close waypoint menu");

        var stretches = activity.Stretches(TimeSpan.Zero, TimeSpan.FromSeconds(9), []);
        var still = IdleAttribution.Of(activity, stretches, requests, [], TimeSpan.FromSeconds(1)).First(s => s.Start >= TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, still.ServerWait);
        Assert.StartsWith("bot: after close waypoint menu", still.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityUpdatesPolledWhileStandingAreWaitingAndNotAnAnswerActedOnLate()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var trace = new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 12);
        for (var at = 3.0; at < 10; at += 0.8)
        {
            trace.Sent(at, EntityUpdate()).Received(at + 0.04, SyntheticGame.Reassign(1, 5160, 2010));
        }

        trace.WriteTo(folder);
        var (activity, requests) = Replay(folder);
        var stretches = activity.Stretches(TimeSpan.Zero, TimeSpan.FromSeconds(12), []);

        var stills = IdleAttribution.Of(activity, stretches, requests, [], TimeSpan.FromSeconds(1)).Where(s => s.Start >= TimeSpan.FromSeconds(2)).ToList();

        Assert.NotEmpty(stills);
        Assert.All(stills, s => Assert.DoesNotContain("acting on an answer late", s.Cause, StringComparison.Ordinal));
        Assert.Contains(stills, s => s.Cause == "bot: waiting, polling entity update (0x4B)");
    }

    private static byte[] PortalObject(uint id, ushort x, ushort y)
        => [0x51, 0x02, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes((ushort)EntityCode.TownPortal), .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), 0x00, 0x4B];

    private static byte[] PortalOwner(uint owner, uint ours, uint theirs)
        => [0x82, .. BitConverter.GetBytes(owner), .. new byte[16], .. BitConverter.GetBytes(ours), .. BitConverter.GetBytes(theirs)];

    [Fact]
    public void ATownPortalCastIsAnsweredByTheClientsOwnNewPortalNotOneComingIntoView()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(1)
            .Received(1.5, SyntheticGame.SetSkill(1, Core.D2GS.Players.Hand.Right, Core.D2GS.Players.Skill.BookOfTownportal))
            .Sent(2, Cast(5162, 2010))
            .Received(2.1, PortalObject(0x300, 5170, 2015))
            .Received(2.1, PortalOwner(2, 0x300, 0x301))
            .Sent(2.2, Cast(5162, 2010))
            .Received(2.4, PortalObject(0x302, 5162, 2010))
            .Received(2.4, PortalOwner(1, 0x302, 0x303))
            .Ticks(3, 5)
            .WriteTo(folder);

        var (_, requests) = Replay(folder);

        var portal = Assert.Single(requests.Requests, r => r.Kind == RequestObserver.TownPortal);
        Assert.Equal(400, portal.Latency.Value.TotalMilliseconds, 0);
    }

    [Fact]
    public void AStandStillSplitsIntoServerWaitTypedWaitAndBotWait()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(1)
            .Ticks(2, 9)
            .Sent(3, EntityUpdate())
            .Received(4, SyntheticGame.Reassign(1, 5160, 2010))
            .WriteTo(folder);
        var (activity, requests) = Replay(folder);
        List<(TimeSpan Start, TimeSpan End, string Reason)> waits = [(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8), "party")];
        var stretches = activity.Stretches(TimeSpan.Zero, TimeSpan.FromSeconds(9), [.. waits.Select(w => (w.Start, w.End))]);

        var stills = IdleAttribution.Of(activity, stretches, requests, waits, TimeSpan.FromSeconds(1)).Where(s => s.Start >= TimeSpan.FromSeconds(2)).ToList();
        var server = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.ServerWait);
        var typed = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.TypedWait);
        var bot = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.BotWait);

        Assert.InRange(server.TotalSeconds, 0.95, 1.05);
        Assert.InRange(typed.TotalSeconds, 1.95, 2.15);
        Assert.Equal(stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration), server + typed + bot);
        Assert.Contains(stills.SelectMany(s => s.Parts), p => p.What == RequestObserver.EntityUpdate);
        Assert.Contains(stills.SelectMany(s => s.Parts), p => p.What == "typed wait: party");
        Assert.All(stills, s => Assert.False(string.IsNullOrEmpty(s.Cause)));
    }

    [Fact]
    public void AnOutingSplitsIntoTravelKillLootAndReturn()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(10)
            .Ticks(11, 30)
            .Sent(15, SyntheticGame.Location(0x05, 5165, 2012))
            .Sent(20, SyntheticGame.Location(0x05, 5165, 2012))
            .Sent(23, Packets.Pickup(0x40))
            .WriteTo(folder);
        var (activity, requests) = Replay(folder);
        var to = TimeSpan.FromSeconds(30);
        var stretches = activity.Stretches(TimeSpan.Zero, to, []);

        var phases = FarmPhases.Of(activity, stretches, requests, TimeSpan.Zero, to);

        Assert.Equal(to, phases.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b));
        Assert.InRange(phases["town"].TotalSeconds, 9.8, 10.1);
        Assert.InRange(phases["travel"].TotalSeconds, 4.9, 5.1);
        Assert.InRange(phases["kill"].TotalSeconds, 6.4, 6.7);
        Assert.InRange(phases["loot"].TotalSeconds, 2.9, 3.1);
        Assert.InRange(phases["return"].TotalSeconds, 5.3, 5.6);
    }

    /// <summary>A Super Healing Potion put in the belt, as a recorded game received it, with its id replaced.</summary>
    private static byte[] BeltPotion(uint id)
    {
        var packet = Convert.FromHexString("9C0E1410380000001000A0000108D28006570302");
        BitConverter.GetBytes(id).CopyTo(packet, 4);
        return packet;
    }

    private static byte[] Used(uint id) => [0x3F, 0x00, .. BitConverter.GetBytes(id), 0x00, 0x00];

    private static byte[] FromBelt(uint id)
    {
        var packet = BeltPotion(id);
        packet[1] = (byte)Core.D2GS.Items.Action.RemoveFromBelt;
        return packet;
    }

    private static byte[] Drink(uint id, bool merc = false) => [0x26, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(merc ? 1u : 0u), 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public void HealingPotionsDrunkFasterThanTheyHealOrAtFullLifeAreWasted()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(2, SyntheticGame.LifeMana(600, 100, 5057, 1902))
            .Received(2.9, BeltPotion(0x38))
            .Sent(3, Drink(0x38))
            .Received(3.04, Used(0x38))
            .Received(3.7, BeltPotion(0x39))
            .Sent(3.8, Drink(0x39))
            .Received(3.84, Used(0x39))
            .Received(4.4, BeltPotion(0x3C))
            .Sent(4.5, Drink(0x3C))
            .Received(5.9, BeltPotion(0x3A))
            .Sent(6, Drink(0x3A))
            .Received(6.04, FromBelt(0x3A))
            .Received(8, SyntheticGame.LifeMana(1000, 100, 5057, 1902))
            .Received(8.9, BeltPotion(0x3B))
            .Sent(9, Drink(0x3B))
            .Received(9.04, Used(0x3B))
            .Received(9.1, BeltPotion(0x3D))
            .Sent(9.2, Drink(0x3D, merc: true))
            .Received(9.24, Used(0x3D))
            .Ticks(10, 12, 5057, 1902)
            .WriteTo(folder);

        var wasted = Assert.Single(Game1.Replayed(folder, "client1", new PotionWasteCheck()));

        Assert.Equal(PotionWasteCheck.Kind, wasted.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3.8), wasted.Offset);
        Assert.Equal(TimeSpan.FromSeconds(9), wasted.End);
        Assert.Contains("2 potions drunk for nothing", wasted.Detail, StringComparison.Ordinal);
        Assert.Contains("800 ms after the previous healing potion", wasted.Detail, StringComparison.Ordinal);
        Assert.Contains("at full life 1000/1000", wasted.Detail, StringComparison.Ordinal);
    }
}
