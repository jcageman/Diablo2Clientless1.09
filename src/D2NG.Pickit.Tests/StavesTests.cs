using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;

namespace D2NG.Pickit.Tests;

public class StavesTests
{
    [Fact]
    public void Keep_Orb_LightningEs()
    {
        var item = TestItemFactory.Create(ClassificationType.SorceressOrb, ItemName.SwirlingCrystal, QualityType.Rare, identified: true);
        TestItemFactory.AddSkillTab(item, SkillTab.SorceressLightningSpells, 5);
        TestItemFactory.AddSingleSkill(item, Skill.EnergyShield, 1);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_Orb_LowSkills()
    {
        var item = TestItemFactory.Create(ClassificationType.SorceressOrb, ItemName.SwirlingCrystal, QualityType.Rare, identified: true);
        TestItemFactory.AddSkillTab(item, SkillTab.SorceressLightningSpells, 2);
        TestItemFactory.AddSingleSkill(item, Skill.EnergyShield, 1);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
