using D2NG.Core.D2GS.Act;
using D2NG.Core.Tracing;

namespace D2NG.Trace.Query;

/// <summary>
/// The area the queries take a client to be in: the replayed game's <see cref="D2NG.Core.Game.KnownArea"/> - the
/// area of the position the client believes it holds, else of the last position the server stated - which is
/// unknown where Game.Area would answer the act's town, and the last resolved area for display.
/// </summary>
public sealed class AreaTracker : IReplayObserver
{
    private ReplayContext _context;

    /// <summary>The last area a position or a load resolved to, in this file.</summary>
    public Area? LastKnown => _context?.Game.LastResolvedArea;

    public void After(ReplayContext context, TraceRecord record) => Observe(context, record);

    public void Observe(ReplayContext context, TraceRecord record) => _context = context;

    public Area? Current(ReplayContext context)
    {
        _context = context;
        return context.Game.KnownArea.Area;
    }

    /// <summary>"Travincal", or "unknown (last known: Travincal)" when no revealed tile holds the position.</summary>
    public static string Describe(Area? area, Area? lastKnown)
        => area is { } known ? Names.Area(known) : lastKnown is { } last ? $"unknown (last known: {Names.Area(last)})" : "unknown";

    public string Describe(ReplayContext context)
    {
        _context = context;
        var (area, source) = context.Game.KnownArea;
        return source == AreaSource.ServerConfirmed && area is { } server
            ? $"{Names.Area(server)} (server position; client believes {Names.Point(context.Position)}, outside its map)"
            : Describe(area, LastKnown);
    }
}
