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
        // Hadriel stands at the River of Flame waypoint: the leveling party hit him for twelve minutes.
        NPCCode.MephistoGhost, NPCCode.IzualAct4, NPCCode.Hadriel, NPCCode.ATrap1, NPCCode.ATrap2, NPCCode.ATrap3, NPCCode.ATrap4,
        NPCCode.ATrap5, NPCCode.ATrap6, NPCCode.ATrap7, NPCCode.Hydra1, NPCCode.Hydra2, NPCCode.Hydra3, NPCCode.CompellingOrb,
        NPCCode.ClayGolem, NPCCode.BloodGolem, NPCCode.FireGolem, NPCCode.IronGolem, NPCCode.Valkyrie,
        NPCCode.Act1Npc, NPCCode.Guard, NPCCode.Navi, NPCCode.DeckardCainQuest, NPCCode.RogueScout, NPCCode.TyraelAct3, NPCCode.Natalya, NPCCode.Hireling1, NPCCode.Hireling2,
        NPCCode.BaalThrone, NPCCode.BaalTentacle1, NPCCode.BaalTentacle2, NPCCode.BaalTentacle3, NPCCode.BaalTentacle4, NPCCode.BaalTentacle5];
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

    /// <param name="stop">Ends the gamble between listings, for one made while the rest of a party is in the game.</param>
    public static bool GambleItems(Game game, WorldObject npc, Func<bool> stop = null)
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
        var listed = true;
        while (stop?.Invoke() != true
            && game.Me.Attributes.TryGetValue(D2NG.Core.D2GS.Players.Attribute.GoldInStash, out var goldInStash)
            && goldInStash > 200000)
        {
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
             {
                 var newItems = game.Items.Values.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();
                 return newItems.Except(oldItems).Any();
             }, TimeSpan.FromSeconds(1)))
            {
                // Closed below like any other end: a merchant window left open may make the server refuse what the
                // character does next, as an open waypoint menu refuses every portal.
                Log.Debug("Waiting for items failed");
                listed = false;
                break;
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

        return listed;
    }

    private static bool GambleCurrentItemsAtNpc(Game game, Entity npc)
    {
        var inventoryItemsToSell = InventoryHelpers.InventoryItemsToSell(game);
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

    /// <summary>Why the character needs the vendor for its belt, tome or life, in the order they are checked; none when it does not.</summary>
    public static IEnumerable<string> RefreshReasons(Game game, TownManagementOptions options)
    {
        var account = options.AccountConfig;
        int Short(PotionKind kind)
            => account.BeltTarget(kind, game.Belt.Height)
                - game.Belt.Items.Count(i => InventoryHelpers.KindOf(i) == kind && account.BeltColumns(kind).Contains(i.Location.X));
        if (Short(PotionKind.Healing) > 1)
        {
            yield return "belt healing";
        }

        if (Short(PotionKind.Mana) > 1)
        {
            yield return "belt mana";
        }

        // Left behind by the town-start cleanup for want of an inventory cell: the vendor sells first and so has room.
        if (InventoryHelpers.HasMisplacedPotionsForVendor(game, account))
        {
            yield return "misplaced potions";
        }

        // A missing tome has to count as needing a trip: with null propagation an absent tome compares
        // the same as a full one, so losing it used to go unnoticed until a portal was needed.
        if (game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeOfTownPortal) is not { Amount: >= 5 })
        {
            yield return "town portal tome";
        }

        if (IsBelowHealthPotionBar(game, options))
        {
            yield return "life";
        }
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
        // The merchant answers one character at a time, so a party shopping together queues: a sorceress next to
        // Malah went unanswered for the whole of another character's 6.5s visit and gave up a second after it.
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
        }, TimeSpan.FromSeconds(20)))
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
        var stockSeen = 0;
        var stockSince = DateTime.Now;

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
            if (healingPotion != null && manaPotion != null)
            {
                return true;
            }

            // A vendor that sells no potions (Anya) held the trade up the full twelve seconds; once her
            // stock has arrived and stopped growing there is nothing more to wait for.
            var stock = game.Items.Values.Count(i => i.IsInMerchantTab());
            if (stock != stockSeen)
            {
                stockSeen = stock;
                stockSince = DateTime.Now;
            }

            return stock > 0 && DateTime.Now - stockSince > TimeSpan.FromMilliseconds(300);
        }, TimeSpan.FromSeconds(12)))
        {
            Log.Warning($"Did not find healing or mana potions at {npc.NPCCode} {game.Me.Location} after 12s, {game.Items.Values.Count(i => i.IsInMerchantTab())} items in the merchant tab - nothing bought here will have worked");
        }

        // The potion reserve is bought a few lines below; selling it here and buying it straight
        // back would be a round trip for nothing.
        var inventoryItemsToSell = InventoryHelpers.InventoryItemsToSell(game);
        var cubeItemsToSell = InventoryHelpers.CubeItemsToSell(game);
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

        // Identifying at Cain leaves a tome two cells of nothing.
        if (!options.IdentifyWithTome)
        {
            foreach (var tome in game.Inventory.Items.Where(i => i.Name == ItemName.TomeofIdentify).ToList())
            {
                Log.Information("{Character} selling the tome of identify", game.Me.Name);
                game.SellItem(npc, tome);
            }
        }

        // Full rejuvenations were picked up without limit and filled the paladin's inventory to the
        // last cell; the cap on pickups stops the growth, this clears what is already there.
        if (options.RejuvenationsToKeep is int keep)
        {
            var surplus = game.Inventory.Items
                .Where(i => i.Classification == ClassificationType.RejuvenationPotion)
                .OrderBy(i => i.Name == ItemName.FullRejuvenationPotion ? 1 : 0)
                .Skip(keep)
                .ToList();
            foreach (var potion in surplus)
            {
                Log.Information("{Character} selling surplus {Potion}", game.Me.Name, potion.Name);
                game.SellItem(npc, potion);
            }
        }

        // Potions in the other kind's columns block the belt from being filled: out to the inventory, which the sales above
        // have just made room in. What still has no cell is drunk once the trade is closed.
        var account = options.AccountConfig;
        var misplacedLeft = new List<Item>();
        var spilled = 0;
        if (options.TendBelt)
        {
            var (moved, left) = InventoryHelpers.MoveMisplacedPotionsToInventory(game, account);
            misplacedLeft = left;
            InventoryHelpers.LogMisplacedPotions(game, moved, [], []);
            InventoryHelpers.FillBeltFromInventory(game, account);
        }

        // With no buy amount set the potions belong in the belt, and the belt's columns can be
        // partly taken by other potions. Buying the target minus what the belt holds then bought
        // the same shortfall every visit into the inventory: the portal sorceress carried fifty
        // healing potions, had no cell left for a town portal tome and could not open the portal
        // at all. The surplus goes back to the vendor before the tome is bought.
        SellPotionSurplus(game, npc, account, PotionKind.Healing, options.HealthPotionsToBuy);
        SellPotionSurplus(game, npc, account, PotionKind.Mana, options.ManaPotionsToBuy);

        RestockTome(game, npc, ItemName.TomeOfTownPortal, ItemName.ScrollofTownPortal);

        if (options.IdentifyWithTome)
        {
            RestockTome(game, npc, ItemName.TomeofIdentify, ItemName.ScrollofIdentify);
        }

        // Belt first, healing columns then mana columns, each from the inventory before buying, and only then the spares,
        // which go to the inventory. A stack buy fills every column the kind may enter, the other kind's empty ones included,
        // so the belt is bought into one potion at a time unless the placement rule says a stack stays in the kind's own columns.
        const long BeltOnly = long.MaxValue / 4;
        long gainedHealing = 0;
        long gainedMana = 0;
        if (options.TendBelt)
        {
            (gainedHealing, gainedMana) = InventoryHelpers.RefillBelt(game, account,
                healingPotion == null ? 0 : options.HealthPotionsToBuy ?? BeltOnly,
                manaPotion == null ? 0 : options.ManaPotionsToBuy ?? BeltOnly,
                (kind, budget) => BuyOneIntoBelt(game, npc, account, kind, budget));
        }

        // A spare goes wherever the server puts it: an empty column of the other kind takes it, and it then has to be moved
        // out, so spares stop once one would land there with no cell to move it to.
        bool SpareCanBeBought(PotionKind kind)
        {
            spilled += InventoryHelpers.MoveMisplacedPotionsToInventory(game, account).Moved.Count;
            var column = BeltPlacement.Column(InventoryHelpers.BeltPotions(game), (int)game.Belt.Height, kind);
            return column == null || account.BeltColumns(kind).Contains(column.Value) || InventoryHelpers.FreeCellForPotion(game) != null;
        }

        if (healingPotion != null && options.HealthPotionsToBuy is long healingSpares && options.TendBelt)
        {
            gainedHealing += BuySinglePotions(game, npc, "hp", healingSpares - gainedHealing, () => SpareCanBeBought(PotionKind.Healing));
        }

        if (manaPotion != null && options.ManaPotionsToBuy is long manaSpares && options.TendBelt)
        {
            gainedMana += BuySinglePotions(game, npc, "mp", manaSpares - gainedMana, () => SpareCanBeBought(PotionKind.Mana));
        }

        if (options.TendBelt)
        {
            spilled += InventoryHelpers.MoveMisplacedPotionsToInventory(game, account).Moved.Count;
            if (spilled > 0)
            {
                Log.Information("{Character} moved {Count} bought potions out of the other kind's belt columns", game.Me.Name, spilled);
            }

            InventoryHelpers.LogBlockedBeltFill(game, account, PotionKind.Healing);
            InventoryHelpers.LogBlockedBeltFill(game, account, PotionKind.Mana);
        }

        // Buying is fire and forget: with no gold every call fails and the character walks out
        // with an empty belt looking like it shopped. Report what it actually came away with.
        // Without a set amount, what was wanted is what came plus what the belt is still short.
        var wantedHealing = options.HealthPotionsToBuy ?? gainedHealing + InventoryHelpers.BeltRoom(game, account, PotionKind.Healing);
        var wantedMana = options.ManaPotionsToBuy ?? gainedMana + InventoryHelpers.BeltRoom(game, account, PotionKind.Mana);
        if (healingPotion != null)
        {
            if (gainedHealing < wantedHealing)
            {
                Log.Warning($"{game.Me.Name} wanted {wantedHealing} {healingPotion.Name} but only got {gainedHealing}, leaving town with belt {game.Belt.NumOfHealthPotions()}, inventory {game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion)}, {game.Inventory.FreeCellCount()} free cells");
            }
            else
            {
                Log.Information($"{game.Me.Name} bought {gainedHealing} {healingPotion.Name}, leaving town with belt {game.Belt.NumOfHealthPotions()}, inventory {game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion)}, {game.Inventory.FreeCellCount()} free cells");
            }
        }

        // Reported the same way as the healing buy above. Without this a half-filled mana belt is
        // invisible: a character leaving town with two mana potions instead of six read exactly
        // like one that needed none.
        if (manaPotion != null)
        {
            if (gainedMana < wantedMana)
            {
                Log.Warning($"{game.Me.Name} wanted {wantedMana} {manaPotion.Name} but only got {gainedMana}, leaving town with belt {game.Belt.NumOfManaPotions()}, inventory {game.Inventory.Items.Count(i => i.Classification == ClassificationType.ManaPotion)}, {game.Inventory.FreeCellCount()} free cells");
            }
            else
            {
                Log.Information($"{game.Me.Name} bought {gainedMana} {manaPotion.Name}, leaving town with belt {game.Belt.NumOfManaPotions()}, inventory {game.Inventory.Items.Count(i => i.Classification == ClassificationType.ManaPotion)}, {game.Inventory.FreeCellCount()} free cells");
            }
        }

        if (options.ItemsToBuy != null)
        {
            foreach (var additionalBuy in options.ItemsToBuy)
            {
                var additionalItem = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == additionalBuy.Key);

                // Both halves said nothing before. A stock list that has not arrived leaves this
                // null, and buying null does nothing at all - the portal character then walked to
                // Tristram with no town portal tome, could not get back, and the whole party waited
                // out its budget on a portal that was never coming. Not one line of it was logged.
                if (additionalItem == null)
                {
                    Log.Warning("{Character} wanted {Count} {Item} but {NPCCode} is not showing any, {TabItems} items in the merchant tab",
                        game.Me.Name, additionalBuy.Value, additionalBuy.Key, npc.NPCCode,
                        game.Items.Values.Count(i => i.IsInMerchantTab()));
                    continue;
                }

                // One at a time, waiting for each to land, exactly as potions are bought. Firing the
                // buys back to back looked like it worked and delivered nothing: the portal
                // character came away with zero town portal tomes from a merchant that was plainly
                // showing them, and lost the run an act later for want of one.
                var before = game.Inventory.Items.Count(i => i.Name == additionalBuy.Key);
                for (var i = 0; i < additionalBuy.Value; ++i)
                {
                    var had = game.Inventory.Items.Count(i2 => i2.Name == additionalBuy.Key);
                    game.BuyItem(npc, additionalItem, false);
                    if (!GeneralHelpers.TryWithTimeout(
                            (_) => game.Inventory.Items.Count(i2 => i2.Name == additionalBuy.Key) > had,
                            TimeSpan.FromSeconds(2)))
                    {
                        // Re-read the stock: the merchant's entry can be replaced between buys.
                        additionalItem = game.Items.Values.FirstOrDefault(i2 => i2.IsInMerchantTab() && i2.Name == additionalBuy.Key);
                        if (additionalItem == null)
                        {
                            break;
                        }
                    }
                }

                var gained = game.Inventory.Items.Count(i => i.Name == additionalBuy.Key) - before;
                if (gained < additionalBuy.Value)
                {
                    Log.Warning("{Character} wanted {Count} {Item} from {NPCCode} but came away with {Gained}",
                        game.Me.Name, additionalBuy.Value, additionalBuy.Key, npc.NPCCode, gained);
                }
                else
                {
                    Log.Information($"{game.Me.Name} bought {gained} {additionalBuy.Key}");
                }
            }
        }

        BuyMagicItemsAtMerchant(game, npc);

        Thread.Sleep(50);
        game.TerminateEntityChat(npc);
        Thread.Sleep(50);
        game.TerminateEntityChat(npc);

        // The last resort for a misplaced potion with no cell to go to, outside the trade: whether the server lets a belt
        // potion be drunk with a shop open is not known.
        if (misplacedLeft.Count > 0)
        {
            var drunk = InventoryHelpers.DrinkMisplacedPotions(game, misplacedLeft);
            InventoryHelpers.LogMisplacedPotions(game, [], misplacedLeft, drunk);
        }

        return true;
    }

    /// <summary>
    /// Sells the potions of this kind in the inventory that the belt has no room for, when no amount to carry is set: the
    /// inventory is then not where this kind is kept.
    /// </summary>
    private static void SellPotionSurplus(Game game, WorldObject npc, Bots.Types.AccountConfig account, PotionKind kind, long? toBuy)
    {
        if (toBuy != null)
        {
            return;
        }

        var surplus = game.Inventory.Items
            .Where(i => InventoryHelpers.KindOf(i) == kind)
            .Skip(InventoryHelpers.BeltRoom(game, account, kind))
            .ToList();
        if (surplus.Count > 0)
        {
            Log.Information("{Character} selling {Count} {Kind} potions the belt has no room for", game.Me.Name, surplus.Count, kind);
        }

        foreach (var potion in surplus)
        {
            game.SellItem(npc, potion);
        }
    }

    /// <summary>
    /// Buys potions one at a time, waiting for each to arrive before ordering the next. Buying is
    /// fire and forget and the server does not keep up with a burst: an order of seven sent 30ms
    /// apart landed one potion, which is how characters reached the cow level with an empty belt.
    /// The stock is looked up each time as well, though a potion vendor keeps the same item id.
    /// </summary>
    internal static long BuyPotions(Game game, WorldObject npc, string typePrefix, long wanted)
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

        var stacked = CountPotions(game, typePrefix) - start;
        return stacked + BuySinglePotions(game, npc, typePrefix, wanted - stacked);
    }

    /// <summary>
    /// Buys potions one at a time, waiting for each. A single potion goes into the belt when it has a column for it, the
    /// other kind's empty columns included, and to the inventory otherwise.
    /// </summary>
    private static long BuySinglePotions(Game game, WorldObject npc, string typePrefix, long wanted, Func<bool> keepBuying = null)
    {
        var start = CountPotions(game, typePrefix);
        while (CountPotions(game, typePrefix) - start < wanted && (keepBuying == null || keepBuying()))
        {
            var stock = FindPotionInStock(game, typePrefix);
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

        return Math.Max(0, CountPotions(game, typePrefix) - start);
    }

    /// <summary>
    /// Buys into the belt for <see cref="InventoryHelpers.RefillBelt"/>: a stack when it cannot reach another kind's column
    /// or buy past the room, a single potion otherwise. Returns how many potions arrived.
    /// </summary>
    private static long BuyOneIntoBelt(Game game, WorldObject npc, Bots.Types.AccountConfig account, PotionKind kind, long budget)
    {
        var typePrefix = kind == PotionKind.Healing ? "hp" : "mp";
        var stock = FindPotionInStock(game, typePrefix);
        if (stock == null)
        {
            Log.Warning($"{game.Me.Name} found no more {typePrefix} potions to buy at {npc.NPCCode}");
            return 0;
        }

        var before = CountPotions(game, typePrefix);
        var stack = budget >= InventoryHelpers.BeltRoom(game, account, kind)
            && BeltPlacement.StackBuyStays(InventoryHelpers.BeltPotions(game), (int)game.Belt.Height, account.BeltColumns(kind), account.BeltTarget(kind, game.Belt.Height), kind);
        game.BuyItem(npc, stock, buyStack: stack);
        if (!GeneralHelpers.TryWithTimeout((_) => CountPotions(game, typePrefix) > before, TimeSpan.FromSeconds(stack ? 2 : 1)))
        {
            return 0;
        }

        // A stack arrives one potion at a time: a single bought before it has settled would go past a belt about to be full.
        if (stack)
        {
            var seen = CountPotions(game, typePrefix);
            var since = DateTime.Now;
            GeneralHelpers.TryWithTimeout((_) =>
            {
                var now = CountPotions(game, typePrefix);
                if (now != seen)
                {
                    seen = now;
                    since = DateTime.Now;
                }

                return InventoryHelpers.NextBeltFill(game, account, kind) != BeltFillStep.Fill || DateTime.Now - since > TimeSpan.FromMilliseconds(100);
            }, TimeSpan.FromSeconds(2));
        }

        return CountPotions(game, typePrefix) - before;
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
    internal static void RestockTome(Game game, WorldObject npc, ItemName tomeName, ItemName scrollName)
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
