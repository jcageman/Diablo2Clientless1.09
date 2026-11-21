using ConsoleBot.Bots.Types;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.TownManagement;

public class TownManagementOptions
{
    public TownManagementOptions(AccountConfig accountConfig, Act act)
    {
        Act = act;
        AccountConfig = accountConfig;
    }

    public Act Act { get; private init; }
    public AccountConfig AccountConfig { get; private init; }
    public Dictionary<ItemName, int> ItemsToBuy { get; set; }
    public long? HealthPotionsToBuy { get; set; }

    public long? ManaPotionsToBuy { get; set; }

    /// <summary>
    /// A healing potion shortfall up to this many sends nobody to the vendor by itself; a visit made for anything else
    /// still buys it, and a belt short by two or more is a reason of its own. The cow followers walked to the vendor
    /// every game for the one potion they drink after battle orders, 6.5-8.3s of the setup with twenty spares carried.
    /// </summary>
    public long HealthPotionsNotWorthAVisit { get; set; } = DefaultHealthPotionsNotWorthAVisit;

    public const long DefaultHealthPotionsNotWorthAVisit = 2;

    public bool IdentifyWithTome { get; set; }

    /// <summary>Rejuvenations to keep in the inventory; anything above is sold at the vendor. Null keeps them all.</summary>
    public int? RejuvenationsToKeep { get; set; }

    /// <summary>
    /// Whether the vendor visit sorts the belt: potions out of columns not theirs, inventory potions into it. Off when the
    /// account config is a stand-in that does not know the character's belt layout.
    /// </summary>
    public bool TendBelt { get; set; } = true;

    /// <summary>
    /// Heal, potions and what cannot wait - repair when needed, a dead mercenary - and nothing else: for a
    /// character going straight back into a running game, where every second in town is one the party
    /// fights a member short.
    /// </summary>
    public bool TopUpOnly { get; set; }

    /// <summary>
    /// Leaves the gamble out, for the bot to run with <see cref="ITownManagementService.GambleIfDue"/> once nobody waits
    /// on this character: a gamble takes about 80s, and a party bot's whole party stood waiting for it.
    /// </summary>
    public bool GambleLater { get; set; }

    public bool Runs(TownStep step) => !(GambleLater && step == TownStep.Gamble)
        && (!TopUpOnly || step is TownStep.Refresh or TownStep.Repair or TownStep.ResurrectMerc);
}

public enum TownStep
{
    Identify,
    Refresh,
    SellRun,
    Repair,
    ResurrectMerc,
    Stash,
    Gamble,
}
