using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleBot.Attack
{
    public interface IAttackService
    {
        Task<bool> IsInLineOfSight(Client client, Point fromLocation, Point toLocation);
        Task<bool> IsInLineOfSight(Client client, Point toLocation);
        Task<bool> IsVisitable(Client client, Point point);
        Task TeleportToNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, double minDistance = 0);
    }
}
