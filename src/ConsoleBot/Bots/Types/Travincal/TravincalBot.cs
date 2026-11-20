using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using Attribute = D2NG.Core.D2GS.Players.Attribute;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Travincal;

public class TravincalBot : SingleClientBotBase, IBotInstance
{
    private readonly IPathingService _pathingService;
    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private readonly IMapApiService _mapApiService;
    private readonly TravincalConfiguration _travConfig;

    public TravincalBot(IOptions<BotConfiguration> config, IOptions<TravincalConfiguration> travconfig, IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
        IMuleService muleService, ITownManagementService townManagementService, IAttackService attackService, IMapApiService mapApiService)
    : base(config.Value, travconfig.Value, externalMessagingClient, muleService)
    {
        _travConfig = travconfig.Value;
        _pathingService = pathingService;
        _townManagementService = townManagementService;
        _attackService = attackService;
        _mapApiService = mapApiService;
    }

    public string GetName()
    {
        return "travincal";
    }

    public async Task Run()
    {
        if (_travConfig.IdentifyWithTome)
        {
            D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.TomeofIdentify);
        }

        var client = new Client();
        _externalMessagingClient.RegisterClient(client);
        await CreateGameLoop(client);
    }

    private static readonly TimeSpan GameDeadline = TimeSpan.FromMinutes(4);

    private static readonly TimeSpan TownDeadline = TimeSpan.FromMinutes(20);

    private int _itemsPickedUp;

    private DateTime _runStarted;

    /// <summary>A potion trip mid-run takes about 40 s; the run's deadline does not cancel it, so it starts only with this much left.</summary>
    private static readonly TimeSpan TownTripLeast = TimeSpan.FromSeconds(60);

    protected override async Task<bool> RunSingleGame(Client client)
    {
        _itemsPickedUp = 0;
        client.Game.MovementSpeedFactor = _travConfig.MovementSpeedFactor;
        if (client.Game.Me.Class != CharacterClass.Barbarian)
        {
            throw new NotSupportedException("Only barbarian is supported on travincal");
        }

        if (!await GeneralHelpers.WithDeadline(() => PerformTownTasks(client), TownDeadline, "Travincal town tasks"))
        {
            return false;
        }

        if (NeedsMule)
        {
            return true;
        }

        if (!await GeneralHelpers.WithDeadline(() => RunTravincal(client), GameDeadline, "Travincal run"))
        {
            return NeedsMule;
        }

        return await GeneralHelpers.WithDeadline(() => FinishInTown(client), TownDeadline, "Travincal town tasks after the run");
    }

    private async Task<bool> PerformTownTasks(Client client)
    {
        var townTaskResult = await _townManagementService.PerformTownTasks(client, TownOptions(client));
        if (townTaskResult.ShouldMule)
        {
            NeedsMule = true;
            return true;
        }

        if (!townTaskResult.Succes)
        {
            Log.Warning($"Town tasks failed for {client.Game.Me.Name}, taking a new game");
            return false;
        }

        return true;
    }

    private async Task<bool> RunTravincal(Client client)
    {
        _runStarted = DateTime.Now;
        Log.Information("Taking travincal wp");
        if (!await _townManagementService.TakeWaypoint(client, Waypoint.Travincal))
        {
            Log.Information("Taking trav waypoint failed");
            return false;
        }

        Log.Information("Doing bo");
        if(!BarbBo(client.Game))
        {
            return false;
        }

        Log.Information("Walking to council members");
        
        var pathToCouncil = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.CompellingOrb, 23, 25, MovementMode.Walking);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToCouncil, MovementMode.Walking))
        {
            Log.Information($"Walking to councile members failed");
            return false;
        }

        if (!await EnsureBeltPotions(client))
        {
            return false;
        }

        Log.Information("Kill council members");
        if (!await KillCouncilMembers(client))
        {
            Log.Information("Kill council members failed");
            return false;
        }

        Log.Information("Using find item");
        if (!await UseFindItemOnCouncilMembers(client.Game))
        {
            Log.Information("Finditem failed");
            return false;
        }

        Log.Information("Picking up left over items");
        if (!await PickupNearbyItems(client.Game, 300))
        {
            Log.Information("Pickup nearby items 1 failed");
        }

        if (!await PickupNearbyItems(client.Game, 300))
        {
            Log.Information("Pickup nearby items 2 failed");
        }

        if (_travConfig.IdentifyWithTome)
        {
            InventoryHelpers.SendIdentifyRequests(client.Game);
        }

        Log.Information("Moving to town");
        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Information("Move to town failed");
            return false;
        }

        return true;
    }

    private static (int BeltHealing, int BeltMana, int InventoryHealing, int InventoryMana) PotionCounts(Game game)
        => (game.Belt.NumOfHealthPotions(), game.Belt.NumOfManaPotions(),
            game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion),
            game.Inventory.Items.Count(i => i.Classification == ClassificationType.ManaPotion));

    private static CouncilPotionAction Decide((int BeltHealing, int BeltMana, int InventoryHealing, int InventoryMana) counts)
        => CouncilPotionCheck.Decide(counts.BeltHealing, counts.BeltMana, counts.InventoryHealing, counts.InventoryMana);

    /// <summary>
    /// Before the council: a belt without a healing or a mana potion is refilled from the inventory, and when that leaves
    /// it still short the character restocks in town and comes back by its portal. False ends the run.
    /// </summary>
    private async Task<bool> EnsureBeltPotions(Client client)
    {
        var game = client.Game;
        var before = PotionCounts(game);
        var action = Decide(before);
        if (action == CouncilPotionAction.Fight)
        {
            return true;
        }

        RefillBeltFromInventory(game, before.BeltHealing == 0, before.BeltMana == 0);
        var refilled = PotionCounts(game);
        var after = Decide(refilled);
        var outcome = after == CouncilPotionAction.Fight ? "refilled from the inventory" : "going to town for potions";
        Log.Information("{Character} reached the council with {BeltHealing} healing and {BeltMana} mana potions in the belt ({InventoryHealing} and {InventoryMana} in the inventory): {Outcome}, belt now {NowHealing} healing and {NowMana} mana",
            game.Me.Name, before.BeltHealing, before.BeltMana, before.InventoryHealing, before.InventoryMana, outcome, refilled.BeltHealing, refilled.BeltMana);
        D2NG.Core.Tracing.TraceEvents.Decision(game, "council potion check",
            ("action", after == CouncilPotionAction.Fight ? "refill" : "town"),
            ("beltHealing", before.BeltHealing), ("beltMana", before.BeltMana),
            ("inventoryHealing", before.InventoryHealing), ("inventoryMana", before.InventoryMana),
            ("nowHealing", refilled.BeltHealing), ("nowMana", refilled.BeltMana));
        if (after == CouncilPotionAction.Fight)
        {
            return true;
        }

        var left = GameDeadline - (DateTime.Now - _runStarted);
        if (left < TownTripLeast)
        {
            Log.Warning("{Character} has {Seconds:F0}s of the run left, too little for a town trip for potions; ending the run", game.Me.Name, left.TotalSeconds);
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Warning("{Character} could not take a portal to town for potions, ending the run", game.Me.Name);
            return false;
        }

        // The portal lands in Kurast Docks: shopping there keeps the portal back to Travincal in reach, where the
        // run's usual Act 4 options would take the waypoint away from it first.
        var town = await _townManagementService.PerformTownTasks(client, TownOptions(client, Act.Act3));
        if (town.ShouldMule)
        {
            Log.Information("{Character} needs to mule, ending the run", game.Me.Name);
            NeedsMule = true;
            return false;
        }

        if (!town.Succes)
        {
            Log.Warning("{Character} town tasks for potions failed, ending the run", game.Me.Name);
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToArea(client, game.Me, Area.Travincal))
        {
            Log.Warning("{Character} could not take the portal back to Travincal, ending the run", game.Me.Name);
            return false;
        }

        var restocked = PotionCounts(game);
        if (Decide(restocked) != CouncilPotionAction.Fight)
        {
            Log.Warning("{Character} still has {BeltHealing} healing and {BeltMana} mana potions in the belt after town, ending the run",
                game.Me.Name, restocked.BeltHealing, restocked.BeltMana);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Moves inventory potions of the missing kinds into the belt. A belt column holds one kind and the server refuses a
    /// potion with no empty or same-kind column left, so each is sent only while one has room, and the first unanswered
    /// put ends it rather than waiting out the timeout again.
    /// </summary>
    private static void RefillBeltFromInventory(Game game, bool healing, bool mana)
    {
        var potions = game.Inventory.Items
            .Where(i => (healing && i.Classification == ClassificationType.HealthPotion) || (mana && i.Classification == ClassificationType.ManaPotion))
            .ToList();
        foreach (var potion in potions)
        {
            if (!BeltTakes(game, potion))
            {
                continue;
            }

            if (!game.PutItemInBelt(potion))
            {
                return;
            }
        }
    }

    private static bool BeltTakes(Game game, Item potion)
        => BeltPlacement.Column(InventoryHelpers.BeltPotions(game), (int)game.Belt.Height, InventoryHelpers.KindOf(potion)) != null;

    private async Task<bool> FinishInTown(Client client)
    {
        var townTaskResult = await _townManagementService.PerformTownTasks(client, TownOptions(client));
        if (townTaskResult.ShouldMule)
        {
            NeedsMule = true;
        }

        if (!townTaskResult.Succes)
        {
            Log.Warning($"Town tasks after the run failed for {client.Game.Me.Name}");
        }

        var onPerson = client.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson, 0);
        var inStash = client.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldInStash, 0);
        Log.Information("Run yield: {Items} items picked up, gold {OnPerson} on person, {InStash} in stash, {Total} total",
            _itemsPickedUp, onPerson, inStash, onPerson + inStash);

        Log.Information("Successfully finished game");
        return true;
    }

    private async Task<bool> PickupNearbyItems(Game game, double distance)
    {
        PickitAudit.LogGroundItems(game, "Travincal", shouldPickupGoldItems: true);
        var pickupItems = game.Items.Values.Where(i =>
        {
            return i.Ground && game.Me.Location.Distance(i.Location) < distance && D2NG.Pickit.Pickit.ShouldPickupItem(game, i, true);
        }).OrderBy(n => game.Me.Location.Distance(n.Location));

        foreach (var item in pickupItems)
        {
            if (!game.IsInGame())
            {
                return false;
            }

            InventoryHelpers.MoveInventoryItemsToCube(game);

            if (!item.IsGold && game.Inventory.FindFreeSpace(item) == null)
            {
                continue;
            }

            await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (game.Me.Location.Distance(item.Location) >= 5)
                {
                    var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, item.Location, MovementMode.Walking);
                    await MovementHelpers.TakePathOfLocations(game, pathNearest, MovementMode.Walking);
                    return false;
                }

                return true;
            }, TimeSpan.FromSeconds(3));

            if (game.Me.Location.Distance(item.Location) < 5)
            {
                game.PickupItem(item);
                _itemsPickedUp++;
            }
        }

        InventoryHelpers.MoveInventoryItemsToCube(game);
        return true;
    }

    private async Task<bool> UseFindItemOnCouncilMembers(Game game)
    {
        var remaining = GetCouncilMembers(game)
            .Where(m => m.State == EntityState.Dead || m.State == EntityState.Dieing)
            .ToList();

        while (remaining.Count > 0)
        {
            if (!game.IsInGame())
            {
                return false;
            }

            var nearest = remaining.MinBy(m => game.Me.Location.Distance(m.Location));
            remaining.Remove(nearest);

            if (nearest.Effects.Contains(EntityEffect.CorpseNoDraw))
            {
                continue;
            }

            if (!await ClassHelpers.FindItemOnDeadEnemy(game, _pathingService, _mapApiService, nearest))
            {
                Log.Warning("Failed to do find item on corpse");
            }

            await PickupNearbyItems(game, 10);
        }

        return true;
    }

    private async Task<bool> KillCouncilMembers(Client client)
    {
        var startTime = DateTime.Now;
        var givenUp = new HashSet<uint>();
        var progress = new KillProgress(NoProgressLimit);
        (uint Pick, uint Skipped)? lastSkip = null;
        List<WorldObject> aliveMembers;
        do
        {
            var passStarted = DateTime.Now;
            List<WorldObject> councilMembers = GetCouncilMembers(client.Game);
            aliveMembers = councilMembers
                .Where(n => n.State != EntityState.Dead && n.State != EntityState.Dieing && n.LifePercentage > 0 && !givenUp.Contains(n.Id))
                .OrderBy(n => client.Game.Me.Location.Distance(n.Location))
                .ToList();

            var nearest = aliveMembers.FirstOrDefault();
            if (nearest != null)
            {
                var me = client.Game.Me.Location;
                var (pick, skipped) = await WallAwareWhirl.ChooseMySideFirst(aliveMembers, n => me.Distance(n.Location), n => _attackService.IsOnMySide(client, n));
                if (skipped.Count > 0 && lastSkip != (pick.Id, skipped[0].Id))
                {
                    Log.Information("{Character} skipping {Code} {Id} at {Location} across a wall for {PickCode} {PickId} at {PickLocation}, from {MyLocation}",
                        client.Game.Me.Name, skipped[0].NPCCode, skipped[0].Id, skipped[0].Location, pick.NPCCode, pick.Id, pick.Location, me);
                    D2NG.Core.Tracing.TraceEvents.Decision(client.Game, "council member across wall skipped",
                        ("skipped", skipped[0].Id), ("skippedAt", skipped[0].Location), ("pick", pick.Id), ("pickAt", pick.Location), ("at", me));
                }

                lastSkip = skipped.Count > 0 ? (pick.Id, skipped[0].Id) : null;
                nearest = pick;
            }

            if (nearest != null)
            {
                var distance = client.Game.Me.Location.Distance(nearest.Location);
                if (progress.Stalled(nearest.Id, distance, nearest.LifePercentage, DateTime.Now))
                {
                    Log.Warning("{Character} got no closer to {Code} {Id} at {Location} and did not hurt it in {Seconds:F0}s, {Distance:F0} away at {MyLocation}, its life {Life:F0}%, state {State}; leaving it",
                        client.Game.Me.Name, nearest.NPCCode, nearest.Id, nearest.Location, NoProgressLimit.TotalSeconds, distance, client.Game.Me.Location, nearest.LifePercentage, nearest.State);
                    givenUp.Add(nearest.Id);
                    progress.GaveUp(DateTime.Now);
                    continue;
                }

                if (!client.Game.IsInGame())
                {
                    return false;
                }

                if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMinutes(2))
                {
                    Log.Information("Passed maximum elapsed time for killing council members");
                    return false;
                }

                if (client.Game.Me.Location.Distance(nearest.Location) > 10)
                {
                    var nearestFindItemMember = councilMembers
                    .Where(n => n.State == EntityState.Dead && !n.Effects.Contains(EntityEffect.CorpseNoDraw))
                    .OrderBy(n => client.Game.Me.Location.Distance(n.Location))
                    .FirstOrDefault();
                    if (nearestFindItemMember != null && nearestFindItemMember.Location.Distance(client.Game.Me.Location) <= 10)
                    {
                        await ClassHelpers.FindItemOnDeadEnemy(client.Game, _pathingService, _mapApiService, nearestFindItemMember);
                    }

                    var pathNearest = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.Travincal, client.Game.Me.Location, nearest.Location, MovementMode.Walking);
                    pathNearest = pathNearest.SkipLast(2).ToList();
                    if (pathNearest.Count > 0 && !await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                    {
                        Log.Warning($"Walking to Council Member from {client.Game.Me.Location} to {pathNearest.Last()} failed at {client.Game.Me.Location}");
                    }
                }

                await _attackService.AssistPlayer(client, client.Game.Me, wallAwareWhirl: true, targetId: nearest.Id);
            }

            var pass = DateTime.Now - passStarted;
            if (pass < MinimumPass)
            {
                await Task.Delay(MinimumPass - pass);
            }
        } while (aliveMembers.Count != 0);

        return true;
    }

    /// <summary>Mana Burn council packs drained the belt in a fight and ended the game; these ride in the inventory.</summary>
    private const int ManaSpares = 4;

    private TownManagementOptions TownOptions(Client client, Act act = Act.Act4) => new(_accountConfig, act)
    {
        IdentifyWithTome = _travConfig.IdentifyWithTome,
        ManaPotionsToBuy = Math.Max(0, _accountConfig.ManaPotionTarget(client.Game.Belt.Height) + ManaSpares - InventoryHelpers.GetTotalManaPotions(client.Game)),
    };

    public static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan MinimumPass = TimeSpan.FromMilliseconds(100);

    /// <summary>The council members the run kills: CouncilMember3 is not among them.</summary>
    public static readonly IReadOnlyList<NPCCode> CouncilCodes = [NPCCode.CouncilMember1, NPCCode.CouncilMember2];

    private static List<WorldObject> GetCouncilMembers(Game game)
    {
        var councilMembers = game.GetNPCsByCode(CouncilCodes[0]);
        councilMembers.AddRange(game.GetNPCsByCode(CouncilCodes[1]));
        return councilMembers;
    }

    private static bool BarbBo(Game game)
    {
        if(!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.UseRightHandSkillOnLocation(Skill.BattleCommand, game.Me.Location);
            Thread.Sleep(200);
            return game.Me.Effects.ContainsKey(EntityEffect.Battlecommand);
        }, TimeSpan.FromSeconds(3)))
        {
            Log.Warning("Battle command failed");
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.UseRightHandSkillOnLocation(Skill.BattleOrders, game.Me.Location);
            Thread.Sleep(200);
            return game.Me.Effects.ContainsKey(EntityEffect.BattleOrders);
        }, TimeSpan.FromSeconds(3)))
        {
            Log.Warning("Battle orders failed");
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.UseRightHandSkillOnLocation(Skill.Shout, game.Me.Location);
            Thread.Sleep(200);
            return game.Me.Effects.ContainsKey(EntityEffect.Shout);
        }, TimeSpan.FromSeconds(3)))
        {
            Log.Warning("Shout failed");
            return false;
        }

        // The buffs raise maximum life and mana without raising the current values on 1.09. The
        // chicken service sees that maximum change and drinks one potion to lift the bar, so there
        // is nothing to do here.
        return true;
    }
}
