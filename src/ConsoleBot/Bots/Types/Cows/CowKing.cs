using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// The Cow King, for the weak walkers' distance: he and his guards are lightning enchanted, and the amazon
/// died shooting multiple shot into them from three units.
/// </summary>
public static class CowKing
{
    /// <summary>
    /// He arrives as an ordinary Hell Bovine with the super unique marker: superuniques.bin record 39 gives
    /// his base class as 391, and he is the only Hell Bovine super unique. His guards are boss minions.
    /// </summary>
    public static bool IsCowKing(NPCCode code, IReadOnlyCollection<MonsterEnchantment> enchantments, bool isBossMinion)
        => code == NPCCode.HellBovine && enchantments.Contains(MonsterEnchantment.IsSuperUnique) && !isBossMinion;
}
