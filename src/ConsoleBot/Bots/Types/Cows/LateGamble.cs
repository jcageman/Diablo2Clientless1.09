using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// Who gambles once the party has started rather than in the town visit before it. A gamble takes about 80s, and the
/// whole party stood waiting at the catacombs for the member doing it. A member the run does not hang on gets its
/// battle orders with the others, gambles while they go in, and comes into the level after them.
/// </summary>
public static class LateGamble
{
    /// <summary>
    /// Not the portal character, who opens both portals; not the one who created the game, the party's host; not the
    /// barbarian who gives the party its battle orders; not a weak walker, who would cross the level alone to the party,
    /// as a rejoin does not; not a killer, whom the party would hunt without for a minute and a half while still counting
    /// her as one. Those gamble before the run, with the party waiting. A member whose battle orders level is not known is
    /// taken for the one who gives them.
    /// </summary>
    /// <param name="battleOrders">Each account's battle orders level in account order, null where not yet seen.</param>
    public static bool Allowed(bool portalCharacter, bool createsGame, bool weakWalker, int ownIndex, IReadOnlyList<int?> battleOrders, bool killer = false)
        => !portalCharacter && !createsGame && !weakWalker && !killer
            && battleOrders[ownIndex] is int own
            && (own == 0 || BattleOrdersRank.AnotherGives(ownIndex, own, battleOrders));
}

/// <summary>Which barbarian gives the party its battle orders, as the bot picks its lead once the party is in.</summary>
public static class BattleOrdersRank
{
    /// <summary>Another member outranks this one: more battle orders, or as many and earlier in the accounts.</summary>
    /// <param name="battleOrders">Each account's battle orders level in account order, null where not yet seen.</param>
    public static bool AnotherGives(int ownIndex, int own, IReadOnlyList<int?> battleOrders)
        => battleOrders.Select((level, index) => (level, index))
            .Any(other => other.index != ownIndex && other.level is int level && (level > own || (level == own && other.index < ownIndex)));
}
