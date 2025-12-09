using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Mule;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types;

public class TestBot : IBotInstance
{
    private readonly BotConfiguration _config;
    private readonly IExternalMessagingClient _externalMessagingClient;
    private readonly IPathingService _pathingService;
    private readonly IMapApiService _mapApiService;
    private readonly IMuleService _muleService;
    private readonly IAttackService _attackService;

    public TestBot(
        IOptions<BotConfiguration> config,
        IExternalMessagingClient externalMessagingClient,
        IPathingService pathingService,
        IMapApiService mapApiService,
        IMuleService muleService,
        IAttackService attackService)
    {
        _config = config.Value;
        _externalMessagingClient = externalMessagingClient;
        _pathingService = pathingService;
        _mapApiService = mapApiService;
        _muleService = muleService;
        _attackService = attackService;
    }

    public string GetName()
    {
        return "test";
    }

    public async Task Run()
    {
        var lineOfSight = await IsInLineOfSight(1196731532, new Point(7740, 5260), new Point(7740, 5270));
        Log.Information($"In line of sight: {lineOfSight}");
        /*
        var client1 = new Client();
        if (!client1.Connect(
_config.Realm,
_config.KeyOwner,
_config.GameFolder))
        {
            return;
        }
        var selectedCharacter1 = (await client1.Login("test", "1234"))?.Single(c =>
            c.Name.Equals("testcharacter", StringComparison.CurrentCultureIgnoreCase));
        if (selectedCharacter1 == null)
        {
            throw new CharacterNotFoundException("testcharacter");
        }
        await client1.SelectCharacter(selectedCharacter1);
        client1.Chat.EnterChat();

        await _muleService.MuleItemsForClient(client1);
        */
    }

    public async Task<bool> IsInLineOfSight(uint mapId, Point fromLocation, Point toLocation)
    {
        var directDistance = fromLocation.Distance(toLocation);
        if (directDistance == 0)
        {
            return true;
        }

        var area = await _mapApiService.GetAreaFromLocation(mapId, Difficulty.Normal, toLocation, D2NG.Core.D2GS.Act.Act.Act4, D2NG.Core.D2GS.Act.Area.RiverOfFlame);
        if (area == null)
        {
            return true;
        }

        var areaMap = await _mapApiService.GetArea(mapId, Difficulty.Normal, area.Value);
        var pointsOnLine = GetPointsOnLine(fromLocation.X, fromLocation.Y, toLocation.X, toLocation.Y);
        foreach (var point in pointsOnLine)
        {
            var mapValue = areaMap.Map[point.Y - areaMap.LevelOrigin.Y][point.X - areaMap.LevelOrigin.X];
            if (mapValue % 2 != 0 && mapValue != 1)
            {
                Log.Information($"mapvalue: {mapValue}");
            }
        }

        return true;
    }
    public static IEnumerable<Point> GetPointsOnLine(ushort x0, ushort y0, ushort x1, ushort y1)
    {
        bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep)
        {
            ushort t;
            t = x0; // swap x0 and y0
            x0 = y0;
            y0 = t;
            t = x1; // swap x1 and y1
            x1 = y1;
            y1 = t;
        }
        if (x0 > x1)
        {
            ushort t;
            t = x0; // swap x0 and x1
            x0 = x1;
            x1 = t;
            t = y0; // swap y0 and y1
            y0 = y1;
            y1 = t;
        }
        ushort dx = (ushort)(x1 - x0);
        ushort dy = (ushort)(Math.Abs(y1 - y0));
        ushort error = (ushort)(dx / 2);
        ushort ystep = (ushort)((y0 < y1) ? 1 : -1);
        ushort y = y0;
        for (ushort x = x0; x <= x1; x++)
        {
            yield return new Point((steep ? y : x), (steep ? x : y));
            error = (ushort)(error - dy);
            if (error < 0)
            {
                y += ystep;
                error += dx;
            }
        }
        yield break;
    }
}
