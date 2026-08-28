using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.MapApi;

public interface IMapApiService
{
    /// <summary>
    /// Returns whether the map API answers at all. Used as a startup preflight so a map server
    /// that is not running is reported before the bot logs on and starts creating games.
    /// </summary>
    Task<bool> IsAvailable();

    Task<AreaMap> GetArea(uint mapId, Difficulty difficulty, Area areaId);

    Task<Area?> GetAreaFromLocation(uint mapId, Difficulty difficulty, Point point, Act act, Area? hintArea);
}
