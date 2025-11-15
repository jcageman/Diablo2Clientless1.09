using D2NG.Core.D2GS.Items;
using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.MuleManager.Models;
using Marten;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager;

public class MuleManagerRepository : IMuleManagerRepository
{
    private readonly IDocumentStore _store;

    public MuleManagerRepository(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<List<MuleItemDb>> GetAllItems(QualityType? qualityType, ItemName? itemName,StatType[] statTypes, ClassificationType? classificationType)
    {
        var query = _store.QuerySession()
            .Query<MuleItemDb>().AsQueryable();
        if(statTypes.Length > 0)
        {
            var listStatTypes = statTypes.Select(s => s.ToString()).ToHashSet().ToList();
            foreach(var statType in listStatTypes)
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
        return (await query
            .ToListAsync()).ToList();
    }

    public async Task<List<MuleItemDb>> GetAllItemsOfCharacter(MuleManagerAccount account, Character character)
    {
        return (await _store.QuerySession()
            .Query<MuleItemDb>()
            .Where(x => x.AccountName == account.Name && x.CharacterName == character.Name).ToListAsync()).ToList();
    }

    public async Task UpdateCharacter(MuleManagerAccount account, Character character, List<MuleItemDb> muleItems)
    {
        var session = _store.LightweightSession();
        var itemsOfAccount = await GetAllItemsOfCharacter(account, character);
        foreach(var item in itemsOfAccount)
        {
            session.Delete(item);
        }

        session.Store<MuleItemDb>(muleItems);
        
        await session.SaveChangesAsync();
    }
}
