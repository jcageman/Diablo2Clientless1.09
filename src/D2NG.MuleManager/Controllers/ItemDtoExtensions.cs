using D2NG.MuleManager.Controllers.Models;
using D2NG.MuleManager.Services.MuleManager.Models;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.MuleManager.Controllers
{
    public static class ItemDtoExtensions
    {
        public static List<MuleItemDto> MapToDto(this List<MuleItemDb> muleItemsDb)
        {
            return muleItemsDb.Select(MapToDto).ToList();
        }

            public static MuleItemDto MapToDto(this MuleItemDb muleItemDb)
        {
            return new MuleItemDto
            {
                Id = muleItemDb.Id,
                AccountName = muleItemDb.AccountName,
                CharacterName = muleItemDb.CharacterName,
                ItemName = muleItemDb.ItemName,
                QualityType = muleItemDb.QualityType,
                ClassificationType = muleItemDb.ClassificationType,
                Ethereal = muleItemDb.Ethereal,
                Level = muleItemDb.Level,
                Sockets = muleItemDb.Sockets,
                Stats = muleItemDb.Stats.Select(MapToDto).ToList()
            };
        }

        public static StatDto MapToDto(this StatDb statDb)
        {
            return new StatDto
            {
                Type = statDb.Type,
                Value = statDb.Value
            };
        }
    }
}
