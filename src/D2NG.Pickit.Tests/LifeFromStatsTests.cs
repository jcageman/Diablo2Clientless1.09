using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;

namespace D2NG.Pickit.Tests;

/// <summary>
/// What an item's attributes are worth to a class, which the <c>[barbarianlife]</c> family of nip
/// keywords is built on.
/// </summary>
public class LifeFromStatsTests
{
    private static Item WithStats(int strength = 0, int dexterity = 0, int vitality = 0, int life = 0)
    {
        var item = new Item
        {
            Classification = ClassificationType.Boots,
            Quality = QualityType.Rare,
            Name = ItemName.WarBoots,
            IsIdentified = true,
            Properties = []
        };

        void Set(StatType type, int value)
        {
            if (value != 0)
            {
                item.Properties[type] = new ItemProperty { Type = type, Value = value };
            }
        }

        Set(StatType.Strength, strength);
        Set(StatType.Dexterity, dexterity);
        Set(StatType.Vitality, vitality);
        Set(StatType.Life, life);
        return item;
    }

    [Fact]
    public void Vitality_counts_towards_what_the_attributes_are_worth()
    {
        // The roll these rules most want to keep used to score nothing at all.
        Assert.Equal(40, WithStats(vitality: 10).GetTotalLifeFromStats(CharacterClass.Barbarian));
    }

    [Fact]
    public void A_barbarian_values_every_attribute_at_four()
    {
        Assert.Equal(60, WithStats(strength: 5, dexterity: 5, vitality: 5).GetTotalLifeFromStats(CharacterClass.Barbarian));
    }

    [Fact]
    public void Flat_life_is_counted_on_top_of_the_attributes()
    {
        Assert.Equal(60, WithStats(vitality: 5, life: 40).GetTotalLifeFromStats(CharacterClass.Barbarian));
    }

    [Theory]
    [InlineData(CharacterClass.Barbarian, 40)]
    [InlineData(CharacterClass.Amazon, 30)]
    [InlineData(CharacterClass.Paladin, 20)]
    [InlineData(CharacterClass.Sorceress, 20)]
    public void Each_class_weighs_an_attribute_its_own_way(CharacterClass characterClass, int expected)
    {
        Assert.Equal(expected, WithStats(vitality: 10).GetTotalLifeFromStats(characterClass));
    }
}
