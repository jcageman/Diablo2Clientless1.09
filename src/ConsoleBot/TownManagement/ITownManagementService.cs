using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using System.Threading.Tasks;

namespace ConsoleBot.TownManagement
{
    public interface ITownManagementService
    {
        Task<bool> PerformTownTasks(Client client, TownManagementOptions options);
        Task<bool> TakeTownPortalToTown(Client client);
        Task<bool> TakeWaypoint(Client client, Waypoint waypoint);
    }
}
