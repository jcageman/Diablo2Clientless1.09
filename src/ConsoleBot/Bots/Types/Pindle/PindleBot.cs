using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Bots.Types.Pindle;

public class PindleBot : SingleClientBotBase, IBotInstance
{
    private readonly IPathingService _pathingService;
    private readonly ITownManagementService _townManagementService;

    public PindleBot(
        IOptions<BotConfiguration> config,
        IOptions<PindleConfiguration> pindleConfig,
        IExternalMessagingClient externalMessagingClient,
        IPathingService pathingService,
        IMuleService muleService,
        ITownManagementService townManagementService) : base(config.Value, pindleConfig.Value, externalMessagingClient, muleService)
    {
        _pathingService = pathingService;
        _townManagementService = townManagementService;
        // Identifying from the tome needs it in the inventory: unreserved it was cubed and sold.
        D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.TomeofIdentify);
    }

    public string GetName()
    {
        return "pindle";
    }

    public async Task Run()
    {
        var client = new Client();
        _externalMessagingClient.RegisterClient(client);
        await CreateGameLoop(client);
    }

    protected override async Task<bool> RunSingleGame(Client client)
    {
        if (client.Game.Me.Class != CharacterClass.Sorceress)
        {
            throw new NotSupportedException("Only sorceress is supported on Pindle");
        }

        if (client.Game.Act == Act.Act5)
        {
            var sellNpc = NPCHelpers.GetSellNPC(client.Game.Act);
            GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                var uniqueNPC = NPCHelpers.GetUniqueNPC(client.Game, sellNpc);
                return uniqueNPC != null;
            }, TimeSpan.FromSeconds(2));
        }

        var phase = Stopwatch.StartNew();
        // Loot is only cubed mid-pickup when it would not fit, so a filling inventory is emptied here,
        // safe in town: left full it blocked stashing and every trade of a mule pass. Not every game:
        // loot in the cube can only be identified with the cube open.
        if (!HasRoomForLargeItem(client.Game.Inventory))
        {
            InventoryHelpers.MoveInventoryItemsToCube(client.Game);
        }
        // Cain stands 45 from where a game starts and the walk there was most of every long town visit;
        // the tome identifies in place. Malah stays the vendor: she is the nearest, and pathing to
        // Anya beside the portal fails outright.
        var townManagementOptions = new TownManagementOptions(_accountConfig, Act.Act5)
        {
            IdentifyWithTome = true,
        };

        var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
        if(townTaskResult.ShouldMule)
        {
            NeedsMule = true;
            return true;
        }
        else if(!townTaskResult.Succes)
        {
            return false;
        }

        Log.Information($"Has merc with id {client.Game.Me.MercId}");
        Log.Information("Computed maximum life {MaxLife}, mana {MaxMana}, now {Life} and {Mana}", client.Game.Me.MaxLife, client.Game.Me.MaxMana, client.Game.Me.Life, client.Game.Me.Mana);
        Log.Information("Pindle phase town done in {Seconds:F1}s", phase.Elapsed.TotalSeconds);
        phase.Restart();

        if (client.Game.Area != Area.Harrogath)
        {
            Log.Information("Taking Harrogath Waypoint");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.Harrogath))
            {
                Log.Information("Taking Harrogath waypoint failed");
                return false;
            }
        }

        var pathToPortal = await _pathingService.GetPathToLocation(client.Game, new Point(5117, 5119), MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPortal, MovementMode.Teleport))
        {
            Log.Error($"Teleporting to {EntityCode.RedTownPortal}  failed");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (client.Game.Area == Area.NihlathaksTemple)
            {
                return true;
            }

            var pindlePortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(t => t.TownPortalArea == Area.NihlathaksTemple);
            if (pindlePortal == null)
            {
                return false;
            }
            await client.Game.MoveToAsync(pindlePortal);

            client.Game.InteractWithEntity(pindlePortal);
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                return client.Game.Area == Area.NihlathaksTemple;
            }, TimeSpan.FromSeconds(0.2)))
            {
                return false;
            }

            return true;

        }, TimeSpan.FromSeconds(15)))
        {
            Log.Error($"Moving to {Area.NihlathaksTemple} failed");
            return false;
        }

        Log.Information("Pindle reached the temple after {Seconds:F1}s", phase.Elapsed.TotalSeconds);
        if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
        {
            client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
        }

        if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
        {
            client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
        }

        var orbLevel = SkillLevel(client, Skill.FrozenOrb);
        var novaLevel = SkillLevel(client, Skill.Nova);
        var pointBlank = novaLevel > orbLevel;
        Log.Information("Pindle fought {Strategy}: frozen orb {Orb}, nova {Nova}", pointBlank ? "point blank with nova" : "from range with frozen orb", orbLevel, novaLevel);
        // A nova sorceress stops about nine short of where he stands: inside nova reach, outside his death
        // blast, and the mercenary walks in and takes his attention.
        var pindleLocation = pointBlank ? new Point(10060, 13225) : new Point(10061, 13234);
        var pathToPindle = await _pathingService.GetPathToLocation(client.Game, pindleLocation, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPindle, MovementMode.Teleport))
        {
            Log.Error($"Teleporting to Pindle failed");
            return false;
        }

        Log.Information("Pindle phase travel done in {Seconds:F1}s", phase.Elapsed.TotalSeconds);
        phase.Restart();
        var pindle = client.Game.GetNPCsByCode(NPCCode.UnholyCorpse).First(u => u.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
        // Every nova that hits a Lightning Enchanted Pindleskin fires charged bolts back: the biggest life
        // dips of the run (173 on average, 15% of kills over 400). From the edge of nova reach the dips
        // fell by a quarter to a third over 51 kills, and the kills were no slower.
        var range = pindle.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted) ? NovaRange.Far : NovaRange.Near;
        Log.Information($"Killing Pindle");
        Log.Information("Pindleskin rolled {Enchantments}, {Plan}", string.Join(", ", pindle.MonsterEnchantments), DeathHurts(pindle) ? "stepping out before he dies" : "staying on him");
        var lastTeleportTime = DateTime.MinValue;
        var lastStepOut = DateTime.MinValue;
        var attack = new AttackState();
        var lowestLife = pindle.LifePercentage;
        var lifeLastDropped = DateTime.Now;
        var gaveUp = false;
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (!client.Game.IsInGame())
                {
                    return true;
                }

                if (pindle.LifePercentage < lowestLife)
                {
                    lowestLife = pindle.LifePercentage;
                    lifeLastDropped = DateTime.Now;
                }

                if (KillIsHopeless(phase.Elapsed, lowestLife, DateTime.Now - lifeLastDropped))
                {
                    gaveUp = true;
                    return true;
                }

                // Only a previous step holds the next one back: coming back into nova reach must never stop her
                // stepping out again, or he walks up and dies next to her in the second after.
                if (DateTime.Now - lastStepOut > TimeSpan.FromMilliseconds(500) && await StepOutOfDeathExplosion(client, pindle))
                {
                    lastStepOut = DateTime.Now;
                    lastTeleportTime = DateTime.Now;
                    attack.HandChanged();
                }
                else if (!pointBlank && await MoveToAlternativeLocationIfNeeded(client, pindle, lastTeleportTime))
                {
                    lastTeleportTime = DateTime.Now;
                    attack.HandChanged();
                }
                else if (pointBlank && DateTime.Now - lastTeleportTime > TimeSpan.FromSeconds(1) && await KeepNovaRange(client, pindle, range))
                {
                    lastTeleportTime = DateTime.Now;
                    attack.HandChanged();
                }
                else
                {
                    Attack(client, pindle, attack, staticWorks: !pointBlank && pindle.LifePercentage > 50, range.Reach);
                }

                // Dying is enough: the dead state follows about 0.7s later, and his items drop with the dying one.
                if (!IsAlive(pindle))
                {
                    return true;
                }

                await Task.Delay(30);
                return false;
            },
            KillLimit) || gaveUp)
        {
            Log.Warning($"Killing Pindle failed at location {client.Game.Me.Location}");
            return false;
        }

        Log.Information("Pindle phase kill done in {Seconds:F1}s", phase.Elapsed.TotalSeconds);
        phase.Restart();
        // A repeat cast blocks picking up, so the hand is taken off nova before looting.
        if (client.Game.Me.HasSkill(Skill.Teleport))
        {
            client.Game.ChangeSkill(Skill.Teleport, Hand.Right);
        }

        // His death nova lands four frames after he starts dying; on any other roll she walks straight in.
        if (DeathHurts(pindle))
        {
            await Task.Delay(250);
        }

        var pickedUp = await PickupNearbyItems(client);
        if (pickedUp == null)
        {
            Log.Warning($"Failed to pickup items at location {client.Game.Me.Location}");
            return false;
        }

        Log.Information("Pindle phase loot done in {Seconds:F1}s", phase.Elapsed.TotalSeconds);
        var onPerson = client.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson, 0);
        var inStash = client.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldInStash, 0);
        var rejuvenations = client.Game.Belt.NumOfRejuvenationPotions()
            + client.Game.Inventory.Items.Count(i => i.Classification == ClassificationType.RejuvenationPotion);
        Log.Information("Run yield: {Items} items picked up, gold {OnPerson} on person, {InStash} in stash, {Total} total, {Rejuvenations} rejuvenations",
            pickedUp, onPerson, inStash, onPerson + inStash, rejuvenations);
        return true;
    }

    /// <summary>Frozen Orb has a hard one second cast delay; a cast inside it is dropped.</summary>
    private static readonly TimeSpan FrozenOrbInterval = TimeSpan.FromMilliseconds(1050);

    private static readonly TimeSpan RepeatRefreshInterval = TimeSpan.FromMilliseconds(300);

    

    private const double MinionItemClearance = 7;


    // Pindleskin is fire enchanted, and a unique fire enchanted monster explodes four frames after it
    // starts dying, for up to its base life as physical and again as fire, within six of its corpse in
    // Hell. It took 495 of a 1014 life sorceress standing three away. His minions do not explode.
    // His life arrives in jumps between hit packets: at 40% the step often came at 1-9%, too late for
    // the blast, and two of 43 games chickened against none of 65 at 60%.
    private const double DeathExplosionClearance = 8;

    private const double NovaReach = 10;

    private const int DeathExplosionLifePercent = 60;

    private sealed class AttackState
    {
        public DateTime LastOrb { get; set; } = DateTime.MinValue;

        public DateTime LastRepeat { get; set; } = DateTime.MinValue;

        public bool StaticCast { get; set; }

        public void HandChanged()
        {
            StaticCast = false;
            LastRepeat = DateTime.MinValue;
        }
    }

    private static void Attack(Client client, WorldObject target, AttackState state, bool staticWorks, double novaReach)
    {
        var game = client.Game;
        var distance = target.Location.Distance(game.Me.Location);
        if (game.Me.HasSkill(Skill.FrozenOrb) && DateTime.Now - state.LastOrb >= FrozenOrbInterval)
        {
            game.UseRightHandSkillOnEntity(Skill.FrozenOrb, target);
            state.LastOrb = DateTime.Now;
            state.HandChanged();
        }
        // A repeat cast goes on by itself until the next orb changes the hand, so once per orb.
        // Static Field stops taking life at half in expansion Hell.
        else if (staticWorks && !state.StaticCast && game.Me.HasSkill(Skill.StaticField) && distance < 30)
        {
            game.RepeatRightHandSkillOnLocation(Skill.StaticField, game.Me.Location);
            state.StaticCast = true;
        }
        else if (game.Me.HasSkill(Skill.Nova) && distance < novaReach && DateTime.Now - state.LastRepeat >= RepeatRefreshInterval)
        {
            game.RepeatRightHandSkillOnLocation(Skill.Nova, game.Me.Location);
            state.LastRepeat = DateTime.Now;
        }
    }

    private static int SkillLevel(Client client, Skill skill)
        => client.Game.Me.Skills.GetValueOrDefault(skill) + client.Game.Me.ItemSkills.GetValueOrDefault(skill);

    private static bool HasRoomForLargeItem(D2NG.Core.D2GS.Items.Containers.Container inventory)
    {
        for (ushort y = 0; y < inventory.Height; y++)
        {
            for (ushort x = 0; x < inventory.Width; x++)
            {
                if (inventory.SpaceIsFree(new Point(x, y), 2, 4))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A Lightning Enchanted and Stone Skin roll resists both the nova and the mercenary: one was down to
    // 5% when 25 seconds ran out, and the run's loot went with it. Past 25 seconds the kill goes on only
    // while he is low and still losing life; otherwise a new game is the quicker loot.
    private static readonly TimeSpan KillLimit = TimeSpan.FromSeconds(40);

    private static bool KillIsHopeless(TimeSpan fighting, double lowestLife, TimeSpan sinceLifeDropped)
        => fighting > TimeSpan.FromSeconds(25) && (lowestLife > 30 || sinceLifeDropped > TimeSpan.FromSeconds(5));

    private static bool IsAlive(WorldObject monster) => monster.State != EntityState.Dead && monster.State != EntityState.Dieing;

    private static List<WorldObject> MinionsNear(Client client, IReadOnlyCollection<Point> spots, double radius)
        => client.Game.GetNPCsByCode(NPCCode.UnholyCorpse)
            .Where(IsAlive)
            .Where(m => m.Location.Distance(client.Game.Me.Location) < radius || spots.Any(s => m.Location.Distance(s) < radius))
            .ToList();

    private static async Task<bool> StepOutOfDeathExplosion(Client client, WorldObject pindle)
    {
        var me = client.Game.Me.Location;
        if (!DeathHurts(pindle) || !IsAlive(pindle) || pindle.LifePercentage > DeathExplosionLifePercent || pindle.Location.Distance(me) >= DeathExplosionClearance)
        {
            return false;
        }

        // Teleport does nothing closer than ten.
        var spot = new[] { new Point(10061, 13234), new Point(10059, 13219) }
            .Where(p => p.Distance(me) >= 10)
            .OrderByDescending(p => p.Distance(pindle.Location))
            .FirstOrDefault();
        if (spot == null)
        {
            return false;
        }

        Log.Information("Stepping away from Pindleskin at {Life:F0}% life, {Distance:F0} away", pindle.LifePercentage, pindle.Location.Distance(me));
        return await client.Game.TeleportToLocationAsync(spot);
    }

    /// <summary>Where a nova sorceress stands: Keep is the distance she goes to, Reach the furthest she casts
    /// from, TooClose where she backs off again (0 never).</summary>
    private sealed record NovaRange(double Keep, double Reach, double TooClose)
    {
        public static readonly NovaRange Near = new((DeathExplosionClearance + NovaReach) / 2, NovaReach, 0);

        public static readonly NovaRange Far = new(11.5, 13, 7);
    }

    // Once out of the blast she has to come back: Pindleskin stays on the mercenary, and a nova sorceress
    // standing off cast nothing until the kill timed out. With a far range she also backs off when he
    // walks up to her.
    private static async Task<bool> KeepNovaRange(Client client, WorldObject pindle, NovaRange range)
    {
        var me = client.Game.Me.Location;
        var distance = pindle.Location.Distance(me);
        if (!IsAlive(pindle) || (distance < range.Reach && distance >= range.TooClose))
        {
            return false;
        }

        var keep = range.Keep / Math.Max(distance, 1);
        var spot = new Point(
            (ushort)Math.Round(pindle.Location.X + (me.X - pindle.Location.X) * keep),
            (ushort)Math.Round(pindle.Location.Y + (me.Y - pindle.Location.Y) * keep));
        Log.Information("Moving to {Distance:F0} from Pindleskin at {Life:F0}% life", spot.Distance(pindle.Location), pindle.LifePercentage);
        return spot.Distance(me) >= 10 ? await client.Game.TeleportToLocationAsync(spot) : await client.Game.MoveToAsync(spot);
    }

    // Life lost in the 0.6s after his death over about 900 games: with Cold Enchanted his death also casts
    // a nova, 145-173 on average; Cursed without it cost nothing (0 over 42 games standing on him).
    // Only Cold Enchanted is worth stepping out for.
    private static bool DeathHurts(WorldObject pindle)
        => pindle.MonsterEnchantments.Contains(MonsterEnchantment.ColdEnchanted);

    private static async Task<bool> MoveToAlternativeLocationIfNeeded(Client client, WorldObject pindle, DateTime lastTeleportTime)
    {
        if (DateTime.Now.Subtract(lastTeleportTime) > TimeSpan.FromSeconds(4))
        {
            if (client.Game.Me.Life / (double)client.Game.Me.MaxLife < 0.8 || pindle.Location.Distance(client.Game.Me.Location) < 10)
            {
                var startPoint = new Point(10061, 13234);
                var alternativePoint = new Point(10059, 13219);
                if (startPoint.Distance(client.Game.Me.Location) < alternativePoint.Distance(client.Game.Me.Location))
                {
                    Log.Information($"Moving to alternative location due to low life or close pindle");
                    return await client.Game.TeleportToLocationAsync(alternativePoint);
                }
                else
                {
                    Log.Information($"Moving back to start location due to low life or close pindle");
                    return await client.Game.TeleportToLocationAsync(startPoint);
                }
            }
        }

        return false;
    }

    /// <returns>The items picked up, gold not counted, or null when the game was left.</returns>
    private static async Task<int?> PickupNearbyItems(Client client)
    {
        var pickedUp = 0;
        PickitAudit.LogGroundItems(client.Game, "Pindle", shouldPickupGoldItems: true);
        var pickupItems = client.Game.Items.Values.Where(i => i.Ground && D2NG.Pickit.Pickit.ShouldPickupItem(client.Game, i, true)).OrderBy(n => n.Location.Distance(client.Game.Me.Location)).ToList();
        var revCountInInventory = client.Game.Inventory.Items.Count(i => i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion);
        if (revCountInInventory < 5)
        {
            var revs = client.Game.Items.Values
                .Where(i => i.Ground && (i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion))
                .OrderBy(n => n.Location.Distance(client.Game.Me.Location))
                .ToList();
            Log.Information($"Killed Pindle, picking up {revs.Count} rev potions since only {revCountInInventory} in inventory");
            pickupItems.AddRange(revs);
        }
        Log.Information($"Killed Pindle, picking up {pickupItems.Count} items ");
        // The pickup starts the moment he begins dying, so a drop can still be on its way: the ground is
        // looked at once more after the first round.
        var tried = new HashSet<uint>();
        for (var round = 0; round < 2; round++)
        {
            if (round == 1)
            {
                pickupItems = client.Game.Items.Values
                    .Where(i => i.Ground && !tried.Contains(i.Id) && D2NG.Pickit.Pickit.ShouldPickupItem(client.Game, i, true))
                    .OrderBy(n => n.Location.Distance(client.Game.Me.Location))
                    .ToList();
                if (pickupItems.Count == 0)
                {
                    break;
                }

                Log.Information("Picking up {Count} items that dropped after the first look", pickupItems.Count);
            }

            foreach (var item in pickupItems)
            {
                tried.Add(item.Id);
                if (item.Location.Distance(client.Game.Me.Location) > 30)
                {
                    Log.Warning($"Skipped {item} since it's at location {item.Location}, while player at {client.Game.Me.Location}");
                    continue;
                }

                if (!client.Game.IsInGame())
                {
                    return null;
                }

                if (MinionsNear(client, [item.Location], MinionItemClearance).Any(m => m.Location.Distance(item.Location) < MinionItemClearance))
                {
                    Log.Warning("Skipped {Item} at {Location}, a minion of Pindleskin is still standing next to it", item.GetFullDescription(), item.Location);
                    continue;
                }


                // His minions get up again while she loots, and a risen one hit her for up to 280 a time, so
                // one nearby gets a nova first. Only a single cast: a repeat cast kept going through the
                // pickups made the server ignore every pick up and run to the item for nine seconds.
                if (client.Game.Me.HasSkill(Skill.Nova) && MinionsNear(client, [], NovaReach).Count > 0)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                }

                // Cubing takes three round trips per item, about 0.6s; only when the inventory is full.
                if (client.Game.Inventory.FindFreeSpace(item) == null)
                {
                    InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                }

                if (client.Game.Inventory.FindFreeSpace(item) == null)
                {
                    Log.Warning($"Skipped {item.GetFullDescription()} since inventory is full");
                    continue;
                }

                if (!await GeneralHelpers.TryWithTimeout((async retryCount =>
                {
                    // His drops land in one pile: teleport onto it, then everything within five is picked
                    // straight up. Running the last five to ten took about half a second an item.
                    var distance = client.Game.Me.Location.Distance(item.Location);
                    if (distance >= 5)
                    {
                        if (!await client.Game.TeleportToLocationAsync(item.Location, minDistance: 3) && distance < 10)
                        {
                            await client.Game.MoveToAsync(item);
                        }

                        return false;
                    }
                    else
                    {
                        client.Game.PickupItem(item);
                        Thread.Sleep(50);
                        if (client.Game.Inventory.FindItemById(item.Id) == null && !item.IsGold)
                        {
                            return false;
                        }
                    }

                    return true;
                }), TimeSpan.FromSeconds(3)))
                {
                    Log.Warning($"Picking up item {item.GetFullDescription()} at location {item.Location} from location {client.Game.Me.Location} failed");
                }
                else if (!item.IsGold)
                {
                    pickedUp++;
                }
            }
        }

        return pickedUp;
    }
}
