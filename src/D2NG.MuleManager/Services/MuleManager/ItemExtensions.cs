using D2NG.Core.D2GS.Items;
using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using System.Linq;

namespace D2NG.MuleManager.Services.MuleManager
{
    public static class ItemExtensions
    {
        public static MuleItem MapToMuleItem(this Item item, MuleManagerAccount account, Character character)
        {
            return new MuleItem
            {
                Id = $"{account.Name}-{character.Name}-{item.Id}",
                AccountName = account.Name,
                CharacterName = character.Name,
                ItemName = item.Name.ToString(),
                QualityType = item.Quality.ToString(),
                ClassificationType = item.Classification.ToString(),
                Stats = item.Properties.ToDictionary(k => k.Key.ToString(), v => v.Value.Value)
            };
        }
    }
}
