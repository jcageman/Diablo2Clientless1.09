using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Quest;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2NG.Trace.Query;

/// <summary>
/// The replayed state of one client at one instant, as JSON with one property per line and one array
/// element per line. Only that client is replayed, and only up to the instant.
/// </summary>
public static class StateQuery
{
    public static readonly string[] Vocabulary = ["self", "units", "players", "portals", "objects", "inventory", "bag", "cube", "stash", "belt", "equipped", "cursor", "quests", "party"];

    public const string DefaultSelect = "self,units:40,players,portals";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static List<(string Name, double? Radius)> ParseSelect(string select)
    {
        var parts = new List<(string, double?)>();
        foreach (var part in (select ?? DefaultSelect).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':');
            var name = pieces[0].ToLowerInvariant();
            if (!Vocabulary.Contains(name))
            {
                throw new ArgumentException($"unknown --select '{name}': use {string.Join(", ", Vocabulary)}; units and objects take :radius");
            }

            double? radius = pieces.Length > 1 && double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
            parts.Add((name, radius));
        }

        return parts;
    }

    public static readonly string[] Motions = ["legacy", "anchored", "simulated", "server"];

    /// <param name="motion">
    /// Where the units section places a monster: legacy (the game's default, a move's destination), anchored (the last
    /// exact position a packet stated), simulated (moved along its path at its speed; needs the speeds in the data folder,
    /// else legacy) or server (the position in the last packet that named it, whatever kind).
    /// </param>
    public static string Render(GameQuery query, string client, TimeSpan at, string select, bool fullItems = false, string motion = null)
    {
        var parts = ParseSelect(select);
        motion = (motion ?? "legacy").ToLowerInvariant();
        if (!Motions.Contains(motion))
        {
            throw new ArgumentException($"--motion takes {string.Join(", ", Motions)}");
        }

        var areas = new AreaTracker();
        var placement = new UnitPlacement(motion, new StatedPositions());
        var context = ClientReplay.Run(query.Game, client, at, motion == "server" ? [areas, placement.Stated] : [areas]);
        if (context == null)
        {
            return $"{{\"error\": \"{client} has no trace starting before {TraceTime.Format(at)}\"}}";
        }

        try
        {
            var game = context.Game;
            var output = new StringBuilder();
            output.AppendLine("{");
            var properties = new List<(string, object)>
            {
                ("client", client),
                ("at", TraceTime.Format(context.Now)),
                ("asked", TraceTime.Format(at)),
                ("file", System.IO.Path.GetFileName(context.File.Path)),
            };
            if (game.Me == null)
            {
                properties.Add(("note", "no player assigned yet at this time"));
            }
            else
            {
                foreach (var (name, radius) in parts)
                {
                    properties.Add((name, Section(context, name, radius, areas, fullItems, placement)));
                }
            }

            for (var i = 0; i < properties.Count; i++)
            {
                var (name, value) = properties[i];
                output.Append($"  \"{name}\": ");
                if (value is System.Collections.IList list && value is not string)
                {
                    if (list.Count == 0)
                    {
                        output.Append("[]");
                    }
                    else
                    {
                        output.AppendLine("[");
                        for (var j = 0; j < list.Count; j++)
                        {
                            output.Append("    ").Append(JsonSerializer.Serialize(list[j], Json)).AppendLine(j < list.Count - 1 ? "," : "");
                        }

                        output.Append("  ]");
                    }
                }
                else
                {
                    output.Append(JsonSerializer.Serialize(value, Json));
                }

                output.AppendLine(i < properties.Count - 1 ? "," : "");
            }

            output.Append('}');
            return output.ToString();
        }
        finally
        {
            context.Replayer.Dispose();
        }
    }

    /// <summary>The selected sections of the state a replay has reached, as one JSON object on one line.</summary>
    public static string OneLine(ReplayContext context, IReadOnlyList<(string Name, double? Radius)> parts, IEnumerable<(string Key, object Value)> leading, AreaTracker areas, bool fullItems = false)
    {
        var properties = new Dictionary<string, object>();
        foreach (var (key, value) in leading)
        {
            properties[key] = value;
        }

        properties["at"] = TraceTime.Format(context.Now);
        if (context.Game.Me == null)
        {
            properties["note"] = "no player assigned yet at this time";
        }
        else
        {
            foreach (var (name, radius) in parts)
            {
                try
                {
                    properties[name] = Section(context, name, radius, areas, fullItems);
                }
                catch (Exception e) when (e is NullReferenceException or InvalidOperationException or KeyNotFoundException)
                {
                    properties[name] = $"unavailable: {e.Message}";
                }
            }
        }

        return JsonSerializer.Serialize(properties, Json);
    }

    private static object Section(ReplayContext context, string name, double? radius, AreaTracker areas, bool fullItems, UnitPlacement placement = null)
    {
        var game = context.Game;
        var me = game.Me;
        return name switch
        {
            "self" => Self(context, areas),
            "units" => game.WorldObjects.Values
                .Where(o => o.Type == EntityType.NPC)
                .Select(o => (Unit: o, At: PlaceOf(o, placement, context.Now)))
                .Select(u => (u.Unit, u.At, Distance: context.DistanceTo(u.At.Position)))
                .Where(u => radius == null || u.Distance <= radius)
                .OrderBy(u => u.Distance)
                .ThenBy(u => u.Unit.Id)
                .Select(u => new
                {
                    id = Names.Id(u.Unit.Id),
                    code = Names.Npc(u.Unit.NPCCode),
                    at = Xy(u.At.Position),
                    legacyAt = u.At.Position == u.Unit.LegacyLocation ? null : Xy(u.Unit.LegacyLocation),
                    stated = u.At.Stated,
                    life = Math.Round(u.Unit.LifePercentage),
                    state = u.Unit.State.ToString(),
                    distance = Math.Round(u.Distance, 1),
                    boss = u.Unit.IsBoss ? true : (bool?)null,
                    champion = u.Unit.IsChampion ? true : (bool?)null,
                    minion = u.Unit.IsBossMinion ? true : (bool?)null,
                    enchantments = u.Unit.MonsterEnchantments.Count > 0 ? u.Unit.MonsterEnchantments.Select(e => e.ToString()).ToArray() : null,
                })
                .Cast<object>().ToList(),
            "players" => game.Players.Where(p => p.Id != me.Id).Select(p => new
            {
                id = Names.Id(p.Id),
                name = p.Name,
                @class = p.Class.ToString(),
                at = Xy(p.Location),
                distance = p.Location == null ? (double?)null : Math.Round(context.DistanceTo(p.Location), 1),
                area = p.Area?.ToString(),
                life = Math.Round(p.LifePercentage),
                party = p.PartyId,
                corpse = p.CorpseId is { } corpse ? Names.Id(corpse) : null,
            }).Cast<object>().ToList(),
            "portals" => game.WorldObjects.Values.Where(o => o.Code is EntityCode.TownPortal or EntityCode.RedTownPortal).OrderBy(o => context.DistanceTo(o.Location)).ThenBy(o => o.Type).ThenBy(o => o.Id).Select(o => new
            {
                id = Names.Id(o.Id),
                code = o.Code.ToString(),
                at = Xy(o.Location),
                distance = Math.Round(context.DistanceTo(o.Location), 1),
                owner = o.TownPortalOwnerId == 0 ? null : context.UnitName(o.TownPortalOwnerId, EntityType.Player),
                leadsTo = o.TownPortalArea == Core.D2GS.Act.Area.None ? null : o.TownPortalArea.ToString(),
                state = o.State.ToString(),
            }).Cast<object>().ToList(),
            "objects" => game.WorldObjects.Values
                .Where(o => o.Type != EntityType.NPC)
                .Select(o => (Object: o, Distance: context.DistanceTo(o.Location)))
                .Where(o => radius == null || o.Distance <= radius)
                .OrderBy(o => o.Distance)
                .ThenBy(o => o.Object.Type)
                .ThenBy(o => o.Object.Id)
                .Select(o => new { id = Names.Id(o.Object.Id), type = o.Object.Type.ToString(), code = o.Object.Code.ToString(), at = Xy(o.Object.Location), state = o.Object.State.ToString(), distance = Math.Round(o.Distance, 1) })
                .Cast<object>().ToList(),
            "inventory" => Items(game.Inventory.Items.Concat(game.Cube.Items).Concat(game.Stash.Items), fullItems),
            "bag" => Items(game.Inventory.Items, fullItems),
            "cube" => Items(game.Cube.Items, fullItems),
            "stash" => Items(game.Stash.Items, fullItems),
            "belt" => Items(game.Belt.Items, fullItems),
            "equipped" => Equipped(game, fullItems),
            "cursor" => game.CursorItem == null ? null : ItemView.Of(game.CursorItem, fullItems),
            "quests" => Quests(game),
            "party" => new
            {
                self = me.PartyId,
                members = game.Players.Where(p => p.Id != me.Id).Select(p => new { name = p.Name, party = p.PartyId, withSelf = game.IsInPartyWith(p) }).ToArray(),
            },
            _ => null,
        };
    }

    /// <summary>Where the chosen motion model places a monster; for server, also what stated it and how long ago.</summary>
    private static (Point Position, string Stated) PlaceOf(WorldObject unit, UnitPlacement placement, TimeSpan now)
    {
        switch (placement?.Motion)
        {
            case "anchored":
                return (unit.LastKnownLocation, null);
            case "simulated":
                return (unit.LocationIn(Core.D2GS.Motion.MonsterMotion.Simulated), null);
            case "server" when placement.Stated.Last.TryGetValue(unit.Id, out var last):
                return (last.Position, $"{last.Kind} (0x{last.Packet:X2}) {(now - last.At).TotalSeconds:0.00}s before");
            case "server":
                return (unit.LegacyLocation, "no packet stated a position");
            default:
                return (unit.LegacyLocation, null);
        }
    }

    private static object Self(ReplayContext context, AreaTracker areas)
    {
        var game = context.Game;
        var me = game.Me;
        var area = areas.Describe(context);

        int Attribute(Core.D2GS.Players.Attribute attribute) => me.Attributes.GetValueOrDefault(attribute, 0);
        return new
        {
            id = Names.Id(me.Id),
            name = me.Name,
            @class = me.Class.ToString(),
            at = Xy(me.Location),
            area,
            act = game.Act.ToString(),
            life = me.Life,
            maxLife = context.MaxLife,
            mana = me.Mana,
            maxMana = SafeMaxMana(me),
            level = Attribute(Core.D2GS.Players.Attribute.Level),
            experience = me.Experience,
            gold = Attribute(Core.D2GS.Players.Attribute.GoldOnPerson),
            goldInStash = Attribute(Core.D2GS.Players.Attribute.GoldInStash),
            effects = me.Effects.Keys.Select(e => e.ToString()).OrderBy(e => e, StringComparer.Ordinal).ToArray(),
            cursor = game.CursorItem == null ? null : $"{game.CursorItem.Name} {Names.Id(game.CursorItem.Id)}",
            leftSkill = me.ActiveSkills.TryGetValue(Hand.Left, out var left) ? left.ToString() : null,
            rightSkill = me.ActiveSkills.TryGetValue(Hand.Right, out var right) ? right.ToString() : null,
            corpse = me.CorpseId is { } corpse ? Names.Id(corpse) : null,
            party = me.PartyId,
            difficulty = game.Difficulty.ToString(),
            mapId = $"0x{game.MapId:X8}",
        };
    }

    private static int? SafeMaxMana(Self me)
    {
        try
        {
            return me.MaxMana;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A container's items in grid order, so two runs list them alike.</summary>
    private static List<object> Items(IEnumerable<Item> items, bool full)
        => [.. items.OrderBy(i => i.Container).ThenBy(i => ItemView.Cell(i)?.Y ?? 0).ThenBy(i => i.Location?.X ?? 0).ThenBy(i => i.Id).Select(i => ItemView.Of(i, full))];

    /// <summary>
    /// The client's own body slots, both weapon sets when it knows the second, then the mercenary's items: those whose last
    /// item packet put them on the unit the server assigned as this client's mercenary.
    /// </summary>
    private static List<object> Equipped(Game game, bool full)
    {
        var equipped = game.Me.Equipment.OrderBy(e => e.Key).Select(e => ItemView.Equipped(e.Key.ToString(), e.Value, full)).ToList();
        if (game.Me.MercId is { } merc)
        {
            equipped.AddRange(game.Items.Values
                .Where(i => i.PlayerId == merc && i.EntityType == Core.D2GS.Objects.EntityType.NPC && i.Action is Core.D2GS.Items.Action.Equip or Core.D2GS.Items.Action.SwapBodyItem or Core.D2GS.Items.Action.IndirectlySwapBodyItem)
                .OrderBy(i => i.Directory)
                .ThenBy(i => i.Id)
                .Select(i => ItemView.Equipped($"mercenary {Names.Id(merc)} {i.Directory}", i, full)));
        }

        return equipped;
    }

    private static object Quests(Game game)
    {
        var words = game.Quests.CharacterWords;
        var gameWords = game.Quests.GameWords;
        string Name(int word) => Enum.IsDefined(typeof(QuestId), word) ? ((QuestId)word).ToString() : $"word{word}";
        return new
        {
            character = Enumerable.Range(0, words.Length).Where(i => words[i] != 0).ToDictionary(Name, i => $"0x{words[i]:X4}"),
            game = Enumerable.Range(0, gameWords.Length).Where(i => gameWords[i] != 0).ToDictionary(Name, i => $"0x{gameWords[i]:X4}"),
        };
    }

    private static int[] Xy(Point point) => point == null ? null : [point.X, point.Y];
}

/// <summary>Which motion model the units section places monsters by, and for "server" what the packets stated.</summary>
public sealed record UnitPlacement(string Motion, StatedPositions Stated);

/// <summary>For every monster, the position in the last packet that stated one (a move's destination too), what kind of packet, and when.</summary>
public sealed class StatedPositions : IReplayObserver
{
    public Dictionary<uint, (TimeSpan At, Point Position, Core.D2GS.Motion.MotionUpdateKind Kind, byte Packet)> Last { get; } = [];

    private ClientFile _file;

    public void After(ReplayContext context, TraceRecord record)
    {
        if (!ReferenceEquals(context.File, _file))
        {
            _file = context.File;
            Last.Clear();
        }

        if (record.Kind != Core.Tracing.TraceRecordKind.D2gsReceived)
        {
            return;
        }

        var update = Core.D2GS.Motion.MotionUpdate.Read(record.Payload);
        if (update.Kind != Core.D2GS.Motion.MotionUpdateKind.None && update.Position is { } at && (at.X != 0 || at.Y != 0))
        {
            Last[update.EntityId] = (context.Now, at, update.Kind, update.Packet);
        }
    }
}
