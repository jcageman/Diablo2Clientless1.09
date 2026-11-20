using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>Something that watches a client's replay: it sees each record before and after the game applied it.</summary>
public interface IReplayObserver
{
    void Before(ReplayContext context, TraceRecord record)
    {
    }

    void After(ReplayContext context, TraceRecord record);

    void Finished(ReplayContext context)
    {
    }
}

/// <summary>An attack packet that named this client's character as its target, or a skill aimed at a point close to it.</summary>
public sealed record AttackOnSelf(TimeSpan Offset, uint Attacker, EntityType AttackerType, string How, bool Named);

/// <summary>
/// The replay of one client in a game, file after file (a rejoin is a fresh game state), with what
/// every observer needs kept up to date: the offset from the game start, life and its maximum, the
/// units ever seen and the attacks aimed at this client.
/// </summary>
public sealed class ReplayContext
{
    private const double PointAttackReach = 5;
    private static readonly TimeSpan AttackMemory = TimeSpan.FromSeconds(10);

    private readonly Dictionary<uint, NPCCode> _npcCodes = [];
    private readonly Dictionary<uint, string> _playerNames = [];
    private readonly LinkedList<AttackOnSelf> _attacks = new();
    private bool _maxLifeDirty = true;
    private int _maxLife;

    internal ReplayContext(GameTrace game, ClientFile file, Replayer replayer)
    {
        GameTrace = game;
        File = file;
        Replayer = replayer;
        Client = file.Label;
    }

    public GameTrace GameTrace { get; }

    public ClientFile File { get; }

    public Replayer Replayer { get; }

    public string Client { get; }

    public Game Game => Replayer.Game;

    public TimeSpan Now { get; private set; }

    public bool InGame { get; private set; }

    public int Life { get; private set; }

    public int PreviousLife { get; private set; }

    /// <summary>When life last went down, or MinValue.</summary>
    public TimeSpan LastLifeLoss { get; private set; } = TimeSpan.MinValue;

    /// <summary>How long ago life last went down; MaxValue when it never has.</summary>
    public TimeSpan SinceLifeLoss => LastLifeLoss == TimeSpan.MinValue ? TimeSpan.MaxValue : Now - LastLifeLoss;

    public int MaxLife
    {
        get
        {
            if (_maxLifeDirty && Game.Me != null)
            {
                try
                {
                    _maxLife = Game.Me.MaxLife;
                }
                catch (Exception e) when (e is NullReferenceException or InvalidOperationException or KeyNotFoundException)
                {
                }

                _maxLifeDirty = false;
            }

            return _maxLife;
        }
    }

    public double LifeFraction => MaxLife > 0 ? (double)Life / MaxLife : 0;

    public Point Position => Game.Me?.Location;

    public uint? SelfId => Game.Me?.Id;

    public IEnumerable<AttackOnSelf> Attacks => _attacks;

    public TimeSpan Offset(TraceRecord record) => File.Header.StartUtc + record.Elapsed - GameTrace.StartUtc;

    private readonly Dictionary<uint, string> _unitNames = [];

    public string UnitName(uint id, EntityType type = EntityType.NPC)
    {
        if (type == EntityType.Player)
        {
            return _playerNames.TryGetValue(id, out var player) ? player : $"player {Names.Id(id)}";
        }

        if (!_unitNames.TryGetValue(id, out var name))
        {
            name = _npcCodes.TryGetValue(id, out var code) ? $"{Names.Npc(code)} {Names.Id(id)}" : $"unit {Names.Id(id)}";
            _unitNames[id] = name;
        }

        return name;
    }

    public NPCCode? CodeOf(uint id) => _npcCodes.TryGetValue(id, out var code) ? code : null;

    public WorldObject Npc(uint id)
    {
        try
        {
            return Game.WorldObjects.GetValueOrDefault((id, EntityType.NPC));
        }
        catch (Exception e) when (e is NullReferenceException)
        {
            return null;
        }
    }

    public double DistanceTo(Point point) => Position == null || point == null ? double.MaxValue : Position.Distance(point);

    public IEnumerable<AttackOnSelf> AttacksBetween(TimeSpan from, TimeSpan to) => _attacks.Where(a => a.Offset >= from && a.Offset <= to);

    internal void Before(TraceRecord record)
    {
        Now = Offset(record);
        PreviousLife = Life;
        if (record.Kind == TraceRecordKind.D2gsReceived && record.Payload.Length > 0)
        {
            switch (record.Payload[0])
            {
                case 0x4C or 0x4D or 0x6C or 0x68:
                    NoteAttack(record.Payload);
                    NoteCastNearby(record.Payload);
                    break;
                case 0x69 when record.Payload.Length >= 10 && record.Payload[5] is (byte)EntityState.Dieing or (byte)EntityState.Dead:
                    NoteDeath(record.Payload);
                    break;
                case 0x0C when record.Payload.Length >= 6 && record.Payload[1] == (byte)EntityType.NPC:
                    NoteHit(PacketDecoder.U32(record.Payload, 2));
                    break;
            }
        }
    }

    /// <summary>A unit's death or a hit on it that can hurt whoever stands close, or a monster skill aimed beside this client or at a party member next to it.</summary>
    public sealed record NearbyEvent(TimeSpan At, string Text, double Distance, string Kind);

    private const double DeathEffectReach = 8;
    private const double ChargedBoltReach = 15;
    private const double CastReach = 10;
    private const double PartyMemberReach = 6;
    private static readonly TimeSpan InferenceMemory = TimeSpan.FromSeconds(2);
    private readonly LinkedList<NearbyEvent> _nearby = new();

    private void Remember(NearbyEvent nearby)
    {
        _nearby.AddLast(nearby);
        while (_nearby.First != null && Now - _nearby.First.Value.At > InferenceMemory)
        {
            _nearby.RemoveFirst();
        }
    }

    private static readonly (MonsterEnchantment Enchantment, string What)[] DeathEffects =
    [
        (MonsterEnchantment.FireEnchanted, "death explosion"),
        (MonsterEnchantment.ColdEnchanted, "frost nova on death"),
    ];

    private void NoteDeath(byte[] payload)
    {
        var id = PacketDecoder.U32(payload, 1);
        var unit = Npc(id);
        if (unit?.MonsterEnchantments == null)
        {
            return;
        }

        var at = new Point(PacketDecoder.U16(payload, 6), PacketDecoder.U16(payload, 8));
        var where = at.X != 0 || at.Y != 0 ? at : unit.Location;
        foreach (var (enchantment, what) in DeathEffects.Where(e => unit.MonsterEnchantments.Contains(e.Enchantment)))
        {
            var distance = DistanceTo(where);
            if (distance <= DeathEffectReach)
            {
                Remember(new NearbyEvent(Now, $"{what} of {enchantment} {UnitName(id)}", distance, "died"));
            }
        }
    }

    private void NoteHit(uint id)
    {
        var unit = Npc(id);
        if (unit?.MonsterEnchantments == null || !unit.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
        {
            return;
        }

        var distance = DistanceTo(unit.Location);
        if (distance <= ChargedBoltReach)
        {
            Remember(new NearbyEvent(Now, $"charged bolts of LightningEnchanted {UnitName(id)}", distance, "was hit"));
        }
    }

    private void NoteCastNearby(byte[] payload)
    {
        var self = SelfId;
        if (self == null || payload.Length < 13 || payload[0] is not (0x4C or 0x4D) || payload[1] == (byte)EntityType.Player)
        {
            return;
        }

        var caster = PacketDecoder.U32(payload, 2);
        string Skill() => Names.SkillOf(PacketDecoder.U16(payload, 6), (EntityType)payload[1], CasterCode(payload));
        if (payload[0] == 0x4D)
        {
            var point = new Point(PacketDecoder.U16(payload, 9), PacketDecoder.U16(payload, 11));
            var distance = DistanceTo(point);
            if (distance > PointAttackReach && distance <= CastReach)
            {
                Remember(new NearbyEvent(Now, $"{Skill()} of {UnitName(caster, (EntityType)payload[1])} cast at {Names.Point(point)}", distance, "cast"));
            }

            return;
        }

        if (payload.Length < 16 || payload[9] != (byte)EntityType.Player || PacketDecoder.U32(payload, 10) == self)
        {
            return;
        }

        var target = PacketDecoder.U32(payload, 10);
        var member = Game.Players.FirstOrDefault(p => p.Id == target);
        var gap = DistanceTo(member?.Location);
        if (gap <= PartyMemberReach)
        {
            Remember(new NearbyEvent(Now, $"{Skill()} of {UnitName(caster, (EntityType)payload[1])} cast at {UnitName(target, EntityType.Player)} standing {gap:0} away", gap, "cast"));
        }
    }

    private static readonly EntityEffect[] TickingEffects = [EntityEffect.Poison, EntityEffect.Burning, EntityEffect.Openwounds];

    /// <summary>
    /// What may have caused a drop no attack packet explains: an enchanted unit that died or was hit close by,
    /// a monster skill aimed beside this client or at a party member next to it, or an effect that ticks damage
    /// on the client. Inference, not attribution.
    /// </summary>
    public List<string> Inferred(TimeSpan at, TimeSpan? window = null)
    {
        var since = at - (window ?? TimeSpan.FromSeconds(0.6));
        var found = _nearby.Where(n => n.At >= since && n.At <= at)
            .GroupBy(n => n.Text)
            .Select(g => g.First())
            .Select(n => $"{n.Text}, which {n.Kind} {(at - n.At).TotalSeconds:0.00}s before at distance {n.Distance:0}")
            .ToList();
        var ticking = Game.Me?.Effects.Keys.Where(TickingEffects.Contains).ToList() ?? [];
        if (ticking.Count > 0)
        {
            found.Add($"{string.Join(" and ", ticking)} on the client at the time");
        }

        return found;
    }

    internal void After(TraceRecord record)
    {
        if (record.Payload.Length == 0)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            var payload = record.Payload;
            switch (payload[0])
            {
                case 0x01:
                    InGame = true;
                    break;
                case 0x18 or 0x95 when Game.Me != null:
                    Life = Game.Me.Life;
                    if (Life < PreviousLife)
                    {
                        LastLifeLoss = Now;
                    }

                    break;
                case 0x1D or 0x1E or 0x1F or 0x9C or 0x9D or 0x12 or 0x13 or 0x14 or 0x21 or 0x22 or 0x94:
                    _maxLifeDirty = true;
                    break;
                case 0x55 or 0x56 when payload.Length >= 7:
                    _npcCodes[PacketDecoder.U32(payload, 1)] = (NPCCode)PacketDecoder.U16(payload, 5);
                    _unitNames.Remove(PacketDecoder.U32(payload, 1));
                    break;
                case 0x59 or 0x5B:
                    foreach (var player in Game.Players)
                    {
                        _playerNames[player.Id] = player.Name;
                    }

                    break;
            }
        }
        else if (record.Kind == TraceRecordKind.D2gsSent && record.Payload[0] == (byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame)
        {
            InGame = false;
        }
    }

    private void NoteAttack(byte[] payload)
    {
        var self = SelfId;
        if (self == null)
        {
            return;
        }

        AttackOnSelf attack = null;
        switch (payload[0])
        {
            case 0x6C when payload.Length >= 16 && PacketDecoder.U32(payload, 7) == self:
                attack = new AttackOnSelf(Now, PacketDecoder.U32(payload, 1), EntityType.NPC, $"attack {PacketDecoder.U16(payload, 5)}", true);
                break;
            case 0x4C when payload.Length >= 16 && PacketDecoder.U32(payload, 10) == self:
                attack = new AttackOnSelf(Now, PacketDecoder.U32(payload, 2), (EntityType)payload[1], Names.SkillOf(PacketDecoder.U16(payload, 6), (EntityType)payload[1], CasterCode(payload)), true);
                break;
            case 0x4D when payload.Length >= 13 && payload[1] != (byte)EntityType.Player
                && DistanceTo(new Point(PacketDecoder.U16(payload, 9), PacketDecoder.U16(payload, 11))) <= PointAttackReach:
                attack = new AttackOnSelf(Now, PacketDecoder.U32(payload, 2), (EntityType)payload[1], Names.SkillOf(PacketDecoder.U16(payload, 6), (EntityType)payload[1], CasterCode(payload)) + " at a point beside it", false);
                break;
            case 0x68 when payload.Length >= 15 && PacketDecoder.U32(payload, 11) == self:
                attack = new AttackOnSelf(Now, PacketDecoder.U32(payload, 1), EntityType.NPC, "closing in", false);
                break;
        }

        if (attack == null)
        {
            return;
        }

        _attacks.AddLast(attack);
        while (_attacks.First != null && Now - _attacks.First.Value.Offset > AttackMemory)
        {
            _attacks.RemoveFirst();
        }
    }

    private string CasterCode(byte[] payload)
        => (EntityType)payload[1] == EntityType.NPC && CodeOf(PacketDecoder.U32(payload, 2)) is { } code ? Names.Npc(code) : null;

    /// <summary>Who went for this client in a window, worst first: "Zombie 0xE9 (attack 1 x3)".</summary>
    public string Attackers(TimeSpan from, TimeSpan to, int shown = 4)
    {
        var groups = AttacksBetween(from, to)
            .GroupBy(a => (a.Attacker, a.AttackerType))
            .Select(g => (Name: UnitName(g.Key.Attacker, g.Key.AttackerType), Named: g.Any(a => a.Named), Hows: g.GroupBy(a => a.How).Select(h => h.Count() > 1 ? $"{h.Key} x{h.Count()}" : h.Key)))
            .OrderByDescending(g => g.Named)
            .ToList();
        if (groups.Count == 0)
        {
            return "no attack packet named this client";
        }

        var text = string.Join(", ", groups.Take(shown).Select(g => $"{g.Name} ({string.Join(", ", g.Hows)})"));
        return groups.Count > shown ? $"{text} and {groups.Count - shown} more" : text;
    }
}

public static class ClientReplay
{
    /// <summary>
    /// Replays each of the client's files to <paramref name="until"/> (an offset from the game start).
    /// Returns the context of the last file replayed, whose game holds the state at that point.
    /// </summary>
    public static ReplayContext Run(GameTrace game, string label, TimeSpan until, IReadOnlyList<IReplayObserver> observers, Action<ReplayContext> finishedFile = null)
    {
        game.CountReplay(label);
        if (Profile.Enabled)
        {
            return RunProfiled(game, label, until, observers, finishedFile);
        }

        ReplayContext last = null;
        foreach (var file in game.FilesOf(label))
        {
            var fileStart = file.Header.StartUtc - game.StartUtc;
            if (fileStart > until)
            {
                break;
            }

            var replayer = new Replayer(file.Path);
            var context = new ReplayContext(game, file, replayer);
            replayer.Applying = record =>
            {
                context.Before(record);
                foreach (var observer in observers)
                {
                    observer.Before(context, record);
                }
            };
            replayer.Applied = record =>
            {
                context.After(record);
                foreach (var observer in observers)
                {
                    observer.After(context, record);
                }
            };
            var untilElapsed = until == TimeSpan.MaxValue ? TimeSpan.MaxValue : until - fileStart;
            replayer.ReplayTo(untilElapsed);
            foreach (var observer in observers)
            {
                observer.Finished(context);
            }

            finishedFile?.Invoke(context);
            last?.Replayer.Dispose();
            last = context;
        }

        return last;
    }

    private static ReplayContext RunProfiled(GameTrace game, string label, TimeSpan until, IReadOnlyList<IReplayObserver> observers, Action<ReplayContext> finishedFile)
    {
        var names = observers.Select(o => "replay: observer " + o.GetType().Name).ToArray();
        var ticks = new long[observers.Count];
        long contextTicks = 0;
        long records = 0;
        ReplayContext last = null;
        foreach (var file in game.FilesOf(label))
        {
            var fileStart = file.Header.StartUtc - game.StartUtc;
            if (fileStart > until)
            {
                break;
            }

            Replayer replayer;
            using (Profile.Time("replay: open file"))
            {
                replayer = new Replayer(file.Path);
            }

            var context = new ReplayContext(game, file, replayer);
            replayer.Applying = record =>
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                context.Before(record);
                var at = System.Diagnostics.Stopwatch.GetTimestamp();
                contextTicks += at - start;
                for (var i = 0; i < observers.Count; i++)
                {
                    observers[i].Before(context, record);
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    ticks[i] += now - at;
                    at = now;
                }
            };
            replayer.Applied = record =>
            {
                records++;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                context.After(record);
                var at = System.Diagnostics.Stopwatch.GetTimestamp();
                contextTicks += at - start;
                for (var i = 0; i < observers.Count; i++)
                {
                    observers[i].After(context, record);
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    ticks[i] += now - at;
                    at = now;
                }
            };
            var untilElapsed = until == TimeSpan.MaxValue ? TimeSpan.MaxValue : until - fileStart;
            using (Profile.Time("replay: total, game and observers"))
            {
                replayer.ReplayTo(untilElapsed);
            }

            foreach (var observer in observers)
            {
                observer.Finished(context);
            }

            finishedFile?.Invoke(context);
            last?.Replayer.Dispose();
            last = context;
        }

        Profile.Add("replay: context bookkeeping", contextTicks, records);
        for (var i = 0; i < observers.Count; i++)
        {
            Profile.Add(names[i], ticks[i], records);
        }

        return last;
    }
}
