using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using System.Threading.Tasks;

namespace ConsoleBot.TownManagement;

public interface ITownManagementService
{
    Task<bool> CreateTownPortal(Client client);
    /// <summary>
    /// Identifies what the character carries from the tome and sells what the pickit does not keep,
    /// buying nothing. Where the character stands in town is where it starts from.
    /// </summary>
    Task<bool> IdentifyAndSell(Client client);

    /// <summary>
    /// Before a mule game: back to town, everything identified at Cain and the rest sold. The mule game
    /// is Normal, where Cain only stands for a character that rescued him there.
    /// </summary>
    Task<bool> IdentifyAtCainAndSell(Client client);
    Task<TownTaskResult> PerformTownTasks(Client client, TownManagementOptions options);
    Task<bool> SwitchAct(Client client, Act act);
    Task<bool> TakeTownPortalToArea(Client client, Player player, Area area);
    Task<bool> TakeTownPortalToTown(Client client);
    Task<bool> TakeWaypoint(Client client, Waypoint waypoint);
}
