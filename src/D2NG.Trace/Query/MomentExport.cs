using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace D2NG.Trace.Query;

/// <summary>
/// A recorded moment as a game-free test fixture: the collision grid around the client (raw collision values, so the walk
/// and line-of-sight bits survive), the client and every unit in sight. It names nobody: the client is "self", other
/// players are "player1", "player2"... in the order the client knows them, and no account, character or game name is kept.
/// </summary>
public static class MomentExport
{
    public const int Format = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The fixture as JSON, one top-level property per line and one grid row per line, so a diff of two reads well.</summary>
    public static string Export(GameQuery query, string client, TimeSpan at, int radius, MapSource maps)
    {
        var areas = new AreaTracker();
        var stated = new StatedPositions();
        var server = new ServerPositions();
        var context = ClientReplay.Run(query.Game, client, at, [areas, stated, server])
            ?? throw new ArgumentException($"{client} has no trace starting before {TraceTime.Format(at)}");
        try
        {
            var game = context.Game;
            var me = game.Me ?? throw new ArgumentException($"{client} has no player assigned at {TraceTime.Format(at)}");
            var center = me.Location ?? throw new ArgumentException($"{client} has no position at {TraceTime.Format(at)}");
            var area = areas.Current(context) ?? areas.LastKnown ?? throw new ArgumentException($"{client}'s area is unknown at {TraceTime.Format(at)}");
            var map = maps.Get(game.MapId, game.Difficulty, area);
            var origin = new Point((ushort)Math.Max(0, center.X - radius), (ushort)Math.Max(0, center.Y - radius));
            var rows = new List<int[]>();
            for (var y = origin.Y; y <= center.Y + radius; y++)
            {
                var row = new int[center.X + radius - origin.X + 1];
                for (var x = origin.X; x <= center.X + radius; x++)
                {
                    var r = y - map.LevelOrigin.Y;
                    var c = x - map.LevelOrigin.X;
                    row[x - origin.X] = r < 0 || r >= map.Map.Length || c < 0 || c >= map.Map[r].Length ? -1 : map.Map[r][c];
                }

                rows.Add(row);
            }

            var players = game.Players.Where(p => p.Id != me.Id).OrderBy(p => p.Id).Select((p, i) => (Player: p, Role: $"player{i + 1}")).ToList();
            var roles = players.ToDictionary(p => p.Player.Id, p => p.Role);
            var belt = SafeItems(() => game.Belt.Items);
            var bag = SafeItems(() => game.Inventory.Items);
            var properties = new List<(string Name, object Value)>
            {
                ("format", Format),
                ("at", TraceTime.Format(context.Now)),
                ("area", area.ToString()),
                ("difficulty", game.Difficulty.ToString()),
                ("mapId", $"0x{game.MapId:X8}"),
                ("levelOrigin", Xy(map.LevelOrigin)),
                ("gridOrigin", Xy(origin)),
                ("collision", "raw map service values: odd blocks walking, bit 0x2 blocks line of sight and missiles, -1 outside the area"),
                ("self", new
                {
                    @class = me.Class.ToString(),
                    at = Xy(center),
                    serverAt = Xy(server.At(context.Now)?.Position),
                    life = me.Life,
                    maxLife = context.MaxLife,
                    mana = me.Mana,
                    maxMana = PotionWasteCheck.SafeMaxMana(me),
                    leftSkill = me.ActiveSkills.TryGetValue(Hand.Left, out var left) ? left.ToString() : null,
                    rightSkill = me.ActiveSkills.TryGetValue(Hand.Right, out var right) ? right.ToString() : null,
                    skills = me.Skills.Where(s => s.Value > 0).OrderBy(s => s.Key).ToDictionary(s => s.Key.ToString(), s => s.Value),
                    effects = me.Effects.Keys.Select(e => e.ToString()).OrderBy(e => e, StringComparer.Ordinal).ToArray(),
                    belt = Potions(belt),
                    inventoryPotions = Potions(bag),
                    mercenary = me.MercId is { } merc ? $"0x{merc:X}" : null,
                }),
                ("players", players.Select(p => new
                {
                    role = p.Role,
                    @class = p.Player.Class.ToString(),
                    at = Xy(p.Player.Location),
                    life = Math.Round(p.Player.LifePercentage),
                    mercenary = p.Player.MercId is { } merc ? $"0x{merc:X}" : null,
                }).ToList()),
                ("units", game.WorldObjects.Values
                    .Where(o => o.Type == EntityType.NPC && o.Location is not null && o.Location.Distance(center) <= radius)
                    .OrderBy(o => o.Location.Distance(center)).ThenBy(o => o.Id)
                    .Select(o => new
                    {
                        id = $"0x{o.Id:X}",
                        code = Names.Npc(o.NPCCode),
                        classId = (int)o.NPCCode,
                        state = o.State.ToString(),
                        life = Math.Round(o.LifePercentage, 1),
                        legacy = Xy(o.LegacyLocation),
                        anchored = Xy(o.LastKnownLocation),
                        simulated = Xy(o.LocationIn(Core.D2GS.Motion.MonsterMotion.Simulated)),
                        server = stated.Last.TryGetValue(o.Id, out var last) ? Xy(last.Position) : null,
                        serverSaid = stated.Last.TryGetValue(o.Id, out var said) ? $"{said.Kind} {(context.Now - said.At).TotalSeconds:0.00}s before" : null,
                        boss = o.IsBoss,
                        champion = o.IsChampion,
                        minion = o.IsBossMinion,
                        enchantments = o.MonsterEnchantments.Select(e => e.ToString()).OrderBy(e => e, StringComparer.Ordinal).ToArray(),
                        effects = o.Effects.Select(e => e.ToString()).OrderBy(e => e, StringComparer.Ordinal).ToArray(),
                        mercenaryOf = players.FirstOrDefault(p => p.Player.MercId == o.Id).Role ?? (me.MercId == o.Id ? "self" : null),
                    }).ToList()),
            };

            var text = new StringBuilder("{\n");
            foreach (var (name, value) in properties)
            {
                text.Append($"  \"{name}\": {JsonSerializer.Serialize(value, Json)},\n");
            }

            text.Append("  \"grid\": [\n");
            for (var i = 0; i < rows.Count; i++)
            {
                text.Append("    [").Append(string.Join(",", rows[i])).Append(i < rows.Count - 1 ? "],\n" : "]\n");
            }

            text.Append("  ]\n}\n");
            return text.ToString();
        }
        finally
        {
            context.Replayer.Dispose();
        }
    }

    private static int[] Xy(Point point) => point is null ? null : [point.X, point.Y];

    private static List<Item> SafeItems(Func<IEnumerable<Item>> items)
    {
        try
        {
            return [.. items()];
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            return [];
        }
    }

    private static Dictionary<string, int> Potions(IEnumerable<Item> items)
        => items.Where(i => i.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion)
            .GroupBy(i => i.Classification.ToString())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
}
