using D2NG.Core.Tracing;
using D2NG.Trace.Capture;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TraceTool;

/// <summary>
///   Trace import-capture &lt;sniffer text log&gt; --out &lt;trace root&gt;
/// Turns a text capture of the packet sniffer (raw payloads on) into trace files, source sniffer.
/// </summary>
internal static class ImportCapture
{
    public static int Run(string[] args)
    {
        var file = args.Length >= 2 ? args[1] : null;
        var outIndex = Array.IndexOf(args, "--out");
        var root = outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null;
        if (file == null || root == null)
        {
            Usage();
            return 1;
        }

        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"{file} does not exist");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = TextCaptureImport.Import(file, root, line => Console.WriteLine($"  {line}"));
        stopwatch.Stop();
        var stats = result.Stats;
        Console.WriteLine($"{file}: {result.Lines:N0} lines, {result.UsedLines:N0} used, imported in {stopwatch.Elapsed.TotalSeconds:F1}s");
        if (result.GameConnections == 0 && stats.Files.Count == 0)
        {
            Console.WriteLine("  nothing to import: no line carries a raw payload with a full timestamp. The sniffer's log.txt with raw=true is needed;");
            Console.WriteLine("  its console output ([HH:mm:ss INF] lines) holds only decoded fields, and no received game packets.");
            return 1;
        }
        foreach (var path in stats.Files)
        {
            Console.WriteLine($"  wrote {Path.GetRelativePath(root, path)} ({new FileInfo(path).Length:N0} bytes)");
        }

        Console.WriteLine($"  packets: {string.Join(", ", stats.Packets.OrderBy(p => p.Key).Select(p => $"{Name(p.Key)} {p.Value:N0}"))}");
        Console.WriteLine($"  game connections {result.GameConnections}: {stats.GameFiles} game files ({stats.MidGameFiles} joined mid-game, {stats.UnknownGameFiles} with no join seen)");
        Console.WriteLine($"  received segments: {result.SegmentsNamed:N0} named by the log, {result.SegmentsFoundInFrame:N0} found inside a later frame, {result.SegmentsGuessed:N0} guessed");
        Console.WriteLine($"  sent segments: {string.Join(", ", result.SentAttribution.OrderByDescending(m => m.Value).Select(m => $"{m.Value:N0} {m.Key}"))}; {result.SentInconsistent:N0} look wrong for the client chosen");
        if (result.Rebuilt.Count > 0)
        {
            Console.WriteLine($"  sent packets rebuilt from decoded lines: {string.Join(", ", result.Rebuilt.Select(r => $"{r.Key} {r.Value:N0}"))}");
        }

        if (result.UnusableSentLines > 0)
        {
            Console.WriteLine($"  sent lines without bytes, skipped: {result.UnusableSentLines:N0}");
        }

        Console.WriteLine($"  sends marked in the traces as given to their client without evidence: {stats.SentWithoutEvidence:N0}");
        Console.WriteLine($"  undecodable frames {stats.BadFrames:N0}{Ids(stats.UnsizedReceived)}, bytes skipped while resyncing {stats.SkippedBytes:N0}, gaps {stats.Gaps}, records whose time ran backwards {stats.ClampedRecords:N0}");
        if (stats.UnsizedSent.Count > 0)
        {
            Console.WriteLine($"  sent ids of unknown size: {string.Join(", ", stats.UnsizedSent.OrderByDescending(i => i.Value).Select(i => $"0x{i.Key.Id:X2} x{i.Value} {(i.Key.How == D2NG.Core.Capture.UnsizedCut.RestOfSegment ? "took the rest of the segment" : "cut before known packets")}"))}");
        }

        if (stats.FileFailures > 0)
        {
            Console.WriteLine($"  trace files that failed: {stats.FileFailures}");
        }

        return 0;
    }

    public static void Usage() => Console.Error.WriteLine("       Trace import-capture <sniffer text log> --out <trace root>");

    private static string Ids(System.Collections.Generic.Dictionary<byte, int> ids)
        => ids.Count == 0 ? "" : " (" + string.Join(", ", ids.OrderByDescending(i => i.Value).Select(i => $"0x{i.Key:X2} x{i.Value}")) + ")";

    private static string Name(TraceRecordKind kind) => kind.ToString();
}
