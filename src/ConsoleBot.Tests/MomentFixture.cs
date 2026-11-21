using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Services.MapApi;
using System.Text.Json;

namespace ConsoleBot.Tests;

/// <summary>
/// A recorded moment written by <c>Trace export</c> (data/fixtures), turned into what the bot's attack code works on: an
/// <see cref="AreaMap"/> of the collision grid around the client and the monsters as <see cref="WorldObject"/>s at the
/// position one motion model gives them (legacy, anchored, simulated or server).
/// </summary>
internal sealed class MomentFixture
{
    private readonly JsonElement _root;

    private MomentFixture(JsonElement root) => _root = root;

    public static MomentFixture Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "fixtures", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new MomentFixture(document.RootElement.Clone());
    }

    public string Area => _root.GetProperty("area").GetString()!;

    public Point Self => PointOf(_root.GetProperty("self").GetProperty("at"))!;

    public string SelfClass => _root.GetProperty("self").GetProperty("class").GetString()!;

    /// <summary>The grid as the map service gives an area: its origin is the grid's corner, so map lookups by world position work unchanged.</summary>
    public AreaMap Map()
    {
        var rows = _root.GetProperty("grid").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
        return new AreaMap { LevelOrigin = PointOf(_root.GetProperty("gridOrigin"))!, Map = rows, AdjacentLevels = [], Npcs = [], Objects = [] };
    }

    /// <summary>The monsters as the client held them, placed by <paramref name="model"/>; a unit the model has no position for is left out.</summary>
    public List<WorldObject> Monsters(string model = "legacy")
    {
        var monsters = new List<WorldObject>();
        foreach (var unit in _root.GetProperty("units").EnumerateArray())
        {
            if (PointOf(unit.GetProperty(model)) is not { } at)
            {
                continue;
            }

            var id = Convert.ToUInt32(unit.GetProperty("id").GetString(), 16);
            var state = Enum.Parse<EntityState>(unit.GetProperty("state").GetString()!);
            var monster = new WorldObject(EntityType.NPC, id, 0, at, state, 0)
            {
                NPCCode = (NPCCode)unit.GetProperty("classId").GetInt32(),
                LifePercentage = unit.GetProperty("life").GetDouble(),
                IsBoss = unit.GetProperty("boss").GetBoolean(),
                IsChampion = unit.GetProperty("champion").GetBoolean(),
                IsBossMinion = unit.GetProperty("minion").GetBoolean(),
                MonsterEnchantments = [.. unit.GetProperty("enchantments").EnumerateArray().Select(e => Enum.Parse<MonsterEnchantment>(e.GetString()!))],
                Effects = [.. unit.GetProperty("effects").EnumerateArray().Select(e => Enum.Parse<EntityEffect>(e.GetString()!))],
            };
            monsters.Add(monster);
        }

        return monsters;
    }

    private static Point? PointOf(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? new Point((ushort)element[0].GetInt32(), (ushort)element[1].GetInt32()) : null;
}
