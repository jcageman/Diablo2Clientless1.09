using D2NG.Core;
using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Mule;

/// <summary>
/// Reads what the character a client plays wears and is made of, so mule items can be judged
/// against it without the armory.
/// </summary>
public static class GearSnapshot
{
    private static readonly Dictionary<Attribute, string> BaseResists = new()
    {
        [Attribute.FireResistance] = nameof(StatType.FireResistance),
        [Attribute.ColdResistance] = nameof(StatType.ColdResistance),
        [Attribute.LightningResistance] = nameof(StatType.LightningResistance),
        [Attribute.PoisonResistance] = nameof(StatType.PoisonResistance),
        [Attribute.MaxFireResistance] = nameof(StatType.MaximumFireResistance),
        [Attribute.MaxColdResistance] = nameof(StatType.MaximumColdResistance),
        [Attribute.MaxLightningResistance] = nameof(StatType.MaximumLightningResistance),
        [Attribute.MaxPoisonResistance] = nameof(StatType.MaximumPoisonResistance),
    };

    private static readonly HashSet<ClassificationType> Charms = [ClassificationType.SmallCharm, ClassificationType.LargeCharm, ClassificationType.GrandCharm];

    /// <summary>Null while the client does not know who it is yet.</summary>
    public static GearCharacterDb Take(Game game, string accountName, string characterName)
    {
        var me = game.Me;
        if (me == null)
        {
            return null;
        }

        int Attribute(Attribute attribute) => me.Attributes.TryGetValue(attribute, out var value) ? value : 0;

        var equipped = new Dictionary<string, MuleItemDb>();
        foreach (var item in me.Equipment.Values)
        {
            var slot = GearSlots.Of(item.Directory);
            if (slot != null)
            {
                equipped[slot] = item.MapToMuleItem(accountName, characterName);
            }
        }

        return new GearCharacterDb
        {
            Id = MuleCharacterDb.MakeId(accountName, characterName),
            AccountName = MuleNames.Normalize(accountName),
            CharacterName = MuleNames.Normalize(characterName),
            Class = me.Class.ToString(),
            Level = Attribute(Core.D2GS.Players.Attribute.Level),
            Expansion = game.ClientCharacter?.IsExpansion ?? false,
            SeenAt = DateTimeOffset.UtcNow,
            Strength = Attribute(Core.D2GS.Players.Attribute.Strength),
            Dexterity = Attribute(Core.D2GS.Players.Attribute.Dexterity),
            Vitality = Attribute(Core.D2GS.Players.Attribute.Vitality),
            Energy = Attribute(Core.D2GS.Players.Attribute.Energy),
            BaseLife = Attribute(Core.D2GS.Players.Attribute.MaxLife) >> 8,
            BaseMana = Attribute(Core.D2GS.Players.Attribute.MaxMana) >> 8,
            BaseStats = BaseResists.Where(r => Attribute(r.Key) != 0).ToDictionary(r => r.Value, r => Attribute(r.Key)),
            HardSkills = me.Skills.Where(s => s.Value > 0).ToDictionary(s => s.Key.ToString(), s => s.Value),
            Equipped = equipped,
            Charms = game.Inventory.Items.Where(i => Charms.Contains(i.Classification)).Select(i => i.MapToMuleItem(accountName, characterName)).ToList(),
        };
    }
}
