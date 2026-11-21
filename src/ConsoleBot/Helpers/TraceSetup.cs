using D2NG.Core.Tracing;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Globalization;
using System.IO;

namespace ConsoleBot.Helpers;

public static class TraceSetup
{
    private const double DefaultMaxGigabytes = 10;

    /// <summary>
    /// Unlike the log, the trace folder survives a restart; it is only trimmed to its cap, oldest games
    /// first. A setting that does not parse falls back to its default rather than stopping the bot.
    /// </summary>
    /// <summary>On unless bot:trace is false; a value that does not parse leaves it on, as <see cref="Start"/> reports.</summary>
    public static bool IsEnabled(IConfigurationSection bot)
        => bot["trace"] is not { } trace || !bool.TryParse(trace, out var enabled) || enabled;

    public static void Start(IConfigurationSection bot, string logFile)
    {
        var enabled = true;
        if (bot["trace"] is { } trace && !bool.TryParse(trace, out enabled))
        {
            Log.Warning("bot:trace is '{Value}', not true or false; tracing stays on", trace);
            enabled = true;
        }

        if (!enabled)
        {
            Log.Information("Packet tracing is off");
            return;
        }

        var defaultFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logFile)), "traces");
        var folder = defaultFolder;
        if (bot["traceFolder"] is { } configuredFolder)
        {
            try
            {
                folder = Path.GetFullPath(configuredFolder);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log.Warning("bot:traceFolder '{Value}' is not a usable path ({Error}); tracing to {Default}", configuredFolder, e.Message, defaultFolder);
            }
        }

        var maxGigabytes = DefaultMaxGigabytes;
        if (bot["traceMaxGigabytes"] is { } cap
            && (!double.TryParse(cap, NumberStyles.Float, CultureInfo.InvariantCulture, out maxGigabytes) || maxGigabytes <= 0))
        {
            Log.Warning("bot:traceMaxGigabytes is '{Value}', not a positive number; using {Default}", cap, DefaultMaxGigabytes);
            maxGigabytes = DefaultMaxGigabytes;
        }

        var deleted = 0;
        try
        {
            deleted = TraceRecorder.EnforceCap(folder, (long)(maxGigabytes * 1024 * 1024 * 1024));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Trimming {TraceFolder} to its cap failed: {Error}", folder, e.Message);
        }

        TraceRecorder.Start(new TraceRecorderOptions { Root = folder });
        Log.Information("Tracing packets to {TraceFolder} (cap {MaxGigabytes} GB{Deleted})",
            folder, maxGigabytes, deleted > 0 ? $", deleted the {deleted} oldest games" : "");
    }
}
