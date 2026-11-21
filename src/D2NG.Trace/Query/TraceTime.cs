using System;
using System.Globalization;

namespace D2NG.Trace.Query;

/// <summary>Times as offsets from a game's start: mm:ss.fff, minutes running past 59.</summary>
public static class TraceTime
{
    public static string Format(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "";
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{(int)abs.TotalMinutes:00}:{abs.Seconds:00}.{abs.Milliseconds:000}");
    }

    /// <summary>
    /// Accepts mm:ss[.fff], h:mm:ss[.fff], a number of seconds (90, 90.5, 90s), or a number of minutes
    /// with an m suffix (2m).
    /// </summary>
    public static bool TryParse(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var negative = text.StartsWith('-');
        if (negative)
        {
            text = text[1..];
        }

        var parts = text.Split(':');
        double seconds;
        if (parts.Length == 1)
        {
            var scale = 1.0;
            if (text.EndsWith('s'))
            {
                text = text[..^1];
            }
            else if (text.EndsWith('m'))
            {
                text = text[..^1];
                scale = 60;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds < 0)
            {
                return false;
            }

            seconds *= scale;
        }
        else if (parts.Length is 2 or 3)
        {
            if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) || secs < 0 || secs >= 60)
            {
                return false;
            }

            if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                return false;
            }

            var hours = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || minutes >= 60))
            {
                return false;
            }

            seconds = hours * 3600 + minutes * 60 + secs;
        }
        else
        {
            return false;
        }

        offset = TimeSpan.FromSeconds(negative ? -seconds : seconds);
        return true;
    }

    public static TimeSpan Parse(string text)
        => TryParse(text, out var offset) ? offset : throw new FormatException($"'{text}' is not a time: use mm:ss.fff, h:mm:ss or seconds");

    /// <summary>How far outside a game an offset may still be meant: realm traffic before it, the moments after its last record.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A time given for one game: an offset from its start, or - written hh:mm:ss, two colons - a wall-clock time in
    /// <paramref name="zone"/> (the bot's log is local time) that lies within the game, converted to the offset; a game
    /// that ran past midnight is matched on either day, and a time in the hour the clocks go back is tried in both its
    /// readings. An offset counts when it lies within the game, give or take <see cref="Slack"/>. A text that reads as
    /// both is read as the clock time, and <paramref name="note"/> gives the offset it would have been; one that reads as
    /// neither is an error whose message gives the game's span in both forms. <paramref name="note"/> says how a clock
    /// time was read, else null.
    /// </summary>
    public static TimeSpan Resolve(string text, DateTime startUtc, DateTime endUtc, TimeZoneInfo zone, out string note)
    {
        note = null;
        if (!TryParse(text, out var offset))
        {
            throw new FormatException($"'{text}' is not a time: use an offset (mm:ss.fff, h:mm:ss or seconds) or a clock time hh:mm:ss; {Span(startUtc, endUtc, zone)}");
        }

        var span = endUtc - startUtc;
        var offsetInside = offset >= -Slack && offset <= span + Slack;
        TimeSpan? clock = null;
        DateTime clockUtc = default;
        var trimmed = text.Trim();
        if (trimmed.Split(':').Length == 3 && !trimmed.StartsWith('-') && offset < TimeSpan.FromDays(1))
        {
            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
            for (var day = -1; day <= 1 && clock == null; day++)
            {
                var local = DateTime.SpecifyKind(startLocal.Date.AddDays(day) + offset, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local))
                {
                    continue;
                }

                TimeSpan[] offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
                foreach (var utcOffset in offsets)
                {
                    var utc = DateTime.SpecifyKind(local - utcOffset, DateTimeKind.Utc);
                    if (clock == null && utc >= startUtc - TimeSpan.FromSeconds(1) && utc <= endUtc + TimeSpan.FromSeconds(1))
                    {
                        clock = utc - startUtc;
                        clockUtc = utc;
                    }
                }
            }
        }

        if (clock is { } converted)
        {
            note = $"'{text}' read as a clock time in {zone.DisplayName}: {TimeZoneInfo.ConvertTimeFromUtc(clockUtc, zone):yyyy-MM-dd HH:mm:ss.fff} local, {clockUtc:HH:mm:ss.fff} UTC = {Format(converted)} into the game"
                + (offsetInside && converted != offset ? $"; as an offset it would be {Format(offset)}, which mm:ss or seconds says unambiguously" : "");
            return converted;
        }

        if (offsetInside)
        {
            return offset;
        }

        throw new FormatException($"'{text}' is neither an offset inside the game nor a clock time within it; {Span(startUtc, endUtc, zone)}");
    }

    /// <summary>The game's span as offsets and as clock times, local and UTC.</summary>
    public static string Span(DateTime startUtc, DateTime endUtc, TimeZoneInfo zone)
    {
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, zone);
        return $"the game runs from offset {Format(TimeSpan.Zero)} to {Format(endUtc - startUtc)}, clock {startLocal:yyyy-MM-dd HH:mm:ss} to {endLocal:HH:mm:ss} local ({zone.DisplayName}), {startUtc:HH:mm:ss} to {endUtc:HH:mm:ss} UTC";
    }
}
