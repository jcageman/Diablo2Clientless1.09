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

namespace ConsoleBot.Helpers
{
    public static class NPCHelpers
    {
        private static readonly HashSet<NPCCode> FriendlyNPCs = [ 
            NPCCode.MephistoGhost, NPCCode.ATrap1, NPCCode.ATrap2, NPCCode.ATrap3, NPCCode.ATrap4,
            NPCCode.ATrap5, NPCCode.ATrap6, NPCCode.ATrap7, NPCCode.Hydra1, NPCCode.Hydra2, NPCCode.Hydra3, NPCCode.CompellingOrb,
            NPCCode.ClayGolem, NPCCode.BloodGolem, NPCCode.FireGolem, NPCCode.IronGolem, NPCCode.Valkyrie,
            NPCCode.Act1Npc, NPCCode.Guard, NPCCode.BaalThrone, NPCCode.BaalTentacle1, NPCCode.BaalTentacle2, NPCCode.BaalTentacle3, NPCCode.BaalTentacle4, NPCCode.BaalTentacle5];
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
            var inventoryItemsToSell = game.Inventory.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(game, i)).ToList();
            foreach (Item item in inventoryItemsToSell)
            {
                if (item.Quality == QualityType.Rare)
                {
                    Log.Information($"Selling item {item.GetFullDescription()}");
                }

                game.SellItem(npc, item);
            }

            var inventoryFull = false;

            foreach (var gambleItem in game.Items.Values.Where(i => i.Container == ContainerType.ArmorTab && Pickit.Pickit.ShouldGamble(game.Me, i)))
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
                || game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeOfTownPortal)?.Amount < 5
                || (game.Me.Life / (double)game.Me.MaxLife) < 0.7;
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
            GeneralHelpers.TryWithTimeout((retryCount) =>
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
            }, TimeSpan.FromSeconds(3));

            Thread.Sleep(50);
            game.InitiateEntityChat(npc);

            game.TownFolkAction(npc, TownFolkActionType.Trade);
            Item healingPotion = null;
            Item manaPotion = null;

            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                healingPotion = game.Items.Values.Where(i => i.IsInMerchantTab() && i.Type.StartsWith("hp", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => (int)i.Type.Last()).FirstOrDefault();
                manaPotion = game.Items.Values.Where(i => i.IsInMerchantTab() && i.Type.StartsWith("mp", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => (int)i.Type.Last()).FirstOrDefault();
                return healingPotion != null && manaPotion != null;
            }, TimeSpan.FromSeconds(3)))
            {
                Log.Warning($"Did not find healing or mana potions at {npc.NPCCode} {game.Me.Location}");
            }

            var inventoryItemsToSell = game.Inventory.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(game, i) && Pickit.Pickit.CanTouchInventoryItem(game, i)).ToList();
            var cubeItemsToSell = game.Cube.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(game, i)).ToList();
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

            var tomeOfTownPortal = game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeOfTownPortal);
            var scrollOfTownPortal = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == ItemName.ScrollofTownPortal);
            if (tomeOfTownPortal != null && scrollOfTownPortal != null && tomeOfTownPortal.Amount < 100)
            {
                game.BuyItem(npc, scrollOfTownPortal, true);
            }

            var tomeOfIdentify = game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeofIdentify);
            var scrollOfIdentify = game.Items.Values.FirstOrDefault(i => i.IsInMerchantTab() && i.Name == ItemName.ScrollofIdentify);
            if (tomeOfIdentify != null && scrollOfIdentify != null && tomeOfIdentify.Amount < 100)
            {
                game.BuyItem(npc, scrollOfIdentify, true);
            }

            if(healingPotion != null)
            {
                var numberOfHealthPotions = options.HealthPotionsToBuy ?? game.Belt.Height * options.AccountConfig.HealthSlots.Count - game.Belt.NumOfHealthPotions();
                while (numberOfHealthPotions > 0)
                {
                    game.BuyItem(npc, healingPotion, false);
                    numberOfHealthPotions -= 1;
                }
            }

            if(manaPotion != null)
            {
                var numberOfManaPotions = options.ManaPotionsToBuy ?? game.Belt.Height * options.AccountConfig.ManaSlots.Count - game.Belt.NumOfManaPotions();
                while (numberOfManaPotions > 0)
                {
                    game.BuyItem(npc, manaPotion, false);
                    numberOfManaPotions -= 1;
                }
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
            var merchantItemsToBuy = game.Items.Values.Where(i => i.IsInMerchantTab() && Pickit.Pickit.ShouldKeepItem(game, i)).ToList();
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
}
