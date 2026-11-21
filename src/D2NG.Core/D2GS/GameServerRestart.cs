using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace D2NG.Core.D2GS;

/// <summary>
/// The realm restarts a game server after a countdown its administrator announces in every game on it: "This gameserver is
/// restarting in 4 minutes" and on down to the minute, "Restarting gameserver in 30 seconds. Please leave your game.", then
/// "** GAMESERVER RESTARTING **" every second until the server drops every client about fifteen seconds later. A game created
/// during the countdown hears it on joining and is dropped with the rest. Every client in a game hears each line within a few
/// milliseconds of the others and leaves a few seconds before the restart (<see cref="Client"/>). The time is kept for the whole
/// process, not per client: the leveling bot logs in new clients for every game, and with the time kept per client its next
/// game was created a second after the old one was left and dropped with the server.
/// </summary>
public static partial class GameServerRestart
{
    public const string Administrator = "[administrator]";

    /// <summary>How long before the announced restart a client leaves its game.</summary>
    public static readonly TimeSpan LeaveBefore = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after the announced restart new games wait. The announcements run a few seconds early - the drop came six
    /// seconds after the "30 seconds" one ran out - and a party that created its next game 95 seconds after the drop played it
    /// without trouble.
    /// </summary>
    public static readonly TimeSpan Downtime = TimeSpan.FromSeconds(75);

    /// <summary>What "** GAMESERVER RESTARTING **" leaves: it repeats every second for about fifteen before the drop.</summary>
    private static readonly TimeSpan Restarting = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"restarting in (\d+) minutes?", RegexOptions.IgnoreCase)]
    private static partial Regex Minutes();

    [GeneratedRegex(@"in (\d+) seconds?", RegexOptions.IgnoreCase)]
    private static partial Regex Seconds();

    /// <summary>How long until the restart an administrator message announces; null for any other chat.</summary>
    public static TimeSpan? Announced(string characterName, string message)
    {
        if (!string.Equals(characterName, Administrator, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(message))
        {
            return null;
        }

        if (Minutes().Match(message) is { Success: true } minutes)
        {
            return TimeSpan.FromMinutes(int.Parse(minutes.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (message.Contains("restart", StringComparison.OrdinalIgnoreCase) && Seconds().Match(message) is { Success: true } seconds)
        {
            return TimeSpan.FromSeconds(int.Parse(seconds.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        return message.Contains("GAMESERVER RESTARTING", StringComparison.OrdinalIgnoreCase) ? Restarting : null;
    }

    /// <summary>
    /// The restart time to keep after hearing one at <paramref name="heard"/>: the earliest of a countdown still running, a new
    /// one once the last restart is over.
    /// </summary>
    public static DateTime Merge(DateTime? known, DateTime heard, DateTime now)
        => known is { } at && at + Downtime > now && at < heard ? at : heard;

    private static readonly object Gate = new();

    private static DateTime? _restartAt;

    /// <summary>The restart this process's clients last heard announced, if any.</summary>
    public static DateTime? RestartAt
    {
        get
        {
            lock (Gate)
            {
                return _restartAt;
            }
        }
    }

    /// <summary>Records an announcement heard now, returning the restart time kept.</summary>
    public static DateTime Heard(TimeSpan until, DateTime now)
    {
        lock (Gate)
        {
            _restartAt = Merge(_restartAt, now + until, now);
            return _restartAt.Value;
        }
    }

    /// <summary>How long a new game waits for a restart known at <paramref name="at"/>; zero when it is over or none is known.</summary>
    public static TimeSpan WaitFor(DateTime? at, DateTime now)
        => at is { } restart && restart + Downtime > now ? restart + Downtime - now : TimeSpan.Zero;
}
