namespace ConsoleBot.Bots;

public class HumanizationConfiguration
{
    public bool Enabled { get; set; }

    public int ActionJitterMinMs { get; set; }
    public int ActionJitterMaxMs { get; set; } = 150;

    public int TownTaskPauseMinMs { get; set; } = 300;
    public int TownTaskPauseMaxMs { get; set; } = 1500;

    public int WaypointPauseMinMs { get; set; } = 400;
    public int WaypointPauseMaxMs { get; set; } = 1800;

    public int PreGameCreateDelayMinSeconds { get; set; } = 3;
    public int PreGameCreateDelayMaxSeconds { get; set; } = 15;

    public int ShortBreakEveryGamesMin { get; set; } = 8;
    public int ShortBreakEveryGamesMax { get; set; } = 20;
    public int ShortBreakDurationMinSeconds { get; set; } = 90;
    public int ShortBreakDurationMaxSeconds { get; set; } = 300;

    public int LongBreakEveryGamesMin { get; set; } = 40;
    public int LongBreakEveryGamesMax { get; set; } = 80;
    public int LongBreakDurationMinSeconds { get; set; } = 600;
    public int LongBreakDurationMaxSeconds { get; set; } = 1800;

    public int JoinStaggerMinSeconds { get; set; } = 5;
    public int JoinStaggerMaxSeconds { get; set; } = 25;
}
