using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager
{
    public interface IMuleManagerRepository
    {
        Task UpdateCharacter(MuleManagerAccount account, Character character, List<MuleItem> muleItems);

        Task<List<MuleItem>> GetAllItemsOfCharacter(MuleManagerAccount account, Character character);

        Task<List<MuleItem>> GetAllItems();
    }
}
