using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace D2NG.Trace;

/// <summary>
/// Phase timings for the query tool, on only when D2NG_TRACE_PROFILE is set: each named phase sums the time
/// spent in it on every thread, so phases run in parallel add up to more than the wall clock.
/// </summary>
public static class Profile
{
    public const string EnvironmentVariable = "D2NG_TRACE_PROFILE";

    public static readonly bool Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable));

    private static readonly ConcurrentDictionary<string, Phase> Phases = new(StringComparer.Ordinal);

    private sealed class Phase
    {
        public long Ticks;
        public long Count;
    }

    public static Scope Time(string name) => Enabled ? new Scope(name, Stopwatch.GetTimestamp()) : default;

    public static void Add(string name, long ticks, long count = 1)
    {
        var phase = Phases.GetOrAdd(name, _ => new Phase());
        Interlocked.Add(ref phase.Ticks, ticks);
        Interlocked.Add(ref phase.Count, count);
    }

    public readonly struct Scope(string name, long start) : IDisposable
    {
        public void Dispose()
        {
            if (name != null)
            {
                Add(name, Stopwatch.GetTimestamp() - start);
            }
        }
    }

    public static void Report(TextWriter writer)
    {
        if (!Enabled)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        writer.WriteLine($"profile: wall {(DateTime.Now - process.StartTime).TotalMilliseconds:N0} ms, cpu {process.TotalProcessorTime.TotalMilliseconds:N0} ms, peak working set {process.PeakWorkingSet64 / 1048576.0:N0} MB, allocated {GC.GetTotalAllocatedBytes() / 1048576.0:N0} MB, GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, JIT {System.Runtime.JitInfo.GetCompiledMethodCount():N0} methods in {System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:N0} ms");
        foreach (var (name, phase) in Phases.OrderByDescending(p => p.Value.Ticks))
        {
            writer.WriteLine($"profile: {phase.Ticks * 1000.0 / Stopwatch.Frequency,9:N1} ms {phase.Count,10:N0}x  {name}");
        }
    }
}
