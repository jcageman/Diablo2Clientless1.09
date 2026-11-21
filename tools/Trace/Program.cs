using D2NG.Core;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using D2NG.Trace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

// Reads the packet traces a bot writes, one .d2trace per client per game. Trace with no arguments prints the usage.

if (args.Length == 0)
{
    return Usage();
}

try
{
    ReplayRules.UseOption(args);
    TraceCache.Enabled = !args.Contains("--no-cache");
    return args[0] switch
    {
        "info" when args.Length >= 2 => Info(args[1]),
        "replay" when args.Length >= 2 => Replay(args[1], ValueOf("--datadir"), ValueOf("--to"), Enum.Parse<D2NG.Core.D2GS.Motion.MonsterMotion>(ValueOf("--motion") ?? "Legacy", ignoreCase: true)),
        "accuracy" => new TraceTool.Accuracy(args, Console.Out).Run(),
        "import-capture" => TraceTool.ImportCapture.Run(args),
        "compare" => TraceTool.Compare.Run(args),
        "bench" => Bench(ValueOf("--keep")),
        "synthetic" when args.Length >= 2 && ValueOf("--minutes") is { } minutes => Realistic(args[1], int.Parse(ValueOf("--clients") ?? "4", CultureInfo.InvariantCulture), double.Parse(minutes, CultureInfo.InvariantCulture)),
        "synthetic" when args.Length >= 2 => Synthetic(args[1]),
        "bench-queries" => TraceTool.Commands.BenchQueries(ValueOf("--keep"), int.Parse(ValueOf("--clients") ?? "4", CultureInfo.InvariantCulture), int.Parse(ValueOf("--packets") ?? "500000", CultureInfo.InvariantCulture)),
        var command when TraceTool.Commands.Names.Contains(command) => new TraceTool.Commands(args, Console.Out).Run(),
        _ => Usage(),
    };
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
finally
{
    TraceCache.Flush();
    Profile.Report(Console.Error);
}

static int Usage()
    => TraceTool.Commands.Usage(TraceTool.Accuracy.UsageLines + Environment.NewLine + """
               Trace info <file or folder>                                    records, gaps and duration of each trace file
               Trace replay <file, game folder or folder#N> [--to <seconds>] [--motion legacy|anchored|simulated]     replay fidelity: digests and handler failures
               every command that replays takes --rules <n|latest|recorded>: the client rules Game reads the packets under, by default those each trace recorded
               Trace import-capture <sniffer text log> --out <trace root>     turns a sniffer text capture (raw payloads on) into trace files
               Trace compare <trace A> <trace B> [--from T] [--to T] [--from-b T] [--to-b T] [--types 0x13,0x2F] [--sent-only] [--evidence-only] [--tolerance 10] [--shown 3]
                              where what two clients sent for the same interaction parts, and how the server answered each
               Trace synthetic <root> [--minutes 60 [--clients 4]]            writes a test game under <root>/<date>/ to try the commands on
               Trace bench [--keep <dir>]
               Trace bench-queries [--keep <dir>] [--clients 4] [--packets 500000]
        """);

string ValueOf(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int Synthetic(string root)
{
    Console.WriteLine(SyntheticGame.WriteScenario(root));
    return 0;
}

static int Realistic(string root, int clients, double minutes)
{
    var writing = Stopwatch.StartNew();
    var folder = SyntheticGame.WriteRealistic(root, clients, TimeSpan.FromMinutes(minutes));
    Console.WriteLine($"{folder}: {clients} clients over {minutes:0} minutes with a real game's packet mix, written in {writing.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

static int Info(string target)
{
    var files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*" + TraceFormat.Extension, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
        : [target];
    foreach (var file in files)
    {
        using var reader = OpenOrSkip(file, () => new TraceReader(file), skip: Directory.Exists(target));
        if (reader == null)
        {
            continue;
        }

        var counts = new SortedDictionary<TraceRecordKind, (long Records, long Bytes)>();
        var unknownReceived = new SortedDictionary<byte, long>();
        var unknownSent = new SortedDictionary<byte, long>();
        var unsizedSent = new SortedDictionary<byte, long>();
        var unknownActions = new SortedSet<byte>();
        var last = TimeSpan.Zero;
        foreach (var record in reader.Records())
        {
            counts.TryGetValue(record.Kind, out var count);
            counts[record.Kind] = (count.Records + 1, count.Bytes + record.Payload.Length);
            last = record.Elapsed;
            if (record.Kind == TraceRecordKind.D2gsSent && record.Payload.Length > 0 && D2NG.Core.Capture.D2gsClientStream.Size(record.Payload) == D2NG.Core.Capture.D2gsClientStream.Unknown)
            {
                unsizedSent[record.Payload[0]] = unsizedSent.GetValueOrDefault(record.Payload[0]) + 1;
            }

            if (record.Kind == TraceRecordKind.ClientAction && record.Payload.Length > 0 && !Enum.IsDefined((ClientAction)record.Payload[0]))
            {
                unknownActions.Add(record.Payload[0]);
            }

            if (record.Payload.Length > 0 && record.Kind == TraceRecordKind.D2gsReceived && !Enum.IsDefined(typeof(D2NG.Core.D2GS.Packet.InComingPacket), record.Payload[0]))
            {
                unknownReceived[record.Payload[0]] = unknownReceived.GetValueOrDefault(record.Payload[0]) + 1;
            }
            else if (record.Payload.Length > 0 && record.Kind == TraceRecordKind.D2gsSent && !Enum.IsDefined(typeof(D2NG.Core.D2GS.Packet.OutGoingPacket), record.Payload[0]))
            {
                unknownSent[record.Payload[0]] = unknownSent.GetValueOrDefault(record.Payload[0]) + 1;
            }
        }

        var header = reader.Header;
        Console.WriteLine(file);
        Console.WriteLine($"  source {header.Source}, client {header.ClientLabel}, game {(header.GameName.Length > 0 ? header.GameName : "-")}, format v{header.Version}");
        Console.WriteLine($"  started {header.StartUtc:yyyy-MM-dd HH:mm:ss.fff} UTC, lasts {last.TotalSeconds:F1}s, {new FileInfo(file).Length:N0} bytes");
        Console.WriteLine($"  recorded by {TraceHeaderIdentity.Describe(header)}; {TraceHeaderIdentity.InForce(header, file)}");
        foreach (var (kind, (records, bytes)) in counts)
        {
            Console.WriteLine($"  {Name(kind),-14} {records,10:N0} records {bytes,14:N0} payload bytes");
        }

        Console.WriteLine($"  gaps {reader.Gaps} ({reader.DroppedRecords:N0} records dropped){(reader.Truncated ? ", last record cut short" : "")}");
        if (unknownReceived.Count + unknownSent.Count > 0)
        {
            Console.WriteLine($"  ids the bot has no name for: received{Ids(unknownReceived)}, sent{Ids(unknownSent)}");
        }

        foreach (var action in unknownActions)
        {
            Console.WriteLine($"  {Replayer.UnknownClientActionNote(action)}");
        }

        if (header.Source != D2NG.Core.Tracing.TraceSource.Bot && unsizedSent.Count > 0)
        {
            Console.WriteLine($"  sent packets whose size the client table does not know, their length decided by the capture:{Ids(unsizedSent)}");
        }

        if (counts.TryGetValue(TraceRecordKind.SentWithoutEvidence, out var guessed))
        {
            Console.WriteLine($"  sent packets attributed to this client without evidence: {guessed.Records:N0} of {counts.GetValueOrDefault(TraceRecordKind.D2gsSent).Records:N0}");
        }
    }

    return 0;
}

static string Ids(IEnumerable<KeyValuePair<byte, long>> ids)
    => ids.Any() ? " " + string.Join(", ", ids.Select(i => $"0x{i.Key:X2} x{i.Value:N0}")) : " none";

static string Name(TraceRecordKind kind) => Enum.IsDefined(kind) ? kind.ToString() : $"kind {(byte)kind}";

static int Replay(string target, string dataDirectory, string to, D2NG.Core.D2GS.Motion.MonsterMotion motion)
{
    if (!D2NG.Trace.Query.TraceDataLocation.Use(dataDirectory, target))
    {
        Console.Error.WriteLine($"no item data found: item packets will not parse; pass --datadir or set {D2NG.Trace.Query.TraceDataLocation.EnvironmentVariable}");
    }

    var until = to == null ? TimeSpan.MaxValue : TimeSpan.FromSeconds(double.Parse(to, CultureInfo.InvariantCulture));
    var game = !Directory.Exists(target) && !File.Exists(target) && D2NG.Trace.Query.GameTrace.IsGameTarget(target) ? D2NG.Trace.Query.GameTrace.Open(target) : null;
    if (game == null && !Directory.Exists(target))
    {
        D2NG.Trace.Query.RulesNotes.OfFiles([target]).ForEach(note => Console.WriteLine($"note: {note}"));
        using var replayer = new Replayer(target);
        replayer.Game.MonsterMotion = motion;
        var elapsed = Timed(() => replayer.ReplayTo(until));
        PrintClient(replayer, elapsed);
        PrintDetails(replayer.Result);
        PrintState(replayer.Game);
        return 0;
    }

    string[] files = game != null
        ? [.. game.Files.Select(f => f.Path).OrderBy(f => f, StringComparer.Ordinal)]
        : Directory.GetFiles(target, "*" + TraceFormat.Extension).OrderBy(f => f, StringComparer.Ordinal).ToArray();
    D2NG.Trace.Query.RulesNotes.OfFiles(files).ForEach(note => Console.WriteLine($"note: {note}"));
    var totalElapsed = TimeSpan.Zero;
    var replayed = new List<(string Label, ReplayResult Result)>();
    foreach (var file in files)
    {
        using var replayer = OpenOrSkip(file, () => new Replayer(file), skip: true);
        if (replayer == null)
        {
            continue;
        }

        replayer.Game.MonsterMotion = motion;
        var elapsed = Timed(() => replayer.ReplayTo(until));
        PrintClient(replayer, elapsed, Path.GetFileNameWithoutExtension(file));
        replayed.Add((Path.GetFileNameWithoutExtension(file), replayer.Result));
        totalElapsed += elapsed;
    }

    var results = replayed.Select(r => r.Result).ToList();
    Console.WriteLine($"{"total",-24} {results.Sum(r => r.PacketsReplayed),10:N0} packets {totalElapsed.TotalMilliseconds,7:N0} ms  digests {results.Sum(r => r.Digests),5}  mismatched {results.Sum(r => r.MismatchedDigests),4} (+{results.Sum(r => r.MismatchedDigestsAfterGap)} after a gap)  handler failures {results.Sum(r => r.HandlerFailures)}");
    foreach (var (label, result) in replayed.Where(r => r.Result.FirstMismatches.Count + r.Result.FirstMismatchesAfterGap.Count + r.Result.FirstHandlerFailures.Count
        + r.Result.Notes.Count + r.Result.DerivedClientActions.Count + r.Result.UnknownReceived.Count + r.Result.UnknownSent.Count + r.Result.UnsizedSent.Count > 0))
    {
        Console.WriteLine();
        Console.WriteLine(label);
        PrintDetails(result);
    }

    return 0;
}

static T OpenOrSkip<T>(string file, Func<T> open, bool skip) where T : class
{
    try
    {
        return open();
    }
    catch (Exception e) when (skip && e is TraceFormatException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"skipped {file}: {e.Message}");
        return null;
    }
}

static TimeSpan Timed(Action action)
{
    var stopwatch = Stopwatch.StartNew();
    action();
    return stopwatch.Elapsed;
}

static void PrintClient(Replayer replayer, TimeSpan elapsed, string label = null)
{
    var result = replayer.Result;
    Console.WriteLine($"{label ?? replayer.Header.ClientLabel,-24} {result.PacketsReplayed,10:N0} packets {elapsed.TotalMilliseconds,7:N0} ms  digests {result.Digests,5}  mismatched {result.MismatchedDigests,4} (+{result.MismatchedDigestsAfterGap} after a gap)  handler failures {result.HandlerFailures}  to {result.ReachedElapsed.TotalSeconds:F1}s{(result.Gaps > 0 ? $"  gaps {result.Gaps} ({result.DroppedRecords:N0} dropped)" : "")}{(result.Truncated ? "  cut short" : "")}");
}

static void PrintDetails(ReplayResult result)
{
    foreach (var (title, mismatches) in new[] { ("mismatches with no gap before them, a replay fidelity problem:", result.FirstMismatches), ("mismatches after a gap, state was already unreliable:", result.FirstMismatchesAfterGap) })
    {
        if (mismatches.Count == 0)
        {
            continue;
        }

        Console.WriteLine($"  {title}");
        foreach (var mismatch in mismatches)
        {
            Console.WriteLine($"    {mismatch.Elapsed.TotalSeconds,9:F3}s {mismatch.Field}: live {mismatch.Expected}, replay {mismatch.Actual}  after {string.Join(", ", mismatch.Preceding)}");
        }
    }

    foreach (var failure in result.FirstHandlerFailures)
    {
        Console.WriteLine($"  handler failure {failure.Elapsed.TotalSeconds,9:F3}s {(failure.Sent ? "sent" : "recv")} 0x{failure.PacketType:X2}: {failure.Error}");
    }

    foreach (var note in result.Notes)
    {
        Console.WriteLine($"  note: {note}");
    }

    if (result.DerivedClientActions.Count > 0)
    {
        Console.WriteLine($"  client actions derived from sent packets: {string.Join(", ", result.DerivedClientActions.OrderBy(a => a.Key).Select(a => $"{a.Key} {a.Value:N0}"))}");
    }

    if (result.UnknownReceived.Count + result.UnknownSent.Count > 0)
    {
        Console.WriteLine($"  ids the bot has no name for: received{Ids(result.UnknownReceived.OrderBy(i => i.Key))}, sent{Ids(result.UnknownSent.OrderBy(i => i.Key))}");
    }

    if (result.UnhandledReceived.Count > 0)
    {
        Console.WriteLine($"  received ids no handler takes ({result.UnhandledReceived.Values.Sum():N0} packets):{Ids(result.UnhandledReceived.OrderByDescending(i => i.Value).Take(12))}{(result.UnhandledReceived.Count > 12 ? $" and {result.UnhandledReceived.Count - 12} more ids" : "")}");
    }
}

static void PrintState(Game game)
{
    var me = game.Me;
    if (me == null)
    {
        Console.WriteLine("  no player assigned yet");
        return;
    }

    var objects = game.WorldObjects.Values.GroupBy(o => o.Type).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine($"  {me.Name}: {game.Difficulty}, map seed 0x{game.MapId:X8}, {game.Act} {game.Area} at ({me.Location?.X}, {me.Location?.Y})");
    Console.WriteLine($"  life {me.Life}, mana {me.Mana}, experience {me.Experience:N0}, cursor item {(game.CursorItem == null ? "none" : $"{game.CursorItem.Id} {game.CursorItem.Name}")}");
    Console.WriteLine($"  monsters {objects.GetValueOrDefault(EntityType.NPC)}, objects {objects.GetValueOrDefault(EntityType.Object)}, other units {objects.Where(o => o.Key is not EntityType.NPC and not EntityType.Object).Sum(o => o.Value)}, players {game.Players.Count}, items {game.Items.Count}");
}

static int Bench(string keep)
{
    var root = Path.Combine(Path.GetTempPath(), "d2trace-bench-" + Guid.NewGuid().ToString("N"));
    var replayRoot = keep ?? root;
    try
    {
        const int packets = 1_000_000;
        HotPath(root, "default buffers", 1, packets, new TraceRecorderOptions { Root = root });
        HotPath(root, "buffers holding the whole run", 1, packets, new TraceRecorderOptions { Root = root, BufferBytes = 48 * 1024 * 1024 });
        HotPath(root, "8 clients at once, default buffers", 8, packets, new TraceRecorderOptions { Root = root });

        using var recorder = new TraceRecorder(new TraceRecorderOptions { Root = replayRoot, BufferBytes = 4 * 1024 * 1024, DigestInterval = TimeSpan.FromMilliseconds(2) });
        var recording = Stopwatch.StartNew();
        var file = SyntheticTrace.Record(recorder, "replay-bench", 500_000);
        recording.Stop();
        Console.WriteLine($"synthetic trace: 500,006 game server packets recorded through the live receive path in {recording.Elapsed.TotalMilliseconds:N0} ms, {new FileInfo(file).Length:N0} bytes");

        using var replayer = new Replayer(file);
        var replaying = Stopwatch.StartNew();
        var result = replayer.ReplayToEnd();
        replaying.Stop();
        Console.WriteLine($"replay: {result.PacketsReplayed:N0} packets in {replaying.Elapsed.TotalMilliseconds:N0} ms ({result.PacketsReplayed / replaying.Elapsed.TotalSeconds:N0} packets/s), digests {result.Digests}, mismatched {result.MismatchedDigests}, handler failures {result.HandlerFailures}");
        return 0;
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void HotPath(string root, string description, int clients, int packetsPerClient, TraceRecorderOptions options)
{
    using var recorder = new TraceRecorder(options);
    var channels = Enumerable.Range(1, clients).Select(i => recorder.OpenGame("hot-path-bench", $"client{i}")).ToArray();
    var payload = new byte[30];
    payload[0] = 0x6D;
    foreach (var channel in channels)
    {
        for (var i = 0; i < 10_000; i++)
        {
            channel.Write(TraceRecordKind.D2gsReceived, payload);
        }
    }

    recorder.FlushNow();
    var results = new (double Nanoseconds, long Allocated)[clients];
    using var start = new ManualResetEventSlim(false);
    var threads = channels.Select((channel, index) => new Thread(() =>
    {
        start.Wait();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < packetsPerClient; i++)
        {
            channel.Write(TraceRecordKind.D2gsReceived, payload);
        }

        stopwatch.Stop();
        results[index] = (stopwatch.Elapsed.TotalNanoseconds / packetsPerClient, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    })).ToArray();
    foreach (var thread in threads)
    {
        thread.Start();
    }

    start.Set();
    foreach (var thread in threads)
    {
        thread.Join();
    }

    recorder.FlushNow();
    var dropped = channels.Sum(c => c.DroppedRecords);
    Console.WriteLine($"hot path, {description}: {packetsPerClient:N0} x 30-byte packets per client, mean {results.Average(r => r.Nanoseconds):F1} ns/packet (worst client {results.Max(r => r.Nanoseconds):F1}), {results.Max(r => r.Allocated):N0} bytes allocated on the calling thread, {dropped:N0} of {(long)packetsPerClient * clients:N0} dropped while the writer ran");
}
