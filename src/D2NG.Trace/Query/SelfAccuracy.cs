using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>Why neither Game.IsInTownConfirmed() nor Game.IsOutOfTownConfirmed() answered.</summary>
public enum TownUnknownCause
{
    /// <summary>No position from the server yet since the client joined (this trace file began).</summary>
    Join,

    /// <summary>Game.ResolvedArea is an act load's town, held until a position is known.</summary>
    ActLoadHold,

    /// <summary>After an act load, the believed position on a tile, and no position from the server yet.</summary>
    ActLoadNoStatement,

    /// <summary>Game.ServerDisagreesOnTown.</summary>
    Dispute,

    /// <summary>The server's position is outside every tile revealed to the client.</summary>
    ServerOffMap,

    Other,
}

/// <summary>Seconds a condition held, in how many separate stretches, and the longest.</summary>
public sealed class Stretches
{
    private double _current;
    private bool _on;

    public double Seconds { get; private set; }

    public int Count { get; private set; }

    public double Longest { get; private set; }

    internal void Add(bool on, double seconds)
    {
        if (!on)
        {
            Break();
            return;
        }

        if (seconds <= 0)
        {
            return;
        }

        if (!_on)
        {
            Count++;
            _on = true;
        }

        _current += seconds;
        Seconds += seconds;
        Longest = Math.Max(Longest, _current);
    }

    internal void Break()
    {
        _current = 0;
        _on = false;
    }

    public static Stretches Merge(IEnumerable<Stretches> all)
    {
        var merged = new Stretches();
        foreach (var s in all)
        {
            merged.Seconds += s.Seconds;
            merged.Count += s.Count;
            merged.Longest = Math.Max(merged.Longest, s.Longest);
        }

        return merged;
    }
}

/// <summary>
/// How far a client's idea of itself is from the server's, over one client's replay: the time Game.Area
/// and Game.ResolvedArea disagree, the time it walked on with no word from the server about its
/// position, and how far it had drifted each time the server stated where it was.
/// </summary>
public sealed class SelfAccuracy : IReplayObserver
{
    public static readonly double[] UnconfirmedThresholds = [3, 5, 10];

    /// <summary>A position the server states this soon after a waypoint, a portal, stairs or an act load is where the warp put the character.</summary>
    public static readonly TimeSpan WarpExplains = TimeSpan.FromSeconds(3);

    /// <summary>A position the server states this soon after a Teleport cast is where the teleport put the character.</summary>
    public static readonly TimeSpan TeleportExplains = TimeSpan.FromSeconds(1);

    private ReplayContext _context;
    private PositionConfirmation _lastConfirmation;
    private bool _confirmedThisFile;
    private TimeSpan _fileFirst;
    private TimeSpan _lastAt;
    private TimeSpan _lastWarp = TimeSpan.MinValue;
    private TimeSpan _lastTeleport = TimeSpan.MinValue;
    private State? _last;
    private TimeSpan? _residualStart;
    private string _residualWhere;
    private readonly HashSet<long> _moving = [];
    private readonly HashSet<long>[] _movingUnconfirmed = [.. UnconfirmedThresholds.Select(_ => new HashSet<long>())];
    private readonly Stretches _inTownUnconfirmed = new();
    private readonly Stretches _notTownUnconfirmed = new();
    private readonly Stretches[] _unknown = [.. Enum.GetValues<TownUnknownCause>().Select(_ => new Stretches())];

    private readonly record struct State(bool BelievedUnknown, bool Residual, bool Different, bool TownWhileUnknown, bool TownWhileOutside,
        bool NotTownWhileInTown, bool ServerAreaDifferent, bool ServerDisagreesOnTown, bool InTownUnconfirmed, bool NotTownUnconfirmed, TownUnknownCause? Unknown);

    public string Client { get; private set; }

    /// <summary>The game's folder name, with #N when it holds several.</summary>
    public string Game { get; private set; }

    public string Class { get; private set; }

    /// <summary>Seconds in the game and placed.</summary>
    public double Seconds { get; private set; }

    /// <summary>Seconds Game.ResolvedArea (the believed position's) was unknown; Game.Area then names some area all the same.</summary>
    public double AreaUnknown { get; private set; }

    /// <summary>Seconds Game.KnownArea was unknown too: neither the believed nor the server-confirmed position resolved.</summary>
    public double Residual { get; private set; }

    /// <summary>Seconds Game.Area and Game.ResolvedArea both named an area and not the same one.</summary>
    public double AreaDifferent { get; private set; }

    /// <summary>Seconds Game.IsInTown() was true while the believed position's area was unknown.</summary>
    public double TownWhileUnknown { get; private set; }

    /// <summary>Seconds Game.IsInTown() was true while Game.IsOutOfTownConfirmed() was.</summary>
    public double TownWhileOutside { get; private set; }

    /// <summary>Seconds Game.IsInTown() was false while Game.IsInTownConfirmed() was true.</summary>
    public double NotTownWhileInTown { get; private set; }

    /// <summary>Seconds the believed and the server-confirmed positions both resolved, to different areas.</summary>
    public double ServerAreaDifferent { get; private set; }

    /// <summary>Seconds Game.ServerDisagreesOnTown was true.</summary>
    public double ServerDisagreesOnTown { get; private set; }

    /// <summary>Game.IsInTown() true while Game.IsInTownConfirmed() was false.</summary>
    public Stretches InTownUnconfirmed => _inTownUnconfirmed;

    /// <summary>Game.IsInTown() false while Game.IsOutOfTownConfirmed() was false too.</summary>
    public Stretches NotTownUnconfirmed => _notTownUnconfirmed;

    /// <summary>Per <see cref="TownUnknownCause"/>: neither town helper true.</summary>
    public IReadOnlyList<Stretches> TownUnknown => _unknown;

    /// <summary>Per trace file: from its first record to the first position the server stated; null when none came.</summary>
    public List<TimeSpan?> FirstStatementAfter { get; } = [];

    /// <summary>Stretches of at least half a second in which neither position resolved.</summary>
    public List<(TimeSpan Start, TimeSpan End, string Where)> ResidualEpisodes { get; } = [];

    /// <summary>Seconds in which the client sent a walk or run packet.</summary>
    public int MovingSeconds => _moving.Count;

    /// <summary>Per <see cref="UnconfirmedThresholds"/>: seconds with a walk or run sent while the server's last word on the position was older than that.</summary>
    public IReadOnlyList<int> MovingUnconfirmedSeconds => [.. _movingUnconfirmed.Select(s => s.Count)];

    /// <summary>At each position the server stated: the packet, the subtiles between it and where the client held itself just before, and whether a warp explains it (<see cref="WarpExplains"/>).</summary>
    public List<(InComingPacket Packet, double Distance, bool Warp)> Corrections { get; } = [];

    /// <summary>
    /// Sent 0x49 (take waypoint) or 0x13 (interact) with a waypoint, a portal or a level warp (stairs, a cave
    /// entrance), or received 0x03 (act load). Other interacts - NPCs, chests, doors, the stash - move nobody.
    /// </summary>
    public static bool IsWarp(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (p.Length == 0)
        {
            return false;
        }

        if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            return p[0] == 0x03;
        }

        if (record.Kind != TraceRecordKind.D2gsSent)
        {
            return false;
        }

        if (p[0] == 0x49)
        {
            return true;
        }

        if (p[0] != 0x13 || p.Length < 9)
        {
            return false;
        }

        var type = (EntityType)PacketDecoder.U32(p, 1);
        return type == EntityType.Doorway
            || (type == EntityType.Object && context.Game.WorldObjects.GetValueOrDefault((PacketDecoder.U32(p, 5), EntityType.Object)) is { } target && IsWarpObject(target.Code));
    }

    public static bool IsWarpObject(EntityCode code)
    {
        var name = code.ToString();
        return name.Contains("Portal", StringComparison.Ordinal) || name.StartsWith("Waypoint", StringComparison.Ordinal);
    }

    /// <summary>Sent a skill on a location (0x05, 0x08 left; 0x0C, 0x0F right) with Teleport on that hand.</summary>
    public static bool IsTeleport(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (record.Kind != TraceRecordKind.D2gsSent || p.Length == 0 || p[0] is not (0x05 or 0x08 or 0x0C or 0x0F))
        {
            return false;
        }

        var hand = p[0] <= 0x0A ? Hand.Left : Hand.Right;
        return context.Game.Me is { } me && me.ActiveSkills.TryGetValue(hand, out var skill) && skill == Skill.Teleport;
    }

    public void Before(ReplayContext context, TraceRecord record)
    {
        Enter(context);
        if (IsWarp(context, record))
        {
            _lastWarp = context.Now;
        }
        else if (IsTeleport(context, record))
        {
            _lastTeleport = context.Now;
        }
    }

    public void After(ReplayContext context, TraceRecord record)
    {
        Enter(context);
        Client ??= context.Client;
        Game ??= System.IO.Path.GetFileName(context.GameTrace.Id);
        var game = context.Game;
        var me = game.Me;
        if (me != null && Class == null)
        {
            Class = me.Class.ToString();
        }

        var now = context.Now;
        if (_last is { } last && now > _lastAt)
        {
            var seconds = (now - _lastAt).TotalSeconds;
            Seconds += seconds;
            AreaUnknown += last.BelievedUnknown ? seconds : 0;
            Residual += last.Residual ? seconds : 0;
            AreaDifferent += last.Different ? seconds : 0;
            TownWhileUnknown += last.TownWhileUnknown ? seconds : 0;
            TownWhileOutside += last.TownWhileOutside ? seconds : 0;
            NotTownWhileInTown += last.NotTownWhileInTown ? seconds : 0;
            ServerAreaDifferent += last.ServerAreaDifferent ? seconds : 0;
            ServerDisagreesOnTown += last.ServerDisagreesOnTown ? seconds : 0;
            _inTownUnconfirmed.Add(last.InTownUnconfirmed, seconds);
            _notTownUnconfirmed.Add(last.NotTownUnconfirmed, seconds);
            for (var i = 0; i < _unknown.Length; i++)
            {
                _unknown[i].Add(last.Unknown == (TownUnknownCause)i, seconds);
            }
        }

        if (game.PositionConfirmation is { } confirmation && !ReferenceEquals(confirmation, _lastConfirmation))
        {
            _lastConfirmation = confirmation;
            if (!_confirmedThisFile)
            {
                _confirmedThisFile = true;
                FirstStatementAfter[^1] = now - _fileFirst;
            }

            if (confirmation.Believed is { } believed && (believed.X != 0 || believed.Y != 0))
            {
                var warp = (_lastWarp != TimeSpan.MinValue && now - _lastWarp <= WarpExplains)
                    || (_lastTeleport != TimeSpan.MinValue && now - _lastTeleport <= TeleportExplains);
                Corrections.Add((confirmation.Packet, believed.Distance(confirmation.Location), warp));
            }
        }

        var placed = context.InGame && me?.Location is { } location && (location.X != 0 || location.Y != 0);
        if (placed && record.Kind == TraceRecordKind.D2gsSent && record.Payload.Length > 0 && IsWalkOrRun(record.Payload[0]))
        {
            var second = (long)Math.Floor(now.TotalSeconds);
            _moving.Add(second);
            var age = game.ServerConfirmedAge.TotalSeconds;
            for (var i = 0; i < UnconfirmedThresholds.Length; i++)
            {
                if (age > UnconfirmedThresholds[i])
                {
                    _movingUnconfirmed[i].Add(second);
                }
            }
        }

        _lastAt = now;
        if (!placed)
        {
            _last = null;
            BreakStretches();
            EndResidual(now);
            return;
        }

        var resolved = game.ResolvedArea;
        var server = game.ServerConfirmedArea;
        var inTown = game.IsInTown();
        var inTownConfirmed = game.IsInTownConfirmed();
        var outConfirmed = game.IsOutOfTownConfirmed();
        TownUnknownCause? unknown = inTownConfirmed || outConfirmed ? null
            : !_confirmedThisFile ? TownUnknownCause.Join
            : game.ResolvedAreaIsActLoad ? TownUnknownCause.ActLoadHold
            : game.PositionConfirmation == null ? TownUnknownCause.ActLoadNoStatement
            : game.ServerDisagreesOnTown ? TownUnknownCause.Dispute
            : server == null ? TownUnknownCause.ServerOffMap
            : TownUnknownCause.Other;
        var state = new State(
            resolved == null,
            game.KnownArea.Source == AreaSource.Unknown,
            resolved != null && resolved != game.Area,
            inTown && resolved == null,
            inTown && game.IsOutOfTownConfirmed(),
            !inTown && game.IsInTownConfirmed(),
            resolved != null && server != null && resolved != server,
            game.ServerDisagreesOnTown,
            inTown && !inTownConfirmed,
            !inTown && !outConfirmed,
            unknown);
        _last = state;
        if (state.Residual && _residualStart == null)
        {
            _residualStart = now;
            _residualWhere = $"at {Names.Point(me.Location)} in {game.Act}, last known {(game.LastResolvedArea is { } known ? Names.Area(known) : "-")}, server position {Names.Point(game.ServerConfirmedLocation)} {game.ServerConfirmedAge.TotalSeconds:0.0}s old";
        }
        else if (!state.Residual)
        {
            EndResidual(now);
        }
    }

    public void Finished(ReplayContext context) => EndResidual(context.Now);

    private void Enter(ReplayContext context)
    {
        if (ReferenceEquals(context, _context))
        {
            return;
        }

        _context = context;
        _last = null;
        _lastConfirmation = null;
        _confirmedThisFile = false;
        _fileFirst = context.Now;
        _lastWarp = TimeSpan.MinValue;
        _lastTeleport = TimeSpan.MinValue;
        FirstStatementAfter.Add(null);
        BreakStretches();
    }

    private void BreakStretches()
    {
        _inTownUnconfirmed.Break();
        _notTownUnconfirmed.Break();
        foreach (var unknown in _unknown)
        {
            unknown.Break();
        }
    }

    private void EndResidual(TimeSpan now)
    {
        if (_residualStart is { } start && now - start >= TimeSpan.FromSeconds(0.5))
        {
            ResidualEpisodes.Add((start, now, _residualWhere));
        }

        _residualStart = null;
    }

    public static bool IsWalkOrRun(byte type)
        => type is (byte)OutGoingPacket.Walk or (byte)OutGoingPacket.WalkToUnit or (byte)OutGoingPacket.Run or (byte)OutGoingPacket.RunToUnit;
}
