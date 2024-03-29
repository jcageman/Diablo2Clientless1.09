﻿using D2NG.Core.D2GS.Items;
using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.MuleManager.Models;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.MuleManager.Services.MuleManager
{
    public static class ItemExtensions
    {
        public static MuleItemDb MapToMuleItem(this Item item, MuleManagerAccount account, Character character)
        {
            return new MuleItemDb
            {
                Id = $"{account.Name}-{character.Name}-{item.Id}",
                AccountName = account.Name,
                CharacterName = character.Name,
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
}
