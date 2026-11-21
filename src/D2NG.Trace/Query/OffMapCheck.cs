using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.Tracing;
using System;

namespace D2NG.Trace.Query;

/// <summary>
/// The client's own position left the map the server revealed to it while the last position the server stated
/// is still on it. The server reveals the rooms around a character's server-side position, so a believed
/// position off the revealed map is one the server does not share: the client has lost sync with it.
/// </summary>
public sealed class OffMapCheck : IncidentCheck, IFamily
{
    public const string Kind = "position off its map";

    private ReplayContext _context;
    private TimeSpan? _start;
    private Point _startBelieved;
    private Point _server;
    private Area _serverArea;
    private double _widest;
    private int _moves;

    public string Family => "state";

    public override void After(ReplayContext context, TraceRecord record)
    {
        if (!ReferenceEquals(context, _context))
        {
            _context = context;
            _start = null;
        }

        var game = context.Game;
        if (_start != null && record.Kind == TraceRecordKind.D2gsSent && record.Payload.Length > 0 && SelfAccuracy.IsWalkOrRun(record.Payload[0]))
        {
            _moves++;
        }

        var believed = game.Me?.Location;
        var off = context.InGame && believed != null && (believed.X != 0 || believed.Y != 0)
            && game.ResolvedArea == null && game.ServerConfirmedArea != null;
        if (off)
        {
            var server = game.ServerConfirmedLocation;
            if (_start == null)
            {
                _start = context.Now;
                _startBelieved = believed;
                _widest = 0;
                _moves = 0;
            }

            _server = server;
            _serverArea = game.ServerConfirmedArea.Value;
            _widest = Math.Max(_widest, believed.Distance(server));
            return;
        }

        if (_start != null)
        {
            Close(context, believed == null ? "the client lost its position" : game.ResolvedArea != null ? $"the client's position {Names.Point(believed)} is on its map again ({Names.Area(game.ResolvedArea.Value)})" : "the server position left the map too");
        }
    }

    public override void Finished(ReplayContext context)
    {
        if (_start != null)
        {
            Close(context, "the file ended");
        }
    }

    private void Close(ReplayContext context, string how)
    {
        var start = _start.Value;
        _start = null;
        if (context.Now - start < CheckThresholds.OffMap)
        {
            return;
        }

        Report(context, start, Kind,
            $"the client held itself at {Names.Point(_startBelieved)}, outside every tile revealed to it, for {(context.Now - start).TotalSeconds:0.0}s while the server's last position {Names.Point(_server)} was in {Names.Area(_serverArea)}; "
            + $"up to {_widest:0} apart, {_moves} walk/run packets sent meanwhile; ended when {how}",
            context.Now);
    }
}
