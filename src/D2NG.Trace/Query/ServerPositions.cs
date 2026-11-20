using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace D2NG.Trace.Query;

/// <summary>
/// Where the server said the client's own character stood, as the replayed game's own position confirmation records
/// it: the position in its life updates (0x95, 0x18), a reassign (0x15), a walk verify (0x96), a stop (0x0D) and the
/// current location of a move (0x0F) of the client's own player. The client's own position also moves on what it
/// sent; this one does not.
/// </summary>
public sealed class ServerPositions : ICachedObserver
{
    private readonly List<(TimeSpan At, Point Position)> _positions = [];

    public string CacheKey => "ServerPositions";

    public void Save(System.IO.BinaryWriter w)
    {
        w.WriteList(_positions, (x, p) => { x.Write(p.At); x.Write(p.Position); });
        w.WriteTimes(_moves);
    }

    public void Load(System.IO.BinaryReader r)
    {
        r.ReadList(_positions, x => (x.ReadTime(), x.ReadPoint()));
        r.ReadTimes(_moves);
    }

    public IReadOnlyList<(TimeSpan At, Point Position)> Positions => _positions;

    public void After(ReplayContext context, TraceRecord record) => Observe(context, record);

    private readonly List<TimeSpan> _moves = [];

    public void Observe(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsSent && p.Length > 0 && p[0] is >= 0x01 and <= 0x04 or 0x0C or 0x0F or 0x5F or 0x13 or 0x49)
        {
            _moves.Add(context.Now);
            return;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived || p.Length == 0)
        {
            return;
        }

        var confirmation = context.Game.PositionConfirmation;
        if (confirmation == null || ReferenceEquals(confirmation, _lastConfirmation))
        {
            return;
        }

        _lastConfirmation = confirmation;
        if (confirmation.Location is { } at && (at.X != 0 || at.Y != 0))
        {
            _positions.Add((context.Now, at));
        }
    }

    /// <summary>
    /// The game's own record of the server stating where its player stands, which the bot's desync checks read too, so
    /// this view and the client cannot disagree on what counts as a confirmation.
    /// </summary>
    private PositionConfirmation _lastConfirmation;

    /// <summary>The last position the server stated at or before <paramref name="t"/>, with when it did.</summary>
    public (TimeSpan At, Point Position)? At(TimeSpan t)
    {
        int low = 0, high = _positions.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_positions[middle].At <= t)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found < 0 ? null : _positions[found];
    }

    public const double Confirmed = 4;

    /// <summary>How long after a move event the server's answer is still taken as where the move ended.</summary>
    public static readonly TimeSpan Answer = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long before a move event a position the server stated still counts as the position then.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Where the server had the client settle after a moment: the last position it stated from
    /// <see cref="Before"/> before to <see cref="Answer"/> after, and before the client sent its next move;
    /// null when it stated none then - it states the position only now and then (life updates, stops,
    /// corrections). The client's own "arrived" is assumed after a delay, so the server may still be walking
    /// when the event is written; the latest answer before the next move is where that walk ended.
    /// </summary>
    public (TimeSpan At, Point Position)? Near(TimeSpan t)
    {
        var nextMove = _moves.BinarySearch(t);
        var firstMoveAfter = nextMove >= 0 ? nextMove + 1 : ~nextMove;
        while (firstMoveAfter < _moves.Count && _moves[firstMoveAfter] <= t)
        {
            firstMoveAfter++;
        }

        var limit = firstMoveAfter < _moves.Count && _moves[firstMoveAfter] < t + Answer ? _moves[firstMoveAfter] : t + Answer;
        (TimeSpan At, Point Position)? latest = null;
        int low = 0, high = _positions.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_positions[middle].At < t - Before)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var i = low; i < _positions.Count && _positions[i].At <= limit; i++)
        {
            latest = _positions[i];
        }

        return latest;
    }

    /// <summary>
    /// For a typed move the bot reported as arrived: the server's position then (<see cref="Near"/>) when it is
    /// more than <see cref="Confirmed"/> subtiles from where the move was going, or null when it agrees or is unknown.
    /// </summary>
    public string Unconfirmed(SidecarEntry move, TimeSpan offset, double over = Confirmed)
    {
        if (move.Kind != "move" || move.Str("outcome") != "arrived" || To(move) is not { } to || Near(offset) is not { } server)
        {
            return null;
        }

        var distance = server.Position.Distance(to);
        return distance > over
            ? $"bot says arrived; server-confirmed position {Names.Point(server.Position)} at {TraceTime.Format(server.At)}, {distance:0} away from {Names.Point(to)}"
            : null;
    }

    /// <summary>
    /// How far off an unconfirmed move must be to be an incident: walks the bot reports as arrived commonly
    /// end 5-6 subtiles short on the server (the client assumes arrival after a delay), so only the clearly
    /// unexecuted ones are incidents; timeline marks every one over <see cref="Confirmed"/>.
    /// </summary>
    public const double IncidentDistance = 10;

    public static Point To(SidecarEntry move)
        => move.Json.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Array && to.GetArrayLength() == 2
            && to[0].TryGetInt32(out var x) && to[1].TryGetInt32(out var y) && x is >= 0 and <= ushort.MaxValue && y is >= 0 and <= ushort.MaxValue
                ? new Point((ushort)x, (ushort)y)
                : null;
}

/// <summary>A move the bot reported as arrived while the server, just after it, held the character well away from its destination.</summary>
public sealed class MoveNotConfirmedCheck : CompletedCheck
{
    public const string Kind = "move not confirmed";

    public override string Family => "state";

    public override IEnumerable<Incident> Run(CheckContext context)
    {
        foreach (var move in context.Query.GameEntries().Where(e => e.Kind == "move" && e.Client == context.Client))
        {
            var offset = context.Query.Offset(move);
            if (context.Activity.Server.Unconfirmed(move, offset, ServerPositions.IncidentDistance) is { } why)
            {
                yield return new Incident(offset, context.Client, Kind, $"move {move.Str("kind")} -> {Names.Point(ServerPositions.To(move))}: {why}");
            }
        }
    }
}
