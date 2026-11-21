using Serilog.Core;
using Serilog.Events;
using System;

namespace D2NG.Core.Tracing;

/// <summary>
/// Hands every log event to the trace recorder's sidecar, where the writer thread renders it. The
/// "unhandled D2GS packet" Debug line is left out: it comes once per unhandled packet, up to a third
/// of a Debug log, and the packet trace already holds those packets. An Error or Fatal wakes the writer rather than waiting for its next pass; the logging
/// thread never drains, and <see cref="Emit"/> never throws.
/// </summary>
public sealed class TraceLogSink(Func<TraceRecorder> recorder) : ILogEventSink
{
    public TraceLogSink() : this(() => TraceRecorder.Current)
    {
    }

    public void Emit(LogEvent logEvent)
    {
        TraceRecorder target = null;
        try
        {
            target = recorder();
            if (target == null || IsUnhandledPacketLine(logEvent))
            {
                return;
            }

            target.Sidecar.Enqueue(logEvent);
            if (logEvent.Level >= LogEventLevel.Error)
            {
                target.Wake();
            }
        }
        catch (Exception)
        {
            target?.Sidecar.CountFailure();
        }
    }

    internal static bool IsUnhandledPacketLine(LogEvent logEvent)
        => logEvent.Level <= LogEventLevel.Debug
            && logEvent.MessageTemplate.Text.Contains("unhandled D2GS packet of type: ", StringComparison.Ordinal);
}
