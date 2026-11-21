using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Bots.Types.Leveling;

public sealed partial class LevelingBot
{
    /// <summary>
    /// The party's town visit, in three passes. Everyone heals and sells first, so the gold the junk fetched
    /// exists before it is shared; then gold goes to whoever is short; then everyone buys - arrows for a bow,
    /// portal and identify scrolls, potions. Gear changes hands and empty slots fill in between. The members
    /// shop at the same time; each has its own trade window with a merchant.
    /// </summary>
    private async Task VisitTown(Party party)
    {
        var present = party.Members.Where(member => member.InGame && member.Game.IsInTown()).ToList();
        foreach (var member in present)
        {
            RememberTownNpcs(member.Game);
            await ClearCursor(member);
            LevelingLoot.EmptyCube(member);
        }

        // Shopping walks every member between merchants; following the leader at the same time pulls it away.
        foreach (var member in party.Followers)
        {
            member.Mode = FollowMode.Off;
        }

        try
        {
            await ShopTogether(party, present);
        }
        finally
        {
            foreach (var member in party.Followers)
            {
                member.Mode = FollowMode.Follow;
            }
        }

        foreach (var member in present.Where(member => member.InGame))
        {
            LevelingInventory.Tidy(member);
        }
    }

    private async Task ShopTogether(Party party, List<PartyMember> present)
    {
        // Every member trades with the merchants at the same time - each has its own trade window - instead of
        // queueing behind the others, which made every town visit take minutes. Only the hand-overs between members,
        // which drop items for one another, stay one at a time.
        await Task.WhenAll(present.Select(member => Safely(member, "heal and sell", () => HealAndSell(member, party))));
        // One gambler with the party's spare gold, once a game: the gamble listing is one view for the whole game, so the
        // members could only take turns at it, and gambling costs the party its time.
        if (present.Where(member => member.InGame).OrderByDescending(member => LevelingGear.Gold(member.Game)).FirstOrDefault() is { } gambler
            && LevelingGamble.PartyCanFund(present, gambler) && _gambledThisGame.TryAdd(gambler.Name, true))
        {
            await LevelingGear.PoolGold(present, gambler, LevelingGamble.Keeps, LevelingGamble.GoldCap(gambler));
            if (LevelingGamble.Wants(gambler))
            {
                await Safely(gambler, "gamble", async () =>
                {
                    var code = NPCHelpers.GetGambleNPC(gambler.Game.Act);
                    if (await WalkToNpc(gambler, code) && NPCHelpers.GetUniqueNPC(gambler.Game, code) is { } npc)
                    {
                        LevelingGamble.Gamble(gambler, party, _gear, npc);
                    }
                });
            }
        }

        // What the stash holds for later comes out once someone can wear it, before the gear goes round.
        await _gear.TakeFromStash(party, present);
        await _gear.ShareInTown(party);
        await _gear.ShareGear(party);
        await LevelingGear.ShareGold(party);
        await LevelingGems.Work(present, party.Members);
        _gemCollector = LevelingGems.Collector(present);
        await Task.WhenAll(present.Select(async member =>
        {
            await Safely(member, "buy", () => Buy(member));
            await _gear.FillEmptySlots(member);
        }));
        // A shield bought just now gets its diamonds before the party leaves.
        await LevelingGems.Work(present, party.Members);
        await LevelingGems.CubeFlawlessGems(present);
        foreach (var member in present.Where(member => member.InGame))
        {
            await _gear.WearUpgrades(member);
        }

        await _gear.StashKeepers(party, present);
    }

    /// <summary>
    /// The three-socket shield from Fara in Lut Gholein in a Normal game: every stock of hers has one plain Large Shield, a
    /// third of them socketed and then one to three sockets alike - about one stock in nine - and a walk out of town and back
    /// gives her a new stock (the user's research). Hell's act 4 merchants only ever stock magic shields, which take no
    /// sockets; past level 20 the Nightmare and Hell merchants sell no plain items at all. Between acts only a waypoint goes:
    /// the area route walks, and "no route from RogueEncampment to LutGholein" ended the first shopping games at once.
    /// </summary>
    private async Task<bool> ShopForShield(Party party, PartyMember collector)
    {
        for (var look = 1; look <= FaraStocksPerShop; look++)
        {
            if (party.Area != D2NG.Core.D2GS.Act.Area.LutGholein && !await party.TakeWaypoint(D2NG.Core.D2GS.Objects.Waypoint.LutGholein))
            {
                Log.Warning("{Character} could not take the waypoint to Lut Gholein to look at Fara's stock ({Look} of {Looks})", collector.Name, look, FaraStocksPerShop);
                return false;
            }

            if (await BrowseSmithForShield(collector))
            {
                Log.Information("{Character} found the shield in Fara's stock {Look} of {Looks}", collector.Name, look, FaraStocksPerShop);
                return true;
            }

            // Out of town and back gives her a new stock: the Dry Hills waypoint and back, quicker than walking out and without
            // the load of another act; the Rogue Encampment when a member lacks that waypoint.
            var hop = AllHoldWaypoint(party, D2NG.Core.D2GS.Objects.Waypoint.DryHills) ? D2NG.Core.D2GS.Objects.Waypoint.DryHills : D2NG.Core.D2GS.Objects.Waypoint.RogueEncampment;
            if (look < FaraStocksPerShop && !await party.TakeWaypoint(hop))
            {
                Log.Warning("{Character} could not take the waypoint to {Waypoint} for a new stock at Fara's", collector.Name, hop);
                return false;
            }
        }

        return false;
    }

    /// <summary>Every member in the game holds the waypoint; a list not seen yet counts as not holding it.</summary>
    private static bool AllHoldWaypoint(Party party, D2NG.Core.D2GS.Objects.Waypoint waypoint)
        => party.Members.Where(member => member.InGame).All(member => member.Game.Me.AllowedWaypoints.Contains(waypoint));

    /// <summary>Fara's stocks looked at in one shopping game: twenty give a three-socket shield nine times in ten.</summary>
    private const int FaraStocksPerShop = 20;

    private async Task<bool> BrowseSmithForShield(PartyMember member)
    {
        var code = NPCHelpers.GetRepairNPC(member.Game.Act);
        var bought = false;
        if (await WalkToNpc(member, code) && NPCHelpers.GetUniqueNPC(member.Game, code) is { } smith)
        {
            await LevelingShop.Browse(member, smith, () =>
            {
                bought = LevelingGems.BuyShield(member, smith, (buyer, shield) => _gear.CanWear(buyer, shield));
                return Task.CompletedTask;
            });
        }

        return bought;
    }

    private static async Task Safely(PartyMember member, string what, Func<Task> action)
    {
        try
        {
            member.Game.CleanupCursorItem();
            await action();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "{Character} could not {What} in town", member.Name, what);
        }
    }

    private async Task HealAndSell(PartyMember member, Party party)
    {
        var game = member.Game;
        var act = game.Act;
        if (game.Me.Life < game.Me.MaxLife * 0.9 || game.Me.Mana < game.Me.MaxMana * 0.5)
        {
            if (await WalkToNpc(member, NPCHelpers.GetHealNPC(act)))
            {
                NPCHelpers.HealAtHealer(game);
            }
        }

        // The tome identifies where the member stands, a fraction of a second an item; Cain only when it has too few
        // charges. The walk to Cain and back was part of every town visit, which took a median 74 s in Hell.
        var cain = NPCHelpers.GetDeckardCainForAct(act);
        if (game.Inventory.Items.Any(item => !item.IsIdentified && item.Quality >= QualityType.Magical)
            && !InventoryHelpers.IdentifyInventoryWithTome(game)
            && NPCHelpers.GetUniqueNPC(game, cain) != null && await WalkToNpc(member, cain))
        {
            NPCHelpers.IdentifyItemsAtDeckardCain(game);
        }

        var seller = NPCHelpers.GetSellNPC(act);
        // Only a member still without a bow keeps every one it finds; an upgrade is kept anyway as wanted. Keeping them all
        // filled the amazon's bag with Short and Composite Bows weaker than the one she wore.
        var keepsBows = LevelingGear.WantsBow(member);
        if (!LevelingShop.HasSomethingToSell(member, keepsBows, item => _gear.Keeps(party, member, item)))
        {
            return;
        }

        if (await WalkToNpc(member, seller) && NPCHelpers.GetUniqueNPC(game, seller) is { } npc)
        {
            // Cain or a scroll first, so a weapon's worth is known before deciding to sell it.
            foreach (var weapon in game.Inventory.Items.Where(item => LevelingGear.IsWeaponOrShield(item) && !item.IsIdentified && item.Quality >= QualityType.Magical).ToList())
            {
                await LevelingGear.IdentifyWithScroll(member, weapon);
            }

            LevelingShop.Sell(member, npc, keepsBows, item => _gear.Keeps(party, member, item));
            // What the cube still holds comes out into the room the sale made, and what of it is not kept goes the same way:
            // a magic Studded Leather sat unidentified in the paladin's cube for half an hour, and every gem transmute failed
            // for want of room to take it out.
            if (game.Cube.Items.Count > 0)
            {
                LevelingLoot.EmptyCube(member);
                InventoryHelpers.IdentifyInventoryWithTome(game);
                if (LevelingShop.HasSomethingToSell(member, keepsBows, item => _gear.Keeps(party, member, item)))
                {
                    LevelingShop.Sell(member, npc, keepsBows, item => _gear.Keeps(party, member, item));
                }
            }
        }
    }

    private async Task Buy(PartyMember member)
    {
        var game = member.Game;
        var act = game.Act;
        var gold = game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson);
        var smithCode = NPCHelpers.GetRepairNPC(act);
        if ((LevelingGear.WantsBow(member) || LevelingGear.HoldsBow(member)) && !LevelingGear.HasArrows(member)
            && await WalkToNpc(member, smithCode) && NPCHelpers.GetUniqueNPC(game, smithCode) is { } fletcher)
        {
            LevelingShop.BuyArrows(member, fletcher);
        }

        var seller = NPCHelpers.GetSellNPC(act);
        // Nothing short: the member goes straight on. Browsing the merchant's gear alone was a walk and a trade window
        // per member per visit.
        // A member with gold to spare browses the merchant's gear once a game even when short of nothing: skipping every
        // visit without a supply need left 375,000 gold unspent while Burning Souls chickened the party for want of
        // lightning resistance.
        var browse = LevelingGear.Gold(game) > BrowseGold && _browsedThisGame.TryAdd(member.Name, true);
        if ((browse || LevelingShop.NeedsSupplies(member, UsesMana(member), WantsAntidotes(member) ? AntidotesForAndariel : 0))
            && await WalkToNpc(member, seller) && NPCHelpers.GetUniqueNPC(game, seller) is { } npc)
        {
            LevelingShop.Buy(member, npc, UsesMana(member));
            if (WantsAntidotes(member))
            {
                // Bought with the rest: a separate trip back to Akara at the start of the Andariel step cost every
                // member the walk from the waypoint and back.
                LevelingShop.BuyAntidotes(member, npc, AntidotesForAndariel);
            }

            await LevelingShop.Browse(member, npc, () => _gear.BuyUpgrades(member, npc));
        }

        // The diamonds are ready and the shield is missing: the merchants' armor is looked through for one.
        if (LevelingGems.WantsShield(member, _gemCollector))
        {
            foreach (var code in new[] { seller, smithCode }.Distinct())
            {
                var bought = false;
                if (await WalkToNpc(member, code) && NPCHelpers.GetUniqueNPC(game, code) is { } shop)
                {
                    await LevelingShop.Browse(member, shop, () =>
                    {
                        bought = LevelingGems.BuyShield(member, shop, (buyer, shield) => _gear.CanWear(buyer, shield));
                        return Task.CompletedTask;
                    });
                }

                if (bought)
                {
                    break;
                }
            }
        }

        // The smith is only walked to for repairs (and arrows above): browsing its gear cost every member a walk and a
        // trade window per town visit for upgrades that rarely came.
        await _gear.WearUpgrades(member);

        if (NPCHelpers.ShouldGoToRepairNPC(game) && await WalkToNpc(member, smithCode)
            && NPCHelpers.GetUniqueNPC(game, smithCode) is { } smith)
        {
            NPCHelpers.RepairItemsAndBuyArrows(game, smith);
        }

        Log.Information("{Character} leaves town with {Gold} gold (had {Before} after selling), {Potions} healing potions ({Belt} in the belt), {Free} free inventory cells: {Items}",
            member.Name, game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson), gold, InventoryHelpers.GetTotalHealthPotions(game),
            game.Belt.NumOfHealthPotions(), game.Inventory.FreeCellCount(),
            string.Join(", ", game.Inventory.Items.GroupBy(item => item.Name).Select(group => group.Count() > 1 ? $"{group.Key} x{group.Count()}" : $"{group.Key}")));
    }

    private static bool UsesMana(PartyMember member) => LevelingShop.UsesMana(member);

    private const int BrowseGold = 50_000;

    /// <summary>The shield bearer this town visit's diamonds go to.</summary>
    private PartyMember _gemCollector;

    /// <summary>Members that browsed the merchant's gear this game.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _browsedThisGame = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _gambledThisGame = new();

    /// <summary>
    /// Where each town NPC was last seen, per game: one out of sight is forgotten by the client, and Hratli, who is no map
    /// NPC, was not found again once a member had walked off to another merchant.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint MapId, NPCCode Code), D2NG.Core.D2GS.Point> TownNpcSpots = new();

    private static void RememberTownNpcs(D2NG.Core.Game game)
    {
        if (!game.IsInTown())
        {
            return;
        }

        foreach (var npc in game.WorldObjects.Where(entry => entry.Key.Item2 == EntityType.NPC && NPCHelpers.IsFriendly(entry.Value.NPCCode)).Select(entry => entry.Value))
        {
            TownNpcSpots[(game.MapId, npc.NPCCode)] = npc.Location;
        }
    }

    private async Task<bool> WalkToNpc(PartyMember member, NPCCode code)
    {
        var game = member.Game;
        // An NPC the member already sees is walked to where it stands. Hratli is no map NPC at all, and the map object of
        // his name marks where he moves to later: in Normal act 3 he stands by Meshif at the docks, seen on joining, and
        // walking to the object led away from him - the amazon never bought arrows and shot her quiver empty.
        RememberTownNpcs(game);
        var path = (NPCHelpers.GetUniqueNPC(game, code)?.Location ?? TownNpcSpots.GetValueOrDefault((game.MapId, code))) is { } known
            ? await pathingService.GetPathToLocation(game.MapId, AreaTraveler.MapDifficulty, game.Area, game.Me.Location, known, MovementMode.Walking)
            : code == NPCCode.Hratli
                ? await pathingService.GetPathToNPC(game, NPCCode.Meshif2, MovementMode.Walking)
                : await pathingService.GetPathToNPC(game, code, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(game, path, MovementMode.Walking);
        }

        WorldObject npc = null;
        GeneralHelpers.TryWithTimeout(_ => (npc = NPCHelpers.GetUniqueNPC(game, code)) != null, TimeSpan.FromSeconds(5));
        // Not by Meshif: he has moved on to where his map object stands. Walking there first, in a fresh game where the
        // party had not seen him yet, took the amazon away from him and out of the town's bounds.
        if (npc == null && code == NPCCode.Hratli
            && await pathingService.GetPathToObject(game, EntityCode.Hratli, MovementMode.Walking) is { Count: > 0 } toForge)
        {
            await MovementHelpers.TakePathOfLocations(game, toForge, MovementMode.Walking);
            GeneralHelpers.TryWithTimeout(_ => (npc = NPCHelpers.GetUniqueNPC(game, code)) != null, TimeSpan.FromSeconds(5));
        }

        if (npc == null)
        {
            Log.Warning("{Character} cannot find {Npc} in town", member.Name, code);
            return false;
        }

        // Some NPCs are no map preset - Cain stands in the camp only once freed - so the path above found
        // nothing; walk to where the NPC actually is.
        if (npc.Location.Distance(game.Me.Location) > 15)
        {
            await MovementHelpers.MoveToLocation(game, pathingService, mapApiService, npc.Location, MovementMode.Walking);
        }

        if (npc.Location.Distance(game.Me.Location) > 5)
        {
            await game.MoveToAsync(npc);
        }

        return npc.Location.Distance(game.Me.Location) <= 20;
    }
}
