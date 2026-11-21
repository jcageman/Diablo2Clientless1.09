using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>Guards against the costs the query commands once had coming back: counted work, not wall-clock time.</summary>
public class QueryCostTests
{
    private static string TwoClients(TempRoot root)
    {
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(2).Ticks(3, 120).WriteTo(folder);
        new HandTrace("client2", "party1", Game1.Start).Join(2).OutOfTown(2).Ticks(3, 120).WriteTo(folder);
        return folder;
    }

    [Fact]
    public void IncidentsReplayEachClientOnceForEveryCheck()
    {
        using var root = new TempRoot();
        var query = GameQuery.Open(TwoClients(root));

        Incidents.Find(query, new IncidentOptions { History = TraceHistory.InMemory() });

        Assert.Equal(new Dictionary<string, int> { ["client1"] = 1, ["client2"] = 1 }, query.Game.Replays.OrderBy(r => r.Key).ToDictionary());
    }

    [Fact]
    public void AnHourOfActivitySlicesAllocatesPerStretchNotPerSlice()
    {
        using var root = new TempRoot();
        var query = GameQuery.Open(TwoClients(root));
        var (_, activity) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => new ActivityObserver(), o => [o])[0];
        List<(TimeSpan Start, TimeSpan End)> waits = [(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11))];
        activity.Stretches(TimeSpan.Zero, TimeSpan.FromMinutes(1), waits);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stretches = activity.Stretches(TimeSpan.Zero, TimeSpan.FromHours(1), waits);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(stretches.Count < 20);
        Assert.True(allocated < 20_000, $"{allocated:N0} bytes for 36,000 slices");
    }

    [Fact]
    public void AWalkOrStepFailureAfterAStopRequestSaysSo()
    {
        List<Incident> found =
        [
            new(TimeSpan.FromSeconds(10), "client1", "path failures", "3 moves failed"),
            new(TimeSpan.FromSeconds(30), "client1", "path failures", "3 moves failed"),
            new(TimeSpan.FromSeconds(40), "client1", "life drop", "lost 40%"),
        ];

        var marked = Incidents.AfterStopRequest(found, TimeSpan.FromSeconds(20));

        Assert.Equal(["3 moves failed", "3 moves failed; after a stop request at 00:20.000", "lost 40%"], marked.Select(i => i.Detail));
        Assert.Same(found, Incidents.AfterStopRequest(found, null));
    }
}
