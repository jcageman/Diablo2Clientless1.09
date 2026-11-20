using D2NG.Core.D2GS.Motion;
using D2NG.Core.MonsterData;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace ConsoleBot.Helpers;

/// <summary>Every client the bot creates moves monsters along their path at their speed (simulated).</summary>
public static class MonsterMotionSetup
{
    public const MonsterMotion Mode = MonsterMotion.Simulated;

    /// <summary>Call after the game data folder is set, before the first client is made.</summary>
    public static void Apply(IConfigurationSection bot)
    {
        if (bot["monsterMotion"] is string configured)
        {
            Log.Warning("bot:monsterMotion '{Value}' is no longer read: every bot runs simulated monster motion", configured);
        }

        MonsterMotionSettings.DefaultMode = Mode;
        Log.Information("Monster motion: configured {Configured}, effective {Effective}",
            Mode, MonsterMotionSettings.Effective(Mode, MonsterSpeedTable.Shared));
    }
}
