using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.Tracing;
using System;

namespace D2NG.Trace.Query;

/// <summary>
/// The client kept walking or running for a long time with no word from the server about its position,
/// and the server then put it far from where it believed itself: everything it decided by its position
/// in between - the area, town or not, distances to monsters - was decided on a position it did not have.
/// </summary>
public sealed class PositionUnconfirmedCheck : IncidentCheck, IFamily
{
    public const string Kind = "position unconfirmed";

    private ReplayContext _context;
    private PositionConfirmation _confirmation;
    private TimeSpan _confirmedAt;
    private TimeSpan? _firstMove;
    private TimeSpan _lastMove;
    private int _moves;
    private (Area? Resolved, Area? LastKnown) _areaAtFirstMove;
    private Area? _gameAreaBefore;
    private Area? _resolvedBefore;
    private Area? _lastKnownBefore;
    private Area? _serverAreaBefore;
    private TimeSpan _lastWarp = TimeSpan.MinValue;

    public string Family => "state";

    public override void Before(ReplayContext context, TraceRecord record)
    {
        if (SelfAccuracy.IsWarp(context, record))
        {
            _lastWarp = context.Now;
        }

        var game = context.Game;
        if (record.Kind == TraceRecordKind.D2gsReceived && _firstMove is { } first && _lastMove - first > CheckThresholds.UnconfirmedMoving
            && record.Payload.Length > 0 && StatesPosition(record.Payload[0]) && game.Me != null)
        {
            _gameAreaBefore = game.Area;
            _resolvedBefore = game.ResolvedArea;
            _lastKnownBefore = game.LastResolvedArea;
            _serverAreaBefore = game.ServerConfirmedArea;
        }
    }

    /// <summary>
    /// The received packets whose handlers can state the client's position (a new Game.PositionConfirmation):
    /// reassign, walk verify, own move and stop, and the life updates. Only before one of these is the area worth reading.
    /// </summary>
    private static bool StatesPosition(byte type) => type is 0x15 or 0x96 or 0x0F or 0x0D or 0x18 or 0x95;

    public override void After(ReplayContext context, TraceRecord record)
    {
        if (!ReferenceEquals(context, _context))
        {
            _context = context;
            _confirmation = null;
            _firstMove = null;
            _moves = 0;
            _lastWarp = TimeSpan.MinValue;
        }

        var game = context.Game;
        if (record.Kind == TraceRecordKind.D2gsSent && record.Payload.Length > 0 && SelfAccuracy.IsWalkOrRun(record.Payload[0]) && game.Me != null)
        {
            if (_firstMove == null)
            {
                _firstMove = context.Now;
                _areaAtFirstMove = (game.ResolvedArea, game.LastResolvedArea);
            }

            _lastMove = context.Now;
            _moves++;
            return;
        }

        if (game.PositionConfirmation is not { } confirmation || ReferenceEquals(confirmation, _confirmation))
        {
            return;
        }

        var previous = _confirmation;
        var confirmedAt = _confirmedAt;
        _confirmation = confirmation;
        _confirmedAt = context.Now;
        var firstMove = _firstMove;
        var moves = _moves;
        _firstMove = null;
        _moves = 0;
        if (previous == null || firstMove is not { } first || _lastMove - first <= CheckThresholds.UnconfirmedMoving
            || confirmation.Believed is not { } believed || (believed.X == 0 && believed.Y == 0))
        {
            return;
        }

        var distance = believed.Distance(confirmation.Location);
        if (distance <= CheckThresholds.UnconfirmedCorrection || (_lastWarp != TimeSpan.MinValue && context.Now - _lastWarp <= SelfAccuracy.WarpExplains))
        {
            return;
        }

        Report(context, first, Kind,
            $"{moves} walk/run packets over {(_lastMove - first).TotalSeconds:0.0}s with no position from the server since {TraceTime.Format(confirmedAt)} ({Names.Point(previous.Location)}); "
            + $"then {Names.Incoming((byte)confirmation.Packet)} put it at {Names.Point(confirmation.Location)}, {distance:0} from where it held itself ({Names.Point(believed)}), "
            + $"{(context.Now - confirmedAt).TotalSeconds:0.0}s after the last position; area when it set off: {AreaTracker.Describe(_areaAtFirstMove.Resolved, _areaAtFirstMove.LastKnown)}, just before the correction: believed position in {AreaTracker.Describe(_resolvedBefore, _lastKnownBefore)}, server position in {(_serverAreaBefore is { } server ? Names.Area(server) : "unknown")}, Game.Area {(_gameAreaBefore is { } area ? Names.Area(area) : "-")}",
            context.Now);
    }
}
