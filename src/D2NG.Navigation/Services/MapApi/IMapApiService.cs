using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.MapApi
{
    public interface IMapApiService
    {
        public Task<AreaMap> GetArea(uint mapId, Difficulty difficulty, Area areaId);
    }
}
