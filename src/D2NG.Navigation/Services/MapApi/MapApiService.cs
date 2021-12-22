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
            var areaMap = _cache.GetOrCreate(Tuple.Create("mapapi", mapId, difficulty, area), (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
                var (_, mapId, difficulty, area) = (Tuple<string, uint, Difficulty, Area>)cacheEntry.Key;
                return new AsyncLazy<AreaMap>(async () => await GetAreaFromApi(mapId, difficulty, area));
            });

            return await areaMap.Value;
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
