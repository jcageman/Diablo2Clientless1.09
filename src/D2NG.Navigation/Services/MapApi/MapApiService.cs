using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Navigation.Core;
using D2NG.Navigation.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.MapApi
{
    public class MapApiService : IMapApiService
    {
        private const string MapApiKey = "mapapi";
        private readonly MapConfiguration _mapConfiguration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;

        public MapApiService(IOptions<MapConfiguration> config, IHttpClientFactory httpClientFactory, IMemoryCache cache)
        {
            _mapConfiguration = config.Value ?? throw new ArgumentNullException(nameof(config), $"MapApiService constructor fails due to MapConfiguration being null");
            _httpClientFactory = httpClientFactory;
            _cache = cache;
        }
        public async Task<AreaMap> GetArea(uint mapId, Difficulty difficulty, Area area)
        {
            var areaMap = _cache.GetOrCreate(GetMapApiKey(mapId, difficulty, area), (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
                var (_, mapId, difficulty, area) = (Tuple<string, uint, Difficulty, Area>)cacheEntry.Key;
                return new AsyncLazy<AreaMap>(async () => await GetAreaFromApi(mapId, difficulty, area));
            });

            return await areaMap.Value;
        }

        public async Task<Area?> GetAreaFromLocation(uint mapId, Difficulty difficulty, Point point, Area? hintArea)
        {
            if (hintArea != null && _cache.TryGetValue(GetMapApiKey(mapId, difficulty, hintArea.Value), out AsyncLazy<AreaMap> hintAreaMapLazy))
            {
                var hintAreaMap = await hintAreaMapLazy.Value;
                if (hintAreaMap.TryMapToPointInMap(point, out var _))
                {
                    return hintArea;
                }
            }

            foreach (var area in (Area[])Enum.GetValues(typeof(Area)))
            {
                if (_cache.TryGetValue(GetMapApiKey(mapId, difficulty, area), out AsyncLazy<AreaMap> areaMapLazy))
                {
                    var areaMap = await areaMapLazy.Value;
                    if (areaMap.TryMapToPointInMap(point, out var _))
                    {
                        return area;
                    }
                }
            }

            return null;
        }

        private static Tuple<string, uint, Difficulty, Area> GetMapApiKey(uint mapId, Difficulty difficulty, Area area)
        {
            return Tuple.Create(MapApiKey, mapId, difficulty, area);
        }

        private async Task<AreaMap> GetAreaFromApi(uint mapId, Difficulty difficulty, Area area)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync($"{_mapConfiguration.ApiUrl}/maps?mapid={mapId}&area={area}&difficulty={difficulty}");

            var areaDto = await response.Content.ReadAsAsync<AreaMapDto>();
            return areaDto.MapFromDto();
        }
    }
}
