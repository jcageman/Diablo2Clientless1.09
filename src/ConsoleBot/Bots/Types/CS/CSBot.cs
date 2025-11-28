using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.CS
{
    public class CSBot : MultiClientBotBase
    {
        private readonly CsConfiguration _csconfig;

        private readonly ITownManagementService _townManagementService;
        private readonly IAttackService _attackService;
        private readonly IMapApiService _mapApiService;
        private CsState _state = new();

        public CSBot(
            IOptions<BotConfiguration> config,
            IOptions<CsConfiguration> csconfig,
            IExternalMessagingClient externalMessagingClient,
            IMuleService muleService,
            ITownManagementService townManagementService,
            IPathingService pathingService,
            IMapApiService mapApiService,
            IAttackService attackService) : base(config, csconfig, externalMessagingClient, muleService, pathingService)
        {
            _mapApiService = mapApiService;
            _csconfig = csconfig.Value;
            _townManagementService = townManagementService;
            _attackService = attackService;
        }

        public override string GetName()
        {
            return "cs";
        }

        protected override void ResetForNextRun()
        {
            _state = new CsState();
        }

        protected override async Task<bool> PrepareForRun(Client client, AccountConfig account)
        {
            var townManagementOptions = new TownManagementOptions(account, Act.Act4);

            await GeneralHelpers.TryWithTimeout(
                async (_) =>
                {
                    var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
                    if (townTaskResult.ShouldMule)
                    {
                        ClientsNeedingMule.Add(client.LoggedInUserName());
                    }
                    if (!townTaskResult.Succes)
                    {
                        client.Game.RequestUpdate(client.Game.Me.Id);
                    }
                    return townTaskResult.Succes;
                },
                TimeSpan.FromSeconds(20));

            if (IsTeleportClient(client))
            {
                Log.Debug($"Client {client.Game.Me.Name} Taking waypoint to {Waypoint.RiverOfFlame}");
                if (!await _townManagementService.TakeWaypoint(client, Waypoint.RiverOfFlame))
                {
                    Log.Debug($"Client {client.Game.Me.Name} Taking waypoint failed at location {client.Game.Me.Location}");
                    return false;
                }

                Log.Debug($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary}");
                var pathToChaos = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, Area.RiverOfFlame, client.Game.Me.Location, EntityCode.WaypointAct4Levels, -6, -319, MovementMode.Teleport);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToChaos, MovementMode.Teleport))
                {
                    Log.Debug($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary} warp failed at location {client.Game.Me.Location}");
                    return false;
                }

                var goalLocation = client.Game.Me.Location.Add(0, -20);
                if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                {
                    return await client.Game.TeleportToLocationAsync(goalLocation);
                }, TimeSpan.FromSeconds(5)))
                {
                    Log.Debug($"Client {client.Game.Me.Name} Teleporting to location within {Area.ChaosSanctuary} failed at location {client.Game.Me.Location}");
                    return false;
                }

                var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
                {
                    Log.Debug($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                    return false;
                }

                if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                {
                    return await _townManagementService.TakeTownPortalToTown(client);
                }, TimeSpan.FromSeconds(5)))
                {
                    Log.Debug($"Client {client.Game.Me.Name} Taking townportal to town failed");
                    return false;
                }
            }

            return true;
        }

        protected override async Task<bool> PerformRun(Client client, AccountConfig account)
        {
            if (IsTeleportClient(client))
            {
                var result = await TaxiCs(client, account);
                NextGame.TrySetResult(true);

                return result;
            }
            else
            {
                var action = GetKillActionForClass(client, account);

                var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
                var pathToTpLocation = await _pathingService.GetPathToLocation(client.Game, new Point(5042, 5036), movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTpLocation, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {movementMode} to portal area failed at {client.Game.Me.Location}");
                    return false;
                }

                if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                {
                    if (client.Game.IsInTown() && _state.TeleportId != null)
                    {
                        Log.Debug($"Client {client.Game.Me.Name} taking town portal to chaos");
                        var teleportPlayer = client.Game.Players.FirstOrDefault(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.OrdinalIgnoreCase));
                        if (teleportPlayer == null || !await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                        {
                            return false;
                        }

                        return true;
                    }

                    return false;
                }, TimeSpan.FromSeconds(10)))
                {
                    Log.Debug($"Client {client.Game.Me.Name} Taking townportal to {Area.ChaosSanctuary} failed");
                    return false;
                }

                if (!await WaitForBo(client, account, action))
                {
                    return false;
                }

                return await BaseCsBot(client, account, action);
            }
        }

        private async Task<bool> BaseCsBot(Client client, AccountConfig account, Func<Task> action)
        {
            var ownState = new CsState();
            while (!await IsNextGame() && client.Game.IsInGame())
            {
                await Task.Delay(100);
                if (_state.TeleportHasChanged(ownState) && !client.Game.IsInTown())
                {
                    Log.Debug($"Client {client.Game.Me.Name} Taking town portal to town");
                    if (!await _townManagementService.TakeTownPortalToTown(client))
                    {
                        continue;
                    }
                }

                var newTeleportId = _state.TeleportId;
                if (client.Game.IsInTown() && newTeleportId != null && newTeleportId != ownState.TeleportId)
                {
                    Log.Debug($"Client {client.Game.Me.Name} taking town portal to chaos {ownState.TeleportId} --> {newTeleportId}");
                    var teleportPlayer = client.Game.Players.FirstOrDefault(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.OrdinalIgnoreCase));
                    if (teleportPlayer == null || !await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                    {
                        Log.Warning($"Client {client.Game.Me.Name} failed to take town portal");
                        continue;
                    }

                    ownState.TeleportId = newTeleportId;
                }

                if (!client.Game.IsInTown())
                {
                    if (!await KillBosses(client, account, null, action, ownState, _state))
                    {
                        return false;
                    }
                }
            }

            if (!await IsNextGame() && !client.Game.IsInGame())
            {
                return false;
            }

            return true;
        }

        private bool IsTeleportClient(Client client)
        {
            return client.Game.Me.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> TaxiCs(Client client, AccountConfig account)
        {
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                if(client.Game.Area == Area.ChaosSanctuary)
                {
                    return true;
                }
                return await _townManagementService.TakeTownPortalToArea(client, client.Game.Me, Area.ChaosSanctuary);
            }, TimeSpan.FromSeconds(15)))
            {
                Log.Warning($"Client {client.Game.Me.Name} Taking townportal to {Area.ChaosSanctuary} failed");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await _townManagementService.CreateTownPortal(client);
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning($"Client {client.Game.Me.Name} Creating townportal failed");
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);

            _state.TeleportId = myPortal.Id;
            _state.KillLocation = client.Game.Me.Location;
            var action = GetSorceressKillAction(client, account);
            if (!await WaitForBo(client, account, action))
            {
                return false;
            }

            if (await IsNextGame())
            {
                return true;
            }

            _state.TeleportId = null;

            if (!await KillLeftSeal(client, account, _state))
            {
                return false;
            }

            if (await IsNextGame())
            {
                return true;
            }

            _state.TeleportId = null;

            if (!await KillTopSeal(client, account, _state))
            {
                return false;
            }

            if (await IsNextGame())
            {
                return true;
            }

            _state.TeleportId = null;

            if (!await KillRightSeal(client, account, _state))
            {
                return false;
            }

            if (await IsNextGame())
            {
                return true;
            }

            _state.TeleportId = null;

            return await KillDiablo(client, account, _state);
        }

        private async Task<bool> WaitForBo(Client client, AccountConfig account, Func<Task> action)
        {
            var initialLocation = client.Game.Me.Location;
            var csState = new CsState
            {
                KillLocation = initialLocation
            };
            var stopWatch = new Stopwatch();
            var random = new Random();
            stopWatch.Start();
            while (stopWatch.Elapsed < TimeSpan.FromSeconds(30) && ClassHelpers.AnyPlayerIsMissingShouts(client) && !await IsNextGame())
            {
                if (client.Game.Me.Class == CharacterClass.Barbarian)
                {
                    await ClassHelpers.CastAllShouts(client);
                }
                else
                {
                    await client.Game.MoveToAsync(initialLocation.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5)));
                    var killCancellation = new CancellationTokenSource();
                    killCancellation.CancelAfter(500);
                    await KillBosses(client, account, killCancellation, action, csState, csState);
                }
                
                var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
                var pathToInitialLocation = await _pathingService.GetPathToLocation(client.Game, initialLocation, movementMode);
                var movementCancellation = new CancellationTokenSource();
                movementCancellation.CancelAfter(500);
                await MovementHelpers.TakePathOfLocations(client.Game, pathToInitialLocation, movementMode, movementCancellation.Token);
            }

            if (stopWatch.Elapsed >= TimeSpan.FromSeconds(30))
            {
                Log.Warning($"Client {client.Game.Me.Name} Failed waiting for bo at area {client.Game.Area} at location {client.Game.Me.Location}");
                return false;
            }

            return true;
        }

        private async Task<bool> KillDiablo(Client client, AccountConfig account, CsState csState)
        {
            var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = client.Game.Me.Location;
            var action = GetSorceressKillAction(client, account);
            if (!await KillBosses(client, account, null, action, csState, csState, true))
            {
                return false;
            }
            return true;
        }

        private async Task<bool> KillRightSeal(Client client, AccountConfig account, CsState csState)
        {
            Log.Information($"Teleporting to {EntityCode.RightSeal1}");

            var seal1 = await GetSeal(client, EntityCode.RightSeal1);
            var seal2 = await GetSeal(client, EntityCode.RightSeal2);
            var killLocation = seal1.X < seal2.X ? seal1.Add(30, -10) : seal1.Add(12, -38);

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var pathToSeal = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
                return await MovementHelpers.TakePathOfLocations(client.Game, pathToSeal, MovementMode.Teleport);
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Teleporting to killing location of {EntityCode.RightSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = killLocation;

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(seal1);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var rightseal1Entity = client.Game.GetEntityByCode(EntityCode.RightSeal1).First();
                if (rightseal1Entity.State == EntityState.Activating || rightseal1Entity.State == EntityState.Activated)
                {
                    return true;
                }
                client.Game.InteractWithEntity(rightseal1Entity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(killLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            var action = GetSorceressKillAction(client, account);
            if (!await KillBosses(client, account, null, action, csState, csState, true))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(seal2);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var rightseal2Entity = client.Game.GetEntityByCode(EntityCode.RightSeal2).First();
                if (rightseal2Entity.State == EntityState.Activating || rightseal2Entity.State == EntityState.Activated)
                {
                    return true;
                }
                client.Game.InteractWithEntity(rightseal2Entity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> KillTopSeal(Client client, AccountConfig account, CsState csState)
        {
            Log.Information($"Teleporting to {EntityCode.TopSeal}");

            Point topSeal = await GetSeal(client, EntityCode.TopSeal);
            var toLeftOfSealIsValid = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, topSeal.Add(-20, 0));
            var killLocation = toLeftOfSealIsValid ? topSeal.Add(-37, 31) : topSeal.Add(0, 70);
            var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to kill location {killLocation} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = killLocation;

            var pathToTopSeal2 = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.TopSeal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTopSeal2, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.TopSeal} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var topSealEntity = client.Game.GetEntityByCode(EntityCode.TopSeal).First();
                if (topSealEntity.State == EntityState.Activating || topSealEntity.State == EntityState.Activated)
                {
                    return true;
                }
                client.Game.InteractWithEntity(topSealEntity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning($"Opening {EntityCode.TopSeal} failed at location {client.Game.Me.Location} with state {client.Game.GetEntityByCode(EntityCode.TopSeal).FirstOrDefault()?.State}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
                return await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport);
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Teleporting back to kill location failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await MoveKillingLocationIfFar(client, csState))
            {
                Log.Warning($"Bosses are far from usual location, moving location {client.Game.Me.Name}");
                return false;
            }

            Log.Information($"Killing top seal bosses {client.Game.Me.Name}");
            var action = GetSorceressKillAction(client, account);
            if (!await KillBosses(client, account, null, action, csState, csState, true))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> MoveKillingLocationIfFar(Client client, CsState csState)
        {
            Log.Debug($"Kill location {csState.KillLocation}");
            WorldObject boss = null;
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                boss = NPCHelpers.GetNearbyNPCs(client, csState.KillLocation, 50, 80).FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (boss == null)
                {
                    await Task.Delay(100);
                }
                return boss != null;
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Waiting for bosses to spawn failed at {client.Game.Me.Location}");
                return false;
            }

            var walkingPathToBosses = await _pathingService.GetPathToLocation(client.Game, boss.Location, MovementMode.Walking);
            if (walkingPathToBosses.Zip(walkingPathToBosses.Skip(1), (p1, p2) => p1.Distance(p2)).Sum() > 25)
            {
                Log.Debug($"Bosses are far from usual location, moving location {client.Game.Me.Name}");
                var pathToBosses = await _pathingService.GetPathToLocation(client.Game, boss.Location, MovementMode.Teleport);
                if (await MovementHelpers.TakePathOfLocations(client.Game, pathToBosses, MovementMode.Teleport))
                {
                    if (!await _townManagementService.CreateTownPortal(client))
                    {
                        return false;
                    }

                    var newPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
                    csState.TeleportId = newPortal.Id;
                    csState.KillLocation = boss.Location;
                    return true;
                }

                return false;
            }

            return true;
        }

        private async Task<bool> KillLeftSeal(Client client, AccountConfig account, CsState csState)
        {
            Log.Information($"Teleporting to {EntityCode.LeftSeal1}");

            Point leftSeal1 = await GetSeal(client, EntityCode.LeftSeal1);
            Point leftSeal2 = await GetSeal(client, EntityCode.LeftSeal2);
            var leftSealKillLocation = leftSeal1.Y > leftSeal2.Y ? leftSeal1.Add(26, -21) : leftSeal1.Add(20, 40);

            var pathToLeftSeal = await _pathingService.GetPathToLocation(client.Game, leftSealKillLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToLeftSeal, MovementMode.Teleport))
            {
                Log.Debug($"Teleporting to {EntityCode.LeftSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = leftSealKillLocation;
            Log.Debug($"Kill location {csState.KillLocation} with left seal kill {leftSealKillLocation}");
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSeal1);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var leftSeal1Entity = client.Game.GetEntityByCode(EntityCode.LeftSeal1).First();
                if (leftSeal1Entity.State == EntityState.Activating || leftSeal1Entity.State == EntityState.Activated)
                {
                    return true;
                }

                client.Game.InteractWithEntity(leftSeal1Entity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSeal2);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var leftSeal2Entity = client.Game.GetEntityByCode(EntityCode.LeftSeal2).First();
                if (leftSeal2Entity.State == EntityState.Activating || leftSeal2Entity.State == EntityState.Activated)
                {
                    return true;
                }

                client.Game.InteractWithEntity(leftSeal2Entity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSealKillLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await MoveKillingLocationIfFar(client, csState))
            {
                return false;
            }

            var action = GetSorceressKillAction(client, account);
            if (!await KillBosses(client, account, null, action, csState, csState, true))
            {
                return false;
            }

            return true;
        }

        private Func<Task> GetKillActionForClass(Client client, AccountConfig account)
        {
            return client.Game.Me.Class switch
            {
                CharacterClass.Barbarian => GetBarbarianKillAction(client, account),
                CharacterClass.Sorceress => GetSorceressKillAction(client, account),
                CharacterClass.Paladin => GetPaladinKillAction(client, account),
                CharacterClass.Necromancer => GetNecromancerKillAction(client, account),
                CharacterClass.Amazon => GetAmazonKillAction(client, account),
                _ => new Func<Task>(() =>
                {
                    return Task.CompletedTask;
                }),
            };
        }

        private Func<Task> GetAmazonKillAction(Client client, AccountConfig account)
        {
            Func<Task> action = (async () =>
            {
                var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 50);
                var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (nearest == null)
                {
                    await PickupItemsAndPotions(client, account, 10);
                    nearest = enemies.FirstOrDefault();
                }

                if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    return;
                }

                var nearbyPlayer = client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Paladin || p.Class == CharacterClass.Barbarian))
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                nearbyPlayer ??= client.Game.Me;
                if (nearbyPlayer != null)
                {
                    await _attackService.AssistPlayer(client, nearbyPlayer);
                }
            });
            return action;
        }

        private Func<Task> GetBarbarianKillAction(Client client, AccountConfig account)
        {
            var random = new Random();
            Func<Task> action = (async () =>
            {
                var anyPlayersWithoutShouts = ClassHelpers.AnyPlayerIsMissingShouts(client);
                if (anyPlayersWithoutShouts && client.Game.Me.Class == CharacterClass.Barbarian)
                {
                    await ClassHelpers.CastAllShouts(client);
                }

                if (client.Game.Me.Effects.ContainsKey(EntityEffect.Ironmaiden))
                {
                    var nearbyPlayerPala = client.Game.Players.Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Location.Distance(client.Game.Me.Location) > 5 && p.Class == CharacterClass.Paladin).OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                    if (nearbyPlayerPala != null)
                    {
                        if (nearbyPlayerPala.Location.Distance(client.Game.Me.Location) < 40 && nearbyPlayerPala.Location.Distance(client.Game.Me.Location) > 10)
                        {
                            var pathNearest = await _pathingService.GetPathToLocation(client.Game, nearbyPlayerPala.Location, MovementMode.Walking);
                            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                            {
                                Log.Warning($"Walking to Player failed at {client.Game.Me.Location}");
                            }
                        }
                        else if(client.Game.Me.HasSkill(Skill.Leap))
                        {
                            client.Game.RepeatRightHandSkillOnLocation(Skill.Leap, client.Game.Me.Location);
                            await Task.Delay(150);
                        }
                        else
                        {
                            await client.Game.MoveToAsync(nearbyPlayerPala.Location.Add((short)random.Next(-10, 10), (short)random.Next(-10, 10)));
                        }
                    }

                    await PickupItemsAndPotions(client,  account, 20);

                    return;
                }

                var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 20);
                var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (nearest == null)
                {
                    await PickupItemsAndPotions(client, account, 10);
                    nearest = enemies.FirstOrDefault();
                }
                else if(nearest.State == EntityState.Dead || nearest.State == EntityState.Dieing)
                {
                    await ClassHelpers.FindItemOnDeadEnemy(client.Game, _pathingService, _mapApiService, nearest);
                }

                var nearbyPlayer = client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Amazon))
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                nearbyPlayer ??= client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Paladin))
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                nearbyPlayer ??= client.Game.Me;
                if (nearbyPlayer != null)
                {
                    await _attackService.AssistPlayer(client, nearbyPlayer);
                }

            });
            return action;
        }

        private Func<Task> GetPaladinKillAction(Client client, AccountConfig account)
        {
            async Task action()
            {
                var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 50);
                var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (nearest == null)
                {
                    await PickupItemsAndPotions(client, account, 10);
                }

                var nearbyPlayer = client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Amazon))
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                nearbyPlayer ??= client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Barbarian))
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                nearbyPlayer ??= client.Game.Me;
                if (nearbyPlayer != null)
                {
                    await _attackService.AssistPlayer(client, nearbyPlayer);
                }
            }
            return action;
        }

        private Func<Task> GetSorceressKillAction(Client client, AccountConfig account)
        {
            var moveTimer = new Stopwatch();
            moveTimer.Start();
            var orbTimer = new Stopwatch();
            orbTimer.Start();
            Func<Task> action = (async () =>
            {
                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                }

                var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 50);
                var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (nearest == null)
                {
                    await PickupItemsAndPotions(client, account, 30);
                    nearest = enemies.FirstOrDefault();
                }

                if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    return;
                }

                var distanceToNearest = nearest.Location.Distance(client.Game.Me.Location);
                if (nearest.NPCCode == NPCCode.Diablo)
                {
                    if (distanceToNearest > 15)
                    {
                        await client.Game.TeleportToLocationAsync(nearest.Location);
                    }
                }
                else if (distanceToNearest < 5 || moveTimer.Elapsed > TimeSpan.FromSeconds(2))
                {
                    var nearbyPlayer = client.Game.Players
                    .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Paladin)
                    .FirstOrDefault();
                    var nearbyPortal = client.Game.GetEntityByCode(EntityCode.TownPortal)
                    .OrderBy(t => t.Location.Distance(client.Game.Me.Location))
                    .FirstOrDefault();
                    var nearbyLocation = nearbyPlayer != null ? nearbyPlayer.Location : nearbyPortal?.Location;
                    if (nearbyLocation != null && await _attackService.MoveToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), nearbyLocation, MovementMode.Teleport, 10))
                    {
                        moveTimer.Restart();
                    }
                    else if (nearbyLocation != null)
                    {
                        Log.Warning($"Back-up move teleporting to nearby player for {client.Game.Me.Name} on location {client.Game.Me.Location}");
                        await client.Game.TeleportToLocationAsync(nearbyLocation);
                    }
                    return;
                }

                var monster = client.Game.WorldObjects.GetValueOrDefault((nearest.Id, EntityType.NPC));
                if (monster == null)
                {
                    return;
                }

                if (client.Game.Me.HasSkill(Skill.FrozenOrb)
                && orbTimer.Elapsed > TimeSpan.FromSeconds(1)
                && distanceToNearest < 20
                && (!monster.Effects.Contains(EntityEffect.Cold) || !ClassHelpers.CanStaticEntity(client, monster.LifePercentage)))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, monster.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.25));
                    orbTimer.Restart();
                }
                else if (!client.Game.Me.HasSkill(Skill.FrozenOrb) && client.Game.Me.HasSkill(Skill.FrostNova) && distanceToNearest < 10 && !monster.Effects.Contains(EntityEffect.Cold))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.25));
                }
                else if (client.Game.Me.HasSkill(Skill.StaticField) && distanceToNearest < 20)
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                await Task.Delay(TimeSpan.FromSeconds(0.1));
            });
            return action;
        }

        private Func<Task> GetNecromancerKillAction(Client client, AccountConfig account)
        {
            async Task action()
            {
                var nearbyPlayer = client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Paladin)
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if (nearbyPlayer != null)
                {
                    await _attackService.AssistPlayer(client, nearbyPlayer);
                }

                var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 5, 20);
                var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (nearest == null)
                {
                    await PickupItemsAndPotions(client, account, 20);
                }

            }
            return action;
        }

        private async Task<bool> KillBosses(Client client,
                                AccountConfig account,
                                CancellationTokenSource taskCancellation,
                                Func<Task> action,
                                CsState ownState,
                                CsState newState,
                                bool checkBossDead = false)
        {
            var sealMaxTimeout = new Stopwatch();
            sealMaxTimeout.Start();
            do
            {
                await Task.Delay(100);
                if (sealMaxTimeout.Elapsed > TimeSpan.FromSeconds(50))
                {
                    Log.Warning($" {client.Game.Me.Name} reached seal timeout");
                    NextGame.TrySetResult(true);
                    return false;
                }

                if(checkBossDead && NPCHelpers.GetNearbySuperUniques(client).Any(w => w.State == EntityState.Dead || w.State == EntityState.Dieing))
                {
                    break;
                }

                await action.Invoke();

            } while (
            (taskCancellation == null || !taskCancellation.IsCancellationRequested)
            && !await IsNextGame()
            && client.Game.IsInGame()
            && !ownState.TeleportHasChanged(newState));

            var nearest = NPCHelpers.GetNearbySuperUniques(client).FirstOrDefault(w => w.State == EntityState.Dead || w.State == EntityState.Dieing);
            if (nearest != null)
            {
                if (client.Game.Me.HasSkill(Skill.FindItem)
                    && await ClassHelpers.FindItemOnDeadEnemy(client.Game, _pathingService, _mapApiService, nearest))
                {
                    await Task.Delay(300);
                }
            }

            if (client.Game.IsInGame())
            {
                await PickupItemsAndPotions(client, account, 15);
            }

            return client.Game.IsInGame();
        }

        private async Task<Point> GetSeal(Client client, EntityCode entityCode)
        {
            var map = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary);
            if (!map.Objects.TryGetValue((int)entityCode, out var objectPoints) || objectPoints.Count == 0)
            {
                Log.Error($"Did not find a {entityCode} in the mapdata");
                return null;
            }

            return objectPoints.First();
        }
    }
}
