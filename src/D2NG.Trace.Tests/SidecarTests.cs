using D2NG.Core.D2GS;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace D2NG.Trace.Tests;

public class SidecarTests
{
    private static readonly int[] Belt = [1, 2];

    private static List<SidecarEntry> ReadBack(TraceRecorder recorder)
    {
        Assert.NotNull(recorder.Sidecar.Path);
        SidecarReader.Forget(recorder.Sidecar.Path);
        return SidecarReader.Read([recorder.Sidecar.Path], DateTime.MinValue.ToUniversalTime(), DateTime.MaxValue.ToUniversalTime());
    }

    [Fact]
    public void ALogEventReadsBackWithItsLevelMessagePropertiesAndException()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new TraceLogSink(() => recorder)).CreateLogger();

        logger.Warning("{Character} has no health potions at {Life} life, belt {Belt}", "client 1", 412, Belt);
        logger.Error(new InvalidOperationException("boom"), "Run of {Character} failed", "client 2");
        recorder.FlushNow();

        var entries = ReadBack(recorder);
        Assert.Equal(2, entries.Count);
        var warning = entries[0];
        Assert.Equal("log", warning.Kind);
        Assert.Equal("Warning", warning.Level);
        Assert.Equal("\"client 1\" has no health potions at 412 life, belt [1, 2]", warning.Message);
        Assert.Equal("{Character} has no health potions at {Life} life, belt {Belt}", warning.Str("tpl"));
        Assert.Equal("412", warning.Json.GetProperty("p").GetProperty("Life").GetRawText());
        Assert.Equal("[1,2]", warning.Json.GetProperty("p").GetProperty("Belt").GetRawText());
        Assert.Null(warning.Exception);
        Assert.Equal("Error", entries[1].Level);
        Assert.StartsWith("System.InvalidOperationException: boom", entries[1].Exception);
        Assert.True(Math.Abs((entries[0].Utc - DateTime.UtcNow).TotalMinutes) < 1);
    }

    [Fact]
    public void EveryTypedKindReadsBackWithItsFields()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var events = recorder.Events;

        events.Step("client 1", "game-1", "kill pack", StepPhase.Start);
        events.Step("client 1", "game-1", "kill pack", StepPhase.End, 2, StepOutcome.Timeout, "no monster left in sight", TimeSpan.FromMilliseconds(1500));
        events.Target("client 1", "game-1", 0xE9, "Zombie", "nearest", [new RejectedUnit(0xE5, "FoulCrow", "immune to cold")]);
        events.Wait("client 2", "game-1", "taxi portal", entered: true);
        events.Move("client 2", "game-1", new Point(5057, 1902), new Point(5100, 1950), "BloodMoor", MoveKind.PathRequest, MoveOutcome.NotNavigable, "walk");
        events.Decision("client 2", "game-1", "chicken", ("life", 412), ("maxLife", 1000), ("reason", "below 45%"));
        recorder.FlushNow();

        var entries = ReadBack(recorder);
        Assert.Equal(["step", "step", "target", "wait", "move", "decision"], entries.Select(e => e.Kind));
        Assert.All(entries, e => Assert.Equal("game-1", e.Game));

        var end = entries[1];
        Assert.Equal("client 1", end.Client);
        Assert.Equal("kill pack", end.Str("name"));
        Assert.Equal(2, end.Num("attempt"));
        Assert.Equal("end", end.Str("phase"));
        Assert.Equal("timeout", end.Str("outcome"));
        Assert.Equal("no monster left in sight", end.Str("reason"));
        Assert.Equal(1500, end.Num("ms"));
        Assert.Null(entries[0].Str("outcome"));

        var target = entries[2];
        Assert.Equal(0xE9, target.Num("id"));
        Assert.Equal("Zombie", target.Str("code"));
        var rejected = target.Json.GetProperty("rejected")[0];
        Assert.Equal(0xE5, rejected.GetProperty("id").GetInt64());
        Assert.Equal("immune to cold", rejected.GetProperty("filter").GetString());

        Assert.Equal("entered", entries[3].Str("state"));
        Assert.Equal("taxi portal", entries[3].Str("reason"));

        var move = entries[4];
        Assert.Equal("[5057,1902]", move.Json.GetProperty("from").GetRawText());
        Assert.Equal("path-request", move.Str("kind"));
        Assert.Equal("not-navigable", move.Str("outcome"));
        Assert.Equal("walk", move.Str("fallback"));
        Assert.Equal("BloodMoor", move.Str("area"));

        var decision = entries[5];
        Assert.Equal("chicken", decision.Str("name"));
        Assert.Equal("412", decision.Field("life"));
        Assert.Equal("below 45%", decision.Field("reason"));
    }

    [Fact]
    public void TargetAndWaitAreWrittenOnlyWhenTheyChange()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var events = recorder.Events;

        events.Wait("client 1", "game-1", "taxi portal", entered: false);
        events.Wait("client 1", "game-1", "taxi portal", entered: true);
        events.Wait("client 1", "game-1", "taxi portal", entered: true);
        events.Wait("client 1", "game-1", "taxi portal", entered: false);
        events.Target("client 1", "game-1", 0xE9, "Zombie", "nearest");
        events.Target("client 1", "game-1", 0xE9, "Zombie", "nearest");
        events.Target("client 2", "game-1", 0xE9, "Zombie", "nearest");
        events.Target("client 1", "game-1", 0xE9, "Zombie", "boss first");
        recorder.FlushNow();

        var entries = ReadBack(recorder);
        Assert.Equal(["entered", "left"], entries.Where(e => e.Kind == "wait").Select(e => e.Str("state")));
        Assert.Equal(3, entries.Count(e => e.Kind == "target"));
    }

    [Fact]
    public void AFailedStepAChickenAndAnErrorWakeTheWriterInsteadOfDrainingOnTheCallersThread()
    {
        using var root = new TempRoot();
        using var recorder = new TraceRecorder(new TraceRecorderOptions { Root = root.Path, FlushInterval = TimeSpan.FromHours(1) });
        using var logger = new LoggerConfiguration().WriteTo.Sink(new TraceLogSink(() => recorder)).CreateLogger();

        logger.Information("not an incident");
        Thread.Sleep(200);
        Assert.Null(recorder.Sidecar.Path);

        recorder.Events.Step("client 1", "game-1", "run", StepPhase.End, outcome: StepOutcome.Failed);
        Assert.True(SpinWait.SpinUntil(() => recorder.Sidecar.Path != null && ReadBack(recorder).Count == 2, TimeSpan.FromSeconds(5)));
        recorder.Events.Decision("client 1", "game-1", "chicken", ("life", 1));
        Assert.True(SpinWait.SpinUntil(() => ReadBack(recorder).Count == 3, TimeSpan.FromSeconds(5)));

        var probe = new RenderProbe();
        new TraceLogSink(() => recorder).Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, new MessageTemplateParser().Parse("Bot crashed holding {Probe}"), [new LogEventProperty("Probe", new ScalarValue(probe))]));
        Assert.True(SpinWait.SpinUntil(() => ReadBack(recorder).Count == 4, TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, probe.RenderedOn);
    }

    [Fact]
    public void ATraceEventOrLogLineThatThrowsIsCountedInsteadOfThrown()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var sink = new TraceLogSink(() => recorder);

        recorder.Events.Target("client 1", "game-1", 1, "x", "nearest", new ThrowingList());
        sink.Emit(null);
        new TraceLogSink(() => throw new InvalidOperationException()).Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, new MessageTemplateParser().Parse("x"), []));
        recorder.FlushNow();

        Assert.Equal(2, ReadBack(recorder).Single(e => e.Kind == "trace-failed").Num("n"));
    }

    private sealed class ThrowingList : IReadOnlyList<RejectedUnit>
    {
        public RejectedUnit this[int index] => throw new InvalidOperationException();

        public int Count => 1;

        public IEnumerator<RejectedUnit> GetEnumerator() => throw new InvalidOperationException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void TheUnhandledPacketDebugLineIsLeftOut()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new TraceLogSink(() => recorder)).CreateLogger();

        logger.Debug($"Received unhandled D2GS packet of type: Recv24");
        logger.Debug("Teleport to {Point} failing", "(5, 5)");
        logger.Warning($"Instance 3 Received unknown D2GS packet of type: 0xFE with data FE");
        recorder.FlushNow();

        Assert.Equal(["Teleport to \"(5, 5)\" failing", "Instance 3 Received unknown D2GS packet of type: 0xFE with data FE"], ReadBack(recorder).Select(e => e.Message));
    }

    [Fact]
    public void RenderingHappensOnTheWriterThreadNotTheLoggingOne()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var sink = new TraceLogSink(() => recorder);
        var probe = new RenderProbe();
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("Holding {Probe}"), [new LogEventProperty("Probe", new ScalarValue(probe))]);

        sink.Emit(logEvent);
        Assert.Empty(probe.RenderedOn);

        var writer = new Thread(recorder.FlushNow);
        writer.Start();
        writer.Join();

        Assert.NotEmpty(probe.RenderedOn);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, probe.RenderedOn);
        Assert.Equal("Holding rendered", ReadBack(recorder).Single().Message);
    }

    [Fact]
    public void NothingIsWrittenWhileTracingIsOff()
    {
        var sink = new TraceLogSink(() => null);
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, new MessageTemplateParser().Parse("x"), []));
        TraceEvents.Step("client 1", "game-1", "run", StepPhase.End, outcome: StepOutcome.Failed);
        Assert.False(TraceEvents.Enabled && TraceRecorder.Current == null);
    }

    [Fact]
    public void AFullQueueDropsEventsAndSaysHowMany()
    {
        using var root = new TempRoot();
        using var recorder = new TraceRecorder(new TraceRecorderOptions { Root = root.Path, MaxPendingEvents = 3, BackgroundWriter = false });
        for (var i = 0; i < 5; i++)
        {
            recorder.Events.Decision("client 1", "game-1", "tick", ("i", i));
        }

        recorder.FlushNow();
        var entries = ReadBack(recorder);
        Assert.Equal(3, entries.Count(e => e.Kind == "decision"));
        Assert.Equal(2, entries.Single(e => e.Kind == "gap").Num("n"));
    }

    [Fact]
    public void TheSidecarSitsInTheDateFolderNamedForTheProcessStart()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        recorder.Events.Decision("client 1", "game-1", "tick");
        recorder.FlushNow();

        var file = new FileInfo(recorder.Sidecar.Path);
        Assert.Matches(@"^events-\d{6}\.jsonl$", file.Name);
        Assert.Matches(@"^\d{8}$", file.Directory.Name);
        Assert.Equal(Path.GetFullPath(root.Path), file.Directory.Parent.FullName);
    }

    private sealed class RenderProbe
    {
        public List<int> RenderedOn { get; } = [];

        public override string ToString()
        {
            lock (RenderedOn)
            {
                RenderedOn.Add(Environment.CurrentManagedThreadId);
            }

            return "rendered";
        }
    }
}

public class AttributionTests
{
    private static SidecarEntry Log(string message, params (string Key, string Value)[] properties)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { t = "x", k = "log", msg = message, p = properties.ToDictionary(p => p.Key, p => p.Value) });
        return new SidecarEntry { Kind = "log", Message = message, Process = "201950", Json = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone() };
    }

    [Fact]
    public void ALineBelongsToTheClientsItNames()
    {
        var attribution = new Attribution(["client1", "client2"]);
        Assert.Equal(["client2"], attribution.ClientsOf(Log("Leaving game with client2")));
        Assert.Equal(["client1", "client2"], attribution.ClientsOf(Log("client1 is waiting for CLIENT2")));
        Assert.Equal(["client1"], attribution.ClientsOf(Log("Run pickups: nothing", ("Character", "client1"))));
    }

    [Fact]
    public void ALineNamingNoClientOfTheGameBelongsToNone()
    {
        var attribution = new Attribution(["client1", "client2"]);
        Assert.Empty(attribution.ClientsOf(Log("Run pickups: nothing worth picking up")));
        Assert.Empty(attribution.ClientsOf(Log("client10 joined")));
        Assert.Empty(attribution.ClientsOf(Log("subclient1 joined")));
    }

    [Fact]
    public void AGameServerInstanceLineBelongsToTheCharacterThatInstanceLoggedOnWith()
    {
        var attribution = new Attribution(["client1", "client2"], new Dictionary<(string, int), string> { [("201950", 3)] = "client2" });
        Assert.Equal(["client2"], attribution.ClientsOf(Log("Instance 3 game server was silent for 9.1s and then resumed")));
        Assert.Empty(attribution.ClientsOf(Log("Instance 4 game server was silent for 9.1s and then resumed")));
    }
}
