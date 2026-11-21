using System;

namespace ConsoleBot.Bots.Types.Cows;

public enum BattleOrdersStance
{
    /// <summary>Has battle orders, or has waited long enough: fights as usual.</summary>
    Fight,

    /// <summary>Goes to the battle orders barbarian, defending itself only on the way.</summary>
    GoToLead,

    /// <summary>The barbarian is away or dead: holds where it is, defending itself only, until he is back.</summary>
    Hold,
}

/// <summary>What a rejoined member knows when it decides whether it may fight yet.</summary>
public readonly record struct BattleOrdersSituation(
    bool Rejoined,
    bool HasBattleOrders,
    bool LeadPlaced,
    bool InPartyWithLead,
    TimeSpan WaitedInShoutReach,
    TimeSpan SinceArrival);

/// <summary>
/// A party member back from a rejoin has none of the barbarian's shouts, and half its life with them.
/// Two members came back like that, walked straight into the fight they had left, and left again three
/// seconds after arriving, one of them at 114 life. So it fights only once it has battle orders.
/// </summary>
public static class BattleOrdersWait
{
    /// <summary>How long it waits within the barbarian's shout reach for battle orders he does not cast.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);

    /// <summary>How long it waits in all, from arriving: past this it fights whatever kept the shouts away.</summary>
    public static readonly TimeSpan OverallCap = TimeSpan.FromSeconds(45);

    public static BattleOrdersStance Decide(BattleOrdersSituation s)
    {
        if (!s.Rejoined || s.HasBattleOrders || s.SinceArrival >= OverallCap)
        {
            return BattleOrdersStance.Fight;
        }

        if (!s.LeadPlaced)
        {
            return BattleOrdersStance.Hold;
        }

        // Outside his party his shouts never land; waiting for them would hold it all game.
        if (!s.InPartyWithLead)
        {
            return BattleOrdersStance.Fight;
        }

        return s.WaitedInShoutReach < MaxWait ? BattleOrdersStance.GoToLead : BattleOrdersStance.Fight;
    }
}
