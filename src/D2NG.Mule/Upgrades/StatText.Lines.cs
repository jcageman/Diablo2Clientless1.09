using D2NG.Core.D2GS.Players;
using System;
using System.Globalization;

namespace D2NG.Mule.Upgrades;

/// <summary>The full stat lines, which need the skill names from D2NG.Core; the UI links only the labels in StatText.cs.</summary>
public static partial class StatText
{
    /// <summary>The tooltip line: <c>FasterCastRate</c> 20 is "+20% Faster Cast Rate".</summary>
    public static string Line(string stat, double value)
    {
        var number = value.ToString("0.#", CultureInfo.InvariantCulture);
        if (Known.TryGetValue(stat, out var known))
        {
            return string.Format(CultureInfo.InvariantCulture, known.Line, number);
        }

        foreach (var characterClass in Classes)
        {
            if (stat == characterClass + "Skills")
            {
                return $"+{number} to {characterClass} Skill Levels";
            }

            if (stat.StartsWith(characterClass, StringComparison.Ordinal) && stat.EndsWith("Skills", StringComparison.Ordinal))
            {
                return $"+{number} to {Humanize(stat[characterClass.Length..^"Skills".Length])} Skills ({characterClass} Only)";
            }
        }

        if (Enum.TryParse<Skill>(stat, out _))
        {
            return $"+{number} to {Words().Replace(stat, " $1").Trim()}";
        }

        return $"{(value > 0 ? "+" : "")}{number} {Humanize(stat)}";
    }
}
