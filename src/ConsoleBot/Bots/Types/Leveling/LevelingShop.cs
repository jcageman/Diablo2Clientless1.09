using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// A leveling character's trades: sell what the party has no use for, buy what keeps it alive in the order
/// it matters - a way back to town, a way to identify, healing, mana - with whatever gold there is. The
/// farmers' town routine sells by their pickit, which kept nothing a level 5 character needs and sold the
/// starting town portal scroll for a single gold.
/// </summary>
public static class LevelingShop
{
    private static readonly HashSet<ClassificationType> NeverSold =
    [
        ClassificationType.HealthPotion, ClassificationType.ManaPotion, ClassificationType.RejuvenationPotion, ClassificationType.AntidotePotion,
        ClassificationType.Scroll, ClassificationType.Tome, ClassificationType.Key, ClassificationType.QuestItem,
        ClassificationType.BodyPart, ClassificationType.Gold, ClassificationType.Arrows, ClassificationType.Bolts
    ];

    /// <summary>
    /// The belt's columns plus a buffer in the inventory, about fifteen potions in all: dropped ones are kept and the
    /// shop tops the rest up, so a member does not run dry between town visits.
    /// </summary>
    public const int RejuvenationsKept = 10;

    public static int RejuvenationsHeld(D2NG.Core.Game game)
        => game.Belt.NumOfRejuvenationPotions() + game.Inventory.Items.Count(item => item.Classification == ClassificationType.RejuvenationPotion);

    public static int SmallRejuvenationsHeld(D2NG.Core.Game game)
        => game.Belt.GetRejuvenationPotions().Count(item => item.Name != ItemName.FullRejuvenationPotion)
            + game.Inventory.Items.Count(item => item.Name == ItemName.RejuvenationPotion);

    public static int HealingKeep(PartyMember member)
        => (int)member.Game.Belt.Height * 2 + (IsCaster(member) ? 8 : 12);

    /// <summary>
    /// The hammerdin carries the most: one potion fills his whole bar, and in Nightmare Travincal he drank the ten he
    /// had in the first minutes and then hammered with under 15% mana for 60% of a twenty-minute fight.
    /// </summary>
    public static int ManaKeep(PartyMember member)
        => Whirls(member) ? WhirlerManaKeep : !IsCaster(member) ? 3 : (int)member.Game.Belt.Height + (member.Plan.Class == CharacterClass.Paladin ? 14 : 8);

    /// <summary>
    /// A whirl costs the barbarian a sixth of his mana: kept at three potions, he spent a tenth of his Chaos Sanctuary fights
    /// under 15% mana, swinging a slow maul while decrepified or holding back from the pack. Drunk from the bag.
    /// </summary>
    private const int WhirlerManaKeep = 8;

    private static bool Whirls(PartyMember member)
        => member.Plan.Skills.Any(step => step.Skill == D2NG.Core.D2GS.Players.Skill.Whirlwind && step.Level > 1);

    /// <summary>Whoever fights with mana and buys potions for it: the casters and the whirlwind barbarian.</summary>
    public static bool UsesMana(PartyMember member) => IsCaster(member) || Whirls(member);

    /// <summary>
    /// Whoever fights with mana: the sorceress and necromancer, and the hammerdin, whose Blessed Hammer costs mana too -
    /// kept at three mana potions he ran dry and swung his mace at buzzards a dozen times a fight.
    /// </summary>
    public static bool IsCaster(PartyMember member) => member.Plan.Class is CharacterClass.Sorceress or CharacterClass.Necromancer
        || member.Plan.Skills.Any(step => step.Skill == D2NG.Core.D2GS.Players.Skill.BlessedHammer && step.Level > 1);

    /// <summary>How many of <paramref name="item"/>'s kind are more than the member keeps, when it would hold <paramref name="held"/>.</summary>
    public static int Surplus(PartyMember member, Item item, int held)
    {
        var game = member.Game;
        var hasTome = game.Inventory.Items.Any(owned => owned.Name == ItemName.TomeOfTownPortal);
        var hasIdentifyTome = game.Inventory.Items.Any(owned => owned.Name == ItemName.TomeofIdentify);
        var keep = item.Classification switch
        {
            ClassificationType.HealthPotion => HealingKeep(member),
            ClassificationType.ManaPotion => ManaKeep(member),
            ClassificationType.RejuvenationPotion => RejuvenationsKept,
            // Bought for Andariel's poison: once the party has left act 1 they are gold and inventory room.
            ClassificationType.AntidotePotion => game.Act == D2NG.Core.D2GS.Act.Act.Act1 ? int.MaxValue : 0,
            _ when item.Name == ItemName.ScrollofTownPortal => hasTome ? 0 : 2,
            _ when item.Name == ItemName.ScrollofIdentify => hasIdentifyTome ? 0 : 2,
            _ => int.MaxValue
        };
        return Math.Max(0, held - keep);
    }

    public static bool Sellable(Item item, bool keepsBows)
    {
        if (NeverSold.Contains(item.Classification) || item.IsEquipped || LevelingGear.IsQuestItem(item) || LevelingGems.ReservedForDiamonds(item)
            || (LevelingGems.Collecting && LevelingGems.IsDiamond(item)))
        {
            return false;
        }

        return !(keepsBows && item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow);
    }

    public static void Sell(PartyMember member, WorldObject npc, bool keepsBows, Func<Item, bool> keep)
    {
        var game = member.Game;
        if (!OpenTrade(game, npc))
        {
            return;
        }

        // Never unidentified: when the tome gave up, four rings, a Splint Mail and two Sharktooth Armors went for 900 gold each
        // without anyone knowing what they were. They wait for the next identify.
        foreach (var item in game.Inventory.Items.Where(item => Sellable(item, keepsBows) && !keep(item) && Known(item)).ToList())
        {
            game.SellItem(npc, item);
        }

        // Loot left in the cube, for want of room in the bag at the start of the visit, is sold from there. What still needs
        // identifying comes out once the sale has made room.
        foreach (var item in CubeLootToSell(game, keepsBows, keep))
        {
            game.SellItem(npc, item);
        }

        // Supplies above what the member keeps go too, the extra ones of each kind first.
        foreach (var group in game.Inventory.Items.Where(item => item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion
            or ClassificationType.Scroll or ClassificationType.AntidotePotion).GroupBy(item => item.Name).ToList())
        {
            var first = group.First();
            var held = first.Classification switch
            {
                ClassificationType.HealthPotion => InventoryHelpers.GetTotalHealthPotions(game),
                ClassificationType.ManaPotion => InventoryHelpers.GetTotalManaPotions(game),
                _ => group.Count()
            };
            foreach (var item in group.Take(Math.Min(group.Count(), Surplus(member, first, held))).ToList())
            {
                game.SellItem(npc, item);
                Thread.Sleep(150);
            }
        }

        // Selling is answered asynchronously; the gold has to land before anyone counts it.
        Thread.Sleep(500);
        game.TerminateEntityChat(npc);
    }

    private static bool Known(Item item) => item.IsIdentified || item.Quality < QualityType.Magical;

    private static List<Item> CubeLootToSell(Game game, bool keepsBows, Func<Item, bool> keep)
        => [.. game.Cube.Items.Where(item => Sellable(item, keepsBows) && !keep(item) && Known(item))];

    /// <summary>Something in the inventory or the cube goes to the merchant: an item not kept, a weapon to identify first, or spare supplies.</summary>
    public static bool HasSomethingToSell(PartyMember member, bool keepsBows, Func<Item, bool> keep)
    {
        var game = member.Game;
        if (game.Inventory.Items.Any(item => (Sellable(item, keepsBows) && !keep(item) && Known(item))
            || (LevelingGear.IsWeaponOrShield(item) && !item.IsIdentified && item.Quality >= QualityType.Magical))
            || CubeLootToSell(game, keepsBows, keep).Count > 0)
        {
            return true;
        }

        return game.Inventory.Items.Where(item => item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.Scroll
                or ClassificationType.AntidotePotion)
            .GroupBy(item => item.Name).Any(group =>
            {
                var first = group.First();
                var held = first.Classification switch
                {
                    ClassificationType.HealthPotion => InventoryHelpers.GetTotalHealthPotions(game),
                    ClassificationType.ManaPotion => InventoryHelpers.GetTotalManaPotions(game),
                    _ => group.Count()
                };
                return Surplus(member, first, held) > 0;
            });
    }

    /// <summary>A tome is refilled once it runs this low, not after every scroll read.</summary>
    private const int TomeRefillBelow = 10;

    /// <summary>The member is short of something the merchant sells: potions, tome scrolls or antidotes.</summary>
    public static bool NeedsSupplies(PartyMember member, bool usesMana, int antidotes)
    {
        var game = member.Game;
        if (InventoryHelpers.GetTotalHealthPotions(game) < HealingKeep(member)
            || (usesMana && InventoryHelpers.GetTotalManaPotions(game) < ManaKeep(member))
            || game.Inventory.Items.Count(item => item.Name == ItemName.AntidotePotion) < antidotes)
        {
            return true;
        }

        return LevelingGear.Gold(game) >= 300 && new[] { ItemName.TomeOfTownPortal, ItemName.TomeofIdentify }
            .Any(name => game.Inventory.Items.FirstOrDefault(item => item.Name == name) is not { } tome || tome.Amount < TomeRefillBelow);
    }

    /// <summary>Opens the merchant's trade, runs <paramref name="shopping"/> with it open, and closes it.</summary>
    public static async Task Browse(PartyMember member, WorldObject npc, Func<Task> shopping)
    {
        if (!OpenTrade(member.Game, npc))
        {
            return;
        }

        await shopping();
        member.Game.TerminateEntityChat(npc);
    }

    private static void RestockTome(PartyMember member, WorldObject npc, ItemName tomeName, ItemName scrollName)
    {
        var game = member.Game;
        var tome = game.Inventory.Items.FirstOrDefault(item => item.Name == tomeName);
        if (tome == null)
        {
            if (LevelingGear.Gold(game) < 1000 || game.Items.Values.FirstOrDefault(item => item.IsInMerchantTab() && item.Name == tomeName) is not { } forSale)
            {
                return;
            }

            game.BuyItem(npc, forSale, false);
            if (!GeneralHelpers.TryWithTimeout(_ => game.Inventory.Items.Any(item => item.Name == tomeName), TimeSpan.FromSeconds(2)))
            {
                return;
            }

            Log.Information("{Character} bought a {Tome} from {Npc}", member.Name, tomeName, npc.NPCCode);
        }

        // One stack purchase fills the tome, as the other bots buy it. Bought a scroll at a time, waiting to see the count
        // rise, the paladin got one scroll a visit - the client never saw his portal tome's count change - and ran out
        // mid-run, so no portal opened for the members back from their chickens.
        var scroll = game.Items.Values.FirstOrDefault(item => item.IsInMerchantTab() && item.Name == scrollName);
        if (scroll != null && LevelingGear.Gold(game) >= 300)
        {
            game.BuyItem(npc, scroll, true);
            Thread.Sleep(300);
        }
    }

    public static void Buy(PartyMember member, WorldObject npc, bool usesMana)
    {
        var game = member.Game;
        if (!OpenTrade(game, npc))
        {
            return;
        }

        // Every member carries both tomes: with only the leader's, a leader that chickened left the others to walk
        // home, and a member left behind walked up from Catacombs 4 while the party was already in town.
        RestockTome(member, npc, ItemName.TomeOfTownPortal, ItemName.ScrollofTownPortal);
        RestockTome(member, npc, ItemName.TomeofIdentify, ItemName.ScrollofIdentify);

        var hasTome = game.Inventory.Items.Any(item => item.Name == ItemName.TomeOfTownPortal && item.Amount > 0);
        if (!hasTome)
        {
            BuyUpTo(game, npc, ItemName.ScrollofTownPortal, 2);
        }

        if (!game.Inventory.Items.Any(item => item.Name == ItemName.TomeofIdentify && item.Amount > 0))
        {
            BuyUpTo(game, npc, ItemName.ScrollofIdentify, 2);
        }

        var healingWanted = Math.Max(0, HealingKeep(member) - game.Belt.NumOfHealthPotions() - game.Inventory.Items.Count(item => item.Classification == ClassificationType.HealthPotion));
        var healing = NPCHelpers.BuyPotions(game, npc, "hp", healingWanted);
        var mana = usesMana ? NPCHelpers.BuyPotions(game, npc, "mp", Math.Max(0, ManaKeep(member) - InventoryHelpers.GetTotalManaPotions(game))) : 0;
        Log.Information("{Character} bought {Healing} healing and {Mana} mana potions at {Npc}", member.Name, healing, mana, npc.NPCCode);
        game.TerminateEntityChat(npc);
    }

    /// <summary>Antidotes one by one into the inventory - a stack buy would fill the belt with them instead of healing potions.</summary>
    public static void BuyAntidotes(PartyMember member, WorldObject npc, int wanted)
    {
        var game = member.Game;
        if (!OpenTrade(game, npc))
        {
            return;
        }

        var bought = 0;
        while (game.Inventory.Items.Count(item => item.Name == ItemName.AntidotePotion) < wanted)
        {
            var stock = game.Items.Values.FirstOrDefault(item => item.IsInMerchantTab() && item.Name == ItemName.AntidotePotion);
            var before = game.Inventory.Items.Count(item => item.Name == ItemName.AntidotePotion);
            if (stock == null || game.Inventory.FindFreeSpace(stock) == null)
            {
                break;
            }

            game.BuyItem(npc, stock, buyStack: false);
            if (!GeneralHelpers.TryWithTimeout(_ => game.Inventory.Items.Count(item => item.Name == ItemName.AntidotePotion) > before, TimeSpan.FromSeconds(1)))
            {
                break;
            }

            bought++;
        }

        game.TerminateEntityChat(npc);
        Log.Information("{Character} bought {Count} antidote potions at {Npc}", member.Name, bought, npc.NPCCode);
    }

    /// <summary>A bow without arrows does nothing at all: one quiver, and a second when there is gold to spare.</summary>
    public static void BuyArrows(PartyMember member, WorldObject npc)
    {
        var game = member.Game;
        if (!OpenTrade(game, npc))
        {
            return;
        }

        var quiver = game.Items.Values.FirstOrDefault(item => item.IsInMerchantTab() && item.Classification == ClassificationType.Arrows);
        if (quiver == null)
        {
            Log.Warning("{Npc} sells no arrows", npc.NPCCode);
        }
        else
        {
            for (var bought = 0; bought < 2; bought++)
            {
                var before = game.Inventory.Items.Count(item => item.Classification == ClassificationType.Arrows);
                game.BuyItem(npc, quiver, buyStack: false);
                if (!GeneralHelpers.TryWithTimeout(_ => game.Inventory.Items.Count(item => item.Classification == ClassificationType.Arrows) > before, TimeSpan.FromSeconds(1)))
                {
                    break;
                }
            }

            Log.Information("{Character} carries {Quivers} quivers of arrows", member.Name, game.Inventory.Items.Count(item => item.Classification == ClassificationType.Arrows));
        }

        game.TerminateEntityChat(npc);
    }

    private static void BuyUpTo(Game game, WorldObject npc, ItemName name, int wanted)
    {
        var held = game.Inventory.Items.Count(item => item.Name == name);
        var stock = game.Items.Values.FirstOrDefault(item => item.IsInMerchantTab() && item.Name == name);
        for (var bought = held; stock != null && bought < wanted; bought++)
        {
            var before = game.Inventory.Items.Count(item => item.Name == name);
            game.BuyItem(npc, stock, buyStack: false);
            if (!GeneralHelpers.TryWithTimeout(_ => game.Inventory.Items.Count(item => item.Name == name) > before, TimeSpan.FromSeconds(1)))
            {
                return;
            }
        }
    }

    private static bool OpenTrade(Game game, WorldObject npc)
    {
        if (!GeneralHelpers.TryWithTimeout(_ =>
        {
            if (game.Me.Location.Distance(npc.Location) >= 2)
            {
                game.MoveTo(npc);
            }

            return game.Me.Location.Distance(npc.Location) < 5 && game.InteractWithNPC(npc);
        }, TimeSpan.FromSeconds(20)))
        {
            Log.Warning("{Character} never reached {Npc} to trade", game.Me.Name, npc.NPCCode);
            return false;
        }

        Thread.Sleep(50);
        game.InitiateEntityChat(npc);
        game.TownFolkAction(npc, TownFolkActionType.Trade);
        var stockSeen = 0;
        var stockSince = DateTime.Now;
        return GeneralHelpers.TryWithTimeout(retry =>
        {
            if (retry > 0 && retry % 10 == 0)
            {
                game.TownFolkAction(npc, TownFolkActionType.Trade);
            }

            var stock = game.Items.Values.Count(item => item.IsInMerchantTab());
            if (stock != stockSeen)
            {
                stockSeen = stock;
                stockSince = DateTime.Now;
            }

            return stock > 0 && DateTime.Now - stockSince > TimeSpan.FromMilliseconds(300);
        }, TimeSpan.FromSeconds(12));
    }
}
