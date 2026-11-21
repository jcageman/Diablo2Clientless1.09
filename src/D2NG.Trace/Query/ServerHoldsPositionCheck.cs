using D2NG.Core.D2GS;
using D2NG.Core.Tracing;
using System;

namespace D2NG.Trace.Query;

/// <summary>
/// The server kept stating the same own position while the client kept sending walks and runs away from it:
/// each statement snapped the believed position back, so the client never stays off its map for long and is
/// never long without a statement, yet it does not move. Counted from the first walk or run away from the
/// held position, so standing there first and then setting off is no incident. The live counterpart is
/// Game.ServerHoldsPosition.
/// </summary>
public sealed class ServerHoldsPositionCheck : IncidentCheck, IFamily
{
    public const string Kind = "server holds position";

    private ReplayContext _context;
    private PositionConfirmation _confirmation;
    private Point _at;
    private TimeSpan? _firstFarMove;
    private TimeSpan _last;
    private int _statements;
    private int _moves;
    private int _farMoves;
    private double _widest;
    private bool _live;

    public string Family => "state";

    public override void After(ReplayContext context, TraceRecord record)
    {
        if (!ReferenceEquals(context, _context))
        {
            _context = context;
            _confirmation = null;
            _at = null;
            _firstFarMove = null;
        }

        var game = context.Game;
        var payload = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsReceived && payload.Length > 0 && payload[0] == 0x03)
        {
            Close(context, "an act load");
            return;
        }

        if (_at != null && record.Kind == TraceRecordKind.D2gsSent && payload.Length > 0 && SelfAccuracy.IsWalkOrRun(payload[0]))
        {
            if (payload[0] is 0x01 or 0x03 && payload.Length >= 5 && new Point(PacketDecoder.U16(payload, 1), PacketDecoder.U16(payload, 3)).Distance(_at) > CheckThresholds.HeldMoveTarget)
            {
                _farMoves++;
                _firstFarMove ??= context.Now;
            }

            if (_firstFarMove != null)
            {
                _moves++;
            }
        }

        if (game.PositionConfirmation is { } confirmation && !ReferenceEquals(confirmation, _confirmation))
        {
            _confirmation = confirmation;
            if (_at == null || confirmation.Location.Distance(_at) > CheckThresholds.HeldRadius)
            {
                Close(context, $"the server put it at {Names.Point(confirmation.Location)}");
                _at = confirmation.Location;
                _firstFarMove = null;
                _statements = 0;
                _moves = 0;
                _farMoves = 0;
                _widest = 0;
                _live = false;
            }

            if (_firstFarMove != null)
            {
                _statements++;
                _last = context.Now;
            }
        }

        if (_firstFarMove != null && game.Me?.Location is { } believed && (believed.X != 0 || believed.Y != 0))
        {
            _widest = Math.Max(_widest, believed.Distance(_at));
        }

        _live |= _firstFarMove != null && game.ServerHoldsPosition != null;
    }

    public override void Finished(ReplayContext context) => Close(context, "the file ended");

    private void Close(ReplayContext context, string how)
    {
        if (_at != null && _firstFarMove is { } first && _statements >= CheckThresholds.HeldStatements && _last - first >= CheckThresholds.HeldSpan
            && _widest >= CheckThresholds.HeldBelievedDistance)
        {
            Report(context, first, Kind,
                $"{_statements} statements at {Names.Point(_at)} (within {CheckThresholds.HeldRadius:0}) over {(_last - first).TotalSeconds:0.0}s from the first walk or run away from it; "
                + $"meanwhile the client sent {_moves} walk/run packets, {_farMoves} of them to a point over {CheckThresholds.HeldMoveTarget:0} away, and held itself up to {_widest:0} away; ended by {how}; "
                + $"Game.ServerHoldsPosition {(_live ? "was set" : "was never set")}",
                _last);
        }

        _at = null;
        _firstFarMove = null;
    }
}
