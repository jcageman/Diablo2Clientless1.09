using D2NG.Core.D2GS.Enums;
using System.Collections.Generic;

namespace D2NG.Core.MonsterData;

/// <summary>
/// The resistance a boss modifier adds on top of the monster's base resistances. 1.09 has no
/// monumod.txt - the table lives in D2Game.dll - so these come from 1.10's monumod.txt, which
/// matches every community source for the elemental modifiers. The Stone Skin figure is the one
/// sources disagree on (+50 or +80); nothing yet depends on physical immunity.
/// </summary>
public static class MonsterEnchantmentResists
{
    private static readonly Dictionary<MonsterEnchantment, (ResistType Type, int Bonus)[]> Bonuses = new()
    {
        [MonsterEnchantment.MagicResistant] =
        [
            (ResistType.Fire, 40), (ResistType.Cold, 40), (ResistType.Lightning, 40)
        ],
        [MonsterEnchantment.FireEnchanted] = [(ResistType.Fire, 75)],
        [MonsterEnchantment.LightningEnchanted] = [(ResistType.Lightning, 75)],
        [MonsterEnchantment.ColdEnchanted] = [(ResistType.Cold, 75)],
        [MonsterEnchantment.SpectralHit] =
        [
            (ResistType.Fire, 20), (ResistType.Cold, 20), (ResistType.Lightning, 20)
        ],
        [MonsterEnchantment.StoneSkin] = [(ResistType.Physical, 50)]
    };

    public static int Bonus(MonsterEnchantment enchantment, ResistType type)
    {
        if (!Bonuses.TryGetValue(enchantment, out var entries))
        {
            return 0;
        }

        var total = 0;
        foreach (var entry in entries)
        {
            if (entry.Type == type)
            {
                total += entry.Bonus;
            }
        }

        return total;
    }

    public static int Bonus(IEnumerable<MonsterEnchantment> enchantments, ResistType type)
    {
        if (enchantments == null)
        {
            return 0;
        }

        var total = 0;
        foreach (var enchantment in enchantments)
        {
            total += Bonus(enchantment, type);
        }

        return total;
    }
}
