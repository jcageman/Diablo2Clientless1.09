using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using D2NG.Mule;
using D2NG.Mule.Models;
using D2NG.Pickit;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

/// <summary>
/// What the mules hold, as pickit items, so a nip rule's '[maxquantity]' counts the stock on the
/// mules as well as what the character carries. Read from the mule database and refreshed in the
/// background; a pickit call never waits on the database.
/// </summary>
/// <remarks>
/// Only the accounts in this bot's own mule config count. The database also holds the classic
/// mules, whose items an expansion character cannot use until that mule is converted by hand; once
/// it is, adding it to the expansion mule config is what makes its stock count.
/// </remarks>
public class MuledItemStock
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

    private readonly IMuleRepository _repository;
    private readonly ILogger<MuledItemStock> _logger;
    private readonly HashSet<string> _accounts;
    private volatile MuledStock _stock = MuledStock.Empty;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private int _refreshing;

    public MuledItemStock(IMuleRepository repository, IEnumerable<string> accounts, ILogger<MuledItemStock> logger)
    {
        _repository = repository;
        _accounts = accounts.Select(MuleNames.Normalize).ToHashSet();
        _logger = logger;
    }

    /// <summary>
    /// The last loaded stock. Startup awaits a first <see cref="Refresh"/>, so it is empty only when
    /// no load has succeeded yet. Once it is ten minutes old a call starts a refresh in the
    /// background and gets the old stock until that refresh finishes.
    /// </summary>
    public MuledStock Current()
    {
        if (DateTimeOffset.UtcNow - _loadedAt >= RefreshEvery && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(Refresh);
        }

        return _stock;
    }

    public async Task Refresh()
    {
        try
        {
            var muled = await _repository.GetAllItems(null, null, [], null);
            var items = muled.Where(i => _accounts.Contains(MuleNames.Normalize(i.AccountName))).Select(ToItem).ToList();
            _stock = new MuledStock(items);
            _logger.LogInformation("Loaded {Count} muled items for the pickit limits", items.Count);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not load the muled items; pickit limits count only what the character holds");
        }
        finally
        {
            _loadedAt = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// The database keeps no item code; it comes back, with the item's size, from the item table by
    /// name, so one name shared by two codes gets the first. Skill and
    /// skill tab stats are stored by the skill's or tab's name and come back as the numbered skill
    /// stats the packet parser fills, so rules on them do.
    /// </summary>
    private static readonly StatType[] SkillStats = [StatType.SingleSkill1, StatType.SingleSkill2, StatType.SingleSkill3, StatType.SingleSkill4];
    private static readonly StatType[] SkillTabStats = [StatType.SkillTab1, StatType.SkillTab2, StatType.SkillTab3, StatType.SkillTab4, StatType.SkillTab5, StatType.SkillTab6];

    public static Item ToItem(MuleItemDb muled)
    {
        var item = new Item
        {
            Name = Enum.TryParse<ItemName>(muled.ItemName, out var name) ? name : default,
            Quality = Enum.TryParse<QualityType>(muled.QualityType, out var quality) ? quality : default,
            Classification = Enum.TryParse<ClassificationType>(muled.ClassificationType, out var classification) ? classification : default,
            Ethereal = muled.Ethereal,
            Sockets = muled.Sockets,
            Level = muled.Level,
            IsIdentified = true,
            Properties = []
        };

        if (ItemCatalog.TryFind(item.Name, out var code, out var width, out var height))
        {
            item.Type = code;
            item.Width = width;
            item.Height = height;
        }

        var skills = 0;
        var tabs = 0;
        foreach (var stat in muled.Stats ?? [])
        {
            if (Enum.TryParse<StatType>(stat.Type, out var statType))
            {
                item.Properties[statType] = new ItemProperty { Type = statType, Value = stat.Value };
            }
            else if (Enum.TryParse<Skill>(stat.Type, out var skill) && skills < SkillStats.Length)
            {
                var type = SkillStats[skills++];
                item.Properties[type] = new ItemProperty { Type = type, Value = stat.Value, Skill = skill };
            }
            else if (Enum.TryParse<SkillTab>(stat.Type, out var tab) && tabs < SkillTabStats.Length)
            {
                var type = SkillTabStats[tabs++];
                item.Properties[type] = new ItemProperty { Type = type, Value = stat.Value, SkillTab = tab };
            }
        }

        return item;
    }
}
