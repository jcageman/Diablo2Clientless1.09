using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Navigation.Core;
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
            var session = _cache.GetOrCreate(Tuple.Create("mapapi", mapId, difficulty), (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
                cacheEntry.RegisterPostEvictionCallback(DeleteSession);
                var (_, map, diff) = (Tuple<string, uint, Difficulty>)cacheEntry.Key;
                return new AsyncLazy<string>(async () => await CreateSession(map, diff));
            });

            return await session.Value;
        }

        private async Task<AreaMap> GetAreaFromApiCached(string sessionId, Area area)
        {
            var areaMap = _cache.GetOrCreate(Tuple.Create("mapapi", sessionId, area), (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
                var (_, sessionId, area) = (Tuple<string, string, Area>)cacheEntry.Key;
                return new AsyncLazy<AreaMap>(async () => await GetAreaFromApi(sessionId, area));
            });

            return await areaMap.Value;
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
            var lazySession = (AsyncLazy<string>)value;
            if(lazySession.IsValueCreated && lazySession.Value.IsCompleted)
            {
                var sessionId = lazySession.Value.Result;
                client.DeleteAsync($"{_mapConfiguration.ApiUrl}/sessions/{sessionId}").Wait();
            }
        }
    }
}
