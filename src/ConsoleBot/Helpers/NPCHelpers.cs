using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using D2NG.Core;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;

namespace ConsoleBot.Helpers
{
    public static class NPCHelpers
    {
        public static WorldObject GetUniqueNPC(Game game, NPCCode npcCode)
        {
            return game.GetNPCsByCode(npcCode).Single();
        }

        public static void RepairItems(Game game, WorldObject npc)
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

            Thread.Sleep(50);
            game.TerminateEntityChat(npc);
            Thread.Sleep(50);
        }

        public static bool GambleItems(Game game, WorldObject npc)
        {
            Log.Information($"Gambling items");
            bool moveResult = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                if (game.Me.Location.Distance(npc.Location) > 5)
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

            while (game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.GoldInStash] > 200000)
            {
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
                    break;
                }

                Thread.Sleep(50);
                game.InitiateEntityChat(npc);

                var oldItems = game.Items.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();

                game.TownFolkAction(npc, TownFolkActionType.Gamble);

                var itemsResult = GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    var newItems = game.Items.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();
                    return newItems.Except(oldItems).Any();
                }, TimeSpan.FromSeconds(1));

                if (!itemsResult)
                {
                    Log.Debug("Waiting for items failed");
                    continue;
                }

                Thread.Sleep(10);
                Log.Debug("Trying to find gamble items and sell previous onces");

                var inventoryItemsToSell = game.Inventory.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(i)).ToList();
                foreach (Item item in inventoryItemsToSell)
                {
                    if (item.Quality == QualityType.Rare)
                    {
                        Log.Information($"Selling item {item.GetFullDescription()}");
                    }

                    game.SellItem(npc, item);
                }

                var inventoryFull = false;

                foreach (var gambleItem in game.Items.Where(i => i.Container == ContainerType.ArmorTab && Pickit.Pickit.ShouldGamble(i)))
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

                Thread.Sleep(50);
                game.TerminateEntityChat(npc);
                Thread.Sleep(50);

                if (inventoryFull)
                {
                    break;
                }
            }

            return true;
        }

        public static bool IdentifyItemsAtDeckardCain(Game game)
        {
            if (game.Inventory.Items.All(i => i.IsIdentified) && game.Cube.Items.All(i => i.IsIdentified))
            {
                return true;
            }

            var result1 = GeneralHelpers.TryWithTimeout((retryCount) => game.GetNPCsByCode(NPCCode.DeckardCainAct3).Any(), TimeSpan.FromSeconds(2));
            if (!result1)
            {
                return false;
            }

            var deckardCain = NPCHelpers.GetUniqueNPC(game, NPCCode.DeckardCainAct3);
            if (deckardCain == null)
            {
                return false;
            }

            Log.Information($"Identifying items at Cain");
            var result2 = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                if (game.Me.Location.Distance(deckardCain.Location) > 5)
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
                Log.Error($"Failed to interact with Cain");
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

        public static bool SellItemsAndRefreshPotionsAtOrmus(Game game)
        {
            var ormus = NPCHelpers.GetUniqueNPC(game, NPCCode.Ormus);
            if (ormus == null)
            {
                return false;
            }

            GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                if (game.Me.Location.Distance(ormus.Location) >= 5)
                {
                    game.MoveTo(ormus);
                }

                if (game.Me.Location.Distance(ormus.Location) < 5)
                {
                    return game.InteractWithNPC(ormus);
                }
                return false;
            }, TimeSpan.FromSeconds(3));

            Thread.Sleep(50);
            game.InitiateEntityChat(ormus);

            game.TownFolkAction(ormus, TownFolkActionType.Trade);

            Thread.Sleep(300);

            var healingPotion = game.Items.FirstOrDefault(i => i.Container == ContainerType.MiscTab && i.Type == "hp5")
                                ?? game.Items.FirstOrDefault(i => i.Container == ContainerType.MiscTab && i.Type.StartsWith("hp"));
            var manaPotion = game.Items.FirstOrDefault(i => i.Container == ContainerType.MiscTab && i.Type == "mp5")
                             ?? game.Items.FirstOrDefault(i => i.Container == ContainerType.MiscTab && i.Type.StartsWith("mp"));
            if (healingPotion == null || manaPotion == null)
            {
                game.TerminateEntityChat(ormus);
                return false;
            }

            var inventoryItemsToSell = game.Inventory.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(i) && Pickit.Pickit.CanTouchInventoryItem(i)).ToList();
            var cubeItemsToSell = game.Cube.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(i)).ToList();
            Log.Information($"Selling {inventoryItemsToSell.Count} inventory items and {cubeItemsToSell.Count} cube items");

            foreach (Item item in inventoryItemsToSell)
            {
                Log.Information($"Selling inventory item {item.GetFullDescription()}");
                game.SellItem(ormus, item);
            }

            foreach (Item item in cubeItemsToSell)
            {
                Log.Information($"Selling cube item {item.GetFullDescription()}");
                game.SellItem(ormus, item);
            }

            var tomeOfTownPortal = game.Inventory.Items.FirstOrDefault(i => i.Name == "Tome of Town Portal");
            var scrollOfTownPortal = game.Items.FirstOrDefault(i => i.Container == ContainerType.MiscTab && i.Name == "Scroll of Town Portal");
            if (tomeOfTownPortal != null && scrollOfTownPortal != null && tomeOfTownPortal.Amount < 100)
            {
                game.BuyItem(ormus, scrollOfTownPortal, true);
            }

            var numberOfHealthPotions = game.Belt.NumOfHealthPotions();
            while (numberOfHealthPotions < 6)
            {
                game.BuyItem(ormus, healingPotion, false);
                numberOfHealthPotions += 1;
            }

            var numberOfManaPotions = game.Belt.NumOfManaPotions();
            while (numberOfManaPotions < 6)
            {
                game.BuyItem(ormus, manaPotion, false);
                numberOfManaPotions += 1;
            }

            Thread.Sleep(50);
            game.TerminateEntityChat(ormus);
            Thread.Sleep(50);
            game.TerminateEntityChat(ormus);
            return true;
        }
    }
}
