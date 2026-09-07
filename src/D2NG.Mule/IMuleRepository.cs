using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace D2NG.Mule;

public interface IMuleRepository
{
    Task<MuleCharacterDb> GetCharacter(string accountName, string characterName);

    Task UpdateCharacter(string accountName, string characterName, MuleCharacterSnapshot snapshot);

    Task<List<MuleItemDb>> GetAllItemsOfCharacter(string accountName, string characterName);

    Task<List<MuleItemDb>> GetAllItems(QualityType? qualityType, ItemName? itemName, StatType[] statTypes, ClassificationType? classificationType);
}
