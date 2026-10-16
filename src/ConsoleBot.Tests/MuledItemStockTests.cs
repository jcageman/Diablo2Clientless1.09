using ConsoleBot.Mule;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using D2NG.Mule;

namespace ConsoleBot.Tests;

public class MuledItemStockTests
{
    [Fact]
    public void ToItem_KeepsWhatThePickitLimitsMatchOn()
    {
        var original = new Item
        {
            Id = 7,
            Name = ItemName.ColossusBlade,
            Quality = QualityType.Superior,
            Classification = ClassificationType.Sword,
            Ethereal = true,
            Sockets = 3,
            Level = 85,
            Properties = []
        };
        original.Properties[StatType.EnhancedDamage] = new ItemProperty { Type = StatType.EnhancedDamage, Value = 12 };

        var item = MuledItemStock.ToItem(original.MapToMuleItem("account", "character"));

        Assert.Equal(ItemName.ColossusBlade, item.Name);
        Assert.Equal(QualityType.Superior, item.Quality);
        Assert.Equal(ClassificationType.Sword, item.Classification);
        Assert.True(item.Ethereal);
        Assert.Equal(3u, item.Sockets);
        Assert.Equal(85u, item.Level);
        Assert.True(item.IsIdentified);
        Assert.Equal(12, item.Properties[StatType.EnhancedDamage].Value);
    }

    [Fact]
    public void ToItem_RestoresSkillAndSkillTabStats()
    {
        var original = new Item { Name = ItemName.GrandCharm, Quality = QualityType.Magical, Properties = [] };
        original.Properties[StatType.SkillTab1] = new ItemProperty { Type = StatType.SkillTab1, Value = 1, SkillTab = SkillTab.SorceressColdSpells };
        original.Properties[StatType.SingleSkill1] = new ItemProperty { Type = StatType.SingleSkill1, Value = 2, Skill = Skill.FrozenOrb };

        var item = MuledItemStock.ToItem(original.MapToMuleItem("account", "character"));

        Assert.Equal(1, item.GetValueToSkillTab(SkillTab.SorceressColdSpells));
        var skill = Assert.Single(item.Properties.Values, p => p.Type == StatType.SingleSkill1);
        Assert.Equal(Skill.FrozenOrb, skill.Skill);
        Assert.Equal(2, skill.Value);
    }
}
