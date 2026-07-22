using ConsoleBot.Bots;
using Serilog;
using System;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

public class GameBreakScheduler
{
    private readonly HumanizationConfiguration _config;
    private int _gamesUntilShortBreak;
    private int _gamesUntilLongBreak;

    public GameBreakScheduler(HumanizationConfiguration config)
    {
        _config = config ?? new HumanizationConfiguration();
        _gamesUntilShortBreak = NextShortBreakThreshold();
        _gamesUntilLongBreak = NextLongBreakThreshold();
    }

    private int NextShortBreakThreshold()
    {
        return Random.Shared.Next(_config.ShortBreakEveryGamesMin, _config.ShortBreakEveryGamesMax + 1);
    }

    private int NextLongBreakThreshold()
    {
        return Random.Shared.Next(_config.LongBreakEveryGamesMin, _config.LongBreakEveryGamesMax + 1);
    }

    public async Task<bool> MaybeApplyBreakAsync()
    {
        if (!_config.Enabled)
        {
            return false;
        }

        if (_gamesUntilLongBreak <= 0)
        {
            var duration = TimeSpan.FromSeconds(Random.Shared.Next(_config.LongBreakDurationMinSeconds, _config.LongBreakDurationMaxSeconds + 1));
            Log.Information("Taking a long break for {Duration}", duration);
            await Task.Delay(duration);
            _gamesUntilLongBreak = NextLongBreakThreshold();
            _gamesUntilShortBreak = NextShortBreakThreshold();
            return true;
        }

        if (_gamesUntilShortBreak <= 0)
        {
            var duration = TimeSpan.FromSeconds(Random.Shared.Next(_config.ShortBreakDurationMinSeconds, _config.ShortBreakDurationMaxSeconds + 1));
            Log.Information("Taking a short break for {Duration}", duration);
            await Task.Delay(duration);
            _gamesUntilShortBreak = NextShortBreakThreshold();
            return true;
        }

        return false;
    }

    public TimeSpan GetPreGameCreateDelay()
    {
        return HumanizationSettings.GetRandomSeconds(_config.PreGameCreateDelayMinSeconds, _config.PreGameCreateDelayMaxSeconds);
    }

    public void RecordGameCompleted()
    {
        _gamesUntilShortBreak--;
        _gamesUntilLongBreak--;
    }
}
