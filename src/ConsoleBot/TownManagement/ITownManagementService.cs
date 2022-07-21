using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using System.Threading.Tasks;

namespace ConsoleBot.TownManagement
{
    public interface ITownManagementService
    {
        Task<bool> CreateTownPortal(Client client);
        Task<TownTaskResult> PerformTownTasks(Client client, TownManagementOptions options);
        Task<bool> SwitchAct(Client client, Act act);
        Task<bool> TakeTownPortalToArea(Client client, Player player, Area area);
        Task<bool> TakeTownPortalToTown(Client client);
        Task<bool> TakeWaypoint(Client client, Waypoint waypoint);
    }
}
