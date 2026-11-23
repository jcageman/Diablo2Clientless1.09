using D2NG.Core;
using D2NG.Core.ItemData;
using D2NG.Mule;
using D2NG.Mule.Models;
using D2NG.Mule.Upgrades;
using D2NG.MuleManager.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.Stock;

/// <summary>Everything the database knows, read at once and kept for a short while: 30,000 items read in well under a second, but not per keystroke.</summary>
public sealed record StockSnapshot(List<MuleItemDb> Items, List<MuleCharacterDb> Mules, List<GearCharacterDb> Gear, DateTimeOffset ReadAt);

public sealed class StockCache : IDisposable
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(30);
    private readonly IMuleRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StockSnapshot _snapshot;

    public StockCache(IMuleRepository repository)
    {
        _repository = repository;
    }

    public void Invalidate() => _snapshot = null;

    public void Dispose() => _gate.Dispose();

    public async Task<StockSnapshot> Get()
    {
        var snapshot = _snapshot;
        if (snapshot != null && DateTimeOffset.UtcNow - snapshot.ReadAt < KeepFor)
        {
            return snapshot;
        }

        await _gate.WaitAsync();
        try
        {
            if (_snapshot != null && DateTimeOffset.UtcNow - _snapshot.ReadAt < KeepFor)
            {
                return _snapshot;
            }

            _snapshot = new StockSnapshot(
                await _repository.GetAllItems(null, null, [], null),
                await _repository.GetAllCharacters(),
                await _repository.GetAllGear(),
                DateTimeOffset.UtcNow);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The game tables, read once from <c>item-affixes.json</c>; null when the file is not there.</summary>
public class GearTablesProvider
{
    private readonly Lazy<GearTables> _tables;

    public GearTablesProvider(IOptions<MuleManagerConfiguration> configuration)
    {
        var directory = configuration.Value.DataDirectory;
        _tables = new Lazy<GearTables>(() =>
        {
            if (!string.IsNullOrEmpty(directory))
            {
                GameDataLocation.Use(directory);
            }

            if (!GameDataLocation.TryResolve(GameDataLocation.ItemAffixesFile, out var path))
            {
                Log.Warning("{File} not found; the upgrade finder runs without requirements and roll ranges", GameDataLocation.ItemAffixesFile);
                return null;
            }

            return new GearTables(ItemAffixTable.Load(path));
        });
    }

    public GearTables Tables => _tables.Value;
}

/// <summary>The presets from the configuration, or the built-in ones.</summary>
public class PresetProvider
{
    public PresetProvider(IOptions<MuleManagerConfiguration> configuration)
    {
        var configured = configuration.Value.Presets;
        Presets = configured is { Count: > 0 } ? configured : [.. UpgradePresets.Defaults];
    }

    public IReadOnlyList<UpgradePreset> Presets { get; }
}
