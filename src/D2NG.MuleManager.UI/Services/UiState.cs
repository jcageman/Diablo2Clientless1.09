using D2NG.MuleManager.Contracts;
using Microsoft.JSInterop;

namespace D2NG.MuleManager.UI.Services;

/// <summary>
/// What every screen shares: the game mode on show, the overview, the refresh in progress, and the
/// choices the browser remembers between visits (mode, a character's preset).
/// </summary>
public class UiState
{
    private readonly ApiClient _api;
    private readonly IJSRuntime _js;
    private Timer _poll;

    public UiState(ApiClient api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    public GameMode Mode { get; private set; } = GameMode.Classic;
    public MetaResponse Meta { get; private set; }
    public string MetaError { get; private set; }
    public RefreshJobDto Running { get; private set; }
    public RefreshJobDto LastFinished { get; private set; }

    /// <summary>Raised when the mode or the overview changes.</summary>
    public event Action Changed;

    /// <summary>Raised when a refresh finishes, so screens reload what they show.</summary>
    public event Action RefreshFinished;

    public async Task Initialize()
    {
        var stored = await Load("mode");
        if (Enum.TryParse<GameMode>(stored, out var mode))
        {
            Mode = mode;
        }

        await ReloadMeta();
        await PollRefresh();
    }

    public async Task SetMode(GameMode mode)
    {
        if (mode == Mode)
        {
            return;
        }

        Mode = mode;
        await Save("mode", mode.ToString());
        Changed?.Invoke();
    }

    public async Task ReloadMeta()
    {
        try
        {
            Meta = await _api.Meta();
            MetaError = null;
        }
        catch (ApiException e)
        {
            MetaError = e.Message;
        }

        Changed?.Invoke();
    }

    public async Task<RefreshJobDto> StartRefresh(RefreshRequest request)
    {
        var job = await _api.Refresh(request);
        Running = job;
        Changed?.Invoke();
        StartPolling();
        return job;
    }

    private void StartPolling()
    {
        _poll ??= new Timer(async _ => await PollRefresh(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private async Task PollRefresh()
    {
        try
        {
            var jobs = await _api.RefreshJobs();
            var running = jobs.FirstOrDefault(j => j.State is "Queued" or "Running");
            var finished = Running != null && running == null;
            Running = running;
            LastFinished = jobs.FirstOrDefault(j => j.FinishedAt != null);
            if (running != null)
            {
                StartPolling();
            }
            else if (_poll != null)
            {
                await _poll.DisposeAsync();
                _poll = null;
            }

            if (finished)
            {
                await ReloadMeta();
                RefreshFinished?.Invoke();
            }

            Changed?.Invoke();
        }
        catch (ApiException)
        {
        }
    }

    public Task<string> Load(string key) => _js.InvokeAsync<string>("localStorage.getItem", "mulemanager." + key).AsTask();

    public Task Save(string key, string value) => _js.InvokeVoidAsync("localStorage.setItem", "mulemanager." + key, value).AsTask();
}
