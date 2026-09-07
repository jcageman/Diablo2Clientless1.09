using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using Marten;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.Mule;

public class MuleRepository : IMuleRepository
{
    private readonly IDocumentStore _store;

    public MuleRepository(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<MuleCharacterDb> GetCharacter(string accountName, string characterName)
    {
        await using var session = _store.QuerySession();
        return await session.LoadAsync<MuleCharacterDb>(MuleCharacterDb.MakeId(accountName, characterName));
    }

    public async Task<List<MuleItemDb>> GetAllItems(QualityType? qualityType, ItemName? itemName, StatType[] statTypes, ClassificationType? classificationType)
    {
        await using var session = _store.QuerySession();
        var query = session.Query<MuleItemDb>().AsQueryable();
        if (statTypes.Length > 0)
        {
            var listStatTypes = statTypes.Select(s => s.ToString()).ToHashSet().ToList();
            foreach (var statType in listStatTypes)
            {
                query = query.Where(m => m.StatTypes.Contains(statType));
            }
        }
        if (itemName.HasValue)
        {
            query = query.Where(m => m.ItemName == itemName.ToString());
        }

        if (classificationType.HasValue)
        {
            query = query.Where(m => m.ClassificationType == classificationType.ToString());
        }

        if (qualityType.HasValue)
        {
            query = query.Where(m => m.QualityType == qualityType.ToString());
        }
        return (await query.ToListAsync()).ToList();
    }

    public async Task<List<MuleItemDb>> GetAllItemsOfCharacter(string accountName, string characterName)
    {
        await using var session = _store.QuerySession();
        return (await session
            .Query<MuleItemDb>()
            .Where(x => x.AccountName.Equals(accountName, StringComparison.OrdinalIgnoreCase) && x.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase))
            .ToListAsync()).ToList();
    }

    public async Task UpdateCharacter(string accountName, string characterName, MuleCharacterSnapshot snapshot)
    {
        await using var session = _store.LightweightSession();
        var itemsOfCharacter = await GetAllItemsOfCharacter(accountName, characterName);
        foreach (var item in itemsOfCharacter)
        {
            session.Delete(item);
        }

        session.Store<MuleItemDb>(snapshot.MapItems(accountName, characterName));
        session.Store(new MuleCharacterDb
        {
            Id = MuleCharacterDb.MakeId(accountName, characterName),
            AccountName = MuleNames.Normalize(accountName),
            CharacterName = MuleNames.Normalize(characterName),
            SeenAt = DateTimeOffset.UtcNow,
            FreeCells = snapshot.FreeCells,
            FitProfile = snapshot.FitProfile
        });

        await session.SaveChangesAsync();
    }
}
