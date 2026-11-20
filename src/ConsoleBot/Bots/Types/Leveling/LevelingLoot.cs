using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// What a leveling party picks up. Gold, potions and scrolls always; magic and better to sell or wear; plain items to sell
/// while gold is short; plain bows and arrows while an amazon in the party has none.
/// </summary>
public static class LevelingLoot
{
    private const double LootRadius = 20;
    private const int AttemptsPerItem = 3;

    /// <summary>A plain item is picked up to sell when a merchant pays at least this much for it.</summary>
    private const int PlainItemWorth = 250;

    /// <summary>Room kept for magic items: plain ones are only taken while the bag has this much free.</summary>
    private const int MinFreeCellsForPlainItems = 12;

    /// <summary>
    /// Gold on the member and in its stash under which plain items and gold piles are picked up: enough for potions and
    /// repairs. Gambling pools the rest; at 200k the gambler, left with 162k, still clicked 81 piles and a dozen plain
    /// items in one run while the party waited.
    /// </summary>
    private const int PlainItemsWhileGoldBelow = 100_000;

    /// <summary>A gold pile this close is picked up whatever the member holds: it costs no walk.</summary>
    private const double GoldAtFeet = 3;

    private static int Gold(D2NG.Core.Game game)
        => game.Me.Attributes.GetValueOrDefault(D2NG.Core.D2GS.Players.Attribute.GoldOnPerson) + game.Me.Attributes.GetValueOrDefault(D2NG.Core.D2GS.Players.Attribute.GoldInStash);

    private static readonly ConcurrentDictionary<uint, int> Attempts = new();

    public static void Reset() => Attempts.Clear();

    public static bool Wanted(PartyMember member, Item item, bool partyWantsBow)
    {
        if (!item.Ground || Attempts.GetValueOrDefault(item.Id) >= AttemptsPerItem)
        {
            return false;
        }

        var game = member.Game;
        // Gold only while the member is short of it, or at its feet: in one Hell Chaos run the leader clicked 63 piles for
        // 7,423 gold while every member held over 250k, and the party shares what each picks up anyway.
        if (item.Name == ItemName.Gold)
        {
            return Gold(game) < PlainItemsWhileGoldBelow || item.Location.Distance(game.Me.Location) <= GoldAtFeet;
        }

        // Supplies only while the member is short of them: the leader carried 18 portal scrolls, 17 identify
        // scrolls and 33 mana potions a paladin never drinks, and had no room left to buy healing potions.
        // Rejuvenations heal at once: taken whenever there is any room, up to ten.
        if (item.Classification == ClassificationType.RejuvenationPotion)
        {
            // A full one is taken past ten while a small one is held: the small ones are drunk first, so it takes their place.
            return game.Inventory.FindFreeSpace(item) != null && (LevelingShop.RejuvenationsHeld(game) < LevelingShop.RejuvenationsKept
                || (item.Name == ItemName.FullRejuvenationPotion && LevelingShop.SmallRejuvenationsHeld(game) > 0));
        }

        if (item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion)
        {
            return game.Inventory.FreeCellCount() > 4 && LevelingShop.Surplus(member, item, Held(game, item) + 1) == 0;
        }

        if (item.Name is ItemName.ScrollofTownPortal or ItemName.ScrollofIdentify)
        {
            return game.Inventory.FindFreeSpace(item) != null && LevelingShop.Surplus(member, item, Held(game, item) + 1) == 0;
        }

        if (game.Inventory.FindFreeSpace(item) == null)
        {
            return false;
        }

        // What the farming bots' nips take is taken on top: gems, essences, the realm's cube reagents.
        if (item.Quality >= QualityType.Magical || (LevelingGems.PickingUp && LevelingGems.IsDiamond(item))
            || D2NG.Pickit.Pickit.ShouldPickupItem(game, item, shouldPickupGoldItems: false))
        {
            return true;
        }

        // Plain staves, scepters and wands sell for hundreds of gold, more than most magic items at low levels; plain items
        // are taken to sell only while the member is short of gold. At level 70 each member carried 250k, and the 217 plain
        // items a three-hour Hell batch sold brought 0.9M that went to gambling rounds keeping nothing, while the party
        // waited out every walk to them.
        if (Gold(game) < PlainItemsWhileGoldBelow && game.Inventory.FreeCellCount() >= MinFreeCellsForPlainItems
            && LevelingGear.SellValue(item, game) >= PlainItemWorth)
        {
            return true;
        }

        return partyWantsBow && (item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow or ClassificationType.Arrows);
    }

    private static int Held(D2NG.Core.Game game, Item item) => item.Classification switch
    {
        ClassificationType.HealthPotion => ConsoleBot.Helpers.InventoryHelpers.GetTotalHealthPotions(game),
        ClassificationType.ManaPotion => ConsoleBot.Helpers.InventoryHelpers.GetTotalManaPotions(game),
        _ => game.Inventory.Items.Count(held => held.Name == item.Name)
    };

    /// <summary>A member with a monster this close fights rather than loots.</summary>
    private const int BusyRadius = 15;

    /// <summary>An item with a monster this close waits for a later pass.</summary>
    private const int GuardedRadius = 10;

    private static bool MonstersWithin(PartyMember member, Point at, int radius)
        => NPCHelpers.GetNearbyNPCs(member.Client, at, 5, radius).Any(npc => !LevelingCombat.Untargetable.Contains(npc.NPCCode));

    /// <summary>Set, rare and unique items and what the nips take are walked further for, and room is made for them.</summary>
    private const double PrizedLootRadius = 40;

    /// <summary>
    /// Set, rare, unique and crafted items, and whatever the farming bots' nips pick up: on 16 Nov 2026 the party left 22 of
    /// 72 rares on the ground - those of the amazon's and the sorceress's kills 25 to 90 away, the loot only being looked for
    /// around the leader, and those dropped while every bag was too full for a bow or an armor.
    /// </summary>
    private static bool Prized(PartyMember member, Item item)
        => item.Ground && Attempts.GetValueOrDefault(item.Id) < AttemptsPerItem && item.Name != ItemName.Gold
            && (item.Quality >= QualityType.Set || D2NG.Pickit.Pickit.ShouldPickupItem(member.Game, item, shouldPickupGoldItems: false));

    private static double Reach(PartyMember member, Item item) => Prized(member, item) ? PrizedLootRadius : LootRadius;

    /// <summary>
    /// Every member picks up what it wants near any member, nearest member first so two do not walk to one item. A prized
    /// item nobody has room for is taken by the nearest member, who makes room for it.
    /// </summary>
    public static async Task Collect(Party party)
    {
        var partyWantsBow = party.Members.Any(member => member.Plan.Name.Contains("bow", StringComparison.OrdinalIgnoreCase)
            && !member.Game.Items.Values.Any(item => item.IsEquipped && item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow));
        // Not in the middle of a fight: with the leader's ground clear the barbarian, a pack still on him at half life,
        // juggled his bag for a Sharktooth Armor with a Jagged Star on his cursor while the leader stood waiting.
        var field = party.Members.Where(member => member.InGame && !member.Game.IsInTown() && !MonstersWithin(member, member.Game.Me.Location, BusyRadius)).ToList();
        var ground = field
            .SelectMany(member => member.Game.Items.Values.Where(item => item.Ground && item.Location.Distance(member.Game.Me.Location) <= Reach(member, item)
                && !MonstersWithin(member, item.Location, GuardedRadius)))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        if (ground.Count == 0)
        {
            return;
        }

        var claims = ground
            .Select(item =>
            {
                var near = field.Where(member => member.Game.Me.Location.Distance(item.Location) <= Reach(member, item))
                    .OrderBy(member => member.Game.Me.Location.Distance(item.Location)).ToList();
                var taker = near.FirstOrDefault(member => Wanted(member, item, partyWantsBow));
                var makesRoom = taker == null && near.FirstOrDefault(member => Prized(member, item)) is { } nearest ? nearest : null;
                return (Item: item, Member: taker ?? makesRoom, MakesRoom: makesRoom != null);
            })
            .Where(claim => claim.Member != null)
            .GroupBy(claim => claim.Member);
        await Task.WhenAll(claims.Select(async claim =>
        {
            foreach (var (item, _, makesRoom) in claim.OrderBy(entry => entry.Item.Location.Distance(claim.Key.Game.Me.Location)))
            {
                if (makesRoom && !MakeRoomFor(claim.Key, item))
                {
                    continue;
                }

                await Pickup(claim.Key, item);
            }
        }));
    }

    /// <summary>The cube takes the loot once the bag runs low on room, so a member keeps picking up between town visits.</summary>
    private const int CubeWhenFreeBelow = 10;

    private static void StoreInCube(PartyMember member)
    {
        var game = member.Game;
        if (game.Inventory.FreeCellCount() >= CubeWhenFreeBelow || game.Inventory.FindItemByName(ItemName.HoradricCube) == null)
        {
            return;
        }

        var stored = 0;
        foreach (var item in game.Inventory.Items.Where(item => IsLoot(item)).ToList())
        {
            if (game.Cube.FindFreeSpace(item) is not { } space)
            {
                continue;
            }

            if (ConsoleBot.Helpers.InventoryHelpers.PutInventoryItemInCube(game, item, space) != ConsoleBot.Enums.MoveItemResult.Succes)
            {
                break;
            }

            stored++;
        }

        if (stored > 0)
        {
            Log.Information("{Character} put {Count} items in the cube, {Free} inventory cells free", member.Name, stored, game.Inventory.FreeCellCount());
        }
    }

    /// <summary>
    /// Room for a quest item the member is about to pick up, made then and only then: loot goes into the cube, and
    /// when that is not enough the cheapest loot is dropped. A full bag at the Inifuss tree or a quest chest otherwise
    /// costs the step.
    /// </summary>
    public static bool MakeRoomFor(PartyMember member, Item wanted)
    {
        var game = member.Game;
        if (game.Inventory.FindFreeSpace(wanted) != null)
        {
            return true;
        }

        if (game.Inventory.FindItemByName(ItemName.HoradricCube) != null)
        {
            foreach (var item in game.Inventory.Items.Where(IsLoot).OrderByDescending(item => item.Width * item.Height).ToList())
            {
                if (game.Cube.FindFreeSpace(item) is { } space
                    && ConsoleBot.Helpers.InventoryHelpers.PutInventoryItemInCube(game, item, space) == ConsoleBot.Enums.MoveItemResult.Succes
                    && game.Inventory.FindFreeSpace(wanted) != null)
                {
                    break;
                }
            }
        }

        var dropped = new List<string>();
        // Only what is worth no more than its gold goes: never a set, rare or unique item or one the nips keep.
        foreach (var item in game.Inventory.Items.Where(item => IsLoot(item) && item.Quality < QualityType.Set && !D2NG.Pickit.Pickit.ShouldKeepItem(game, item) && !LevelingGems.ReservedForDiamonds(item))
            .OrderBy(item => LevelingGear.SellValue(item, game) ?? 0).ToList())
        {
            if (game.Inventory.FindFreeSpace(wanted) != null)
            {
                break;
            }

            if (ConsoleBot.Helpers.InventoryHelpers.DropItemFromInventory(game, item) == ConsoleBot.Enums.MoveItemResult.Succes)
            {
                dropped.Add(item.Name.ToString());
            }
        }

        var room = game.Inventory.FindFreeSpace(wanted) != null;
        Log.Information("{Character} made room for {Item}: {Outcome}{Dropped}", member.Name, wanted.Name, room ? "done" : "NOT enough",
            dropped.Count > 0 ? $", dropped {string.Join(", ", dropped)}" : "");
        return room;
    }

    /// <summary>Loot to sell or hand on: what the merchant would take, never a quest item or the cube itself.</summary>
    private static bool IsLoot(Item item)
        => item.Name != ItemName.HoradricCube && item.Classification != ClassificationType.QuestItem && LevelingShop.Sellable(item, keepsBows: false);

    /// <summary>In town the loot comes back out of the cube, to be sold or shared like the rest.</summary>
    public static void EmptyCube(PartyMember member)
    {
        var game = member.Game;
        if (game.Inventory.FindItemByName(ItemName.HoradricCube) == null)
        {
            return;
        }

        foreach (var item in game.Cube.Items.Where(IsLoot).ToList())
        {
            if (ConsoleBot.Helpers.InventoryHelpers.PutCubeItemInInventory(game, item) != ConsoleBot.Enums.MoveItemResult.Succes)
            {
                break;
            }
        }
    }

    private static async Task Pickup(PartyMember member, Item item)
    {
        var game = member.Game;
        Attempts.AddOrUpdate(item.Id, 1, (_, count) => count + 1);
        if (game.Me.Location.Distance(item.Location) > LootRadius && LevelingGear.WalkTo != null)
        {
            // Far off, along a path and with the follow loop held: a straight move stops at the first wall, and following
            // pulls the member back to the leader half way.
            var mode = member.Mode;
            member.Mode = FollowMode.Off;
            try
            {
                await LevelingGear.WalkTo(member, item.Location);
            }
            finally
            {
                member.Mode = mode;
            }
        }

        if (game.Me.Location.Distance(item.Location) > 4)
        {
            await game.MoveToAsync(item);
        }

        game.PickupItem(item);
        await Task.Delay(400);
        var gone = !game.Items.TryGetValue(item.Id, out var now) || !now.Ground;
        if (gone)
        {
            Attempts.TryRemove(item.Id, out _);
            if (item.Name != ItemName.Gold && item.Classification is not (ClassificationType.HealthPotion or ClassificationType.ManaPotion))
            {
                Log.Information("{Character} picked up {Quality} {Item}", member.Name, item.Quality, item.Name);
                StoreInCube(member);
            }
        }
    }
}
