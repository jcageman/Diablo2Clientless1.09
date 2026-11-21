using D2NG.Core;
using D2NG.Core.Tracing;
using System;
using System.Globalization;

namespace D2NG.Trace;

/// <summary>The rules a trace is replayed under by default; <paramref name="Basis"/> says why when the header does not name them.</summary>
public sealed record RulesChoice(ClientRules Rules, bool Estimated, string Basis);

/// <summary>
/// The client rules a replay interprets a trace under: by default those its header names (<c>--rules recorded</c>), or one
/// version for every trace of the process (<c>--rules N</c>, <c>--rules latest</c>) - what today's or a proposed rule
/// would have made of an old game. A real client's capture replays under the latest, and a bot trace written before the
/// header named its rules under an estimate (<see cref="UnstampedRules"/>).
/// </summary>
public static class ReplayRules
{
    private static volatile ClientRules _override;
    private static volatile string _overrideText;

    /// <summary>The rules every replay uses whatever its header says; null replays each under its recorded rules.</summary>
    public static ClientRules Override => _override;

    /// <summary>What was asked for, as typed: "latest", "1"; null without an override.</summary>
    public static string OverrideText => _overrideText;

    /// <summary>Reads <c>--rules &lt;n|latest|recorded&gt;</c> from a command line; absent leaves the recorded rules.</summary>
    public static void UseOption(string[] args)
    {
        var index = Array.IndexOf(args, "--rules");
        if (index < 0)
        {
            Use(null);
            return;
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException("--rules takes a version number, latest or recorded");
        }

        Use(args[index + 1]);
    }

    /// <param name="text">A version, "latest" or "recorded"; null is recorded.</param>
    public static void Use(string text)
    {
        if (text == null || text.Equals("recorded", StringComparison.OrdinalIgnoreCase))
        {
            (_override, _overrideText) = (null, null);
            return;
        }

        if (text.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            (_override, _overrideText) = (ClientRules.Latest, "latest");
            return;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version > ClientRules.LatestVersion)
        {
            throw new ArgumentException($"--rules {text}: give a version from 0 to {ClientRules.LatestVersion}, latest or recorded");
        }

        (_override, _overrideText) = (ClientRules.ForVersion(version), text);
    }

    /// <summary>The rules a trace with this header is replayed under.</summary>
    /// <param name="path">The trace file, whose records an unstamped header's estimate looks at; null estimates from the start time alone.</param>
    public static ClientRules For(TraceHeader header, string path = null) => _override ?? Resolve(header, path).Rules;

    /// <summary>The rules the header names, or for a header that names none the best estimate: the latest for a real client, and for a version this build does not know.</summary>
    public static ClientRules Recorded(TraceHeader header, string path = null) => Resolve(header, path).Rules;

    /// <summary>The rules a trace was recorded under as far as can be told, and on what that rests.</summary>
    public static RulesChoice Resolve(TraceHeader header, string path = null)
    {
        if (header.Source == TraceSource.Sniffer || header.RulesVersion == TraceFormat.RulesNotApplicable)
        {
            return new RulesChoice(ClientRules.Latest, false, "a real client interprets packets by none of our rules");
        }

        return header.RulesVersion is { } version
            ? new RulesChoice(ClientRules.ForVersion(version), false, null)
            : UnstampedRules.Estimate(header.StartUtc, path == null ? null : TraceFileSummary.Of(path).Evidence);
    }

    /// <summary>True when the header names rules newer than this build knows, so its replay runs under older ones.</summary>
    public static bool NewerThanKnown(TraceHeader header)
        => header.Source != TraceSource.Sniffer && header.RulesVersion is { } version && version != TraceFormat.RulesNotApplicable && version > ClientRules.LatestVersion;

    /// <summary>The one line a tool prints for a trace recorded under rules this build does not know.</summary>
    public static string NewerRulesNote(int version)
        => $"this trace was recorded under client rules {version}, which this build does not know (it knows up to {ClientRules.LatestVersion}): it is replayed under rules {ClientRules.LatestVersion} and state after any newer rule may differ from what the bot held";
}

/// <summary>What a trace's own records tell about the build that wrote it, for a header that does not name its rules.</summary>
/// <param name="OpensShop">A <see cref="ClientAction.OpenShop"/> record: written by builds since both monster life rules arrived.</param>
/// <param name="AsksVendor">A sent trade, gamble or gamble refresh request (0x38 with action 1, 2 or 4): the bot visited a vendor.</param>
public readonly record struct BuildEvidence(bool OpensShop, bool AsksVendor);

/// <summary>
/// The rules of bot traces written before the header named them. Bot builds of the two days before the stamp ran
/// different rules at different times and wrote nothing that names them, so an unstamped trace is replayed under an
/// estimate, said to be one: first from its own records - a <see cref="ClientAction.OpenShop"/> is written since the commit
/// that brought both monster life rules (rules 1), and vendor visits without one mean a build before it (rules 0) - and
/// otherwise from its start time against the table below. The cursor rule (2) leaves no mark in a trace: the leveling
/// build ran an interim one from about 2026-11-14 12:25 UTC and the Travincal trial build none, so neither is assumed.
/// </summary>
public static class UnstampedRules
{
    /// <summary>Unstamped builds started before this ran neither monster life rule; from it on, both (with or without an interim cursor rule).</summary>
    public static readonly DateTime LifeRulesFromUtc = new(2026, 11, 14, 12, 39, 0, DateTimeKind.Utc);

    /// <summary>From about here the leveling build ran an interim cursor rule that no header names.</summary>
    public static readonly DateTime InterimCursorFromUtc = new(2026, 11, 14, 12, 25, 0, DateTimeKind.Utc);

    private const string CursorUnknown = "whether its build ran a cursor rule cannot be told from a trace (the leveling build ran an interim one from about 2026-11-14 12:25 UTC, the Travincal trial build none); --rules 2 replays with it";

    public static RulesChoice Estimate(DateTime startUtc, BuildEvidence? evidence)
    {
        var cursor = startUtc >= InterimCursorFromUtc ? "; " + CursorUnknown : "";
        if (evidence is { OpensShop: true })
        {
            return new RulesChoice(ClientRules.ForVersion(1), true, "the trace records a shop opening, which builds write since both monster life rules arrived" + cursor);
        }

        if (evidence is { AsksVendor: true })
        {
            return new RulesChoice(ClientRules.None, true, "the trace asks vendors for trades but records no shop opening, so its build predates both monster life rules" + cursor);
        }

        return startUtc < LifeRulesFromUtc
            ? new RulesChoice(ClientRules.None, true, $"from its start time: unstamped builds before {LifeRulesFromUtc:yyyy-MM-dd HH:mm} UTC ran neither monster life rule" + cursor)
            : new RulesChoice(ClientRules.ForVersion(1), true, $"from its start time: unstamped builds from {LifeRulesFromUtc:yyyy-MM-dd HH:mm} UTC ran both monster life rules" + cursor);
    }
}
