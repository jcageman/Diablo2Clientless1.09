using D2NG.Core.D2GS.Objects;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

[CollectionDefinition(nameof(TraceCacheCollection), DisableParallelization = true)]
public sealed class TraceCacheCollection;

/// <summary>The caches are process-wide switches, so these tests run alone.</summary>
[Collection(nameof(TraceCacheCollection))]
public sealed class TraceCacheTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly string _previousRoot = TraceCache.Root;

    public TraceCacheTests()
    {
        TraceCache.Root = Path.Combine(_root.Path, "cache");
        TraceCache.Reset();
    }

    public void Dispose()
    {
        TraceCache.Enabled = false;
        TraceCache.Root = _previousRoot;
        TraceCache.Reset();
        _root.Dispose();
    }

    private static void ForgetFiles(string folder)
    {
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            TraceFileSummary.Forget(file);
            SidecarReader.Forget(file);
        }

        TraceCache.Reset();
    }

    private string Game()
    {
        var folder = Game1.Folder(_root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(2)
            .Sent(5, SyntheticGame.Location(0x0C, 5165, 2012))
            .Sent(5.5, Packets.Pickup(0x40))
            .Received(6, SyntheticGame.LifeMana(400, 100, 5160, 2010))
            .Ticks(7, 30)
            .WriteTo(folder);
        new HandTrace("client2", "party1", Game1.Start).Join(2).OutOfTown(3).Ticks(4, 30).WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(1, "step", "client1", "party1", ("name", "kill"), ("attempt", 1), ("phase", "start"))
            .Typed(20, "step", "client1", "party1", ("name", "kill"), ("attempt", 1), ("phase", "end"), ("outcome", "failed"))
            .WriteTo(Game1.Date(_root), "120000");
        return folder;
    }

    [Fact]
    public void AFileSummaryComesBackFromTheCacheAndAFileThatChangedIsReadAgain()
    {
        var folder = Game();
        var path = Directory.GetFiles(folder, "client1*").Single();
        TraceCache.Enabled = true;
        var read = TraceFileSummary.Of(path);
        TraceCache.Flush();
        ForgetFiles(folder);

        var cached = TraceFileSummary.Of(path);

        Assert.Equal((read.Length, read.EndUtc, read.ServerGame, read.Header.StartUtc, read.Header.ClientLabel, read.Header.GameName, read.Evidence),
            (cached.Length, cached.EndUtc, cached.ServerGame, cached.Header.StartUtc, cached.Header.ClientLabel, cached.Header.GameName, cached.Evidence));
        Assert.Equal(read.Records.OrderBy(r => r.Key), cached.Records.OrderBy(r => r.Key));

        ForgetFiles(folder);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 60).WriteTo(folder);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(new FileInfo(path).Length, TraceFileSummary.Of(path).Length);
        Assert.Equal(Game1.Start + TimeSpan.FromSeconds(60), TraceFileSummary.Of(path).EndUtc);
    }

    private static (List<Incident> Incidents, List<string> Stretches) Answers(string folder)
    {
        var query = GameQuery.Open(folder);
        var incidents = Query.Incidents.Find(query, new IncidentOptions { History = TraceHistory.InMemory() }).Incidents;
        var activity = GameQuery.Open(folder).ReplayAll(["client1", "client2"], TimeSpan.MaxValue, () => (new ActivityObserver(), new FightObserver(), new TownObserver()), o => [o.Item1, o.Item2, o.Item3]);
        var stretches = activity.SelectMany(a => a.Observers.Item1.Stretches(TimeSpan.Zero, TimeSpan.FromSeconds(30), []).Select(s => $"{a.Client} {s}")
            .Concat(a.Observers.Item2.Casts.Select(c => c.ToString())).Concat(a.Observers.Item3.FileEnds.Select(f => f.ToString()))).ToList();
        return (incidents, stretches);
    }

    [Fact]
    public void ACachedReplayGivesTheSameAnswersAsAFreshOneColdAndWarm()
    {
        var folder = Game();
        var fresh = Answers(folder);

        TraceCache.Enabled = true;
        ForgetFiles(folder);
        var cold = Answers(folder);
        TraceCache.Flush();
        ForgetFiles(folder);
        var warm = Answers(folder);

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(TraceCache.Root, "derived"), "*.bin", SearchOption.AllDirectories));
        Assert.Equal(fresh.Incidents, cold.Incidents);
        Assert.Equal(fresh.Incidents, warm.Incidents);
        Assert.Equal(fresh.Stretches, cold.Stretches);
        Assert.Equal(fresh.Stretches, warm.Stretches);
        Assert.Contains(fresh.Incidents, i => i.Kind == "step failed");
    }

    [Fact]
    public void AReplayIsSkippedOnlyWhileItsFilesAndRulesAreUnchanged()
    {
        var folder = Game();
        TraceCache.Enabled = true;
        GameQuery.Open(folder).ReplayAll(["client1"], TimeSpan.MaxValue, () => new ActivityObserver(), o => [o]);
        ForgetFiles(folder);

        var again = GameTrace.Open(folder);
        Assert.True(DerivedCache.TryLoad(again, "client1", [new ActivityObserver()]));
        Assert.False(DerivedCache.TryLoad(again, "client1", [new ActivityObserver(), new RequestObserver()]));

        ReplayRules.Use("0");
        try
        {
            Assert.False(DerivedCache.TryLoad(again, "client1", [new ActivityObserver()]));
        }
        finally
        {
            ReplayRules.Use(null);
        }

        File.SetLastWriteTimeUtc(Directory.GetFiles(folder, "client1*").Single(), DateTime.UtcNow.AddMinutes(1));
        ForgetFiles(folder);
        Assert.False(DerivedCache.TryLoad(GameTrace.Open(folder), "client1", [new ActivityObserver()]));
    }

    private static (List<string> Stretches, int Replays) Activity(string folder)
    {
        var query = GameQuery.Open(folder);
        var (_, activity) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => new ActivityObserver(), o => [o])[0];
        return ([.. activity.Stretches(TimeSpan.Zero, TimeSpan.FromSeconds(30), []).Select(s => s.ToString())], query.Game.Replays.GetValueOrDefault("client1"));
    }

    [Fact]
    public void AWarmCacheSkipsTheReplayAndACutOrCorruptEntryIsReplayedAfresh()
    {
        var folder = Game();
        var fresh = Activity(folder);
        TraceCache.Enabled = true;
        ForgetFiles(folder);
        Activity(folder);
        TraceCache.Flush();
        ForgetFiles(folder);

        var warm = Activity(folder);
        Assert.Equal(fresh.Stretches, warm.Stretches);
        Assert.Equal(0, warm.Replays);

        var entry = Directory.GetFiles(Path.Combine(TraceCache.Root, "derived"), "*.bin", SearchOption.AllDirectories).Single();
        var whole = File.ReadAllBytes(entry);
        File.WriteAllBytes(entry, whole[..^3]);
        ForgetFiles(folder);
        var cut = Activity(folder);
        Assert.Equal(fresh.Stretches, cut.Stretches);
        Assert.Equal(1, cut.Replays);

        var corrupt = (byte[])whole.Clone();
        corrupt[^24] ^= 0xFF;
        File.WriteAllBytes(entry, corrupt);
        ForgetFiles(folder);
        var corrupted = Activity(folder);
        Assert.Equal(fresh.Stretches, corrupted.Stretches);
        Assert.Equal(1, corrupted.Replays);
    }

    [Fact]
    public void ACacheFileClaimingMoreEntriesThanItHoldsIsAMiss()
    {
        var folder = Game();
        var path = Directory.GetFiles(folder, "client1*").Single();
        TraceCache.Enabled = true;
        var read = TraceFileSummary.Of(path);
        TraceCache.Flush();
        ForgetFiles(folder);
        var bucket = Directory.GetFiles(Path.Combine(TraceCache.Root, "files"), "*.bin").Single();
        var bytes = File.ReadAllBytes(bucket);
        using (var reader = new BinaryReader(new MemoryStream(bytes)))
        {
            reader.ReadString();
            reader.ReadString();
            reader.ReadString();
            BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, (int)reader.BaseStream.Position);
        }

        File.WriteAllBytes(bucket, bytes);

        Assert.Equal(read.EndUtc, TraceFileSummary.Of(path).EndUtc);
    }

    [Fact]
    public void SavingTheFileSummariesKeepsWhatAnotherProcessWroteMeanwhile()
    {
        var folder = Game();
        var first = Directory.GetFiles(folder, "client1*").Single();
        var second = Directory.GetFiles(folder, "client2*").Single();
        TraceCache.Enabled = true;
        TraceFileSummary.Of(second);
        TraceCache.Flush();
        var bucket = Directory.GetFiles(Path.Combine(TraceCache.Root, "files"), "*.bin").Single();
        var otherProcess = File.ReadAllBytes(bucket);
        File.Delete(bucket);
        ForgetFiles(folder);

        TraceFileSummary.Of(first);
        File.WriteAllBytes(bucket, otherProcess);
        TraceCache.Flush();

        var saved = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(bucket));
        Assert.Contains(Path.GetFileName(first), saved, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(second), saved, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWholeCacheIsKeptUnderItsCapAndLeftoverTemporaryFilesGo()
    {
        var folder = Game();
        TraceCache.Enabled = true;
        Activity(folder);
        TraceCache.Flush();
        var leftover = Path.Combine(TraceCache.Root, "files", "x.bin.1.abc.tmp");
        File.WriteAllText(leftover, "half");
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));
        var sizes = Directory.GetFiles(TraceCache.Root, "*", SearchOption.AllDirectories).Where(f => f != leftover).Select(f => new FileInfo(f).Length).ToList();
        var previous = TraceCache.CapBytes;
        TraceCache.CapBytes = sizes.Max();
        try
        {
            TraceCache.Prune();
        }
        finally
        {
            TraceCache.CapBytes = previous;
        }

        Assert.False(File.Exists(leftover));
        var left = Directory.GetFiles(TraceCache.Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.InRange(left, 1, sizes.Max());
    }

    [Fact]
    public void ASidecarKnownToEndBeforeAWindowIsNotRead()
    {
        var folder = Game();
        var sidecar = Directory.GetFiles(Game1.Date(_root), "events-*").Single();
        TraceCache.Enabled = true;
        Assert.NotEmpty(SidecarReader.Read([sidecar], Game1.Start, Game1.Start.AddMinutes(1)));
        TraceCache.Flush();
        ForgetFiles(folder);
        ForgetFiles(Game1.Date(_root));

        Assert.False(SidecarReader.MayHold(sidecar, Game1.Start.AddHours(1), Game1.Start.AddHours(2)));
        Assert.True(SidecarReader.MayHold(sidecar, Game1.Start, Game1.Start.AddMinutes(1)));
        Assert.Empty(SidecarReader.Read([sidecar], Game1.Start.AddHours(1), Game1.Start.AddHours(2)));
    }
}
