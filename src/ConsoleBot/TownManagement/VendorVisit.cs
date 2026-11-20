namespace ConsoleBot.TownManagement;

/// <summary>Whether a town visit walks to the vendor who sells potions.</summary>
public static class VendorVisit
{
    /// <summary>
    /// Visited for anything else it is needed for, and for healing potions alone only when more are short than
    /// <paramref name="healthPotionsNotWorthAVisit"/>. A visit made anyway buys every potion short.
    /// </summary>
    /// <param name="otherNeed">Selling, a belt or tome to refill, healing, items or mana potions to buy.</param>
    public static bool Needed(bool otherNeed, long? healthPotionsToBuy, long healthPotionsNotWorthAVisit)
        => otherNeed || healthPotionsToBuy > healthPotionsNotWorthAVisit;
}
