using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2NG.Core.MonsterData;

/// <summary>
/// Monster resistances read out of the game's own monstats table, indexed by the class id the game
/// server sends. Generated on demand by the MpqData tool, because a realm patch can change these
/// values and a hand-maintained copy then lies.
/// </summary>
public sealed class MonsterResistTable
{
    /// <summary>A monster is immune at 100, not above it.</summary>
    public const int ImmuneAt = 100;

    private readonly Dictionary<int, MonsterResistEntry> _byClassId;

    public static MonsterResistTable Empty { get; } = new(new Dictionary<int, MonsterResistEntry>(), null);

    private MonsterResistTable(Dictionary<int, MonsterResistEntry> byClassId, MonsterResistSource source)
    {
        _byClassId = byClassId;
        Source = source;
    }

    /// <summary>Where the values came from, so a stale table can be spotted in the log.</summary>
    public MonsterResistSource Source { get; }

    public int Count => _byClassId.Count;

    public static MonsterResistTable FromEntries(IEnumerable<MonsterResistEntry> entries, MonsterResistSource source)
    {
        var byClassId = new Dictionary<int, MonsterResistEntry>();
        foreach (var entry in entries)
        {
            byClassId[entry.ClassId] = entry;
        }

        return new MonsterResistTable(byClassId, source);
    }

    public static MonsterResistTable Load(string path)
    {
        var file = JsonSerializer.Deserialize<MonsterResistFile>(File.ReadAllText(path), SerializerOptions);
        return FromEntries(file.Monsters, file.Source);
    }

    public void Save(string path)
    {
        var file = new MonsterResistFile { Source = Source, Monsters = [.. _byClassId.Values] };
        file.Monsters.Sort((left, right) => left.ClassId.CompareTo(right.ClassId));
        File.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool TryGet(NPCCode code, out MonsterResistEntry entry)
        => _byClassId.TryGetValue((int)code, out entry);

    /// <summary>
    /// The monster's resistance after the boss modifiers it spawned with. Returns false when the
    /// class is not in the table, which is the caller's cue that it does not know rather than that
    /// the monster is vulnerable.
    /// </summary>
    public bool TryGetResist(
        NPCCode code,
        Difficulty difficulty,
        ResistType type,
        IEnumerable<MonsterEnchantment> enchantments,
        out int resist)
    {
        if (!TryGet(code, out var entry))
        {
            resist = 0;
            return false;
        }

        var total = entry.Base(type, difficulty) + MonsterEnchantmentResists.Bonus(enchantments, type);

        // The floor is real: nothing in the game takes a monster below -100.
        resist = total < -100 ? -100 : total;
        return true;
    }

    /// <summary>
    /// Whether the element is wasted on this monster. On 1.09 immunity is absolute - Conviction and
    /// Lower Resist only started breaking immunities in 1.10 - so an immune target will not take
    /// the damage no matter what the party brings.
    /// </summary>
    public bool IsImmune(
        NPCCode code,
        Difficulty difficulty,
        ResistType type,
        IEnumerable<MonsterEnchantment> enchantments)
        => TryGetResist(code, difficulty, type, enchantments, out var resist) && resist >= ImmuneAt;

    private sealed class MonsterResistFile
    {
        public MonsterResistSource Source { get; set; }
        public List<MonsterResistEntry> Monsters { get; set; } = [];
    }
}
