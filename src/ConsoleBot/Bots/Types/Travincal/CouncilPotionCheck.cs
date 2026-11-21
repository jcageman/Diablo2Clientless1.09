namespace ConsoleBot.Bots.Types.Travincal;

public enum CouncilPotionAction
{
    /// <summary>The belt holds a healing and a mana potion.</summary>
    Fight,

    /// <summary>A kind the belt lacks is in the inventory: move it into the belt.</summary>
    RefillBelt,

    /// <summary>A kind the belt lacks is nowhere on the character: restock in town first.</summary>
    Town,
}

/// <summary>
/// What the barbarian does before the council fight about its potions. A fight started with no healing potion in the
/// belt disengages at once, and one with no mana potion stands back once the whirls drain it.
/// </summary>
public static class CouncilPotionCheck
{
    public static CouncilPotionAction Decide(int beltHealing, int beltMana, int inventoryHealing, int inventoryMana)
    {
        var healingMissing = beltHealing == 0;
        var manaMissing = beltMana == 0;
        if (!healingMissing && !manaMissing)
        {
            return CouncilPotionAction.Fight;
        }

        if ((!healingMissing || inventoryHealing > 0) && (!manaMissing || inventoryMana > 0))
        {
            return CouncilPotionAction.RefillBelt;
        }

        return CouncilPotionAction.Town;
    }
}
