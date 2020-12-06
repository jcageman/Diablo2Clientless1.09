using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using Marten;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager
{
    public class MuleManagerRepository : IMuleManagerRepository
    {
        private readonly IDocumentSession _session;

        public MuleManagerRepository(IDocumentSession session)
        {
            _session = session;
        }

        public async Task<List<MuleItem>> GetAllItems()
        {
            return (await _session
                .Query<MuleItem>()
                .ToListAsync()).ToList();
        }

        public async Task<List<MuleItem>> GetAllItemsOfCharacter(MuleManagerAccount account, Character character)
        {
            return (await _session
                .Query<MuleItem>()
                .Where(x => x.AccountName == account.Name && x.CharacterName == character.Name).ToListAsync()).ToList();
        }

        public async Task UpdateCharacter(MuleManagerAccount account, Character character, List<MuleItem> muleItems)
        {
            var itemsOfAccount = await GetAllItemsOfCharacter(account, character);
            foreach(var item in itemsOfAccount)
            {
                _session.Delete(item);
            }

            _session.Store<MuleItem>(muleItems);
            
            await _session.SaveChangesAsync();
        }
    }
}
