using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule;

public static class ItemExtensions
{
    public static MuleItemDb MapToMuleItem(this Item item, string accountName, string characterName)
    {
        accountName = MuleNames.Normalize(accountName);
        characterName = MuleNames.Normalize(characterName);
        return new MuleItemDb
        {
            Id = $"{accountName}-{characterName}-{item.Id}",
            AccountName = accountName,
            CharacterName = characterName,
            ItemName = item.Name.ToString(),
            QualityType = item.Quality.ToString(),
            ClassificationType = item.Classification.ToString(),
            Ethereal = item.Ethereal,
            Level = item.Level,
            Sockets = item.Sockets,
            Stats = item.Properties.Select(k => new StatDb { Type = k.MapToStatKey(), Value = k.Value.Value }).ToList(),
            StatTypes = item.Properties.Select(k => k.MapToStatKey()).ToList()
        };
    }

    public static string MapToStatKey(this KeyValuePair<StatType, ItemProperty> statTypeAndProperty)
    {
        switch (statTypeAndProperty.Key)
        {
            case StatType.SingleSkill1:
            case StatType.SingleSkill2:
            case StatType.SingleSkill3:
            case StatType.SingleSkill4:
                return statTypeAndProperty.Value.Skill.ToString();
            case StatType.SkillTab1:
            case StatType.SkillTab2:
            case StatType.SkillTab3:
            case StatType.SkillTab4:
            case StatType.SkillTab5:
                return statTypeAndProperty.Value.SkillTab.ToString();
            default:
                return statTypeAndProperty.Key.ToString();
        }
    }
}
