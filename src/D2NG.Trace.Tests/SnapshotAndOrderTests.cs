using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace D2NG.Trace.Tests;

public class SnapshotAndOrderTests
{
    private static void Append(string path, int count, long fromTicks)
    {
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        for (var i = 0; i < count; i++)
        {
            var record = new byte[TraceFormat.RecordHeaderSize + 2];
            TraceFormat.WriteRecordHeader(record, TraceRecordKind.D2gsReceived, fromTicks + i * TimeSpan.TicksPerSecond, 2);
            record[TraceFormat.RecordHeaderSize] = 0x8F;
            file.Write(record);
        }
    }

    [Fact]
    public void AFileThatGrowsIsReadToTheLengthItHadWhenTheProcessFirstOpenedIt()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, "client 1" + TraceFormat.Extension);
        File.WriteAllBytes(path, TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1"));
        Append(path, 2, TimeSpan.TicksPerSecond);

        int first;
        using (var reader = new TraceReader(path))
        {
            first = reader.Records().Count();
        }

        var end = TraceFileSummary.Of(path).EndUtc;
        Append(path, 3, 10 * TimeSpan.TicksPerSecond);

        using (var reader = new TraceReader(path))
        {
            Assert.Equal(first, reader.Records().Count());
        }

        Assert.Equal(end, ClientFile.Read(path).EndUtc);
        using var replayer = new Replayer(path);
        Assert.Equal(2, replayer.ReplayToEnd().PacketsReplayed);
    }

    [Fact]
    public void ASidecarThatGrowsIsReadToTheLengthItHadWhenFirstRead()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, TraceSidecar.FilePrefix + "120000" + TraceSidecar.Extension);
        File.WriteAllText(path, Line("2026-11-14T12:00:00.0000000Z", "one") + "\n");
        var from = new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(2);
        Assert.Single(SidecarReader.Read([path], from, to));

        File.AppendAllText(path, Line("2026-11-14T12:00:01.0000000Z", "two") + "\n");

        Assert.Single(SidecarReader.Read([path], from, to));
        Assert.Single(TraceSources.Sidecar([path], from, to));
    }

    [Fact]
    public void AHitIsScoredOnTheLifeHeldAfterItAndAStopOnTheLifeHeldBefore()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "20261114", "score");
        var trace = new HandTrace(SyntheticTrace.CharacterName, "score", new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc));
        trace.Join(1);
        trace.Received(0.5, SyntheticGame.AssignNpc(0xF1, Core.D2GS.Objects.NPCCode.Zombie, 5100, 1920));
        trace.Received(1.0, SyntheticGame.NpcHit(0xF1, 30));
        trace.Received(1.5, SyntheticGame.NpcState(0xF1, Core.D2GS.Objects.EntityState.Dieing, 5100, 1920, 40));
        trace.WriteTo(folder);
        var score = new RulesScore();

        ClientReplay.Run(GameTrace.Open(folder), SyntheticTrace.CharacterName, TimeSpan.MaxValue, [score])?.Replayer.Dispose();

        Assert.Equal((1L, 1L), (score.Life[RulesScore.HitRow].N, score.Life[RulesScore.HitRow].Agreeing));
        Assert.Equal((1L, 1L), (score.Life[RulesScore.DyingRow].N, score.Life[RulesScore.DyingRow].Agreeing));
        Assert.DoesNotContain(score.Life.Keys, k => k.Contains("other states", StringComparison.Ordinal));
    }

    [Fact]
    public void ABuildWithUncommittedChangesSaysSo()
    {
        var dirty = new TraceHeader { Build = "1.0.0+21548fc211dcfd77931b6dbb367937f1f36b8280-dirty", RulesVersion = 2, BuildUtc = new DateTime(2026, 11, 14, 14, 0, 0, DateTimeKind.Utc) };
        Assert.Equal("21548fc", BuildIdentity.Short(dirty.Build));
        Assert.Equal("build 21548fc (uncommitted changes) (2026-11-14 14:00 UTC), client rules 2", TraceHeaderIdentity.Describe(dirty));
        Assert.False(BuildIdentity.IsDirty("1.0.0+21548fc211dcfd77931b6dbb367937f1f36b8280"));
        Assert.Equal(System.Reflection.Assembly.GetEntryAssembly() is { Location.Length: > 0 } entry ? File.GetLastWriteTimeUtc(entry.Location) : BuildIdentity.Utc, BuildIdentity.Utc);
    }

    private static string Line(string stamp, string message) => $"{{\"t\":\"{stamp}\",\"k\":\"log\",\"lvl\":\"Information\",\"msg\":\"{message}\"}}";

    [Fact]
    public void EntriesStampedAlikeComeInFileThenLineOrderOnEveryPath()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        const string Stamp = "2026-11-14T12:00:00.0000000Z";
        var a = Path.Combine(root.Path, TraceSidecar.FilePrefix + "120000" + TraceSidecar.Extension);
        var b = Path.Combine(root.Path, TraceSidecar.FilePrefix + "120001" + TraceSidecar.Extension);
        foreach (var (file, name) in new[] { (a, "a"), (b, "b") })
        {
            var text = new StringBuilder();
            for (var i = 0; i < 40; i++)
            {
                text.Append(Line(Stamp, $"{name}{i:D2}")).Append('\n');
            }

            File.WriteAllText(file, text.ToString());
        }

        var from = new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Utc);
        var expected = Enumerable.Range(0, 40).Select(i => $"a{i:D2}").Concat(Enumerable.Range(0, 40).Select(i => $"b{i:D2}")).ToList();
        Assert.Equal(expected, SidecarReader.Read([b, a], from, from.AddHours(2)).Select(e => e.Message));
        Assert.Equal(expected, TraceSources.Sidecar([b, a], from, from.AddHours(2)).Select(e => e.Message));
    }
}
