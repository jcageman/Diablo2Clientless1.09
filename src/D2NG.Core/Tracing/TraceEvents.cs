using D2NG.Core.D2GS;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.Tracing;

public enum StepPhase
{
    Start,
    End,
}

public enum StepOutcome
{
    Ok,
    Failed,
    Timeout,
}

public enum MoveKind
{
    Walk,
    Teleport,
    PathRequest,
    Warp,
}

public enum MoveOutcome
{
    Arrived,
    Stuck,
    EmptyPath,
    NotNavigable,
    Timeout,
    Cancelled,
    LeftGame,
}

/// <summary>A unit a target choice passed over, and the filter that removed it.</summary>
public readonly record struct RejectedUnit(uint Id, string Code, string Filter);

/// <summary>
/// The typed events of one recorder's sidecar. Each carries the client (the character name, which is
/// also the trace file name) and the game name. Target and wait are written only when they change;
/// incidents - a failed or timed out step, a chicken, a death - wake the writer so they reach disk
/// without waiting for its next pass. These run inside the bot's own decisions, so no method throws:
/// a failure is counted and the sidecar reports the count.
/// </summary>
public sealed class TraceEventWriter
{
    private readonly TraceRecorder _recorder;
    private readonly ConcurrentDictionary<string, (uint? Id, string Reason)> _targets = new();
    private readonly ConcurrentDictionary<(string Client, string Reason), bool> _waits = new();

    internal TraceEventWriter(TraceRecorder recorder) => _recorder = recorder;

    public void Step(string client, string game, string name, StepPhase phase, int attempt = 1, StepOutcome? outcome = null, string reason = null, TimeSpan? duration = null)
    {
        try
        {
            Write("step", client, game,
            [
                ("name", name),
                ("attempt", attempt),
                ("phase", Lower(phase)),
                ("outcome", outcome == null ? null : Lower(outcome.Value)),
                ("reason", reason),
                ("ms", duration),
            ]);
            if (phase == StepPhase.End && outcome is StepOutcome.Failed or StepOutcome.Timeout)
            {
                _recorder.Wake();
            }
        }
        catch (Exception)
        {
            CountFailure();
        }
    }

    public void Target(string client, string game, uint? unitId, string code, string reason, IReadOnlyList<RejectedUnit> rejected = null)
    {
        try
        {
            var current = (unitId, reason);
            if (_targets.TryGetValue(client ?? "", out var previous) && previous == current)
            {
                return;
            }

            Write("target", client, game,
            [
                ("id", unitId),
                ("code", code),
                ("reason", reason),
                ("rejected", rejected is { Count: > 0 } ? rejected.ToArray() : null),
            ]);
            _targets[client ?? ""] = current;
        }
        catch (Exception)
        {
            CountFailure();
        }
    }

    public void Wait(string client, string game, string reason, bool entered)
    {
        try
        {
            var key = (client ?? "", reason ?? "");
            var known = _waits.TryGetValue(key, out var previous);
            if ((known && previous == entered) || (!known && !entered))
            {
                return;
            }

            Write("wait", client, game, [("reason", reason), ("state", entered ? "entered" : "left")]);
            _waits[key] = entered;
        }
        catch (Exception)
        {
            CountFailure();
        }
    }

    public void Move(string client, string game, Point from, Point to, string area, MoveKind kind, MoveOutcome outcome, string fallback = null)
    {
        try
        {
            Write("move", client, game,
            [
                ("from", from),
                ("to", to),
                ("area", area),
                ("kind", Kebab(kind.ToString())),
                ("outcome", Kebab(outcome.ToString())),
                ("fallback", fallback),
            ]);
        }
        catch (Exception)
        {
            CountFailure();
        }
    }

    public void Decision(string client, string game, string name, params (string Key, object Value)[] fields)
    {
        try
        {
            Write("decision", client, game, [("name", name), ("f", fields)]);
            if (name is "chicken" or "death" or "stranger" or "exception")
            {
                _recorder.Wake();
            }
        }
        catch (Exception)
        {
            CountFailure();
        }
    }

    /// <summary>The bot's settings for a game the client entered: one event under "f", key to value.</summary>
    public void Settings(string client, string game, IReadOnlyList<(string Key, string Value)> settings)
    {
        try
        {
            Write("settings", client, game, [("f", settings.Select(s => (s.Key, (object)s.Value)).ToArray())]);
        }
        catch (Exception)
        {
            CountFailure();
        }
    }

    internal void CountFailure() => _recorder.Sidecar.CountFailure();

    private void Write(string kind, string client, string game, (string Key, object Value)[] fields)
        => _recorder.Sidecar.Enqueue(new TraceEvent(kind, client, game, fields));

    private static string Lower(Enum value) => value.ToString().ToLowerInvariant();

    internal static string Kebab(string name)
        => string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>
/// The typed events, written to the current recorder's sidecar; nothing happens while tracing is off.
/// The <see cref="Game"/> overloads take the client and game name from the game's trace and read any
/// game state they report inside their guard, so no call throws into the bot.
/// </summary>
public static class TraceEvents
{
    public static bool Enabled => TraceRecorder.Current != null;

    private static TraceEventWriter Writer => TraceRecorder.Current?.Events;

    public static void Step(string client, string game, string name, StepPhase phase, int attempt = 1, StepOutcome? outcome = null, string reason = null, TimeSpan? duration = null)
        => Writer?.Step(client, game, name, phase, attempt, outcome, reason, duration);

    public static void Step(Game game, string name, StepPhase phase, int attempt = 1, StepOutcome? outcome = null, string reason = null, TimeSpan? duration = null)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Step(client, gameName, name, phase, attempt, outcome, reason, duration);
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    /// <summary>The reason is worked out inside the guard, and only while tracing is on.</summary>
    public static void Step(Game game, string name, StepPhase phase, int attempt, StepOutcome? outcome, Func<string> reason, TimeSpan? duration = null)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Step(client, gameName, name, phase, attempt, outcome, reason?.Invoke(), duration);
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    public static void Target(Game game, uint? unitId, string code, string reason, IReadOnlyList<RejectedUnit> rejected = null)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Target(client, gameName, unitId, code, reason, rejected);
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    public static void Wait(Game game, string reason, bool entered)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Wait(client, gameName, reason, entered);
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    public static void Move(Game game, Point from, Point to, MoveKind kind, MoveOutcome outcome, string fallback = null)
        => Move(game, from, to, kind, () => outcome, fallback);

    /// <summary>The outcome is worked out inside the guard, and only while tracing is on.</summary>
    public static void Move(Game game, Point from, Point to, MoveKind kind, Func<MoveOutcome> outcome, string fallback = null)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Move(client, gameName, from, to, game.Me == null ? null : game.Area.ToString(), kind, outcome(), fallback);
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    public static void Decision(string client, string game, string name, params (string Key, object Value)[] fields)
        => Writer?.Decision(client, game, name, fields);

    public static void Decision(Game game, string name, params (string Key, object Value)[] fields)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Decision(client, gameName, name, fields);
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    /// <summary>A chicken: the leave the life watch asked for, with the life it saw.</summary>
    public static void Chicken(Game game, string reason)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Decision(client, gameName, "chicken", ("life", game.Me?.Life), ("maxLife", game.Me?.MaxLife), ("reason", reason));
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }

    /// <summary>
    /// The server assigned this client's corpse. A death when damage was just taken; right after a join it
    /// is the body of an earlier one.
    /// </summary>
    public static void Death(Game game, uint corpseId, bool damagedRecently, Point at)
    {
        if (Writer is not { } writer)
        {
            return;
        }

        try
        {
            var (client, gameName) = game.TraceIdentity;
            writer.Decision(client, gameName, "death", ("corpse", corpseId), ("damagedRecently", damagedRecently), ("at", at), ("area", game.Me == null ? null : game.Area.ToString()));
        }
        catch (Exception)
        {
            writer.CountFailure();
        }
    }
}
