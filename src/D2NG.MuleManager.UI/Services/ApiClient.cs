using D2NG.MuleManager.Contracts;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2NG.MuleManager.UI.Services;

/// <summary>The mule manager API. Every call throws <see cref="ApiException"/> with words a person can act on.</summary>
public class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    public string Address => _http.BaseAddress?.ToString();

    public Task<MetaResponse> Meta() => Get<MetaResponse>("api/meta");

    public Task<StockResponse> Stock(GameMode mode, string query, IEnumerable<string> qualities, IEnumerable<string> types, IEnumerable<string> stats, IEnumerable<string> sorts, int page)
    {
        var parts = new List<string> { $"mode={mode}", $"page={page}" };
        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add("q=" + Uri.EscapeDataString(query.Trim()));
        }

        parts.AddRange(sorts.Select(s => "sort=" + Uri.EscapeDataString(s)));

        parts.AddRange(qualities.Select(q => "quality=" + Uri.EscapeDataString(q)));
        parts.AddRange(types.Select(t => "type=" + Uri.EscapeDataString(t)));
        parts.AddRange(stats.Select(s => "stat=" + Uri.EscapeDataString(s)));
        return Get<StockResponse>("api/stock?" + string.Join("&", parts));
    }

    public Task<MulesResponse> Mules(GameMode mode) => Get<MulesResponse>($"api/mules?mode={mode}");

    public Task<CharactersResponse> Characters(GameMode mode) => Get<CharactersResponse>($"api/characters?mode={mode}");

    public Task<UpgradeResponse> Upgrades(UpgradeRequest request) => Post<UpgradeResponse>("api/upgrades", request);

    public Task<List<RefreshJobDto>> RefreshJobs() => Get<List<RefreshJobDto>>("api/refresh");

    public Task<RefreshJobDto> Refresh(RefreshRequest request) => Post<RefreshJobDto>("api/refresh", request);

    private async Task<T> Get<T>(string path)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path);
        }
        catch (HttpRequestException)
        {
            throw Unreachable();
        }

        return await Read<T>(response);
    }

    private async Task<T> Post<T>(string path, object body)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, body, Json);
        }
        catch (HttpRequestException)
        {
            throw Unreachable();
        }

        return await Read<T>(response);
    }

    private ApiException Unreachable()
        => new($"The mule manager API does not answer at {Address}. Start D2NG.MuleManager, or set apiBaseAddress in wwwroot/appsettings.json.");

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new ApiException(string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('<')
                ? $"The API answered {(int)response.StatusCode} {response.ReasonPhrase}."
                : text.Trim('"'));
        }

        return await response.Content.ReadFromJsonAsync<T>(Json);
    }
}

public class ApiException(string message) : Exception(message);
