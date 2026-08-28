using System.Collections.Generic;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Items;

namespace D2NG.Pickit.Tests;

internal static class TestItemFactory
{
    public static Item Create(ClassificationType classification, ItemName name, QualityType quality, bool identified = true, bool ethereal = false, uint sockets = 0, uint level = 0)
    {
        return new Item
        {
            Classification = classification,
            Name = name,
            Quality = quality,
            IsIdentified = identified,
            Ethereal = ethereal,
            Sockets = sockets,
            Level = level,
            Properties = []
        };
    }

    public static void AddStat(Item item, StatType stat, int value)
    {
        item.Properties[stat] = new ItemProperty { Type = stat, Value = value };
    }

    public static void AddSkillTab(Item item, SkillTab tab, int value, StatType slot = StatType.SkillTab1)
    {
        item.Properties[slot] = new ItemProperty { Type = slot, SkillTab = tab, Value = value };
    }

    public static void AddSingleSkill(Item item, Skill skill, int value, StatType slot = StatType.SingleSkill1)
    {
        item.Properties[slot] = new ItemProperty { Type = slot, Skill = skill, Value = value };
    }
}
