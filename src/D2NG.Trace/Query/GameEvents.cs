using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>One timeline line; a collapsed group of repeats carries the offset of its last element as End.</summary>
public sealed record TimelineEntry(TimeSpan Offset, string Client, string Source, string Text, TimeSpan? End = null)
{
    public TimeSpan Last => End ?? Offset;
}

/// <summary>
/// The notable things a client's replay shows, one entry each: area changes, life crossing 75/50/25%,
/// units first seen or dying near it, its own casts and the server moving it (repeats collapsed into
/// one line), the cursor item, portals, quest words, players joining and leaving, potions and deaths.
/// </summary>
public sealed class GameEvents(double radius, TimeSpan? shownFrom = null) : ICachedObserver
{
    public string CacheKey => FormattableString.Invariant($"GameEvents {radius:R} {(shownFrom ?? TimeSpan.MinValue).Ticks}");

    public void Save(System.IO.BinaryWriter w)
        => w.WriteList(_entries, (x, e) => { x.Write(e.Offset); x.WriteText(e.Client); x.WriteText(e.Source); x.WriteText(e.Text); x.Write(e.End); });

    public void Load(System.IO.BinaryReader r)
        => r.ReadList(_entries, x => new TimelineEntry(x.ReadTime(), x.ReadText(), x.ReadText(), x.ReadText(), x.ReadNullableTime()));

    /// <summary>
    /// Entries of a single instant before this are never shown (a timeline drops what ends before its window), so their
    /// text is not built; the state they would have reported is still kept.
    /// </summary>
    private readonly TimeSpan _shownFrom = shownFrom ?? TimeSpan.MinValue;

    private static readonly double[] LifeLines = [0.75, 0.5, 0.25];
    private static readonly TimeSpan RepeatGap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AreaCheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly List<TimelineEntry> _entries = [];
    private readonly HashSet<uint> _dead = [];
    private readonly HashSet<uint> _portals = [];
    private Group _casts;
    private Group _moves;
    private Group _interacts;
    private static readonly TimeSpan UnknownReported = TimeSpan.FromSeconds(2);

    private readonly AreaTracker _areas = new();
    private Area? _area;
    private (TimeSpan At, Point Where)? _unknownSince;
    private bool _unknownReported;
    private TimeSpan? _areaCheckedAt;
    private int _lifeBand = -1;
    private uint? _cursor;
    private string _cursorName;
    private ushort[] _questWords;
    private ushort[] _gameQuestWords;
    private bool _questsSeen;
    private bool _gameQuestsSeen;
    private Point _positionBefore;
    private string _leaving;

    public IReadOnlyList<TimelineEntry> Entries => _entries;

    /// <summary>Starts an entry made of a sent packet the import gave to this client without evidence.</summary>
    public const string WithoutEvidenceMark = "~ ";

    public const string WithoutEvidenceLegend = "~ sent packet attributed to this client without evidence (imported text capture)";

    private bool _nextSentGuessed;
    private bool _sentGuessed;

    private sealed class Group
    {
        public TimeSpan First;
        public TimeSpan Last;
        public string Key;
        public int Count;
        public bool Guessed;
        public HashSet<string> Targets = [];
        public Point From;
        public Point To;
    }

    public void Before(ReplayContext context, TraceRecord record)
    {
        if (record.Payload.Length == 0)
        {
            return;
        }

        _positionBefore = context.Position;
        if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            switch (record.Payload[0])
            {
                case 0x28:
                    _questWords = context.Game.Me == null || !_questsSeen ? null : context.Game.Quests.CharacterWords;
                    break;
                case 0x29:
                    _gameQuestWords = context.Game.Me == null || !_gameQuestsSeen ? null : context.Game.Quests.GameWords;
                    break;
                case 0x5C when record.Payload.Length >= 5:
                    var id = PacketDecoder.U32(record.Payload, 1);
                    _leaving = context.Game.Me == null ? null : context.Game.Players.FirstOrDefault(p => p.Id == id)?.Name;
                    break;
            }
        }
    }

    public void After(ReplayContext context, TraceRecord record)
    {
        _areas.Observe(context, record);
        var payload = record.Payload;
        if (record.Kind == TraceRecordKind.Gap)
        {
            Add(context, $"trace gap: {record.GapText}");
            return;
        }

        if (record.Kind == TraceRecordKind.SentWithoutEvidence)
        {
            _nextSentGuessed = true;
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            _sentGuessed = _nextSentGuessed;
            _nextSentGuessed = false;
        }

        if (payload.Length == 0 || context.Game.Me == null)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            Sent(context, payload);
            _sentGuessed = false;
        }
        else if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            Received(context, payload);
        }

        if (record.Kind is TraceRecordKind.D2gsReceived or TraceRecordKind.ClientAction)
        {
            CheckCursor(context);
        }

        CheckArea(context, record);
    }

    /// <summary>A file ends: the next one is a fresh join, whose first quest packets are a snapshot, not changes.</summary>
    public void Finished(ReplayContext context)
    {
        Flush(context, ref _casts, Casts);
        Flush(context, ref _moves, Moves);
        Flush(context, ref _interacts, Interacts);
        _questsSeen = false;
        _gameQuestsSeen = false;
        _unknownSince = null;
        _unknownReported = false;
    }

    private void Sent(ReplayContext context, byte[] payload)
    {
        var type = payload[0];
        switch (type)
        {
            case 0x05 or 0x08 or 0x0C or 0x0F or 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11:
                var hand = type <= 0x0A ? Hand.Left : Hand.Right;
                var skill = context.Game.Me.ActiveSkills.TryGetValue(hand, out var active) ? active.ToString() : $"{hand.ToString().ToLowerInvariant()} skill";
                var onUnit = type is 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11;
                string target;
                if (onUnit && payload.Length >= 9)
                {
                    target = context.UnitName(PacketDecoder.U32(payload, 5), (EntityType)PacketDecoder.U32(payload, 1));
                }
                else if (payload.Length >= 5)
                {
                    target = $"({PacketDecoder.U16(payload, 1)}, {PacketDecoder.U16(payload, 3)})";
                }
                else
                {
                    target = "?";
                }

                Extend(context, ref _casts, skill, target, Casts);
                break;
            case 0x13 when payload.Length >= 9:
                var interacted = (EntityType)PacketDecoder.U32(payload, 1);
                var id = PacketDecoder.U32(payload, 5);
                Extend(context, ref _interacts, $"{interacted} {Names.Id(id)}", InteractName(context, interacted, id), Interacts);
                break;
            case 0x26 or 0x20 when payload.Length >= 5:
                var item = context.Game.FindItemById(PacketDecoder.U32(payload, 1));
                if (item != null && item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion)
                {
                    var maxLife = context.MaxLife;
                    Add(context, $"drank {item.Name} at {context.Life}/{maxLife} life");
                }
                else if (type == 0x20)
                {
                    Add(context, $"used item {(item == null ? Names.Id(PacketDecoder.U32(payload, 1)) : $"{item.Name} {Names.Id(item.Id)}")}");
                }

                break;
            case (byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame when payload.Length == 1:
                Add(context, "left the game");
                break;
        }
    }

    private static string InteractName(ReplayContext context, EntityType type, uint id)
    {
        var known = context.Game.WorldObjects.GetValueOrDefault((id, type));
        return type switch
        {
            EntityType.NPC => context.UnitName(id),
            EntityType.Player => context.UnitName(id, EntityType.Player),
            _ when known != null => $"{known.Code} {Names.Id(id)}",
            _ => $"{type} {Names.Id(id)}",
        };
    }

    private void Received(ReplayContext context, byte[] payload)
    {
        var self = context.SelfId;
        switch (payload[0])
        {
            case 0x15 when payload.Length >= 6 && payload[1] == 0 && PacketDecoder.U32(payload, 2) == self:
                ExtendMove(context, "reassigned by the server");
                break;
            case 0x96 when _positionBefore != null && context.Position != null:
                var verified = new WalkVerifyAt(payload);
                if (verified.Location != null && verified.Location.Distance(_positionBefore) > 5)
                {
                    ExtendMove(context, $"walk verify {Names.Point(verified.Location)}, client thought {Names.Point(_positionBefore)}");
                }

                break;
            case 0x18 or 0x95:
                LifeLine(context);
                break;
            case 0x55 or 0x56 when payload.Length >= 11:
                var seen = context.Npc(PacketDecoder.U32(payload, 1));
                if (seen != null && context.DistanceTo(seen.Location) <= radius)
                {
                    Add(context, $"seen {context.UnitName(seen.Id)} at {Names.Point(seen.Location)}, {context.DistanceTo(seen.Location):0} away");
                }

                break;
            case 0x69 when payload.Length >= 6 && payload[5] is (byte)EntityState.Dieing or (byte)EntityState.Dead:
                var dying = PacketDecoder.U32(payload, 1);
                var npc = context.Npc(dying);
                if (_dead.Add(dying) && npc != null && context.DistanceTo(npc.Location) <= radius)
                {
                    Add(context, $"{context.UnitName(dying)} died at {Names.Point(npc.Location)}, {context.DistanceTo(npc.Location):0} away");
                }

                break;
            case 0x51 when payload.Length >= 8 && (EntityCode)PacketDecoder.U16(payload, 6) == EntityCode.TownPortal:
                var portal = PacketDecoder.U32(payload, 2);
                _portals.Add(portal);
                var at = new Point(PacketDecoder.U16(payload, 8), PacketDecoder.U16(payload, 10));
                Add(context, $"town portal {Names.Id(portal)} appeared at {Names.Point(at)}, {context.DistanceTo(at):0} away");
                break;
            case 0x82 when payload.Length >= 29:
                var owned = context.Game.WorldObjects.GetValueOrDefault((PacketDecoder.U32(payload, 21), EntityType.Object));
                Add(context, $"town portal {Names.Id(PacketDecoder.U32(payload, 21))} belongs to {context.UnitName(PacketDecoder.U32(payload, 1), EntityType.Player)}{(owned?.TownPortalArea is { } area && area != Area.None ? $", leads to {Names.Area(area)}" : "")}");
                break;
            case 0x0A when payload.Length >= 6 && payload[1] == (byte)EntityType.Object && _portals.Remove(PacketDecoder.U32(payload, 2)):
                Add(context, $"town portal {Names.Id(PacketDecoder.U32(payload, 2))} disappeared");
                break;
            case 0x28:
                QuestChanges(context, _questWords, context.Game.Quests.CharacterWords, "quest");
                _questsSeen = true;
                break;
            case 0x29:
                QuestChanges(context, _gameQuestWords, context.Game.Quests.GameWords, "game quest");
                _gameQuestsSeen = true;
                break;
            case 0x5B when payload.Length >= 9 && PacketDecoder.U32(payload, 3) != self:
                Add(context, $"player {context.UnitName(PacketDecoder.U32(payload, 3), EntityType.Player)} joined");
                break;
            case 0x5C when _leaving != null:
                Add(context, $"player {_leaving} left");
                _leaving = null;
                break;
            case 0x8E when payload.Length >= 10 && payload[1] != 0 && PacketDecoder.U32(payload, 2) == self:
                Add(context, context.SinceLifeLoss < DeathCheck.DamageBeforeDeath
                    ? $"DIED: corpse {Names.Id(PacketDecoder.U32(payload, 6))} assigned at {Names.Point(context.Position)}"
                    : $"corpse {Names.Id(PacketDecoder.U32(payload, 6))} assigned with no recent damage: a body from an earlier death");
                break;
        }
    }

    private void LifeLine(ReplayContext context)
    {
        var max = context.MaxLife;
        if (max <= 0)
        {
            return;
        }

        var fraction = context.LifeFraction;
        var band = LifeLines.Count(line => fraction < line);
        if (_lifeBand < 0)
        {
            _lifeBand = band;
            return;
        }

        if (band > _lifeBand)
        {
            Add(context, $"life {fraction:0%} ({context.Life}/{max}), below {LifeLines[band - 1]:0%}");
        }
        else if (band < _lifeBand && band == 0)
        {
            Add(context, $"life back above 75% ({context.Life}/{max})");
        }

        _lifeBand = band;
    }

    private void QuestChanges(ReplayContext context, ushort[] before, ushort[] after, string what)
    {
        if (before == null)
        {
            return;
        }

        for (var word = 0; word < after.Length && word < before.Length; word++)
        {
            if (before[word] != after[word])
            {
                var name = Enum.IsDefined(typeof(Core.D2GS.Quest.QuestId), word) ? ((Core.D2GS.Quest.QuestId)word).ToString() : $"word {word}";
                Add(context, $"{what} {name}: 0x{before[word]:X4} -> 0x{after[word]:X4}");
            }
        }
    }

    private void CheckCursor(ReplayContext context)
    {
        var cursor = context.Game.CursorItem;
        if (cursor?.Id == _cursor)
        {
            return;
        }

        Add(context, cursor == null ? $"cursor empty (was {_cursorName})" : $"cursor holds {cursor.Name} {Names.Id(cursor.Id)}");
        _cursor = cursor?.Id;
        _cursorName = cursor == null ? null : $"{cursor.Name} {Names.Id(cursor.Id)}";
    }

    private void CheckArea(ReplayContext context, TraceRecord record)
    {
        var forced = record.Kind == TraceRecordKind.D2gsReceived && record.Payload.Length > 0 && record.Payload[0] is 0x03 or 0x15 or 0x07;
        if (!forced && context.Now - _areaCheckedAt < AreaCheckInterval)
        {
            return;
        }

        _areaCheckedAt = context.Now;
        var game = context.Game;
        if (game.ResolvedArea == null)
        {
            _unknownSince ??= (context.Now, context.Position == null ? null : new Point(context.Position.X, context.Position.Y));
            if (!_unknownReported && context.Now - _unknownSince.Value.At >= UnknownReported)
            {
                _unknownReported = true;
                _entries.Add(new TimelineEntry(_unknownSince.Value.At, context.Client, "game",
                    game.ServerConfirmedArea is { } serverArea
                        ? $"off its map: {Names.Point(_unknownSince.Value.Where)} is outside every map tile revealed to this client, while the server's last position {Names.Point(game.ServerConfirmedLocation)} is in {Names.Area(serverArea)}"
                        : $"area unknown: {Names.Point(_unknownSince.Value.Where)} is outside every map tile revealed to this client{(_area is { } last ? $" (last known area {Names.Area(last)})" : "")}"));
            }

            if (_areas.Current(context) is not { } known || known == Area.None || known == _area)
            {
                return;
            }

            Add(context, _area == null ? $"in {Names.Area(known)} by the server's position {Names.Point(game.ServerConfirmedLocation)}" : $"area {Names.Area(_area.Value)} -> {Names.Area(known)} by the server's position {Names.Point(game.ServerConfirmedLocation)}");
            _area = known;
            return;
        }

        var area = _areas.Current(context).Value;
        var wasUnknown = _unknownReported;
        _unknownSince = null;
        _unknownReported = false;
        if (area == Area.None || area == _area)
        {
            if (wasUnknown)
            {
                Add(context, $"back on the revealed map in {Names.Area(area)} at {Names.Point(context.Position)}");
            }

            return;
        }

        Add(context, _area == null ? $"in {Names.Area(area)} at {Names.Point(context.Position)}" : $"area {Names.Area(_area.Value)} -> {Names.Area(area)} at {Names.Point(context.Position)}");
        _area = area;
    }

    private void ExtendMove(ReplayContext context, string what)
    {
        if (_moves != null && (_moves.Key != what.Split(' ')[0] || context.Now - _moves.Last > RepeatGap))
        {
            Flush(context, ref _moves, Moves);
        }

        _moves ??= new Group { First = context.Now, Key = what.Split(' ')[0], From = _positionBefore };
        _moves.Count++;
        _moves.Last = context.Now;
        _moves.To = context.Position;
        _moves.Targets.Add(what);
    }

    private void Extend(ReplayContext context, ref Group group, string key, string target, Func<Group, string> render)
    {
        if (group != null && (group.Key != key || context.Now - group.Last > RepeatGap))
        {
            Flush(context, ref group, render);
        }

        group ??= new Group { First = context.Now, Key = key };
        group.Count++;
        group.Last = context.Now;
        group.Targets.Add(target);
        group.Guessed |= _sentGuessed;
    }

    private void Flush(ReplayContext context, ref Group group, Func<Group, string> render)
    {
        if (group == null)
        {
            return;
        }

        _entries.Add(new TimelineEntry(group.First, context.Client, "game", (group.Guessed ? WithoutEvidenceMark : "") + render(group), group.Last));
        group = null;
    }

    private static string Casts(Group group)
    {
        var targets = Targets(group);
        return group.Count == 1
            ? $"cast {group.Key} at {targets}"
            : $"x{group.Count} {group.Key} at {targets} over {(group.Last - group.First).TotalSeconds:0.0}s";
    }

    private static string Interacts(Group group)
        => group.Count == 1 ? $"interact with {Targets(group)}" : $"x{group.Count} interact with {Targets(group)} over {(group.Last - group.First).TotalSeconds:0.0}s";

    private static string Moves(Group group)
    {
        if (group.Key == "walk")
        {
            return group.Count == 1 ? group.Targets.First() : $"x{group.Count} walk verify corrections over {(group.Last - group.First).TotalSeconds:0.0}s, last: {group.Targets.Last()}";
        }

        return group.Count == 1
            ? $"reassigned by the server {Names.Point(group.From)} -> {Names.Point(group.To)}"
            : $"x{group.Count} reassigned by the server {Names.Point(group.From)} -> {Names.Point(group.To)} over {(group.Last - group.First).TotalSeconds:0.0}s";
    }

    private static string Targets(Group group)
        => group.Targets.Count switch
        {
            1 => group.Targets.First(),
            <= 3 => string.Join(", ", group.Targets),
            _ => group.Targets.All(t => t.StartsWith('(')) ? $"{group.Targets.Count} points, last {group.Targets.Last()}" : $"{group.Targets.Count} targets, last {group.Targets.Last()}",
        };

    private void Add(ReplayContext context, string text)
    {
        if (context.Now >= _shownFrom)
        {
            _entries.Add(new TimelineEntry(context.Now, context.Client, "game", (_sentGuessed ? WithoutEvidenceMark : "") + text));
        }
    }

    private void Add(ReplayContext context, [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("", "context")] ref EntryText text)
    {
        if (text.Wanted)
        {
            _entries.Add(new TimelineEntry(context.Now, context.Client, "game", (_sentGuessed ? WithoutEvidenceMark : "") + text.ToStringAndClear()));
        }
    }

    /// <summary>An entry's text, built only when the entry can be shown: its holes are not evaluated for one before the shown window.</summary>
    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct EntryText
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;

        public EntryText(int literalLength, int formattedCount, GameEvents events, ReplayContext context, out bool wanted)
        {
            Wanted = wanted = context.Now >= events._shownFrom;
            _inner = wanted ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public bool Wanted { get; }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);

        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string format) => _inner.AppendFormatted(value, format);

        public void AppendFormatted(string value) => _inner.AppendFormatted(value);

        public string ToStringAndClear() => _inner.ToStringAndClear();
    }

    private readonly struct WalkVerifyAt
    {
        public WalkVerifyAt(byte[] payload)
        {
            try
            {
                Location = new Core.D2GS.Packet.Incoming.WalkVerifyPacket(new Core.D2GS.Packet.D2gsPacket(payload)).Location;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Location = null;
            }
        }

        public Point Location { get; }
    }
}
