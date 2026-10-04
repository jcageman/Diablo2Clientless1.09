using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2NG.Core.ObjectData;

/// <summary>
/// The shrine objects of the game's own objects table, indexed by the object class id the server
/// sends and the map api reports. Generated on demand by the MpqData tool.
/// </summary>
/// <remarks>
/// Two things the bot can do with it. Preset shrines are part of the level layout, so their
/// positions come out of the map api for the current seed before anyone has seen them:
/// <see cref="Locations"/> turns the map's object dictionary into a list of shrines with their
/// fixed kind (health, mana, or rolled). Shrines spawned from a level's random object groups, such
/// as those in Travincal, are not in the map data and only show up as world objects once in view.
/// </remarks>
public sealed class ShrineTable
{
    private readonly Dictionary<int, ShrineEntry> _byClassId;

    public static ShrineTable Empty { get; } = new(new Dictionary<int, ShrineEntry>(), null);

    private ShrineTable(Dictionary<int, ShrineEntry> byClassId, ShrineSource source)
    {
        _byClassId = byClassId;
        Source = source;
    }

    /// <summary>Where the values came from, so a stale table can be spotted in the log.</summary>
    public ShrineSource Source { get; }

    public int Count => _byClassId.Count;

    public IEnumerable<ShrineEntry> Entries => _byClassId.Values;

    public static ShrineTable FromEntries(IEnumerable<ShrineEntry> entries, ShrineSource source)
    {
        var byClassId = new Dictionary<int, ShrineEntry>();
        foreach (var entry in entries)
        {
            byClassId[entry.ClassId] = entry;
        }

        return new ShrineTable(byClassId, source);
    }

    public static ShrineTable Load(string path)
    {
        var file = JsonSerializer.Deserialize<ShrineFile>(File.ReadAllText(path), SerializerOptions);
        return FromEntries(file.Shrines, file.Source);
    }

    public void Save(string path)
    {
        var file = new ShrineFile { Source = Source, Shrines = [.. _byClassId.Values] };
        file.Shrines.Sort((left, right) => left.ClassId.CompareTo(right.ClassId));
        File.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool IsShrine(EntityCode code) => _byClassId.ContainsKey((int)code);

    public bool TryGet(EntityCode code, out ShrineEntry entry)
        => _byClassId.TryGetValue((int)code, out entry);

    /// <summary>
    /// The kind of a shrine object, or false when the object is not a shrine. A world object with a
    /// <see cref="ShrineKind.Random"/> kind can be any <see cref="ShrineType"/>.
    /// </summary>
    public bool TryGetKind(EntityCode code, out ShrineKind kind)
    {
        if (_byClassId.TryGetValue((int)code, out var entry))
        {
            kind = entry.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>
    /// The preset shrines in a level, from the map api's object dictionary (object class id to
    /// positions). Objects that are not shrines are skipped.
    /// </summary>
    public IReadOnlyList<ShrineLocation> Locations(IReadOnlyDictionary<int, List<Point>> objects)
    {
        var shrines = new List<ShrineLocation>();
        if (objects == null)
        {
            return shrines;
        }

        foreach (var (classId, points) in objects)
        {
            if (!_byClassId.TryGetValue(classId, out var entry))
            {
                continue;
            }

            foreach (var point in points)
            {
                shrines.Add(new ShrineLocation((EntityCode)classId, entry.Kind, point));
            }
        }

        return shrines;
    }

    private sealed class ShrineFile
    {
        public ShrineSource Source { get; set; }
        public List<ShrineEntry> Shrines { get; set; } = [];
    }
}
