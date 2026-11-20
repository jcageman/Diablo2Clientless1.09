using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

public sealed class PacketOptions
{
    public TimeSpan From { get; init; } = TimeSpan.MinValue;

    public TimeSpan To { get; init; } = TimeSpan.MaxValue;

    public HashSet<byte> Types { get; init; }

    public bool? Sent { get; init; }

    public bool Realm { get; init; }
}

/// <summary>Packet views that read the trace files without replaying them.</summary>
public static class PacketQueries
{
    private static readonly TimeSpan RealmBefore = TimeSpan.FromMinutes(2);

    /// <summary>One line per packet of one client: offset, direction (&lt; received, &gt; sent), name and fields.</summary>
    public static IEnumerable<string> Packets(GameQuery query, string client, PacketOptions options)
    {
        var files = options.Realm ? RealmFilesOf(query, client) : query.Game.FilesOf(client).Select(f => f.Path).ToList();
        if (files.Count == 0)
        {
            yield return options.Realm
                ? $"no realm trace names {client}: the realm traces are matched by the character logon they carry"
                : $"no trace for {client}";
            yield break;
        }

        var from = options.From;
        var to = options.To;
        if (options.Realm && options.From == TimeSpan.MinValue)
        {
            from = -RealmBefore;
        }

        if (options.Realm && options.To == TimeSpan.MaxValue)
        {
            to = query.Game.EndUtc - query.Game.StartUtc + GameQuery.RealmAfter;
        }

        foreach (var file in files)
        {
            using var reader = new TraceReader(file);
            var fileStart = reader.Header.StartUtc - query.Game.StartUtc;
            var guessed = false;
            foreach (var record in reader.Records())
            {
                if (record.Kind == TraceRecordKind.SentWithoutEvidence)
                {
                    guessed = true;
                    continue;
                }

                var mark = guessed && record.Kind == TraceRecordKind.D2gsSent;
                if (record.Kind == TraceRecordKind.D2gsSent)
                {
                    guessed = false;
                }

                var offset = fileStart + record.Elapsed;
                if (offset < from)
                {
                    continue;
                }

                if (offset > to)
                {
                    break;
                }

                var sent = record.Kind is TraceRecordKind.D2gsSent or TraceRecordKind.McpSent or TraceRecordKind.BncsSent;
                var received = record.Kind is TraceRecordKind.D2gsReceived or TraceRecordKind.McpReceived or TraceRecordKind.BncsReceived;
                if (!sent && !received)
                {
                    if (record.Kind == TraceRecordKind.Gap)
                    {
                        yield return $"{TraceTime.Format(offset)} -- gap: {record.GapText}";
                    }

                    continue;
                }

                if (options.Sent is { } wantSent && wantSent != sent)
                {
                    continue;
                }

                var type = TypeOf(record);
                if (options.Types != null && !options.Types.Contains(type))
                {
                    continue;
                }

                yield return $"{TraceTime.Format(offset)} {(mark ? WithoutEvidenceMark : sent ? ">" : "<")} {PacketDecoder.Name(record.Kind, record.Payload),-22} {PacketDecoder.Fields(record.Kind, record.Payload)}";
            }
        }
    }

    /// <summary>The direction mark of a sent packet the import gave to this client without evidence.</summary>
    public const string WithoutEvidenceMark = "~>";

    private static byte TypeOf(TraceRecord record) => record.Kind switch
    {
        TraceRecordKind.McpReceived or TraceRecordKind.McpSent when record.Payload.Length > 2 => record.Payload[2],
        TraceRecordKind.BncsReceived or TraceRecordKind.BncsSent when record.Payload.Length > 1 => record.Payload[1],
        _ => record.Payload.Length > 0 ? record.Payload[0] : (byte)0,
    };

    private static List<string> RealmFilesOf(GameQuery query, string client)
    {
        var processes = query.Realm.Characters.Where(c => c.Value == client).Select(c => $"instance{c.Key.Instance}-{c.Key.Process}").ToHashSet();
        return [.. query.Game.RealmFiles().Where(f => processes.Contains(System.IO.Path.GetFileNameWithoutExtension(f).Split('-').Take(2).Aggregate((a, b) => $"{a}-{b}")))];
    }

    /// <summary>
    /// The units a packet names, each with its entity type - ids are unique per type only, so NPC 0x1 and
    /// player 0x1 are different units - and the role it plays in the packet. Every layout is checked
    /// against its own length.
    /// </summary>
    public static IEnumerable<(EntityType Type, uint Id, string Role)> UnitsIn(TraceRecord record)
    {
        var count = UnitsIn(record, out var first, out var second);
        if (count > 0)
        {
            yield return first;
        }

        if (count > 1)
        {
            yield return second;
        }
    }

    /// <summary>The units a packet names, as <see cref="UnitsIn(TraceRecord)"/> without allocating: how many (at most two), in order.</summary>
    public static int UnitsIn(TraceRecord record, out (EntityType Type, uint Id, string Role) first, out (EntityType Type, uint Id, string Role) second)
    {
        first = default;
        second = default;
        var p = record.Payload;
        if (p.Length == 0)
        {
            return 0;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            if (p[0] is 0x02 or 0x04 or 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 or 0x13 or 0x2F or 0x30 or 0x4B or 0x59 && p.Length >= 9)
            {
                first = ((EntityType)PacketDecoder.U32(p, 1), PacketDecoder.U32(p, 5), "target");
                return 1;
            }

            return 0;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived)
        {
            return 0;
        }

        switch (p[0])
        {
            case 0x0A or 0x0C or 0x0D or 0x0E or 0x0F or 0x12 or 0x13 or 0x14 or 0x15 or 0x51 when p.Length >= 6:
                first = ((EntityType)p[1], PacketDecoder.U32(p, 2), "unit");
                return 1;
            case 0x4C when p.Length >= 14:
                first = ((EntityType)p[1], PacketDecoder.U32(p, 2), "caster");
                second = ((EntityType)p[9], PacketDecoder.U32(p, 10), "target");
                return 2;
            case 0x4D when p.Length >= 6:
                first = ((EntityType)p[1], PacketDecoder.U32(p, 2), "caster");
                return 1;
            case 0x6C when p.Length >= 12:
                first = (EntityType.NPC, PacketDecoder.U32(p, 1), "attacker");
                second = ((EntityType)p[11], PacketDecoder.U32(p, 7), "target");
                return 2;
            case 0x68 when p.Length >= 15:
                first = (EntityType.NPC, PacketDecoder.U32(p, 1), "unit");
                second = ((EntityType)p[10], PacketDecoder.U32(p, 11), "target");
                return 2;
            case 0x55 or 0x56 or 0x67 or 0x69 or 0x6B or 0x6D when p.Length >= 5:
                first = (EntityType.NPC, PacketDecoder.U32(p, 1), "unit");
                return 1;
            case 0x60 when p.Length >= 7:
                first = (EntityType.Object, PacketDecoder.U32(p, 3), "unit");
                return 1;
            case 0x59 when p.Length >= 5:
                first = (EntityType.Player, PacketDecoder.U32(p, 1), "unit");
                return 1;
            case 0x8E when p.Length >= 10:
                first = (EntityType.Player, PacketDecoder.U32(p, 2), "player");
                second = (EntityType.Player, PacketDecoder.U32(p, 6), "corpse");
                return 2;
        }

        return 0;
    }

    /// <summary>"npc:0x1C9", "player:0x1", "object:0x2E", or a bare id (then of any type).</summary>
    public static bool TryUnit(string text, out EntityType? type, out uint id)
    {
        type = null;
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            type = text[..colon].ToLowerInvariant() switch
            {
                "player" => EntityType.Player,
                "npc" or "monster" => EntityType.NPC,
                "object" => EntityType.Object,
                "missile" => EntityType.Missiles,
                "item" => EntityType.Item,
                "warp" or "doorway" => EntityType.Doorway,
                _ => null,
            };
            if (type == null)
            {
                id = 0;
                return false;
            }

            text = text[(colon + 1)..];
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id)
            : uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    public sealed record FoundUnit(EntityType Type, uint Id, string Code, bool Exact, TimeSpan First, TimeSpan Last, string At);

    /// <summary>
    /// Monsters and objects whose code name matches the text, with when they were first and last named
    /// by a packet. Exact code-name matches come first ("Mephisto" before "MephistoGhost"), then
    /// substring matches, each in order of first sight.
    /// </summary>
    public static List<FoundUnit> FindUnits(GameQuery query, string client, string code)
    {
        var found = new Dictionary<(EntityType, uint), FoundUnit>();
        foreach (var file in query.Game.FilesOf(client))
        {
            using var reader = new TraceReader(file.Path);
            var fileStart = file.Header.StartUtc - query.Game.StartUtc;
            foreach (var record in reader.Records())
            {
                if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0)
                {
                    continue;
                }

                var p = record.Payload;
                var offset = fileStart + record.Elapsed;
                string name = null;
                (EntityType, uint) key = default;
                string at = null;
                if (p[0] is 0x55 or 0x56 && p.Length >= 11)
                {
                    name = Names.Npc((NPCCode)PacketDecoder.U16(p, 5));
                    key = (EntityType.NPC, PacketDecoder.U32(p, 1));
                    at = $"({PacketDecoder.U16(p, 7)}, {PacketDecoder.U16(p, 9)})";
                }
                else if (p[0] == 0x51 && p.Length >= 12)
                {
                    name = ((EntityCode)PacketDecoder.U16(p, 6)).ToString();
                    key = ((EntityType)p[1], PacketDecoder.U32(p, 2));
                    at = $"({PacketDecoder.U16(p, 8)}, {PacketDecoder.U16(p, 10)})";
                }

                if (name != null && name.Contains(code, StringComparison.OrdinalIgnoreCase))
                {
                    found[key] = found.TryGetValue(key, out var seen)
                        ? seen with { Last = offset }
                        : new FoundUnit(key.Item1, key.Item2, name, string.Equals(name, code, StringComparison.OrdinalIgnoreCase), offset, offset, at);
                    continue;
                }

                foreach (var (type, id, _) in UnitsIn(record))
                {
                    if (found.TryGetValue((type, id), out var known))
                    {
                        found[(type, id)] = known with { Last = offset };
                    }
                }
            }
        }

        return [.. found.Values.OrderByDescending(f => f.Exact).ThenBy(f => f.First)];
    }
}

/// <summary>
/// The history of one unit as one client saw it: every packet that names it, decoded, each followed by
/// what <see cref="Core.Game"/> made of it - so a packet handled wrongly shows up as a state that does
/// not follow from the packet.
/// </summary>
public sealed class UnitHistory(EntityType? unitType, uint unitId, TimeSpan from, TimeSpan to) : IReplayObserver
{
    /// <summary>One entry per packet: its line and the state line after it.</summary>
    public List<(string Packet, string State)> Entries { get; } = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        if (context.Now < from || context.Now > to)
        {
            return;
        }

        var count = PacketQueries.UnitsIn(record, out var first, out var second);
        var match = count > 0 && first.Id == unitId && (unitType == null || first.Type == unitType) ? first
            : count > 1 && second.Id == unitId && (unitType == null || second.Type == unitType) ? second
            : default;
        if (match.Role == null)
        {
            return;
        }

        var sent = record.Kind == TraceRecordKind.D2gsSent;
        Entries.Add((
            $"{TraceTime.Format(context.Now)} {(sent ? ">" : "<")} {PacketDecoder.Name(record.Kind, record.Payload),-18} {PacketDecoder.Fields(record.Kind, record.Payload)}{(match.Role is "unit" or "player" ? "" : $" [{match.Role}]")}{(unitType == null ? $" ({match.Type})" : "")}",
            $"          state: {State(context, record, match.Type)}"));
    }

    private string State(ReplayContext context, TraceRecord record, EntityType type)
    {
        var other = type == EntityType.Player ? null : context.Game.WorldObjects.GetValueOrDefault((unitId, type));
        if (other == null)
        {
            var player = type == EntityType.Player ? context.Game.Players.FirstOrDefault(p => p.Id == unitId) : null;
            return player != null
                ? $"player {player.Name} at {Names.Point(player.Location)}, life {player.LifePercentage:0}%"
                : "not in the client's state (removed, or never assigned)";
        }

        var note = record.Kind == TraceRecordKind.D2gsReceived && record.Payload[0] == 0x67
            ? "; Game took the move's destination as the position at once, the packet carries no current position"
            : "";
        return $"{other.Type} {(other.Type == EntityType.NPC ? Names.Npc(other.NPCCode) : other.Code.ToString())} at {Names.Point(other.Location)}, {context.DistanceTo(other.Location):0.0} from self, life {other.LifePercentage:0}%, {other.State}{note}";
    }
}
