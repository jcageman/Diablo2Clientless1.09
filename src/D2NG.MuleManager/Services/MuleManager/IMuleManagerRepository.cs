using D2NG.Core.D2GS.Items;
using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.MuleManager.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager
{
    public interface IMuleManagerRepository
    {
        Task UpdateCharacter(MuleManagerAccount account, Character character, List<MuleItemDb> muleItems);

        Task<List<MuleItemDb>> GetAllItemsOfCharacter(MuleManagerAccount account, Character character);

        Task<List<MuleItemDb>> GetAllItems(QualityType? qualityType, ItemName? itemName, StatType[] statTypes);
    }
}
