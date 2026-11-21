using D2NG.Core.D2GS.Objects;
using Serilog;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.MonsterData;

public readonly record struct MonsterSpeed(int Velocity, int Run, int MeleeRange, string Class = null);

public enum SpeedConfidence
{
    Unknown,
    FileOnly,
    Measured,
    RealmOverride,
}

/// <summary>
/// Monster walk speeds from the generated monster table (monster-resists.json), with the corrections
/// measured on the realm where it does not use the patch's monstats.bin value.
/// </summary>
public sealed class MonsterSpeedTable
{
    /// <summary>
    /// Velocity the realm uses where it differs from monstats.bin. Measured on the realm, not read from a
    /// file: Council Member 346 walks at ~8.6 subtiles/s, which is the txt's Velocity 8 (9.4/s, ratio
    /// 0.92 like other A* walkers), not the bin's 10 (11.7/s).
    /// </summary>
    public static IReadOnlyDictionary<int, int> RealmVelocityOverrides { get; } = new Dictionary<int, int>
    {
        [(int)NPCCode.CouncilMember2] = 8,
    };

    /// <summary>
    /// Classes whose walk speed was measured on the realm (Trace accuracy, n >= 10 clean walks) and came
    /// out within 10% of the prediction from the file's Velocity. Every other class's speed is the file
    /// value only, except the <see cref="RealmVelocityOverrides"/>. Not in it: 345, on which Extra Fast's bonus
    /// was fitted, walks at 0.89 of its file speed when named (10 walks), and 347 has no clean walk.
    /// </summary>
    public static IReadOnlySet<int> MeasuredClasses { get; } = new HashSet<int>
    {
        (int)NPCCode.Cadaver, (int)NPCCode.Zealot, (int)NPCCode.Heirophant1, (int)NPCCode.DeckardCainAct3, (int)NPCCode.Alkor,
        (int)NPCCode.Ormus, (int)NPCCode.Meshif2, 294, 296, (int)NPCCode.IronWolf,
    };

    private static readonly object SharedLock = new();
    private static volatile SharedTable _shared;

    private readonly Dictionary<int, MonsterSpeed> _byClassId;

    public static MonsterSpeedTable Empty { get; } = new(new Dictionary<int, MonsterSpeed>(), new Dictionary<int, int>());

    private MonsterSpeedTable(Dictionary<int, MonsterSpeed> byClassId, IReadOnlyDictionary<int, int> overrides)
    {
        _byClassId = byClassId;
        Overrides = overrides;
        HasData = byClassId.Values.Any(s => s.Velocity > 0);
    }

    public IReadOnlyDictionary<int, int> Overrides { get; }

    /// <summary>False when the table was missing or generated before speeds were extracted.</summary>
    public bool HasData { get; }

    public static MonsterSpeedTable From(MonsterResistTable table, IReadOnlyDictionary<int, int> overrides = null)
    {
        var byClassId = new Dictionary<int, MonsterSpeed>();
        foreach (var entry in table?.Entries ?? [])
        {
            byClassId[entry.ClassId] = new MonsterSpeed(entry.Velocity, entry.Run, entry.MeleeRange, entry.Class);
        }

        return new MonsterSpeedTable(byClassId, overrides ?? RealmVelocityOverrides);
    }

    public MonsterSpeedTable WithOverrides(IReadOnlyDictionary<int, int> overrides) => new(_byClassId, overrides ?? new Dictionary<int, int>());

    /// <summary>The speed the realm uses, override applied; false when the class is unknown or the table has no speeds.</summary>
    public bool TryGet(NPCCode code, out MonsterSpeed speed)
    {
        if (!HasData || !_byClassId.TryGetValue((int)code, out speed))
        {
            speed = default;
            return false;
        }

        if (Overrides.TryGetValue((int)code, out var velocity))
        {
            speed = speed with { Velocity = velocity };
        }

        return true;
    }

    public SpeedConfidence Confidence(NPCCode code)
    {
        if (!_byClassId.TryGetValue((int)code, out var speed) || speed.Velocity <= 0)
        {
            return SpeedConfidence.Unknown;
        }

        if (Overrides.ContainsKey((int)code))
        {
            return SpeedConfidence.RealmOverride;
        }

        return MeasuredClasses.Contains((int)code) ? SpeedConfidence.Measured : SpeedConfidence.FileOnly;
    }

    /// <summary>The value in monstats.bin, ignoring overrides.</summary>
    public bool TryGetFileValue(NPCCode code, out MonsterSpeed speed) => _byClassId.TryGetValue((int)code, out speed);

    /// <summary>
    /// The table in the game data folder, loaded on first use and again when the folder changes. Missing,
    /// unreadable or without speeds it has no data, which makes Simulated read the legacy position, and
    /// one warning is logged; a failed load is not retried until the folder changes.
    /// </summary>
    public static MonsterSpeedTable Shared
    {
        get
        {
            var folder = GameDataLocation.ConfiguredDirectory;
            var shared = _shared;
            if (shared != null && shared.Folder == folder)
            {
                return shared.Table;
            }

            lock (SharedLock)
            {
                if (_shared == null || _shared.Folder != folder)
                {
                    _shared = new SharedTable(folder, Load(GameDataLocation.TryResolve(GameDataLocation.MonsterResistsFile, out var path) ? path : null));
                }

                return _shared.Table;
            }
        }
    }

    private sealed record SharedTable(string Folder, MonsterSpeedTable Table);

    internal static MonsterSpeedTable Load(string path)
    {
        if (path == null)
        {
            Log.Warning("No {File} in the game data folder: simulated monster motion falls back to legacy. Generate it with MpqData", GameDataLocation.MonsterResistsFile);
            return Empty;
        }

        MonsterSpeedTable table;
        try
        {
            table = From(MonsterResistTable.Load(path));
        }
        catch (System.Exception e)
        {
            Log.Warning(e, "Could not read {Path}: simulated monster motion falls back to legacy", path);
            return Empty;
        }

        if (!table.HasData)
        {
            Log.Warning("{Path} has no monster speeds (generated before they were extracted): simulated monster motion falls back to legacy. Regenerate it with MpqData", path);
        }

        return table;
    }
}
