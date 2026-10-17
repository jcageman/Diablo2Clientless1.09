using ConsoleBot.Attack;
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
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Baal;

public class BaalBot : MultiClientBotBase
{
    // The throne only raises the next wave while no monster is alive within 64 of it (D2MOO
    // AITHINK_Fn134_BaalThrone), so anything left standing in the hall stalls the waves for good. Every
    // walkable cell that close is the hall or the open mouth of the entrance corridor; a larger radius
    // starts reaching rooms behind the hall walls.
    private const int ThroneWaveRadius = 64;
    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private readonly BaalConfiguration _baalConfig;
    private uint? BoClientPlayerId;
    private uint? PortalClientPlayerId;
    private readonly ConcurrentDictionary<string, bool> ShouldFollow = new();
    private readonly ConcurrentDictionary<string, bool> RefilledThisGame = new();
    private readonly ConcurrentDictionary<string, (Point, CancellationTokenSource)> FollowTasks = new();

    public BaalBot(IOptions<BotConfiguration> config, IOptions<BaalConfiguration> baalconfig,
        IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
        ITownManagementService townManagementService,
        IAttackService attackService,
        IMuleService muleService
        ) : base(config, baalconfig, externalMessagingClient, muleService, pathingService)
    {
        _townManagementService = townManagementService;
        _attackService = attackService;
        _baalConfig = baalconfig.Value;
    }

    public override string GetName()
    {
        return "baal";
    }

    protected override void PostInitializeClient(Client client, AccountConfig accountCharacter)
    {
        ShouldFollow.TryAdd(accountCharacter.Character.ToLower(), false);
        FollowTasks.TryAdd(accountCharacter.Character.ToLower(), (null, new CancellationTokenSource()));
        client.OnReceivedPacketEvent(InComingPacket.EntityMove, async (packet) =>
        {
            var entityMovePacket = new EntityMovePacket(packet);
            if (entityMovePacket.UnitType == EntityType.Player && entityMovePacket.UnitId == PortalClientPlayerId && ShouldFollowLeadClient(client))
            {
                await FollowToLocation(client, entityMovePacket.MoveToLocation);
            }
        });

        client.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, async (packet) =>
        {
            var reassignPlayerPacket = new ReassignPlayerPacket(packet);
            if (reassignPlayerPacket.UnitType == EntityType.Player && reassignPlayerPacket.UnitId == PortalClientPlayerId && ShouldFollowLeadClient(client))
            {
                await FollowToLocation(client, reassignPlayerPacket.Location);
            }
        });
        client.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, async (packet) =>
        {
            var partyAutomapInfoPacket = new PartyAutomapInfoPacket(packet);
            if (partyAutomapInfoPacket.Id == PortalClientPlayerId && ShouldFollowLeadClient(client))
            {
                await FollowToLocation(client, partyAutomapInfoPacket.Location);
            }
        });
    }

    protected override async Task<bool> PrepareForRun(Client client, AccountConfig account)
    {
        // Following is switched on for the Baal fight; left on, the paladin chased the portal sorceress around
        // town next game while also walking to Malah, and the two run streams kept him from ever reaching her.
        StopFollowingLead(client);
        RefilledThisGame.TryRemove(client.Game.Me.Name, out _);

        var townManagementOptions = new TownManagementOptions(account,  Act.Act5);

        if(client.Game.Act == Act.Act5)
        {
            var sellNpc = NPCHelpers.GetSellNPC(client.Game.Act);
            GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                var uniqueNPC = NPCHelpers.GetUniqueNPC(client.Game, sellNpc);
                return uniqueNPC != null;
            }, TimeSpan.FromSeconds(2)); 
        }

        var isPortalCharacter = _baalConfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase);
        var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
        if (townTaskResult.ShouldMule)
        {
            ClientsNeedingMule.Add(client.LoggedInUserName());
        }
        if (!townTaskResult.Succes)
        {
            return false;
        }

        if (isPortalCharacter)
        {
            if (!await CreateThroneRoomTp(client))
            {
                return false;
            }
        }
        else
        {
            var tpLocation = new Point(5100, 5025);
            var movementMode = MovementHelpers.PreferredMovement(client.Game);
            var pathBack = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.Harrogath, client.Game.Me.Location, tpLocation, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBack, movementMode))
            {
                Log.Warning($"Client {client.Game.Me.Name} {movementMode} back failed at {client.Game.Me.Location}");
                return false;
            }
        }

        return true;
    }

    protected override Task PostInitializeAllJoined(List<Client> clients)
    {
        var boClient = clients.Aggregate((agg, client) =>
        {
            var boClient = client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0);
            var boAgg = agg?.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) ?? 0;
            if (boClient > 0 && boClient > boAgg)
            {
                return client;
            }

            return agg;
        });

        if (boClient == null)
        {
            Log.Error($"Expected at least bo barb in game");
            return Task.CompletedTask;
        }

        BoClientPlayerId = boClient.Game.Me.Id;
        var portalClient = clients.First(c => _baalConfig.PortalCharacterName.Equals(c.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        PortalClientPlayerId = portalClient.Game.Me.Id;
        return Task.CompletedTask;
    }

    protected override async Task<bool> PerformRun(Client client, AccountConfig account)
    {
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            return await _townManagementService.TakeTownPortalToArea(client, client.Game.Players.First(p => p.Id == PortalClientPlayerId), Area.ThroneOfDestruction);
        }, TimeSpan.FromSeconds(30)))
        {
            Log.Warning($"Client {client.Game.Me.Name} stopped waiting throne run to start");
            NextGame.TrySetResult(true);
            return false;
        }

        if (client.Game.Me.Id == PortalClientPlayerId && !await _townManagementService.CreateTownPortal(client))
        {
            NextGame.TrySetResult(true);
            return false;
        }

        if (!await SetupBo(client))
        {
            NextGame.TrySetResult(true);
            return false;
        }

        await GetTaskForWave(client, account);

        if(!client.Game.IsInGame())
        {
            NextGame.TrySetResult(true);
            return false;
        }

        if (client.Game.Me.Id == PortalClientPlayerId)
        {
            if (!await CreateBaalPortal(client))
            {
                NextGame.TrySetResult(true);
                return false;
            }
            Log.Information($"Client {client.Game.Me.Name} created baal portal");
        }
        else
        {
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(100);
                return await _townManagementService.TakeTownPortalToTown(client);
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Client {client.Game.Me.Name} moving to town failed");
                NextGame.TrySetResult(true);
            }
        }

        await GetTaskForBaal(client, account);
        return true;
    }

    /// <summary>
    /// Takes Baal's own portal behind the throne. The portal character calls it as soon as the last wave is
    /// cleared and keeps clicking until the portal opens, instead of waiting for the throne to empty first.
    /// </summary>
    private async Task<bool> EnterWorldstoneChamber(Client client, MovementMode movementMode, TimeSpan clickFor)
    {
        var baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await Task.Delay(100);
            baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
            if (baalPortal == null)
            {
                return false;
            }
            var pathBaalPortal = await _pathingService.GetPathToLocation(client.Game, baalPortal.Location, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBaalPortal, movementMode))
            {
                Log.Warning($"Client {client.Game.Me.Name} {movementMode} to {EntityCode.BaalPortal} failed at {client.Game.Me.Location}");
            }

            return true;
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Client {client.Game.Me.Name} finding and moving to {EntityCode.BaalPortal} failed at {client.Game.Me.Location}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await client.Game.MoveToAsync(baalPortal);

            if (retryCount > 0 && retryCount % 5 == 0)
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
            }

            client.Game.InteractWithEntity(baalPortal);
            return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(50);
                return client.Game.Area == Area.TheWorldStoneChamber;
            }, TimeSpan.FromSeconds(0.5));
        }, clickFor))
        {
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (retryCount > 0 && retryCount % 5 == 0)
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
            }

            await Task.Delay(100);

            return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneChamber, client.Game.Me.Location);
        }, TimeSpan.FromSeconds(10)))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> CreateBaalPortal(Client client)
    {
        if (!await EnterWorldstoneChamber(client, MovementMode.Teleport, TimeSpan.FromSeconds(20)))
        {
            return false;
        }

        var pathBaal = await _pathingService.GetPathToNPC(client.Game, NPCCode.Baal, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBaal, MovementMode.Teleport))
        {
            Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {NPCCode.Baal} failed at {client.Game.Me.Location}");
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            var baal = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, 200).FirstOrDefault();
            if (baal == null)
            {
                return false;
            }
            if (baal.Location.Distance(client.Game.Me.Location) > 10)
            {
                pathBaal = await _pathingService.GetPathToLocation(client.Game, baal.Location, MovementMode.Teleport);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBaal, MovementMode.Teleport))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {NPCCode.Baal} failed at {client.Game.Me.Location}");
                    return false;
                }
            }

            return true;
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} close to {NPCCode.Baal} failed at {client.Game.Me.Location}");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(client))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> SetupBo(Client client)
    {
        if (client.Game.Me.Id == BoClientPlayerId)
        {
            await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.1));
                if (retryCount % 5 == 0)
                {
                    foreach (var player in client.Game.Players.Where(p => p.Location?.Distance(client.Game.Me.Location) < 10))
                    {
                        client.Game.RequestUpdate(player.Id);
                    }
                }
                return await ClassHelpers.CastAllShouts(client);
            }, TimeSpan.FromSeconds(15));
        }
        else
        {
            var random = new Random();
            await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                var boPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                if (boPlayer != null)
                {
                    var randomPointNear = boPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                    await client.Game.MoveToAsync(randomPointNear);
                }

                await Task.Delay(TimeSpan.FromSeconds(0.1));
                return !ClassHelpers.IsMissingShouts(client.Game.Me);
            }, TimeSpan.FromSeconds(15));
        }

        return true;
    }

    private async Task<bool> CreateThroneRoomTp(Client client)
    {
        var game = client.Game;
        if (!await _townManagementService.TakeWaypoint(client, Waypoint.TheWorldStoneKeepLevel2))
        {
            Log.Information($"Taking {client.Game.Act} waypoint failed");
            return false;
        }

        var pathToWorldStone3 = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneKeepLevel2, Waypoint.TheWorldStoneKeepLevel2, Area.TheWorldStoneKeepLevel3, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToWorldStone3.SkipLast(1).ToList(), MovementMode.Teleport))
        {
            Log.Error($"Teleporting to {Area.TheWorldStoneKeepLevel3}  failed");
            return false;
        }

        var warp1 = client.Game.GetNearestWarp();
        if (warp1 == null)
        {
            Log.Warning($"Warp not found while at location {client.Game.Me.Location}");
            return false;
        }

        var teleportLocation = warp1.Location;
        if(await _attackService.IsVisitable(client, warp1.Location.Add(-30, 0)))
        {
            teleportLocation = warp1.Location.Add(-30, 0);
        }
        else if (await _attackService.IsVisitable(client, warp1.Location.Add(30, 0)))
        {
            teleportLocation = warp1.Location.Add(30, 0);
        }
        else if (await _attackService.IsVisitable(client, warp1.Location.Add(0, 30)))
        {
            teleportLocation = warp1.Location.Add(0, 30);
        }
        else if (await _attackService.IsVisitable(client, warp1.Location.Add(0, -30)))
        {
            teleportLocation = warp1.Location.Add(0, -30);
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            var pathtoTeleportLocation = await _pathingService.GetPathToLocation(client.Game, teleportLocation, MovementMode.Teleport);
            if(pathtoTeleportLocation.Count != 0 && !await MovementHelpers.TakePathOfLocations(client.Game, pathtoTeleportLocation, MovementMode.Teleport))
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(0.5));

            return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (warp1.Location.Distance(client.Game.Me.Location) > 30)
                {
                    return false;
                }

                if (warp1.Location.Distance(client.Game.Me.Location) > 5 && !await game.TeleportToLocationAsync(warp1.Location))
                {
                    return false;
                }

                if (warp1.Location.Distance(client.Game.Me.Location) < 5)
                {
                    await client.Game.MoveToAsync(warp1.Location);
                }

                return client.Game.TakeWarp(warp1) && await ArrivedIn(client, Area.TheWorldStoneKeepLevel3);
            }, TimeSpan.FromSeconds(3.5));
        }, TimeSpan.FromSeconds(4)))
        {
            Log.Warning($"Taking warp with teleport location {teleportLocation} failed at location: {game.Me.Location} with warp at {warp1.Location}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneKeepLevel3, client.Game.Me.Location);
            return isValidPoint;
        }, TimeSpan.FromSeconds(3.5)))
        {
            Log.Error("Checking whether moved to area failed");
            return false;
        }

        var pathToThroneRoom = await _pathingService.GetPathToArea(client.Game, Area.ThroneOfDestruction, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToThroneRoom, MovementMode.Teleport))
        {
            Log.Error($"Teleporting to {Area.ThroneOfDestruction}  failed");
            return false;
        }

        var warp2 = client.Game.GetNearestWarp();
        if (warp2 == null || warp2.Location.Distance(client.Game.Me.Location) > 20)
        {
            Log.Warning($"Warp not close enough at location {warp2?.Location} while at location {client.Game.Me.Location}");
            return false;
        }

        // Monsters waiting at this entrance hit hard and some are extra fast: draw them off one hop back first,
        // the way the first warp is approached, so they never get a swing in at the warp.
        var atWarp = NPCHelpers.GetNearbyNPCs(client, warp2.Location, 10, 10).ToList();
        if (atWarp.Count > 0 && pathToThroneRoom.Count >= 2)
        {
            Log.Information($"Client {client.Game.Me.Name} luring {string.Join(", ", atWarp.Select(m => $"{m.NPCCode} [{string.Join("/", m.MonsterEnchantments)}]"))} away from the throne room warp");
            await MovementHelpers.TakePathOfLocations(client.Game, [pathToThroneRoom[^2]], MovementMode.Teleport);
            await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(100);
                return !NPCHelpers.GetNearbyNPCs(client, warp2.Location, 1, 8).Any();
            }, TimeSpan.FromSeconds(2));
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (warp2.Location.Distance(client.Game.Me.Location) > 5 && !await game.TeleportToLocationAsync(warp2.Location))
            {
                Log.Debug($"Teleport to {warp2.Location} failing retrying at location: {game.Me.Location}");
                return false;
            }

            return client.Game.TakeWarp(warp2) && await ArrivedIn(client, Area.ThroneOfDestruction);
        }, TimeSpan.FromSeconds(4)))
        {
            Log.Warning($"Teleport failed at location: {game.Me.Location}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ThroneOfDestruction, client.Game.Me.Location);
            return isValidPoint;
        }, TimeSpan.FromSeconds(3.5)))
        {
            Log.Error("Checking whether moved to area failed");
            return false;
        }

        var atEntrance = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 10, 15).ToList();
        if (atEntrance.Count > 0)
        {
            Log.Information($"Client {client.Game.Me.Name} entered the throne room next to {string.Join(", ", atEntrance.Select(m => $"{m.NPCCode} [{string.Join("/", m.MonsterEnchantments)}] {m.Location.Distance(client.Game.Me.Location):0} away"))}");
        }

        var pathToThrone = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.BaalPortal, 27, 65, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToThrone, MovementMode.Teleport))
        {
            Log.Error($"Teleporting to {Area.ThroneOfDestruction} starting location failed");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await Task.Delay(100);
            return await _townManagementService.TakeTownPortalToTown(client);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Client {client.Game.Me.Name} moving to town failed");
            NextGame.TrySetResult(true);
        }

        Log.Information("Got to starting location");

        return true;
    }

    private async Task<bool> ArrivedIn(Client client, Area area)
    {
        return await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(50);
            return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, area, client.Game.Me.Location);
        }, TimeSpan.FromSeconds(0.6));
    }

    private async Task FollowToLocation(Client client, Point location)
    {
        if (!client.Game.IsInGame())
        {
            return;
        }

        var (targetLocation, tokenSource) = FollowTasks[client.Game.Me.Name.ToLower()];
        if (targetLocation == null || (targetLocation.Distance(location) > 10 && client.Game.Me.Location.Distance(location) < 1000))
        {
            tokenSource?.Cancel();
            var newSource = new CancellationTokenSource();
            FollowTasks[client.Game.Me.Name.ToLower()] = (location, newSource);
            await MoveToLocation(client, location, newSource.Token);
        }
    }

    private bool ShouldFollowLeadClient(Client client)
    {
        if (ShouldFollow.TryGetValue(client.Game.Me.Name.ToLower(), out var shouldFollow))
        {
            return shouldFollow;
        };

        return false;
    }

    private void SetShouldFollowLead(Client client, bool follow)
    {
        ShouldFollow[client.Game.Me.Name.ToLower()] = follow;
    }

    private void StopFollowingLead(Client client)
    {
        var name = client.Game.Me.Name.ToLower();
        ShouldFollow[name] = false;
        if (FollowTasks.TryGetValue(name, out var follow))
        {
            follow.Item2?.Cancel();
            FollowTasks[name] = (null, new CancellationTokenSource());
        }
    }

    private async Task GetTaskForWave(Client client, AccountConfig account)
    {
        if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] < 50 && !client.Game.Me.HasSkill(Skill.Teleport))
        {
            await BasicIdleClient(client);
            return;
        }

        switch (client.Game.Me.Class)
        {
            case CharacterClass.Amazon:
                await BasicIdleClient(client);

                break;
            case CharacterClass.Sorceress:
                if (client.Game.Me.HasSkill(Skill.StaticField))
                {
                    await StaticSorcClient(client, account);
                }
                else
                {
                    await BasicIdleClient(client);
                }
                break;
            case CharacterClass.Necromancer:
                await BasicIdleClient(client);
                break;
            case CharacterClass.Paladin:
                if (client.Game.Me.Skills.GetValueOrDefault(Skill.BlessedHammer) >= 20)
                {
                    await HammerWaveClient(client, account);
                }
                else
                {
                    await BasicPalaClient(client, account);
                }
                break;
            case CharacterClass.Barbarian:
                await BarbClient(client, account);
                break;
            case CharacterClass.Druid:
            case CharacterClass.Assassin:
                await BasicIdleClient(client);
                break;
        }
    }

    // Lister's wave arrives as class 571 on this realm (named BaalCrabClone in NPCCode); no earlier wave has it.
    private static bool IsLastWave(WorldObject monster)
    {
        return monster.NPCCode is NPCCode.BaalCrabClone or NPCCode.BaalsMinion1 or NPCCode.BaalsMinion2 or NPCCode.BaalsMinion3;
    }

    private async Task<bool> InWorldstoneChamber(Client client)
    {
        return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneChamber, client.Game.Me.Location);
    }

    private async Task GetTaskForBaal(Client client, AccountConfig account)
    {
        var portalPlayer = client.Game.Players.FirstOrDefault(p => p.Id == PortalClientPlayerId);
        if (portalPlayer == null)
        {
            NextGame.TrySetResult(true);
            return;
        }

        switch (client.Game.Me.Class)
        {
            case CharacterClass.Sorceress:
                if (client.Game.Me.HasSkill(Skill.StaticField))
                {
                    if (!await InWorldstoneChamber(client) && !await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        await Task.Delay(100);
                        return await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.TheWorldStoneChamber);
                    }, TimeSpan.FromSeconds(30)))
                    {
                        Log.Warning($"Client {client.Game.Me.Name} stopped waiting for baal portal");
                        NextGame.TrySetResult(true);
                        return;
                    }

                    await StaticSorcClient(client, account);
                    NextGame.TrySetResult(true);
                }
                else
                {
                    await NextGame.Task;
                }
                break;
            case CharacterClass.Paladin:
                if (!await InWorldstoneChamber(client) && !await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(100);
                    return await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.TheWorldStoneChamber);
                }, TimeSpan.FromSeconds(30)))
                {
                    Log.Warning($"Client {client.Game.Me.Name} stopped waiting for baal portal");
                    NextGame.TrySetResult(true);
                    return;
                }
                await BasicPalaClient(client, account);
                break;
            default:
                await NextGame.Task;
                break;
        }

        await Task.Delay(1000);
        await PickupItemsAndPotions(client, account, 100);
    }

    private async Task StaticSorcClient(Client client, AccountConfig account)
    {
        Log.Information($"Starting Sorc Client {client.Game.Me.Name}");
        Point baalThrone = null;
        var runStopWatch = new Stopwatch();
        runStopWatch.Start();

        var random = new Random();
        var lastWaveSeen = false;
        while (!await IsNextGame() && client.Game.IsInGame())
        {
            if (runStopWatch.Elapsed > TimeSpan.FromSeconds(50))
            {
                var known = client.Game.WorldObjects
                    .Where(w => w.Key.Item2 == EntityType.NPC && baalThrone != null && w.Value.Location?.Distance(baalThrone) < 100)
                    .Select(w => $"{w.Value.NPCCode} {w.Value.State} at {w.Value.Location} ({w.Value.Location.Distance(baalThrone):0} from throne)");
                Log.Warning($"Client {client.Game.Me.Name} waiting too long going next game, throne {baalThrone}, monsters near it: {string.Join(", ", known)}");
                NextGame.TrySetResult(true);
                break;
            }

            if(NPCHelpers.GetNearbySuperUniques(client).Any(w => w.NPCCode == NPCCode.Baal && (w.State == EntityState.Dead || w.State == EntityState.Dieing)))
            {
                Log.Information($"Client {client.Game.Me.Name} baal is dead going next game");
                NextGame.TrySetResult(true);
                break;
            }

            var baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
            if (baalThrone == null)
            {
                baalThrone = client.Game.GetNPCsByCode(NPCCode.BaalThrone).FirstOrDefault()?.Location;
            }
            else if(client.Game.Me.Location.Distance(baalThrone) > 60 && baalPortal != null)
            {
                var pathToThrone = await _pathingService.GetPathToLocation(client.Game, baalPortal.Location.Add(0, 30), MovementMode.Teleport);
                if (pathToThrone.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting nearby {EntityCode.BaalPortal}");
                    await MovementHelpers.TakePathOfLocations(client.Game, pathToThrone.ToList(), MovementMode.Teleport);
                }
            }

            await RefillPotionsIfOut(client, account);

            var nearbyMonsters = baalThrone == null
                ? NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 10, 50).ToList()
                : NPCHelpers.GetNearbyNPCs(client, baalThrone, 10, ThroneWaveRadius).ToList();
            if (nearbyMonsters.Count != 0)
            {
                runStopWatch.Restart();
            }
            else
            {
                await PickupItemsAndPotions(client, account, 30);
            }

            lastWaveSeen |= baalThrone != null && nearbyMonsters.Any(IsLastWave);
            if (lastWaveSeen && nearbyMonsters.Count == 0)
            {
                Log.Information($"Last wave cleared, {client.Game.Me.Name} heading for Baal's portal");
                break;
            }

            var boPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
            var portalPlayer = client.Game.Players.FirstOrDefault(p => p.Id == PortalClientPlayerId);
            var monstersNearBoClient = boPlayer != null ? NPCHelpers.GetNearbyNPCs(client, boPlayer.Location, 10, 20) : [];
            if (boPlayer != null && monstersNearBoClient.Any() && boPlayer.Location.Distance(client.Game.Me.Location) > 20)
            {
                Log.Information($"{client.Game.Me.Name}, bo client in danger, moving to bo client");

                var teleportPath = await _pathingService.GetPathToLocation(client.Game, monstersNearBoClient.First().Location, MovementMode.Teleport);
                if (teleportPath.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting at {monstersNearBoClient.First().Location}");
                    await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
                }
                await _attackService.AssistPlayer(client, boPlayer);
                continue;
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
            {
                Log.Information($"Lost bo on client {client.Game.Me.Name}, moving to barb for bo");
                if (boPlayer != null)
                {
                    if (boPlayer.Location.Distance(client.Game.Me.Location) > 10)
                    {
                        var teleportPathLead = await _pathingService.GetPathToLocation(client.Game, boPlayer.Location, MovementMode.Teleport);
                        await MovementHelpers.TakePathOfLocations(client.Game, teleportPathLead.ToList(), MovementMode.Teleport);
                    }
                    else
                    {
                        var randomPointNear = boPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                        await client.Game.TeleportToLocationAsync(randomPointNear);
                    }
                }

                continue;
            }

            if(nearbyMonsters.Count != 0
                && client.Game.Me.Id == PortalClientPlayerId 
                && client.Game.Me.Location.Distance(nearbyMonsters.First().Location) > 30)
            {
                var pathToThrone = await _pathingService.GetPathToLocation(client.Game, nearbyMonsters.First().Location, MovementMode.Teleport);
                if (pathToThrone.Count > 0)
                {
                    await MovementHelpers.TakePathOfLocations(client.Game, pathToThrone.ToList(), MovementMode.Teleport);
                }
            }
            else if(client.Game.Me.Id != PortalClientPlayerId && portalPlayer != null && portalPlayer.Location.Distance(client.Game.Me.Location) > 30)
            {
                var pathToPortalPlayer = await _pathingService.GetPathToLocation(client.Game, portalPlayer.Location, MovementMode.Teleport);
                if (pathToPortalPlayer.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting nearby Portal player");
                    await MovementHelpers.TakePathOfLocations(client.Game, pathToPortalPlayer.ToList(), MovementMode.Teleport);
                }
            }

            if (nearbyMonsters.Count != 0)
            {
                var assistPlayer = client.Game.Players.FirstOrDefault(p => p.Id == PortalClientPlayerId) ?? client.Game.Me;
                await _attackService.AssistPlayer(client, assistPlayer);
            }
            else if(client.Game.GetNPCsByCode(NPCCode.BaalThrone).Count == 0 && baalThrone != null && client.Game.Me.Location.Distance(baalThrone) < 30)
            {
                Log.Information($"Waves done, moving on {client.Game.Me.Name}");
                break;
            }
            else if (baalPortal != null && client.Game.Me.Location.Distance(baalPortal.Location.Add(0, 30)) > 10)
            {
                var pathToThrone = await _pathingService.GetPathToLocation(client.Game, baalPortal.Location.Add(0, 30), MovementMode.Teleport);
                if (pathToThrone.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting nearby {EntityCode.BaalPortal}");
                    await MovementHelpers.TakePathOfLocations(client.Game, pathToThrone.ToList(), MovementMode.Teleport);
                }
            }
        }

        Log.Information($"Finished with Sorc Client {client.Game.Me.Name}");
    }

    /// <summary>
    /// Refills an empty belt in town and comes back through the own portal. Leaving the game instead threw away
    /// whole runs with the character at half life and nothing near.
    /// </summary>
    private async Task RefillPotionsIfOut(Client client, AccountConfig account)
    {
        var game = client.Game;
        // Rejuvenations are kept for the emergency line, so an empty healing belt is what sends a character to town.
        // One trip a game: a character that cannot buy (out of gold) otherwise walked to town and back every 12s.
        if (game.IsInTown()
            || game.Belt.NumOfHealthPotions() > 0
            || game.Inventory.Items.Any(i => i.Classification == ClassificationType.HealthPotion)
            || !RefilledThisGame.TryAdd(game.Me.Name, true))
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        Log.Information($"Client {game.Me.Name} out of healing potions at {game.Me.Life} of {game.Me.MaxLife} life, refilling in town");
        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Warning($"Client {game.Me.Name} could not get to town for potions");
            return;
        }

        var townResult = await _townManagementService.PerformTownTasks(client, new TownManagementOptions(account, Act.Act5));
        if (townResult.ShouldMule)
        {
            ClientsNeedingMule.Add(client.LoggedInUserName());
        }

        var me = game.Players.FirstOrDefault(p => p.Id == game.Me.Id);
        if (me == null || !await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(100);
            return await _townManagementService.TakeTownPortalToArea(client, me, Area.ThroneOfDestruction);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Client {game.Me.Name} could not get back to the throne after refilling");
            return;
        }

        Log.Information($"Client {game.Me.Name} back from refilling potions after {timer.Elapsed.TotalSeconds:0.0}s, belt {game.Belt.NumOfHealthPotions()} healing");
    }

    private async Task BasicIdleClient(Client client)
    {
        Log.Information($"Starting Basic idle Client {client.Game.Me.Name}");
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame())
        {
        }

        Log.Information($"Stopped Idle Client {client.Game.Me.Name}");
    }

    private async Task BasicPalaClient(Client client, AccountConfig account)
    {
        SetShouldFollowLead(client, true);

        client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
        var timer = new Stopwatch();
        timer.Start();
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame())
        {
            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Holyshield) && client.Game.Me.HasSkill(Skill.HolyShield))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
            }

            var assistPlayer = client.Game.Players.FirstOrDefault(p => p.Id == PortalClientPlayerId) ?? client.Game.Me;
            await _attackService.AssistPlayer(client, assistPlayer);

            if (!NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, 200).Any())
            {
                SetShouldFollowLead(client, false);
                await PickupItemsAndPotions(client, account, 25);
                SetShouldFollowLead(client, true);
                var baalThrone = client.Game.GetNPCsByCode(NPCCode.BaalThrone).FirstOrDefault();
                if (baalThrone == null)
                {
                    SetShouldFollowLead(client, false);
                    break;
                }
            }
        }

        StopFollowingLead(client);
    }

    /// <summary>
    /// Holds Blessed Hammer on a fixed spot just below where the throne raises each wave (13 below it), so the
    /// hammers are already spiralling when a wave appears instead of starting once it has walked up.
    /// </summary>
    private async Task HammerWaveClient(Client client, AccountConfig account)
    {
        Log.Information($"Starting Hammer wave Client {client.Game.Me.Name}");
        var throne = client.Game.GetNPCsByCode(NPCCode.BaalThrone).FirstOrDefault()?.Location;
        if (throne == null)
        {
            Log.Warning($"Client {client.Game.Me.Name} sees no throne, waiting for the next game");
            await NextGame.Task;
            return;
        }

        var hammerSpot = throne.Add(4, 18);
        // Conviction for the sorceresses' novas, Fanaticism for the barbarian and the mercenaries, taking turns.
        // On 1.09 an aura lands at once and lingers ~8s after the switch, so 6s turns keep both on the party.
        var auras = new[] { Skill.Conviction, Skill.Fanaticism }.Where(client.Game.Me.HasSkill).ToList();
        if (auras.Count == 0)
        {
            auras.Add(client.Game.Me.HasSkill(Skill.Concentration) ? Skill.Concentration : Skill.Might);
        }

        var auraRotation = Stopwatch.StartNew();
        var auraIndex = 0;
        var movementMode = MovementHelpers.PreferredMovement(client.Game);
        var holdRefresh = Stopwatch.StartNew();
        var holding = false;
        var lastWaveSeen = false;
        while (!await IsNextGame() && client.Game.IsInGame())
        {
            if (client.Game.GetNPCsByCode(NPCCode.BaalThrone).Count == 0)
            {
                Log.Information($"Waves done, moving on {client.Game.Me.Name}");
                break;
            }

            var hallMonsters = NPCHelpers.GetNearbyNPCs(client, throne, 10, ThroneWaveRadius).ToList();
            lastWaveSeen |= hallMonsters.Any(IsLastWave);
            if (lastWaveSeen && hallMonsters.Count == 0)
            {
                Log.Information($"Last wave cleared, {client.Game.Me.Name} stops hammering");
                break;
            }

            await RefillPotionsIfOut(client, account);
            if (client.Game.Me.Location.Distance(hammerSpot) > 3)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, hammerSpot, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
                {
                    await client.Game.MoveToAsync(hammerSpot);
                }

                holding = false;
            }

            if (client.Game.Me.HasSkill(Skill.HolyShield) && !client.Game.Me.Effects.ContainsKey(EntityEffect.Holyshield) && client.Game.Me.Mana > 20)
            {
                client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                await Task.Delay(300);
                holding = false;
            }

            if (auraRotation.Elapsed > TimeSpan.FromSeconds(6))
            {
                auraIndex = (auraIndex + 1) % auras.Count;
                auraRotation.Restart();
            }

            var aura = auras[auraIndex];
            if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var rightSkill) || rightSkill != aura)
            {
                client.Game.ChangeSkill(aura, Hand.Right);
            }

            if (!holding || holdRefresh.Elapsed > TimeSpan.FromSeconds(0.5))
            {
                client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
                holding = true;
                holdRefresh.Restart();
            }

            await Task.Delay(200);
        }

        Log.Information($"Stopped Hammer wave Client {client.Game.Me.Name}");
    }

    private async Task BarbClient(Client client, AccountConfig account)
    {
        Log.Information($"Starting BoBarb Client {client.Game.Me.Name}");
        bool shouldBo = client.Game.Me.Id == BoClientPlayerId;

        // Left where the portal lands, he pulled the hall's monsters to its edge, where they stood within the
        // throne's reach but beyond the sorceresses' and kept the next wave from coming.
        var baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
        if (baalPortal != null && client.Game.GetNPCsByCode(NPCCode.BaalThrone).Count > 0)
        {
            var waveSpot = baalPortal.Location.Add(0, 30);
            var movementMode = MovementHelpers.PreferredMovement(client.Game);
            var pathToWaveSpot = await _pathingService.GetPathToLocation(client.Game, waveSpot, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToWaveSpot, movementMode))
            {
                Log.Warning($"Client {client.Game.Me.Name} could not reach the wave spot {waveSpot}, holding at {client.Game.Me.Location}");
            }
        }

        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame())
        {
            await RefillPotionsIfOut(client, account);
            if (shouldBo)
            {
                await ClassHelpers.CastAllShouts(client);
            }
            else
            {
                var boPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                if (boPlayer != null && boPlayer.Location.Distance(client.Game.Me.Location) > 25)
                {
                    continue;
                }
            }

            var nearbyMonsters = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, 20);
            if (!nearbyMonsters.Any())
            {
                await PickupItemsAndPotions(client, account, 15);
            }
            else if (client.Game.Me.HasSkill(Skill.Whirlwind))
            {
                var assistPlayer = client.Game.Players.FirstOrDefault(p => p.Id == PortalClientPlayerId) ?? client.Game.Me;
                await _attackService.AssistPlayer(client, assistPlayer);
            }
        }

        Log.Information($"Stopped Barb Client {client.Game.Me.Name}");
    }
}
