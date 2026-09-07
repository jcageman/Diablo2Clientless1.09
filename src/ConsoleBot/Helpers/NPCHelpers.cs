using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace ConsoleBot.Helpers;

public static class NPCHelpers
{
    private static readonly HashSet<NPCCode> FriendlyNPCs = [ 
        NPCCode.MephistoGhost, NPCCode.ATrap1, NPCCode.ATrap2, NPCCode.ATrap3, NPCCode.ATrap4,
        NPCCode.ATrap5, NPCCode.ATrap6, NPCCode.ATrap7, NPCCode.Hydra1, NPCCode.Hydra2, NPCCode.Hydra3, NPCCode.CompellingOrb,
        NPCCode.ClayGolem, NPCCode.BloodGolem, NPCCode.FireGolem, NPCCode.IronGolem, NPCCode.Valkyrie,
        NPCCode.Act1Npc, NPCCode.Guard, NPCCode.BaalThrone, NPCCode.BaalTentacle1, NPCCode.BaalTentacle2, NPCCode.BaalTentacle3, NPCCode.BaalTentacle4, NPCCode.BaalTentacle5];
    /// <summary>
    /// Whether an NPC is on our side: a hireling, a summon, a town guard or scenery that never dies.
    /// </summary>
    /// <remarks>
    /// Anything that answers true here must never be counted as something to kill or wait for. A hired
    /// mercenary follows the character everywhere, so treating it as a monster makes a clear-the-area
    /// check that can never finish.
    /// </remarks>
    public static bool IsFriendly(NPCCode npcCode) => FriendlyNPCs.Contains(npcCode);

    public static WorldObject GetUniqueNPC(Game game, NPCCode npcCode)
    {
        return game.GetNPCsByCode(npcCode).FirstOrDefault();
    }

    public static bool RepairItemsAndBuyArrows(Game game, WorldObject npc)
    {
        Log.Information($"Repairing items");
        GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.MoveTo(npc);
            if (game.Me.Location.Distance(npc.Location) < 5)
            {
                return game.InteractWithNPC(npc);
            }
            return false;
        }, TimeSpan.FromSeconds(3));

        Thread.Sleep(50);
        game.InitiateEntityChat(npc);

        game.TownFolkAction(npc, TownFolkActionType.Trade);

        game.RepairItems(npc);

        if (ShouldBuyArrows(game))
        {
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
             {
                 return game.Items.Values.Any(i => i.IsInMerchantTab() && i.Name == ItemName.Arrows);
             }, TimeSpan.FromSeconds(3)))
            {
                Log.Warning($"Did not find arrows at {npc.NPCCode} {game.Me.Location}");
                game.TerminateEntityChat(npc);
                return false;
            }

            Log.Information($"Refreshing arrows at {npc.NPCCode} {game.Me.Location}");
            var arrows = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == ItemName.Arrows);
            var numberOfArrows = game.Inventory.Items.Count(i => i.Name == ItemName.Arrows);
            while (numberOfArrows < 5 && game.Inventory.FindFreeSpace(arrows) != null)
            {
                game.BuyItem(npc, arrows, false);
                numberOfArrows += 1;
            }
        }

        BuyMagicItemsAtMerchant(game, npc);

        Thread.Sleep(50);
        game.TerminateEntityChat(npc);
        Thread.Sleep(50);
        return true;
    }

    public static bool GambleItems(Game game, WorldObject npc)
    {
        Log.Information($"Gambling items");
        bool moveResult = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(npc.Location) >= 2)
            {
                game.MoveTo(npc);
                return false;
            }
            return true;
        }, TimeSpan.FromSeconds(3));

        if (!moveResult)
        {
            Log.Debug("Moving to npc for gamble failed");
            return false;
        }

        bool result = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(npc.Location) < 5)
            {
                return game.InteractWithNPC(npc);
            }
            else
            {
                game.MoveTo(npc);
            }
            return false;
        }, TimeSpan.FromSeconds(3));

        if (!result)
        {
            Log.Debug("Interacting with npc for gamble failed");
            return false;
        }

        Thread.Sleep(50);
        game.InitiateEntityChat(npc);
        game.TownFolkAction(npc, TownFolkActionType.Gamble);
        var oldItems = game.Items.Values.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();
        while (game.Me.Attributes.TryGetValue(D2NG.Core.D2GS.Players.Attribute.GoldInStash, out var goldInStash)
            && goldInStash > 200000)
        {
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
             {
                 var newItems = game.Items.Values.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();
                 return newItems.Except(oldItems).Any();
             }, TimeSpan.FromSeconds(1)))
            {
                Log.Debug("Waiting for items failed");
                return false;
            }

            Thread.Sleep(10);
            Log.Debug("Trying to find gamble items and sell previous onces");
            bool inventoryFull = GambleCurrentItemsAtNpc(game, npc);

            oldItems = game.Items.Values.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();
            game.TownFolkAction(npc, TownFolkActionType.RefreshGamble);
            if (inventoryFull)
            {
                break;
            }
        }

        Thread.Sleep(50);
        game.TerminateEntityChat(npc);
        Thread.Sleep(50);

        return true;
    }

    private static bool GambleCurrentItemsAtNpc(Game game, Entity npc)
    {
        var inventoryItemsToSell = game.Inventory.Items.Where(i => !D2NG.Pickit.Pickit.ShouldKeepItem(game, i)).ToList();
        foreach (Item item in inventoryItemsToSell)
        {
            if (item.Quality == QualityType.Rare)
            {
                Log.Information($"Selling item {item.GetFullDescription()}");
            }

            game.SellItem(npc, item);
        }

        var inventoryFull = false;

        foreach (var gambleItem in game.Items.Values.Where(i => i.Container == ContainerType.ArmorTab && D2NG.Pickit.Pickit.ShouldGamble(game.Me, i)))
        {
            if (game.Inventory.FindFreeSpace(gambleItem) == null)
            {
                Log.Information($"Inventory full, not gambling anymore");
                inventoryFull = true;
                break;
            }

            Log.Debug($"Gambling item {gambleItem.GetFullDescription()}");

            var oldUnidentifiedItems = game.Inventory.Items.Where(i => !i.IsIdentified).ToHashSet();

            game.GambleItem(npc, gambleItem);
            var identifiedItems = new HashSet<uint>();
            bool identifyResult = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                var newUnidentifiedItems = game.Inventory.Items.Where(i => !i.IsIdentified).ToHashSet();
                var deltaItems = newUnidentifiedItems.Except(oldUnidentifiedItems).ToList();
                if (deltaItems.Count > 0)
                {
                    var gambledItem = deltaItems.First();
                    if (!identifiedItems.Contains(gambledItem.Id))
                    {
                        identifiedItems.Add(gambledItem.Id);
                        game.IdentifyGambleItem(gambledItem);
                    }
                }

                if (game.Inventory.Items.Any(i => i.IsIdentified && identifiedItems.Contains(i.Id)))
                {
                    return true;
                }

                return false;
            }, TimeSpan.FromSeconds(2));

            if (!identifyResult)
            {
                Log.Debug($"Identify item {gambleItem.GetFullDescription()} for gamble failed");
                break;
            }
        }

        return inventoryFull;
    }

    public static NPCCode GetDeckardCainForAct(D2NG.Core.D2GS.Act.Act act)
    {
        return act switch
        {
            D2NG.Core.D2GS.Act.Act.Act1 => NPCCode.DeckardCainAct1,
            D2NG.Core.D2GS.Act.Act.Act2 => NPCCode.DeckardCainAct2,
            D2NG.Core.D2GS.Act.Act.Act3 => NPCCode.DeckardCainAct3,
            D2NG.Core.D2GS.Act.Act.Act4 => NPCCode.DeckardCainAct4,
            D2NG.Core.D2GS.Act.Act.Act5 => NPCCode.DeckardCainAct5,
            _ => throw new InvalidEnumArgumentException(nameof(act)),
        };
    }

    public static NPCCode GetMercNPCForAct(D2NG.Core.D2GS.Act.Act act)
    {
        return act switch
        {
            D2NG.Core.D2GS.Act.Act.Act1 => NPCCode.Kashya,
            D2NG.Core.D2GS.Act.Act.Act2 => NPCCode.Greiz,
            D2NG.Core.D2GS.Act.Act.Act3 => NPCCode.Asheara,
            D2NG.Core.D2GS.Act.Act.Act4 => NPCCode.TyraelAct4,
            D2NG.Core.D2GS.Act.Act.Act5 => NPCCode.QualKehk,
            _ => throw new InvalidEnumArgumentException(nameof(act)),
        };
    }

    public static NPCCode GetSellNPC(D2NG.Core.D2GS.Act.Act act)
    {
        return act switch
        {
            D2NG.Core.D2GS.Act.Act.Act1 => NPCCode.Akara,
            D2NG.Core.D2GS.Act.Act.Act2 => NPCCode.Drognan,
            D2NG.Core.D2GS.Act.Act.Act3 => NPCCode.Ormus,
            D2NG.Core.D2GS.Act.Act.Act4 => NPCCode.JamellaAct4,
            D2NG.Core.D2GS.Act.Act.Act5 => NPCCode.Malah,
            _ => throw new InvalidEnumArgumentException(nameof(act)),
        };
    }

    /// <summary>
    /// The NPC that restores life and mana for free just for being talked to. Distinct from
    /// <see cref="GetSellNPC"/> because act 2 heals at Fara rather than at the potion seller.
    /// </summary>
    public static NPCCode GetHealNPC(D2NG.Core.D2GS.Act.Act act)
    {
        return act switch
        {
            D2NG.Core.D2GS.Act.Act.Act1 => NPCCode.Akara,
            D2NG.Core.D2GS.Act.Act.Act2 => NPCCode.Fara,
            D2NG.Core.D2GS.Act.Act.Act3 => NPCCode.Ormus,
            D2NG.Core.D2GS.Act.Act.Act4 => NPCCode.JamellaAct4,
            D2NG.Core.D2GS.Act.Act.Act5 => NPCCode.Malah,
            _ => throw new InvalidEnumArgumentException(nameof(act)),
        };
    }

    /// <summary>
    /// Walks to the town healer and talks to it, which costs nothing and refills life and mana.
    /// This is the only healing a rushee can afford: a level one carries no potions and no gold,
    /// so without it a hurt rushee stays hurt until something kills it.
    /// </summary>
    public static bool HealAtHealer(Game game)
    {
        var healerCode = GetHealNPC(game.Act);
        if (!GeneralHelpers.TryWithTimeout(
            (_) => game.GetNPCsByCode(healerCode).Count != 0,
            TimeSpan.FromSeconds(3)))
        {
            Log.Warning($"{healerCode} was not visible to {game.Me.Name} to heal at");
            return false;
        }

        var healer = GetUniqueNPC(game, healerCode);
        if (healer == null)
        {
            return false;
        }

        var reached = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(healer.Location) > 2)
            {
                game.MoveTo(healer);
            }

            if (game.Me.Location.Distance(healer.Location) < 5)
            {
                Thread.Sleep(100);
                return game.InteractWithNPC(healer);
            }
            return false;
        }, TimeSpan.FromSeconds(5));

        if (!reached)
        {
            Log.Warning($"{game.Me.Name} could not reach {healerCode} at {healer.Location} from {game.Me.Location}");
            return false;
        }

        var lifeBefore = game.Me.Life;
        Thread.Sleep(50);
        game.InitiateEntityChat(healer);
        Thread.Sleep(300);
        game.TerminateEntityChat(healer);
        Thread.Sleep(200);

        Log.Information(
            $"{game.Me.Name} healed at {healerCode}: {lifeBefore} -> {game.Me.Life} of {game.Me.MaxLife} life");
        return true;
    }

    public static NPCCode GetGambleNPC(D2NG.Core.D2GS.Act.Act act)
    {
        return act switch
        {
            D2NG.Core.D2GS.Act.Act.Act1 => NPCCode.Gheed,
            D2NG.Core.D2GS.Act.Act.Act2 => NPCCode.Elzix,
            D2NG.Core.D2GS.Act.Act.Act3 => NPCCode.Alkor,
            D2NG.Core.D2GS.Act.Act.Act4 => NPCCode.JamellaAct4,
            D2NG.Core.D2GS.Act.Act.Act5 => NPCCode.Anya,
            _ => throw new InvalidEnumArgumentException(nameof(act)),
        };
    }

    public static NPCCode GetRepairNPC(D2NG.Core.D2GS.Act.Act act)
    {
        return act switch
        {
            D2NG.Core.D2GS.Act.Act.Act1 => NPCCode.Charsi,
            D2NG.Core.D2GS.Act.Act.Act2 => NPCCode.Fara,
            D2NG.Core.D2GS.Act.Act.Act3 => NPCCode.Hratli,
            D2NG.Core.D2GS.Act.Act.Act4 => NPCCode.Halbu,
            D2NG.Core.D2GS.Act.Act.Act5 => NPCCode.Larzuk,
            _ => throw new InvalidEnumArgumentException(nameof(act)),
        };
    }

    public static bool IdentifyItemsAtDeckardCain(Game game)
    {
        if (game.Inventory.Items.All(i => i.IsIdentified) && game.Cube.Items.All(i => i.IsIdentified))
        {
            return true;
        }

        var deckhardCainCode = GetDeckardCainForAct(game.Act);

        var result1 = GeneralHelpers.TryWithTimeout((retryCount) => game.GetNPCsByCode(deckhardCainCode).Count != 0, TimeSpan.FromSeconds(2));
        if (!result1)
        {
            return false;
        }

        var deckardCain = GetUniqueNPC(game, deckhardCainCode);
        if (deckardCain == null)
        {
            return false;
        }

        Log.Information($"Identifying items at Cain");
        var result2 = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(deckardCain.Location) > 2)
            {
                game.MoveTo(deckardCain);
            }

            if (game.Me.Location.Distance(deckardCain.Location) < 5)
            {
                Thread.Sleep(100);
                return game.InteractWithNPC(deckardCain);
            }
            return false;
        }, TimeSpan.FromSeconds(4));

        if (!result2)
        {
            Log.Error($"Failed to interact with Cain at location {deckardCain.Location} while at location {game.Me.Location}");
            return false;
        }

        Thread.Sleep(50);
        game.InitiateEntityChat(deckardCain);
        Thread.Sleep(50);
        game.IdentifyItems(deckardCain);
        Thread.Sleep(50);
        game.TerminateEntityChat(deckardCain);
        Thread.Sleep(50);
        return true;
    }

    public static bool ResurrectMerc(Game game, WorldObject npc)
    {
        var result2 = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(npc.Location) > 2)
            {
                game.MoveTo(npc);
            }

            if (game.Me.Location.Distance(npc.Location) < 5)
            {
                Thread.Sleep(100);
                return game.InteractWithNPC(npc);
            }
            return false;
        }, TimeSpan.FromSeconds(4));

        if (!result2)
        {
            Log.Error($"Failed to interact with MercOwner at location {npc.Location} while at location {game.Me.Location}");
            return false;
        }

        Thread.Sleep(50);
        game.InitiateEntityChat(npc);
        Thread.Sleep(50);
        game.ResurrectMerc(npc);
        Thread.Sleep(50);
        game.TerminateEntityChat(npc);
        Thread.Sleep(50);
        return true;
    }

    public static bool ShouldRefreshCharacterAtNPC(Game game, TownManagementOptions options)
    {
        return game.Belt.Height * options.AccountConfig.HealthSlots.Count - game.Belt.NumOfHealthPotions() > 1
            || game.Belt.Height * options.AccountConfig.ManaSlots.Count - game.Belt.NumOfManaPotions() > 1
            // A missing tome has to count as needing a trip: with null propagation an absent tome compares
            // the same as a full one, so losing it used to go unnoticed until a portal was needed.
            || game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeOfTownPortal) is not { Amount: >= 5 }
            || IsBelowHealthPotionBar(game, options);
    }

    /// <summary>
    /// Whether life has dropped past the point at which the character would be drinking. Tied to the
    /// chicken configuration rather than a number of its own: the healer is free, so anything the
    /// belt would be spent on is worth a walk to the NPC instead.
    /// </summary>
    public static bool IsBelowHealthPotionBar(Game game, TownManagementOptions options)
    {
        if (game.Me.MaxLife == 0)
        {
            return false;
        }

        var bar = options?.AccountConfig?.Chicken?.UseHealthPotionPercent ?? 0.7;
        return game.Me.Life / (double)game.Me.MaxLife < bar;
    }

    public static bool ShouldGoToRepairNPC(Game game)
    {
        bool shouldRepair = game.Me.Equipment.Values.Any(i => i.MaximumDurability > 0 && ((double)i.Durability / i.MaximumDurability) < 0.2);
        bool shouldBuyArrows = false;
        if (game.Me.Class == CharacterClass.Amazon)
        {
            shouldBuyArrows = ShouldBuyArrows(game);
            if (!shouldRepair
                && (!game.Me.Equipment.TryGetValue(DirectoryType.RightHand, out var javalin)
                || (javalin.Classification == ClassificationType.Javelin && javalin.Amount < 300)))
            {
                shouldRepair = true;
            }
        }

        return shouldRepair || shouldBuyArrows;
    }

    private static bool ShouldBuyArrows(Game game)
    {
        return game.Me.Class == CharacterClass.Amazon
            && game.Inventory.Items.Count(i => i.Name == ItemName.Arrows) <= 2
            && game.Me.Equipment.TryGetValue(DirectoryType.RightHand, out var weapon)
            && weapon.Classification == ClassificationType.Bow;
    }

    public static bool SellItemsAndRefreshPotionsAtNPC(Game game, WorldObject npc, TownManagementOptions options)
    {
        // Checked, and given long enough to actually arrive. Discarding this meant a character that
        // never reached the merchant went on to chat and trade with her from wherever it had got
        // to: the shop then held nothing, and every purchase failed without saying so - the town
        // portal tomes included, which is what strands the portal character a few minutes later.
        if (!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(npc.Location) >= 2)
            {
                game.MoveTo(npc);
            }

            if (game.Me.Location.Distance(npc.Location) < 5)
            {
                return game.InteractWithNPC(npc);
            }

            return false;
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning("{Character} never reached {NPCCode} to trade, {Distance:F0} away at {Location} - skipping the shop rather than buying from nowhere",
                game.Me.Name, npc.NPCCode, game.Me.Location.Distance(npc.Location), game.Me.Location);
            return false;
        }

        Thread.Sleep(50);
        game.InitiateEntityChat(npc);

        game.TownFolkAction(npc, TownFolkActionType.Trade);
        Item healingPotion = null;
        Item manaPotion = null;

        // Longer than the three seconds this used to allow, and the trade is asked for again along
        // the way. The stock arrives in its own packet, and when the server is slow it had not come
        // by the time this gave up - which is not a shop with nothing in it, though the code went on
        // to treat it as one. Everything bought here then failed quietly, the town portal tomes
        // included, and the run died later for want of a tome nobody could see had not been bought.
        if (!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (retryCount > 0 && retryCount % 10 == 0)
            {
                game.TownFolkAction(npc, TownFolkActionType.Trade);
            }

            healingPotion = game.Items.Values.Where(i => i.IsInMerchantTab() && i.Type.StartsWith("hp", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => (int)i.Type.Last()).FirstOrDefault();
            manaPotion = game.Items.Values.Where(i => i.IsInMerchantTab() && i.Type.StartsWith("mp", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => (int)i.Type.Last()).FirstOrDefault();
            return healingPotion != null && manaPotion != null;
        }, TimeSpan.FromSeconds(12)))
        {
            Log.Warning($"Did not find healing or mana potions at {npc.NPCCode} {game.Me.Location} after 12s, {game.Items.Values.Count(i => i.IsInMerchantTab())} items in the merchant tab - nothing bought here will have worked");
        }

        // The potion reserve is bought a few lines below; selling it here and buying it straight
        // back would be a round trip for nothing.
        var inventoryItemsToSell = game.Inventory.Items
            .Where(i => !InventoryHelpers.IsDrinkable(i)
                && !D2NG.Pickit.Pickit.ShouldKeepItem(game, i)
                && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i))
            .ToList();
        var cubeItemsToSell = game.Cube.Items.Where(i => !D2NG.Pickit.Pickit.ShouldKeepItem(game, i) && !D2NG.Pickit.Pickit.IsReservedItem(i)).ToList();
        Log.Debug($"Selling {inventoryItemsToSell.Count} inventory items and {cubeItemsToSell.Count} cube items");

        foreach (Item item in inventoryItemsToSell)
        {
            Log.Information($"Selling inventory item {item.GetFullDescription()}");
            game.SellItem(npc, item);
        }

        foreach (Item item in cubeItemsToSell)
        {
            Log.Information($"Selling cube item {item.GetFullDescription()}");
            game.SellItem(npc, item);
        }

        // Only the town portal tome. Identification happens at Deckard Cain, so an identify tome would be
        // bought, carried and never read.
        RestockTome(game, npc, ItemName.TomeOfTownPortal, ItemName.ScrollofTownPortal);

        if(healingPotion != null)
        {
            var numberOfHealthPotions = options.HealthPotionsToBuy ?? game.Belt.Height * options.AccountConfig.HealthSlots.Count - game.Belt.NumOfHealthPotions();
            // Buying is fire and forget: with no gold every call fails and the character walks out
            // with an empty belt looking like it shopped. Report what it actually came away with.
            var wanted = numberOfHealthPotions;
            var gained = BuyPotions(game, npc, "hp", wanted);
            if (gained < wanted)
            {
                Log.Warning($"{game.Me.Name} wanted {wanted} {healingPotion.Name} but only got {gained}, {game.Inventory.FreeCellCount()} free inventory cells");
            }
            else
            {
                Log.Information($"{game.Me.Name} bought {gained} {healingPotion.Name}, leaving town with belt {game.Belt.NumOfHealthPotions()}, inventory {game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion)}, {game.Inventory.FreeCellCount()} free cells");
            }
        }

        if(manaPotion != null)
        {
            var numberOfManaPotions = options.ManaPotionsToBuy ?? game.Belt.Height * options.AccountConfig.ManaSlots.Count - game.Belt.NumOfManaPotions();
            BuyPotions(game, npc, "mp", numberOfManaPotions);
        }

        if (options.ItemsToBuy != null)
        {
            foreach (var additionalBuy in options.ItemsToBuy)
            {
                var additionalItem = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == additionalBuy.Key);
                for (var i = 0; i < additionalBuy.Value; ++i)
                {
                    game.BuyItem(npc, additionalItem, false);
                }
            }
        }

        BuyMagicItemsAtMerchant(game, npc);

        Thread.Sleep(50);
        game.TerminateEntityChat(npc);
        Thread.Sleep(50);
        game.TerminateEntityChat(npc);
        return true;
    }

    /// <summary>
    /// Buys potions one at a time, waiting for each to arrive before ordering the next. Buying is
    /// fire and forget and the server does not keep up with a burst: an order of seven sent 30ms
    /// apart landed one potion, which is how characters reached the cow level with an empty belt.
    /// The stock is looked up each time as well, though a potion vendor keeps the same item id.
    /// </summary>
    private static long BuyPotions(Game game, WorldObject npc, string typePrefix, long wanted)
    {
        if (wanted <= 0)
        {
            return 0;
        }

        var start = CountPotions(game, typePrefix);

        // One stack buy fills the belt in a single action. It only works for the belt, so whatever
        // is still wanted after it goes to the inventory and has to be bought one at a time.
        var stock = FindPotionInStock(game, typePrefix);
        if (stock != null)
        {
            var before = CountPotions(game, typePrefix);
            game.BuyItem(npc, stock, buyStack: true);
            GeneralHelpers.TryWithTimeout((_) => CountPotions(game, typePrefix) > before, TimeSpan.FromSeconds(2));
        }

        while (CountPotions(game, typePrefix) - start < wanted)
        {
            stock = FindPotionInStock(game, typePrefix);
            if (stock == null)
            {
                Log.Warning($"{game.Me.Name} found no more {typePrefix} potions to buy at {npc.NPCCode}");
                break;
            }

            var before = CountPotions(game, typePrefix);
            game.BuyItem(npc, stock, buyStack: false);
            if (!GeneralHelpers.TryWithTimeout((_) => CountPotions(game, typePrefix) > before, TimeSpan.FromSeconds(1)))
            {
                break;
            }
        }

        return CountPotions(game, typePrefix) - start;
    }

    private static Item FindPotionInStock(Game game, string typePrefix)
    {
        return game.Items.Values
            .Where(i => i.IsInMerchantTab() && i.Type.StartsWith(typePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => (int)i.Type.Last())
            .FirstOrDefault();
    }

    /// <summary>Potions of this type held anywhere the character can drink from.</summary>
    private static int CountPotions(Game game, string typePrefix)
    {
        return game.Inventory.Items.Count(i => i.Type.StartsWith(typePrefix, StringComparison.OrdinalIgnoreCase))
            + game.Belt.Items.Count(i => i.Type.StartsWith(typePrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Makes sure the character owns a tome and that it is full. Buys the tome itself when there is none:
    /// a character that has lost its tome of town portal cannot make a portal, and every attempt to leave
    /// an area fails until somebody notices.
    /// </summary>
    private static void RestockTome(Game game, WorldObject npc, ItemName tomeName, ItemName scrollName)
    {
        var tome = game.Inventory.Items.FirstOrDefault(i => i.Name == tomeName);
        if (tome == null)
        {
            var tomeForSale = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == tomeName);
            if (tomeForSale == null)
            {
                Log.Warning($"{game.Me.Name} has no {tomeName} and {npc.NPCCode} does not stock one");
                return;
            }

            Log.Information($"{game.Me.Name} has no {tomeName}, buying one from {npc.NPCCode}");
            game.BuyItem(npc, tomeForSale, false);
            if (!GeneralHelpers.TryWithTimeout(
                (_) => game.Inventory.Items.Any(i => i.Name == tomeName),
                TimeSpan.FromSeconds(3)))
            {
                Log.Warning($"{game.Me.Name} failed to buy a {tomeName}, it may be out of gold or out of space");
                return;
            }

            tome = game.Inventory.Items.FirstOrDefault(i => i.Name == tomeName);
        }

        var scroll = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == scrollName);
        if (tome != null && scroll != null && tome.Amount < 100)
        {
            game.BuyItem(npc, scroll, true);
        }
    }

    public static IEnumerable<WorldObject> GetNearbyNPCs(Client client, Point point, int numberOfEnemies, int distance)
    {
        return client.Game.WorldObjects
        .Where(w => w.Key.Item2 == EntityType.NPC && !FriendlyNPCs.Contains(w.Value.NPCCode) && w.Value.State != EntityState.Dead && w.Value.State != EntityState.Dieing && w.Value.Location.Distance(point) < distance)
        .OrderBy(w => w.Value.Location.Distance(point))
        .Take(numberOfEnemies)
        .Select(w => w.Value);
    }

    public static List<WorldObject> GetNearbyCorpses(Client client, Point point, int numberOfEnemies)
    {
        return client.Game.WorldObjects
        .Where(w => w.Key.Item2 == EntityType.NPC
            && w.Value.State == EntityState.Dead
            && !w.Value.Effects.Contains(EntityEffect.CorpseNoDraw)
            && w.Value.Location.Distance(point) < 7)
        .OrderBy(w => w.Value.Location.Distance(point))
        .Take(numberOfEnemies)
        .Select(w => w.Value)
        .ToList();
    }

    public static IEnumerable<WorldObject> GetNearbySuperUniques(Client client, Point point, double distance = 40.0)
    {
        return client.Game.WorldObjects
        .Where(w => w.Key.Item2 == EntityType.NPC
            && w.Value.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique)
            && w.Value.Location.Distance(point) < distance)
        .Select(w => w.Value);
    }

    public static IEnumerable<WorldObject> GetNearbySuperUniques(Client client, double distance = 40.0)
    {
        return GetNearbySuperUniques(client, client.Game.Me.Location, distance);
    }

    private static void BuyMagicItemsAtMerchant(Game game, WorldObject npc)
    {
        var merchantItemsToBuy = game.Items.Values.Where(i => i.IsInMerchantTab() && D2NG.Pickit.Pickit.ShouldKeepItem(game, i)).ToList();
        if (merchantItemsToBuy.Count > 0)
        {
            foreach (Item item in merchantItemsToBuy)
            {
                Log.Information($"Buying item {item.GetFullDescription()} from {npc.NPCCode}");
                game.BuyItem(npc, item, false);
            }
        }
    }
}
