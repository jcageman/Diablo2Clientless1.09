using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Navigation.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Net.Http;
using System.Net.Http.Formatting;
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
            var sessionId = await GetSessionCached(mapId, difficulty);
            return await GetAreaFromApiCached(sessionId, area);
        }

        private async Task<string> GetSessionCached(uint mapId, Difficulty difficulty)
        {
            var session = await _cache.GetOrCreateAsync(Tuple.Create(mapId, difficulty), async (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
                cacheEntry.RegisterPostEvictionCallback(DeleteSession);
                var (map, diff) = (Tuple<uint, Difficulty>)cacheEntry.Key;
                return await CreateSession(map, diff);
            });

            return session;
        }

        private async Task<AreaMap> GetAreaFromApiCached(string sessionId, Area area)
        {
            var areaMap = await _cache.GetOrCreateAsync(Tuple.Create(sessionId, area), async (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
                var (sessionId, area) = (Tuple<string, Area>)cacheEntry.Key;
                return await GetAreaFromApi(sessionId, area);
            });

            return areaMap;
        }

        private async Task<AreaMap> GetAreaFromApi(string sessionId, Area area)
        {
            var client = _httpClientFactory.CreateClient();
            var areaId = (int)area;
            var response = await client.GetAsync($"{_mapConfiguration.ApiUrl}/sessions/{sessionId}/areas/{areaId}");

            var areaDto = await response.Content.ReadAsAsync<AreaMapDto>();
            return areaDto.MapFromDto();
        }

        private async Task<string> CreateSession(uint mapId, Difficulty difficulty)
        {
            var client = _httpClientFactory.CreateClient();
            var newSession = new CreateSessionDto
            {
                Difficulty = (int)difficulty,
                MapId = (int)mapId
            };
            var response = await client.PostAsync($"{_mapConfiguration.ApiUrl}/sessions/", newSession,
                new JsonMediaTypeFormatter());

            var createdSession = await response.Content.ReadAsAsync<SessionDto>();
            return createdSession.Id;
        }

        private void DeleteSession(object key, object value, EvictionReason reason, object state)
        {
            var client = _httpClientFactory.CreateClient();
            var (sessionId, _) = (Tuple<uint, Difficulty>)key;
            client.DeleteAsync($"{_mapConfiguration.ApiUrl}/sessions/{sessionId}").Wait();
        }
    }
}
