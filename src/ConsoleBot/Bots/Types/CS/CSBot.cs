using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.CS
{
    public class CSBot : IBotInstance
    {
        /*
         * Notes
            top seal
            easy
            "x": 7815,
            "y": 5155
            Good kill location (7778, 5186)

            slalom
            "x": 7773,
            "y": 5155
            Good kill location (7777, 5222)

            right seal
            "x": 7915,
            "y": 5275
            Good kill locations
            (7924, 5275)  --> seals below each other
            (7917, 5290) --> one left down, one right up
            
            left seal
            "x": 7655,
            "y": 5275
            Good kill locations
            (7681, 5296) split seal 
            (7675, 5315) long road

         * 
         * */

        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        ITownManagementService _townManagementService;
        private readonly CsConfiguration _csconfig;

        public CSBot(
            IOptions<BotConfiguration> config,
            IOptions<CsConfiguration> csconfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            ITownManagementService townManagementService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _csconfig = csconfig.Value;
            _townManagementService = townManagementService;
        }

        public string GetName()
        {
            return "cs";
        }

        public async Task Run()
        {
            _csconfig.Validate();
            var clients = new List<Tuple<AccountCharacter,Client>>();
            foreach (var account in _csconfig.Accounts)
            {
                var client = new Client();
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                _externalMessagingClient.RegisterClient(client);
                clients.Add(Tuple.Create(account,client));
            }

            int gameCount = 1;

            while (true)
            {
                await GameLoop(clients, gameCount++);
            }

        }

        public async Task GameLoop(List<Tuple<AccountCharacter, Client>> clients, int gameCount)
        {
            var townTasks = clients.Select(async c =>
            {
                var townManagementOptions = new TownManagementOptions()
                {
                    Act = Act.Act4
                };

                if (!await _townManagementService.PerformTownTasks(c.Item2, townManagementOptions))
                {
                    return false;
                }

                if(c.Item1.Character.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return await TeleportToCs(c.Item2);
                }

                return true;
            }
            ).ToList();
            var townResults = await Task.WhenAll(townTasks);
            if (townResults.Any(r => !r))
            {
                Log.Warning($"One or more characters failed there town task");
                return;
            }
        }

        private async Task<bool> TeleportToCs(Client client)
        {
            Log.Information("Teleporting to WayPoint");
            var path1 = await _pathingService.GetPathToObject(client.Game, EntityCode.WaypointAct4, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, path1, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting failed at location {client.Game.Me.Location}");
                return false;
            }

            var waypoint = client.Game.GetEntityByCode(EntityCode.WaypointAct4).Single();
            Log.Information($"Taking waypoint to {Waypoint.RiverOfFlame}");
            GeneralHelpers.TryWithTimeout((_) =>
            {

                client.Game.TakeWaypoint(waypoint, Waypoint.RiverOfFlame);
                return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == Waypoint.RiverOfFlame.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5));

            var path2 = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, Area.RiverOfFlame, Waypoint.RiverOfFlame, Area.ChaosSanctuary, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, path2, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {Area.ChaosSanctuary} warp failed at location {client.Game.Me.Location}");
                return false;
            }

            return true;
        }

        void HandleEventMessage(Client client, EventNotifyPacket eventNotifyPacket)
        {
            if (eventNotifyPacket.PlayerRelationType == PlayerRelationType.InvitesYouToParty)
            {
                var relevantPlayer = client.Game.Players.Where(p => p.Id == eventNotifyPacket.EntityId).FirstOrDefault();
                client.Game.AcceptInvite(relevantPlayer);
            }
        }
    }
}
