using ConsoleBot.Bots.Types.Travincal;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.MonsterData;
using D2NG.Core.Tracing;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace ConsoleBot.Helpers;

/// <summary>
/// The settings that change how a bot behaves, written into every game's trace so recorded games can be split and
/// compared by them (Trace --split, --where): the bot type, the monster motion model and the one in effect, and the
/// switches of the bot's own section listed in <see cref="Switches"/>, each with the value in effect - the configuration
/// class's default when the config leaves it out. Only listed keys are written: a section also holds accounts and passwords.
/// </summary>
public static class TracedSettings
{
    /// <summary>
    /// Per bot type, its configuration class and the keys of bot:&lt;type&gt; that change its behaviour, each with the value
    /// in effect when neither the config nor the class has it - a switch another branch adds is recorded with its default
    /// until then. A switch added to a bot goes here too, and one removed leaves: Travincal's wallAwareWhirl did when the
    /// wall-aware fight became the only one, so games from then on record no such key and every one of them is wall-aware.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (Type Configuration, (string Key, string Default)[] Keys)> Switches =
        new Dictionary<string, (Type, (string, string)[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["travincal"] = (typeof(TravincalConfiguration), [("identifyWithTome", "false"), ("movementSpeedFactor", "1")]),
            ["cows"] = (typeof(ConsoleBot.Bots.Types.Cows.CowConfiguration), [("huntBudgetSeconds", "")]),
        };

    public static List<(string Key, string Value)> Of(IConfigurationSection bot)
    {
        var botType = bot["botType"];
        var configured = MonsterMotionSetup.Mode;
        var settings = new List<(string Key, string Value)>
        {
            ("botType", botType),
            ("monsterMotion", configured.ToString()),
            ("monsterMotionEffective", MonsterMotionSettings.Effective(configured, MonsterSpeedTable.Shared).ToString()),
        };
        if (botType == null || !Switches.TryGetValue(botType, out var switches))
        {
            return settings;
        }

        var section = bot.GetSection(botType);
        var bound = section.Get(switches.Configuration) ?? Activator.CreateInstance(switches.Configuration);
        foreach (var (key, fallback) in switches.Keys)
        {
            var property = switches.Configuration.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            settings.Add((key, property != null ? Format(property.GetValue(bound)) : section[key] is { } raw ? Format(raw) : fallback));
        }

        return settings;
    }

    private static string Format(object value) => value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        string text when bool.TryParse(text, out var flag) => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Call after <see cref="MonsterMotionSetup.Apply"/> and the trace recorder started, before the first client joins a game.</summary>
    public static void Apply(IConfigurationSection bot)
    {
        if (TraceRecorder.Current is { } recorder)
        {
            recorder.Settings = Of(bot);
        }
    }
}
