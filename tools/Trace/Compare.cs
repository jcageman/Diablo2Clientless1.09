using D2NG.Core;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TraceTool;

/// <summary>
///   Trace compare &lt;trace A&gt; &lt;trace B&gt; [--from T] [--to T] [--from-b T] [--to-b T] [--types 0x13,0x2F] [--sent-only]
///                 [--tolerance 10] [--shown 3] [--datadir dir]
/// Times are offsets from each file's start; --from/--to apply to both files unless --from-b/--to-b are given.
/// </summary>
internal static class Compare
{
    public static int Run(string[] args)
    {
        var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count != 2 || files.Any(f => !File.Exists(f)))
        {
            Usage();
            return 1;
        }

        if (Value(args, "--datadir") is { } dataDirectory)
        {
            GameDataLocation.Use(dataDirectory);
        }

        var spanA = Span(files[0]);
        var spanB = Span(files[1]);
        var options = new TraceCompareOptions
        {
            FromA = Time(args, "--from", spanA) ?? TimeSpan.Zero,
            ToA = Time(args, "--to", spanA) ?? TimeSpan.MaxValue,
            FromB = Time(args, "--from-b", spanB) ?? Time(args, "--from", spanB) ?? TimeSpan.Zero,
            ToB = Time(args, "--to-b", spanB) ?? Time(args, "--to", spanB) ?? TimeSpan.MaxValue,
            Types = (Value(args, "--types") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => byte.Parse(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToHashSet(),
            SentOnly = args.Contains("--sent-only"),
            EvidenceOnly = args.Contains("--evidence-only"),
            Tolerance = Value(args, "--tolerance") is { } tolerance ? double.Parse(tolerance, CultureInfo.InvariantCulture) : 10,
        };
        var shown = Value(args, "--shown") is { } count ? int.Parse(count, CultureInfo.InvariantCulture) : 3;

        var result = TraceCompare.Compare(files[0], files[1], options);
        Console.WriteLine($"A {result.LabelA}: {files[0]}  {Window(options.FromA, options.ToA)}, {result.StepsA} steps");
        Console.WriteLine($"B {result.LabelB}: {files[1]}  {Window(options.FromB, options.ToB)}, {result.StepsB} steps");
        foreach (var (side, file) in new[] { ("A", files[0]), ("B", files[1]) })
        {
            foreach (var note in FileNotes(file))
            {
                Console.WriteLine($"note: {side}: {note}");
            }
        }

        Console.WriteLine($"{result.Matched} steps line up; {result.Divergences.Count} places differ. Normalised away: pings, timing, unit and item ids (matched by code or name), runs of moves (one move, targets within {options.Tolerance} units){(options.SentOnly ? "" : "; steps both took are also compared on the answers that follow them")}.");
        Console.WriteLine($"  of the {result.Matched} matches, {result.MatchedByKind} paired different units or items of the same kind (NPC code, object code or item name) and {result.MatchedUnseen} paired units a side never saw (\"unseen\" matches any unseen unit of its type){(result.Windowed ? $"; aligned in windows of {TraceCompare.Window} steps, so a match needing one side to skip over {TraceCompare.Window / 2} steps is not found" : "")}");
        if (result.GuessedA + result.GuessedB > 0)
        {
            Console.WriteLine($"~ sent packet attributed to this client without evidence (imported text capture): A {result.GuessedA}, B {result.GuessedB} steps; --evidence-only leaves them out");
        }

        if (options.EvidenceOnly)
        {
            Console.WriteLine($"--evidence-only: left out {result.LeftOutA} sends of A and {result.LeftOutB} of B attributed without evidence");
        }
        if (result.QuestsAtStart.Count > 0)
        {
            Console.WriteLine($"character quest state at the start differs: {string.Join(", ", result.QuestsAtStart.Select(q => $"{q.Quest} A {q.A} B {q.B}"))}");
        }

        foreach (var (divergence, index) in result.Divergences.Take(shown).Select((d, i) => (d, i + 1)))
        {
            Console.WriteLine();
            Console.WriteLine($"#{index} {divergence.Kind}");
            foreach (var step in divergence.Before)
            {
                Console.WriteLine($"   both  {TraceTime.Format(step.Elapsed)}  {step.Text}");
            }

            Side("A", result.LabelA, divergence.OnlyA, divergence.AroundA);
            Side("B", result.LabelB, divergence.OnlyB, divergence.AroundB);
        }

        if (result.Divergences.Count > shown)
        {
            Console.WriteLine();
            Console.WriteLine($"{result.Divergences.Count - shown} more places differ; --shown N lists more, --from/--to narrow the windows");
        }

        return 0;
    }

    public static void Usage()
        => Console.Error.WriteLine("       Trace compare <trace A> <trace B> [--from T] [--to T] [--from-b T] [--to-b T] [--types 0x13,0x2F] [--sent-only] [--evidence-only] [--tolerance 10] [--shown 3]");

    /// <summary>A file not recorded by the bot, and what its replay had to assume.</summary>
    private static List<string> FileNotes(string file)
    {
        using var replayer = new D2NG.Trace.Replayer(file);
        return replayer.Header.Source == D2NG.Core.Tracing.TraceSource.Bot
            ? []
            : [$"source: {replayer.Header.Source.ToString().ToLowerInvariant()}, a capture of a real client", .. replayer.Result.Notes];
    }

    private static void Side(string side, string label, IReadOnlyList<CompareStep> only, IReadOnlyList<ComparePacket> around)
    {
        if (around.Count == 0 && only.Count == 0)
        {
            return;
        }

        Console.WriteLine($"   {side} {label}:");
        var lines = around.Count > 0 ? around : only.Select(s => new ComparePacket(s.Elapsed, true, s.Text)).ToList();
        foreach (var packet in lines)
        {
            Console.WriteLine($"     {TraceTime.Format(packet.Elapsed)} {(packet.Sent ? ">" : "<")} {packet.Text}");
        }
    }

    private static string Window(TimeSpan from, TimeSpan to)
        => $"{TraceTime.Format(from)} to {(to == TimeSpan.MaxValue ? "end" : TraceTime.Format(to))}";

    private static string Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>When a file starts and ends, which its times are read against.</summary>
    private static (DateTime Start, DateTime End) Span(string file)
    {
        using var reader = new D2NG.Trace.TraceReader(file);
        return (reader.Header.StartUtc, reader.Header.StartUtc + reader.LastElapsed());
    }

    /// <summary>An offset from the file's start or a clock time within it (see Trace's usage); a note says when a clock time was read.</summary>
    private static TimeSpan? Time(string[] args, string name, (DateTime Start, DateTime End) span)
    {
        if (Value(args, name) is not { } text)
        {
            return null;
        }

        var time = TraceTime.Resolve(text, span.Start, span.End, TimeZoneInfo.Local, out var note);
        if (note != null)
        {
            Console.WriteLine($"note: {name} {note}");
        }

        return time;
    }
}
