using D2NG.Core.D2GS.Objects;
using D2NG.Core.MonsterData;
using Serilog;
using System;
using System.Threading;

namespace D2NG.Core.D2GS.Motion;

/// <summary>The motion settings of one client, kept across its games.</summary>
public sealed class MonsterMotionSettings
{
    private volatile int _mode = (int)DefaultMode;
    private readonly TimeProvider _clock = TimeProvider.System;
    private volatile MotionRules _rules = MotionRules.Default;

    /// <summary>The mode every client created afterwards starts in; the bot sets it from its config.</summary>
    public static MonsterMotion DefaultMode { get; set; } = MonsterMotion.Legacy;

    public MonsterMotion Mode
    {
        get => (MonsterMotion)_mode;
        set => _mode = (int)value;
    }

    /// <summary>What <see cref="Mode"/> amounts to with the speed data at hand.</summary>
    public MonsterMotion EffectiveMode => Effective(Mode, Rules.SpeedTable);

    /// <summary>
    /// Simulated without speeds would stand every monster at its last exact position, which is worse than
    /// Legacy on walks, so it reads the legacy position instead.
    /// </summary>
    public static MonsterMotion Effective(MonsterMotion mode, MonsterSpeedTable speeds)
        => mode == MonsterMotion.Simulated && speeds?.HasData != true ? MonsterMotion.Legacy : mode;

    /// <summary>Monotonic; a replay drives it from the record timestamps. Fixed once made: anchors taken with one clock cannot be read with another.</summary>
    public TimeProvider Clock
    {
        get => _clock;
        init => _clock = value ?? TimeProvider.System;
    }

    public MotionRules Rules
    {
        get => _rules;
        set => _rules = value ?? MotionRules.Default;
    }

    /// <summary>Reads the speed table now, so the first move packet does not parse it on a receive thread.</summary>
    public void LoadSpeeds() => _ = Rules.SpeedTable;
}

/// <summary>What a monster needs to answer where it is now: the settings, the clock, and where a chased unit is.</summary>
internal sealed class MotionContext(MonsterMotionSettings settings, Func<EntityType, uint, Point> target)
{
    public MonsterMotion EffectiveMode => settings.EffectiveMode;

    public MotionRules Rules => settings.Rules;

    public long Now => settings.Clock.GetTimestamp();

    public double Seconds(long from, long to) => settings.Clock.GetElapsedTime(from, to).TotalSeconds;

    public Point Target(EntityType type, uint id)
    {
        try
        {
            return target?.Invoke(type, id);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException or NullReferenceException)
        {
            return null;
        }
    }
}

/// <summary>
/// Motion tracking shares the packet handlers with the game state and the bots: a failure in it must
/// not leave the handler, or it would end the connection and skip every handler registered after it.
/// </summary>
internal static class MotionFailures
{
    private static int _logged;
    private static long _count;

    public static long Count => Interlocked.Read(ref _count);

    public static void Report(Exception e)
    {
        Interlocked.Increment(ref _count);
        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            Log.Warning(e, "Monster motion tracking failed on a packet and skipped it; later failures in this process are not logged");
        }
    }
}
