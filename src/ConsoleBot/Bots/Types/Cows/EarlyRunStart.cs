using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>When the portal character may start her run before the rest of the party has prepared.</summary>
public static class EarlyRunStart
{
    /// <summary>Another party member as the portal character's start sees it.</summary>
    public readonly record struct Member(bool InGame, bool BattleOrders);

    /// <summary>
    /// With her own battle orders, once every other member has its own: what is left of the party's prepare is the
    /// walk back through her portal at the catacombs, shorter than her trip to her start in the level, so she does not
    /// stand there waiting. She lands beside a pack, two to twelve from the nearest cow in fifteen recorded games, so
    /// starting while members were still being shouted at would have kept her idle among cows for as long as that took.
    /// </summary>
    public static bool MayStart(bool ownBattleOrders, IEnumerable<Member> others)
        => ownBattleOrders && others.All(member => member.InGame && member.BattleOrders);
}
