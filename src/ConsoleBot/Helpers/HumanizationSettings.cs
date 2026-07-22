using ConsoleBot.Bots;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

public static class HumanizationSettings
{
    private static HumanizationConfiguration _config = new();

    public static void Configure(HumanizationConfiguration config)
    {
        _config = config ?? new HumanizationConfiguration();
    }

    public static int GetActionDelayMs(int baseMs)
    {
        if (!_config.Enabled)
        {
            return baseMs;
        }

        return baseMs + Random.Shared.Next(_config.ActionJitterMinMs, _config.ActionJitterMaxMs + 1);
    }

    public static Task DelayAsync(int baseMs)
    {
        return Task.Delay(GetActionDelayMs(baseMs));
    }

    public static void SleepThread(int baseMs)
    {
        Thread.Sleep(GetActionDelayMs(baseMs));
    }

    public static Task StandaloneDelayAsync(int minMs, int maxMs)
    {
        if (!_config.Enabled)
        {
            return Task.CompletedTask;
        }

        return Task.Delay(Random.Shared.Next(minMs, maxMs + 1));
    }

    public static Task WaypointPauseAsync()
    {
        return StandaloneDelayAsync(_config.WaypointPauseMinMs, _config.WaypointPauseMaxMs);
    }

    public static Task TownTaskPauseAsync()
    {
        return StandaloneDelayAsync(_config.TownTaskPauseMinMs, _config.TownTaskPauseMaxMs);
    }

    public static TimeSpan GetRandomSeconds(int minSeconds, int maxSeconds)
    {
        if (!_config.Enabled)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(Random.Shared.Next(minSeconds, maxSeconds + 1));
    }
}
