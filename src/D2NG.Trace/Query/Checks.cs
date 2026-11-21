using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>Something that went wrong; Source says what saw it: replay, sidecar or realm.</summary>
public sealed record Incident(TimeSpan Offset, string Client, string Kind, string Detail, string Source = "replay")
{
    /// <summary>For the incidents that span time - a stand-still, an unanswered stretch - when they end; otherwise null.</summary>
    public TimeSpan? End { get; init; }

    /// <summary>For incidents about a unit that several clients see alike: the unit, so their reports merge into one line.</summary>
    public string Subject { get; init; }

    /// <summary>With <see cref="Subject"/>: how many packets this client sent at it; the merged line sums them per client.</summary>
    public int? Count { get; init; }

    /// <summary>With <see cref="Subject"/>: what this client did, which the merged line lists per client instead of a packet count.</summary>
    public string PerClient { get; init; }
}

/// <summary>
/// A behaviour check over a client's replay: it watches the records go by and reports what went
/// wrong. New checks are a subclass and a line in <see cref="IncidentCheck.Defaults"/>.
/// </summary>
public abstract class IncidentCheck : ICachedObserver
{
    private readonly List<Incident> _found = [];

    /// <summary>The check and its settings; a check with settings adds them.</summary>
    public virtual string CacheKey => GetType().Name;

    /// <summary>What a check holds after a whole replay is what it found; a check that offers more saves that too.</summary>
    public virtual void Save(System.IO.BinaryWriter w) => w.WriteList(_found, (x, i) => x.Write(i));

    public virtual void Load(System.IO.BinaryReader r) => r.ReadList(_found, x => x.ReadIncident());

    public IReadOnlyList<Incident> Found => _found;

    public virtual void Before(ReplayContext context, TraceRecord record)
    {
    }

    public abstract void After(ReplayContext context, TraceRecord record);

    public virtual void Finished(ReplayContext context)
    {
    }

    protected void Report(ReplayContext context, TimeSpan offset, string kind, string detail, TimeSpan? end = null)
        => _found.Add(new Incident(offset, context.Client, kind, detail) { End = end });

    protected void AddFound(Incident incident) => _found.Add(incident);

    protected void ReplaceFound(int index, Incident incident) => _found[index] = incident;

    /// <summary>The checks that always run: deaths, life drops, silences and replay fidelity.</summary>
    public static List<IncidentCheck> Defaults() => [new DeathCheck(), new LifeDropCheck(), new SilenceCheck(), new ReplayFidelityCheck()];

    /// <summary>The replay checks of a family ("state", "bot" or "all"), on top of <see cref="Defaults"/>.</summary>
    public static List<IncidentCheck> Of(string family)
    {
        var checks = Defaults();
        IncidentCheck[] extra = [new StaleUnitCheck(), new PositionMismatchCheck(), new PositionUnconfirmedCheck(), new OffMapCheck(), new ServerHoldsPositionCheck(), new StuckCursorCheck(), new LifeAboveMaxCheck(), new UnansweredAttackCheck(), new AttackingWithoutHitsCheck(), new GoldDropCheck(), new PotionWasteCheck(), new ItemStateCheck(), new LootLeftBehindCheck(), new BossNotAttackedCheck()];
        checks.AddRange(extra.Where(c => family == "all" || ((IFamily)c).Family == family));
        return checks;
    }
}

/// <summary>
/// A death is any of: the server's "was slain by" event message (0x5A type 6) naming the client's own
/// character, which every client of the game receives; life reaching 0 and then the gold on the person
/// set to 0 (the server drops it on death); the server assigning the client's own corpse (0x8E) after
/// damage. Signals of one death are one incident that says which fired. The lifetime experience falling
/// between one file's running total and the next file's join total corroborates a death found in the
/// previous file; alone it is an "experience fell" incident, since the running total can over-count.
/// Life reading 0 alone is not a death, and a corpse assigned with no damage taken just before is the
/// body of an earlier death, handed over on joining.
/// </summary>
public sealed class DeathCheck : IncidentCheck
{
    public static readonly TimeSpan DamageBeforeDeath = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SameDeath = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Summary = TimeSpan.FromSeconds(3);

    public const string SlainSignal = "the server's \"was slain by\" message";
    public const string GoldSignal = "life 0, then the gold on the person set to 0";
    public const string CorpseSignal = "corpse assigned";
    public const string ExperienceSignal = "lifetime experience fell on the next join";

    private const byte SlainEvent = 6;
    private const byte GoldOnPerson = 0x0E;

    private readonly AreaTracker _areas = new();
    private readonly List<LifeDrop> _drops = [];
    private Pending _pending;
    private TimeSpan? _lifeZeroAt;
    private long? _firstExperience;
    private long? _lastExperience;
    private long? _previousFileExperience;
    private int _lastDeathIndex = -1;
    private bool _deathInPreviousFile;
    private bool _deathInThisFile;

    private sealed class Pending
    {
        public TimeSpan At;
        public readonly List<string> Signals = [];
        public string Killer;
        public string Where;
        public string Area;
        public TimeSpan? LeftAt;
    }

    /// <summary>Every fall in life this client saw, with what was credited or inferred.</summary>
    public IReadOnlyList<LifeDrop> Drops => _drops;

    public override void Save(System.IO.BinaryWriter w)
    {
        base.Save(w);
        w.WriteList(_drops, (x, d) =>
        {
            x.Write(d.At);
            x.Write(d.Before);
            x.Write(d.After);
            x.Write(d.MaxLife);
            x.WriteText(d.Basis);
            x.WriteList(d.Credited, (y, c) => { y.WriteText(c.Name); y.WriteText(c.How); y.Write(c.At); });
            x.WriteList(d.Inferred, (y, text) => y.WriteText(text));
            x.WriteList(d.Curses ?? [], (y, text) => y.WriteText(text));
        });
    }

    public override void Load(System.IO.BinaryReader r)
    {
        base.Load(r);
        r.ReadList(_drops, x =>
        {
            var (at, before, after, maxLife, basis) = (x.ReadTime(), x.ReadInt32(), x.ReadInt32(), x.ReadInt32(), x.ReadText());
            var credited = new List<(string, string, TimeSpan)>();
            x.ReadList(credited, y => (y.ReadText(), y.ReadText(), y.ReadTime()));
            var inferred = new List<string>();
            x.ReadList(inferred, y => y.ReadText());
            var curses = new List<string>();
            x.ReadList(curses, y => y.ReadText());
            return new LifeDrop(at, before, after, maxLife, basis, credited, inferred, curses);
        });
    }

    public override void After(ReplayContext context, TraceRecord record)
    {
        _areas.Observe(context, record);
        if (_pending != null && context.Now - _pending.At > SameDeath)
        {
            Close(context);
        }

        var payload = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsSent && payload.Length > 0 && payload[0] == (byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame && _pending != null)
        {
            _pending.LeftAt ??= context.Now;
            return;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived || payload.Length == 0 || context.Game.Me == null)
        {
            return;
        }

        switch (payload[0])
        {
            case 0x18 or 0x95:
                if (context.Life < context.PreviousLife)
                {
                    _drops.Add(LifeDrop.Of(context));
                }

                if (context.Life == 0 && context.PreviousLife > 0)
                {
                    _lifeZeroAt = context.Now;
                }

                break;
            case 0x1D or 0x1E or 0x1F when payload.Length >= 3 && payload[1] == GoldOnPerson && AttributeValue(payload) == 0
                && _lifeZeroAt is { } zero && context.Now - zero <= TimeSpan.FromSeconds(2):
                Signal(context, GoldSignal, zero);
                break;
            case 0x5A when SlainSelf(context, payload):
                Signal(context, SlainSignal, context.Now, Killer(context, payload));
                break;
            case 0x8E when payload.Length >= 10 && payload[1] != 0 && context.SelfId != null && PacketDecoder.U32(payload, 2) == context.SelfId
                && context.SinceLifeLoss <= DamageBeforeDeath:
                Signal(context, CorpseSignal, context.Now);
                break;
            case 0x1A or 0x1B or 0x1C when _firstExperience == null:
                _firstExperience = context.Game.Me.Experience;
                if (_previousFileExperience is { } before && _firstExperience < before)
                {
                    ExperienceFell(context, before - _firstExperience.Value);
                }

                break;
        }

        if (payload[0] is 0x1A or 0x1B or 0x1C)
        {
            _lastExperience = context.Game.Me.Experience;
        }
    }

    public override void Finished(ReplayContext context)
    {
        if (_pending != null)
        {
            Close(context);
        }

        _previousFileExperience = _lastExperience ?? _previousFileExperience;
        _firstExperience = null;
        _lastExperience = null;
        _lifeZeroAt = null;
        _deathInPreviousFile = _deathInThisFile;
        _deathInThisFile = false;
        _fileEnd = context.Now;
    }

    private TimeSpan _fileEnd;

    private void ExperienceFell(ReplayContext context, long lost)
    {
        var signal = $"{ExperienceSignal} ({lost:N0} lost)";
        if (_deathInPreviousFile && _lastDeathIndex >= 0)
        {
            var earlier = Found[_lastDeathIndex];
            ReplaceFound(_lastDeathIndex, earlier with { Detail = earlier.Detail.Replace("signals: ", $"signals: {signal}, ", StringComparison.Ordinal) });
            return;
        }

        AddFound(new Incident(_fileEnd, context.Client, ExperienceFellKind,
            $"the lifetime total on joining is {lost:N0} under the running total the previous file ended with ({TraceTime.Format(_fileEnd)}), and no packet of that file showed a death;"
            + " a death outside Normal costs experience, but the running total can also over-count (seen for a whole party at once, with no death message to any of them), so this alone is no death"));
    }

    public const string ExperienceFellKind = "experience fell";

    private void Signal(ReplayContext context, string signal, TimeSpan at, string killer = null)
    {
        if (_pending == null)
        {
            _pending = new Pending
            {
                At = at,
                Where = Names.Point(context.Position),
                Area = _areas.Describe(context),
            };
        }
        else if (at < _pending.At)
        {
            _pending.At = at;
        }

        if (!_pending.Signals.Contains(signal))
        {
            _pending.Signals.Add(signal);
        }

        _pending.Killer ??= killer;
    }

    private string LastSeconds(ReplayContext context, TimeSpan at)
    {
        var window = _drops.Where(d => d.At >= at - Summary && d.At <= at + TimeSpan.FromSeconds(1)).ToList();
        if (window.Count == 0)
        {
            return $"no life lost in the {Summary.TotalSeconds:0}s before; attack packets in them: {context.Attackers(at - Summary, at)}";
        }

        var hit = window.MaxBy(d => d.Lost);
        return $"life {window[0].Before} -> {window[^1].After} in the {Summary.TotalSeconds:0}s before; the biggest hit {hit.Describe()}; attack packets in them: {context.Attackers(at - Summary, at)}";
    }

    private void Close(ReplayContext context)
    {
        var pending = _pending;
        _pending = null;
        var left = pending.LeftAt is { } leftAt ? $"; the bot left {(leftAt - pending.At).TotalMilliseconds:0} ms after dying" : "";
        Report(context, pending.At, "death",
            $"{(pending.Killer == null ? "" : $"slain by {pending.Killer} ")}at {pending.Where} in {pending.Area}; signals: {string.Join(", ", pending.Signals)}{left}; {LastSeconds(context, pending.At)}");
        _lastDeathIndex = Found.Count - 1;
        _deathInThisFile = true;
    }

    private static int AttributeValue(byte[] payload) => payload[0] switch
    {
        0x1D => payload[2],
        0x1E when payload.Length >= 4 => PacketDecoder.U16(payload, 2),
        0x1F when payload.Length >= 6 => (int)PacketDecoder.U32(payload, 2),
        _ => -1,
    };

    /// <summary>The server's "was slain by" message naming the client's own character.</summary>
    internal static bool SlainSelf(ReplayContext context, byte[] payload)
        => payload.Length >= 24 && payload[0] == 0x5A && payload[1] == SlainEvent && context.Game.Me != null && NameAt(payload, 8) is { } victim
            && string.Equals(victim, context.Game.Me.Name, StringComparison.OrdinalIgnoreCase);

    private static string NameAt(byte[] payload, int offset)
    {
        if (payload.Length < offset + 1)
        {
            return null;
        }

        var end = Array.IndexOf(payload, (byte)0, offset, Math.Min(16, payload.Length - offset));
        return System.Text.Encoding.ASCII.GetString(payload, offset, (end < 0 ? Math.Min(offset + 16, payload.Length) : end) - offset);
    }

    /// <summary>
    /// The killer of a "was slain by" message: byte 7 is the killer's unit type; a monster is named by
    /// the class id in the dword at 3, a player by the second name.
    /// </summary>
    public static string Killer(ReplayContext context, byte[] payload)
    {
        var classId = PacketDecoder.U32(payload, 3);
        return payload[7] switch
        {
            (byte)EntityType.Player => NameAt(payload, 24) is { Length: > 0 } player ? $"player {player}" : "a player",
            (byte)EntityType.NPC => $"{Names.Npc((NPCCode)classId)} (monster class {classId})",
            var type => $"a unit of type {type}, class {classId}",
        };
    }
}

/// <summary>
/// Life falling by more than a share of the maximum within a short window, with whoever attack packets
/// name. Reported once per episode - from the life before the first drop to the lowest point, the
/// episode ending when life has not fallen further for a window.
/// </summary>
public sealed class LifeDropCheck(double share = 0.3, double windowSeconds = 2) : IncidentCheck
{
    public override string CacheKey => FormattableString.Invariant($"{nameof(LifeDropCheck)} {share:R} {windowSeconds:R}");

    private readonly Queue<(TimeSpan At, int Life)> _recent = new();
    private readonly TimeSpan _window = TimeSpan.FromSeconds(windowSeconds);
    private readonly List<(TimeSpan At, string Text)> _inferred = [];
    private Episode _episode;

    private sealed class Episode
    {
        public TimeSpan PeakAt;
        public int Peak;
        public TimeSpan LowestAt;
        public int Lowest;
        public int MaxLife;
        public Point At;
    }

    public override void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0 || record.Payload[0] is not (0x18 or 0x95) || context.Game.Me == null)
        {
            return;
        }

        if (_episode != null && context.Now - _episode.LowestAt > _window)
        {
            Close(context);
        }

        _recent.Enqueue((context.Now, context.PreviousLife));
        _recent.Enqueue((context.Now, context.Life));
        while (_recent.Count > 0 && context.Now - _recent.Peek().At > _window)
        {
            _recent.Dequeue();
        }

        if (context.Life >= context.PreviousLife || context.MaxLife <= 0)
        {
            return;
        }

        if (!context.AttacksBetween(context.Now - DamageObserver.Window, context.Now).Any())
        {
            _inferred.AddRange(context.Inferred(context.Now).Select(i => (context.Now, i)));
            _inferred.RemoveAll(i => context.Now - i.At > TimeSpan.FromSeconds(10));
        }

        if (_episode != null)
        {
            if (context.Life < _episode.Lowest)
            {
                _episode.Lowest = context.Life;
                _episode.LowestAt = context.Now;
                _episode.At = context.Position;
            }

            return;
        }

        var (peakAt, peak) = _recent.MaxBy(r => r.Life);
        if (peak - context.Life >= share * context.MaxLife)
        {
            _episode = new Episode { PeakAt = peakAt, Peak = peak, LowestAt = context.Now, Lowest = context.Life, MaxLife = context.MaxLife, At = context.Position };
        }
    }

    public override void Finished(ReplayContext context)
    {
        if (_episode != null)
        {
            Close(context);
        }
    }

    private void Close(ReplayContext context)
    {
        var episode = _episode;
        _episode = null;
        var lost = episode.Peak - episode.Lowest;
        Report(context, episode.LowestAt, "life drop",
            $"lost {(double)lost / episode.MaxLife:0%} of max life ({episode.Peak} -> {episode.Lowest} of {episode.MaxLife}) in {(episode.LowestAt - episode.PeakAt).TotalSeconds:0.0}s at {Names.Point(episode.At)}; attacking: {context.Attackers(episode.PeakAt - TimeSpan.FromSeconds(1), episode.LowestAt)}{Likely(_inferred.Where(i => i.At >= episode.PeakAt && i.At <= episode.LowestAt).Select(i => i.Text))}");
    }

    /// <summary>The inferred causes of drops no attack packet explains, marked as inference; empty when there are none.</summary>
    public static string Likely(IEnumerable<string> inferred)
    {
        var causes = inferred.Distinct().ToList();
        return causes.Count == 0 ? "" : $"; INFERENCE likely: {string.Join("; ", causes)}";
    }
}

/// <summary>Nothing received from the game server for longer than the bot's own warning threshold while in the game.</summary>
public sealed class SilenceCheck(double seconds = 8) : IncidentCheck
{
    public override string CacheKey => FormattableString.Invariant($"{nameof(SilenceCheck)} {seconds:R}");

    private TimeSpan? _lastReceived;

    public override void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind != TraceRecordKind.D2gsReceived)
        {
            return;
        }

        if (context.InGame && _lastReceived is { } last && (context.Now - last).TotalSeconds > seconds)
        {
            Report(context, context.Now, "server silent", $"nothing received for {(context.Now - last).TotalSeconds:0.0}s, from {TraceTime.Format(last)} until this packet");
        }

        _lastReceived = context.Now;
    }

    /// <summary>A rejoin is a new connection: the silence before it is the time out of the game.</summary>
    public override void Finished(ReplayContext context) => _lastReceived = null;
}

/// <summary>Dropped records, and digests the replay could not reproduce although nothing was dropped before them.</summary>
public sealed class ReplayFidelityCheck : IncidentCheck
{
    public override void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind == TraceRecordKind.Gap)
        {
            Report(context, context.Now, "trace gap", $"{record.DroppedCount} records dropped on a full buffer; state after this is unreliable");
        }
    }

    public override void Finished(ReplayContext context)
    {
        var result = context.Replayer.Result;
        var fileStart = context.File.Header.StartUtc - context.GameTrace.StartUtc;
        foreach (var mismatch in result.FirstMismatches.GroupBy(m => m.Elapsed))
        {
            Report(context, fileStart + mismatch.Key, "digest mismatch",
                $"replay differs from the live state with no gap before it: {string.Join(", ", mismatch.Select(m => $"{m.Field} live {m.Expected} replay {m.Actual}"))}; after {string.Join(", ", mismatch.First().Preceding)}"
                + (result.MismatchedDigests > result.FirstMismatches.Select(m => m.Elapsed).Distinct().Count() ? $" ({result.MismatchedDigests} mismatched digests in all)" : ""));
        }

        if (result.HandlerFailures > 0)
        {
            var first = result.FirstHandlerFailures[0];
            Report(context, fileStart + first.Elapsed, "replay failure",
                $"{result.HandlerFailures} packets the replay could not apply, first {(first.Sent ? "sent" : "received")} 0x{first.PacketType:X2}: {first.Error}");
        }
    }
}
