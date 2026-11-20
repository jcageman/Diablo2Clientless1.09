using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.MonsterData;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// A predictor of a monster's position, scored against every packet that carries where the monster
/// really is. A model sees each record after the game applied it and is asked for its prediction just
/// before the game applies the next truth packet.
/// </summary>
public interface IMotionModel
{
    string Name { get; }

    /// <summary>Where the model has the monster now; null when it does not know the monster.</summary>
    Point Predict(ReplayContext context, uint id, WorldObject current);

    void Apply(ReplayContext context, in MotionUpdate update, WorldObject current)
    {
    }

    void Removed(ReplayContext context, uint id)
    {
    }
}

/// <summary>Reads one of the positions the game itself keeps, so the score is of the real implementation.</summary>
public sealed class GameMotionModel(string name, Func<WorldObject, Point> position) : IMotionModel
{
    public string Name => name;

    public Point Predict(ReplayContext context, uint id, WorldObject current) => current == null ? null : position(current);

    public static GameMotionModel Legacy { get; } = new("legacy", o => o.LegacyLocation);

    public static GameMotionModel Anchored { get; } = new("anchored", o => o.LastKnownLocation);

    /// <summary>What Location returns in Simulated mode, so without speed data it is the legacy position.</summary>
    public static GameMotionModel Simulated { get; } = new("simulated", o => o.LocationIn(MonsterMotion.Simulated));
}

/// <summary>
/// The simulation with other rules, kept beside the game's own: a variant is a name, a
/// <see cref="MotionRules"/> and optionally a change to what the rules are told about the monster.
/// It calls the same <see cref="MotionRules"/> code as the game; only the snapshots, the clock reading
/// and the chase target lookup are its own.
/// </summary>
public sealed class RulesMotionModel(string name, MotionRules rules, Func<MotionUnit, MotionUnit> unit = null) : IMotionModel
{
    private readonly Dictionary<uint, (MotionSnapshot Snapshot, MotionUnit Unit)> _units = [];

    public string Name => name;

    public MotionRules Rules => rules;

    public Point Predict(ReplayContext context, uint id, WorldObject current)
        => _units.TryGetValue(id, out var state) ? MotionRules.ToPoint(PositionAt(context, state.Snapshot, state.Unit)) : null;

    public void Apply(ReplayContext context, in MotionUpdate update, WorldObject current)
    {
        var now = context.Replayer.Clock.GetTimestamp();
        if (update.Kind == MotionUpdateKind.Assign)
        {
            var motionUnit = current == null ? default : current.MotionUnit;
            motionUnit = unit?.Invoke(motionUnit) ?? motionUnit;
            _units[update.EntityId] = (rules.Next(null, update, now, default, motionUnit), motionUnit);
            return;
        }

        if (!_units.TryGetValue(update.EntityId, out var state))
        {
            return;
        }

        var next = rules.Next(state.Snapshot, update, now, PositionAt(context, state.Snapshot, state.Unit), state.Unit);
        if (next != null)
        {
            _units[update.EntityId] = (next, state.Unit);
        }
    }

    private (double X, double Y) PositionAt(ReplayContext context, MotionSnapshot snapshot, MotionUnit motionUnit)
    {
        var target = snapshot.Mode == MotionMode.MovingToUnit ? MotionAccuracy.TargetLocation(context, snapshot.TargetType, snapshot.TargetId) : null;
        var seconds = (context.Replayer.Clock.GetTimestamp() - snapshot.AnchorAt) / (double)TimeSpan.TicksPerSecond;
        return rules.PositionAt(snapshot, seconds, target, motionUnit);
    }
}

/// <summary>One truth packet seen by one client, with each model's error in subtiles.</summary>
public sealed record MotionObservation(
    string Game,
    string Client,
    TimeSpan Offset,
    uint Unit,
    NPCCode Class,
    string Packet,
    bool Moving,
    MotionMode ModeBefore,
    byte PathType,
    byte RunBonus,
    string Flags,
    string States,
    double SinceAnchor,
    double[] Errors,
    bool Friendly = false);

/// <summary>
/// One model prediction at a fixed time step inside a straight move whose two ends the server gave
/// exactly, against the position interpolated between them at their record times.
/// </summary>
public sealed record MotionSample(string Game, string Client, uint Unit, NPCCode Class, string Segment, bool Friendly, double SinceStart, double[] Errors);

/// <summary>A walk from an exact position to a 0x6D on the 0x67 destination: a direct speed measurement.</summary>
public sealed record SpeedSample(string Game, uint Unit, NPCCode Class, string Flags, byte RunBonus, byte PathType, Point Start, Point Destination, double Seconds, MotionUnit MotionUnit)
{
    public double Distance => Start.Distance(Destination);

    public double Measured => Distance / Seconds;
}

/// <summary>
/// Scores every model at every packet that carries a fresh exact position for a monster: 0x68, 0x6B,
/// 0x6C, 0x6D, 0x69 get-hit and dead, 0x15 and a re-assign after the monster left view. All models run
/// in the same replay.
/// </summary>
public sealed class MotionAccuracy : IReplayObserver
{
    /// <summary>
    /// The default rules run outside the game. It shares <see cref="MotionRules"/> with the game, so
    /// agreeing with it checks the game's snapshot storage, clock conversion and chase target lookup,
    /// not the rules.
    /// </summary>
    public const string ReplicaName = "rules-replica";

    /// <summary>A sampled move longer than this is not taken as one straight walk.</summary>
    public static readonly TimeSpan LongestSampledSegment = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<IMotionModel> _models;
    private readonly long _sampleTicks;
    private readonly Dictionary<uint, WorldObject> _gone = [];
    private readonly Dictionary<uint, UnitHistory> _history = [];
    private readonly Dictionary<uint, Segment> _segments = [];

    /// <param name="sample">Zero for no <see cref="Samples"/>; otherwise the step between them.</param>
    public MotionAccuracy(IReadOnlyList<IMotionModel> models, TimeSpan sample = default)
    {
        _models = models;
        _sampleTicks = sample.Ticks;
    }

    public IReadOnlyList<IMotionModel> Models => _models;

    /// <summary>One result from the per-client results of a parallel run; <paramref name="models"/> names the columns.</summary>
    public static MotionAccuracy Merge(IReadOnlyList<IMotionModel> models, IEnumerable<MotionAccuracy> parts)
    {
        var merged = new MotionAccuracy(models);
        foreach (var part in parts)
        {
            merged.Observations.AddRange(part.Observations);
            merged.SpeedSamples.AddRange(part.SpeedSamples);
            merged.Samples.AddRange(part.Samples);
            merged.Classes.UnionWith(part.Classes);
            merged.Disagreements += part.Disagreements;
            for (var i = 0; i < merged.Unplaced.Length && i < part.Unplaced.Length; i++)
            {
                merged.Unplaced[i] += part.Unplaced[i];
            }
        }

        return merged;
    }

    public List<MotionObservation> Observations { get; } = [];

    public List<SpeedSample> SpeedSamples { get; } = [];

    /// <summary>The time-step view; empty unless a sample step was given.</summary>
    public List<MotionSample> Samples { get; } = [];

    /// <summary>Every monster class assigned in the replayed games.</summary>
    public HashSet<NPCCode> Classes { get; } = [];

    /// <summary>Truth packets where the game's simulated position and <see cref="ReplicaName"/> differ, counted when both are among the models.</summary>
    public int Disagreements { get; private set; }

    /// <summary>
    /// Per model, truth packets where it held the monster at (0, 0) - a 0x67 of move type 20 carries no
    /// destination and legacy puts the monster on it - left out of its scores rather than scored as 7000 off.
    /// </summary>
    public int[] Unplaced => _unplaced ??= new int[_models.Count];

    private int[] _unplaced;

    public static IReadOnlyList<IMotionModel> StandardModels => [GameMotionModel.Legacy, GameMotionModel.Anchored, GameMotionModel.Simulated];

    /// <summary>
    /// The models "all" or a comma separated list names, null when one is unknown. Without monster speeds
    /// simulated reads legacy and every variant reads anchored, so only those two are kept.
    /// </summary>
    public static string[] ModelNames(string names, bool speeds)
    {
        var known = StandardModels.Concat(Variants()).Select(m => m.Name).ToList();
        var wanted = names == "all" ? [.. known] : names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0 || wanted.Any(w => !known.Contains(w)))
        {
            return null;
        }

        return speeds ? wanted : [.. wanted.Where(w => w is "legacy" or "anchored").DefaultIfEmpty("legacy").Distinct()];
    }

    /// <summary>The variants measured beside the game's own models. A new one is a line here.</summary>
    public static IReadOnlyList<IMotionModel> Variants() =>
    [
        new RulesMotionModel(ReplicaName, MotionRules.Default),
        new RulesMotionModel("sim-bin-velocity", new MotionRules { Speeds = MonsterSpeedTable.Shared.WithOverrides(null) }),
        new RulesMotionModel("sim-run-added", new MotionRules { RunBonus = RunBonusFormula.Added }),
        new RulesMotionModel("sim-melee-range", new MotionRules { ChaseStopsAtMeleeRange = true }),
        new RulesMotionModel("sim-stop-1", new MotionRules { ChaseStopDistance = 1 }),
        new RulesMotionModel("sim-stop-3", new MotionRules { ChaseStopDistance = 3 }),
        new RulesMotionModel("sim-no-extrafast", MotionRules.Default, u => u with { ExtraFast = false }),
        new RulesMotionModel("sim-extrafast-75", new MotionRules { ExtraFastBonus = 75 }),
        new RulesMotionModel("sim-extrafast-100", new MotionRules { ExtraFastBonus = 100 }),
    ];

    public static Point TargetLocation(ReplayContext context, EntityType type, uint id)
    {
        var game = context.Game;
        if (type == EntityType.NPC)
        {
            return context.Npc(id)?.LastKnownLocation;
        }

        if (game.Me is { } me && me.Id == id)
        {
            return me.Location;
        }

        return game.Players.FirstOrDefault(p => p.Id == id)?.Location;
    }

    public void Before(ReplayContext context, TraceRecord record)
    {
        if (_sampleTicks > 0 && _segments.Count > 0)
        {
            SampleUntil(context);
        }

        if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0)
        {
            return;
        }

        var payload = record.Payload;
        if (payload[0] == 0x0A && payload.Length >= 6 && payload[1] == (byte)EntityType.NPC)
        {
            var id = PacketDecoder.U32(payload, 2);
            if (context.Npc(id) is { } leaving)
            {
                _gone[id] = leaving;
            }

            _history.Remove(id);
            _segments.Remove(id);
            foreach (var model in _models)
            {
                model.Removed(context, id);
            }

            return;
        }

        if (payload[0] is 0x0C or 0x4C or 0x4D && payload.Length >= 6 && payload[1] == (byte)EntityType.NPC)
        {
            var id = PacketDecoder.U32(payload, 2);
            _segments.Remove(id);
            if (_history.TryGetValue(id, out var interrupted))
            {
                interrupted.LastKind = MotionUpdateKind.None;
            }
        }

        var update = Read(payload);
        if (update.Kind == MotionUpdateKind.None)
        {
            if (update.EntityId != 0)
            {
                _segments.Remove(update.EntityId);
            }

            return;
        }

        WorldObject unit;
        string packet;
        if (update.Kind == MotionUpdateKind.Assign)
        {
            if (!_gone.Remove(update.EntityId, out unit))
            {
                return;
            }

            packet = "reassign";
        }
        else
        {
            unit = context.Npc(update.EntityId);
            packet = update.Kind switch
            {
                MotionUpdateKind.Hit => "69 hit",
                MotionUpdateKind.Dead => "69 dead",
                MotionUpdateKind.Dying => "69 dying",
                _ => $"{update.Packet:X2}",
            };
            Track(context, update, unit);
            NoteSpeed(context, update, unit);
        }

        if (unit?.Motion is not { } motion || !update.HasExactPosition)
        {
            return;
        }

        var truth = update.Position;
        var errors = new double[_models.Count];
        var predictions = new Point[_models.Count];
        for (var i = 0; i < _models.Count; i++)
        {
            predictions[i] = _models[i].Predict(context, update.EntityId, unit);
            if (predictions[i] is { X: 0, Y: 0 })
            {
                Unplaced[i]++;
                predictions[i] = null;
            }

            errors[i] = predictions[i] == null ? double.NaN : predictions[i].Distance(truth);
        }

        var simulated = IndexOf(GameMotionModel.Simulated.Name);
        var replica = IndexOf(ReplicaName);
        if (simulated >= 0 && replica >= 0 && predictions[simulated] != predictions[replica])
        {
            Disagreements++;
        }

        var now = context.Replayer.Clock.GetTimestamp();
        Observations.Add(new MotionObservation(
            context.GameTrace.GameName,
            context.Client,
            context.Now,
            update.EntityId,
            unit.NPCCode,
            packet,
            motion.IsMoving,
            motion.Mode,
            motion.PathType,
            motion.RunBonus,
            Flags(unit),
            States(unit),
            (now - motion.AnchorAt) / (double)TimeSpan.TicksPerSecond,
            errors,
            IsFriendly(unit.NPCCode)));
    }

    public void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0)
        {
            return;
        }

        var update = Read(record.Payload);
        if (update.Kind == MotionUpdateKind.None)
        {
            return;
        }

        var unit = context.Npc(update.EntityId);
        if (update.Kind == MotionUpdateKind.Assign && unit != null)
        {
            Classes.Add(unit.NPCCode);
        }

        foreach (var model in _models)
        {
            model.Apply(context, update, unit);
        }
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < _models.Count; i++)
        {
            if (_models[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool IsFriendly(NPCCode code) => FriendlyUnits.IsFriendly(code, MonsterSpeedTable.Shared);

    /// <summary>
    /// A move whose both ends the server gave exactly, open until its closing packet decides whether it
    /// qualifies: a walk is an exact standing or acting position (0x6D, 0x6B, 0x6C), a 0x67, and a 0x6D on
    /// that 0x67's destination; a chase is a 0x68 and the next 0x68. Any other packet about the unit in
    /// between drops it.
    /// </summary>
    private sealed class Segment
    {
        public string Kind;
        public NPCCode Class;
        public bool Friendly;
        public Point Start;
        public long StartAt;
        public Point Destination;
        public long NextAt;
        public readonly List<(long At, Point[] Predicted)> Pending = [];
    }

    private void Track(ReplayContext context, in MotionUpdate update, WorldObject unit)
    {
        if (_sampleTicks <= 0)
        {
            return;
        }

        var now = context.Replayer.Clock.GetTimestamp();
        if (_segments.Remove(update.EntityId, out var open))
        {
            var closes = open.Destination != null
                ? update.Kind == MotionUpdateKind.Stop && update.Position == open.Destination
                : update.Kind == MotionUpdateKind.MoveToUnit;
            if (closes && now > open.StartAt && now - open.StartAt <= LongestSampledSegment.Ticks)
            {
                Emit(context, update.EntityId, open, update.Position, now);
            }
        }

        if (unit == null)
        {
            return;
        }

        Segment started = null;
        if (update.Kind == MotionUpdateKind.MoveToPoint
            && _history.TryGetValue(update.EntityId, out var history)
            && history.LastKind is MotionUpdateKind.Stop or MotionUpdateKind.Action or MotionUpdateKind.Attack
            && history.LastExact != null)
        {
            started = new Segment { Kind = $"walk, path {update.PathType}", Start = history.LastExact, Destination = update.Position };
        }
        else if (update.Kind == MotionUpdateKind.MoveToUnit)
        {
            started = new Segment { Kind = "chase", Start = update.Position };
        }

        if (started != null)
        {
            started.Class = unit.NPCCode;
            started.Friendly = IsFriendly(unit.NPCCode);
            started.StartAt = now;
            started.NextAt = now + _sampleTicks;
            _segments[update.EntityId] = started;
        }
    }

    /// <summary>
    /// Asks every model where each open move's unit is at the time steps before this record. Nothing has
    /// been applied since the previous record, so turning the replay clock back to a step reads the state
    /// the client really had then.
    /// </summary>
    private void SampleUntil(ReplayContext context)
    {
        var clock = context.Replayer.Clock;
        var now = clock.GetTimestamp();
        List<uint> expired = null;
        foreach (var (id, segment) in _segments)
        {
            if (now - segment.StartAt > LongestSampledSegment.Ticks)
            {
                (expired ??= []).Add(id);
                continue;
            }

            if (segment.NextAt >= now)
            {
                continue;
            }

            var unit = context.Npc(id);
            while (segment.NextAt < now)
            {
                clock.Elapsed = TimeSpan.FromTicks(segment.NextAt);
                var predicted = new Point[_models.Count];
                for (var i = 0; i < _models.Count; i++)
                {
                    predicted[i] = _models[i].Predict(context, id, unit);
                }

                segment.Pending.Add((segment.NextAt, predicted));
                segment.NextAt += _sampleTicks;
            }
        }

        clock.Elapsed = TimeSpan.FromTicks(now);
        foreach (var id in expired ?? [])
        {
            _segments.Remove(id);
        }
    }

    private void Emit(ReplayContext context, uint id, Segment segment, Point end, long endAt)
    {
        var span = (double)(endAt - segment.StartAt);
        foreach (var (at, predicted) in segment.Pending)
        {
            var f = (at - segment.StartAt) / span;
            var truth = (segment.Start.X + (end.X - segment.Start.X) * f, segment.Start.Y + (end.Y - segment.Start.Y) * f);
            var errors = new double[predicted.Length];
            for (var i = 0; i < predicted.Length; i++)
            {
                errors[i] = predicted[i] == null ? double.NaN : MotionRules.Distance((predicted[i].X, predicted[i].Y), truth);
            }

            Samples.Add(new MotionSample(context.GameTrace.GameName, context.Client, id, segment.Class, segment.Kind, segment.Friendly,
                (at - segment.StartAt) / (double)TimeSpan.TicksPerSecond, errors));
        }
    }

    private static MotionUpdate Read(byte[] payload)
    {
        try
        {
            return MotionUpdate.Read(payload);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or System.IO.EndOfStreamException)
        {
            return default;
        }
    }

    public void Finished(ReplayContext context)
    {
        _gone.Clear();
        _history.Clear();
        _segments.Clear();
    }

    private sealed class UnitHistory
    {
        public MotionUpdateKind LastKind;
        public Point LastExact;
        public Point WalkStart;
        public Point WalkDestination;
        public long WalkAt;
        public byte WalkRunBonus;
        public byte WalkPathType;
    }

    /// <summary>Exact position (0x6D, 0x6B, 0x6C), then 0x67, then 0x6D on its destination: the research's clean-walk filter.</summary>
    private void NoteSpeed(ReplayContext context, in MotionUpdate update, WorldObject unit)
    {
        if (unit == null)
        {
            return;
        }

        if (!_history.TryGetValue(update.EntityId, out var history))
        {
            history = new UnitHistory();
            _history[update.EntityId] = history;
        }

        var now = context.Replayer.Clock.GetTimestamp();
        if (update.Kind == MotionUpdateKind.MoveToPoint)
        {
            history.WalkStart = history.LastKind is MotionUpdateKind.Stop or MotionUpdateKind.Action or MotionUpdateKind.Attack ? history.LastExact : null;
            history.WalkDestination = update.Position;
            history.WalkAt = now;
            history.WalkRunBonus = update.RunBonus;
            history.WalkPathType = update.PathType;
        }
        else if (update.Kind == MotionUpdateKind.Stop && history.LastKind == MotionUpdateKind.MoveToPoint && history.WalkStart != null && update.Position == history.WalkDestination)
        {
            var distance = history.WalkStart.Distance(update.Position);
            var seconds = (now - history.WalkAt) / (double)TimeSpan.TicksPerSecond;
            if (distance >= 2 && seconds > 0)
            {
                SpeedSamples.Add(new SpeedSample(context.GameTrace.GameName, update.EntityId, unit.NPCCode, Flags(unit), history.WalkRunBonus, history.WalkPathType, history.WalkStart, update.Position, seconds, unit.MotionUnit));
            }
        }

        history.LastKind = update.Kind;
        if (update.HasExactPosition)
        {
            history.LastExact = update.Position;
        }
    }

    public static string Flags(WorldObject unit)
    {
        var parts = new List<string>();
        if (unit.IsChampion)
        {
            parts.Add("champion");
        }
        else if (unit.IsBoss)
        {
            parts.Add("named");
        }

        if (unit.IsBossMinion)
        {
            parts.Add("minion");
        }

        if (unit.MotionUnit.ExtraFast)
        {
            parts.Add("extra-fast");
        }

        return parts.Count == 0 ? "plain" : string.Join("+", parts);
    }

    /// <summary>The states on the unit that could change its speed, as far as the client was told.</summary>
    public static string States(WorldObject unit)
    {
        EntityEffect[] effects;
        try
        {
            effects = [.. unit.Effects];
        }
        catch (InvalidOperationException)
        {
            return "?";
        }

        var slowing = effects.Where(e => e is EntityEffect.Cold or EntityEffect.Freeze or EntityEffect.Slowed or EntityEffect.Slowmissiles
            or EntityEffect.Decrepify or EntityEffect.Stunned or EntityEffect.Conviction or EntityEffect.Convicted).Select(e => e.ToString()).OrderBy(e => e, StringComparer.Ordinal).ToList();
        return slowing.Count == 0 ? (effects.Length == 0 ? "none" : "other") : string.Join("+", slowing);
    }
}
