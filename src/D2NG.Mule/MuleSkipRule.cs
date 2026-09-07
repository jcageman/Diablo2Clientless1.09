using D2NG.Mule.Models;
using D2NG.Mule.Packing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule;

/// <summary>
/// Decides whether a mule character can be left alone because it was recently seen with no room
/// left for what we carry. Items only leave a mule by hand, so a full character stays full until someone empties it.
/// </summary>
public static class MuleSkipRule
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    public static bool ShouldSkip(MuleCharacterDb lastSeen, DateTimeOffset now)
    {
        return ShouldSkip(lastSeen, now, null);
    }

    /// <summary>
    /// <paramref name="carried"/> is what we would offer this character; null means "unknown, assume anything".
    /// </summary>
    public static bool ShouldSkip(MuleCharacterDb lastSeen, DateTimeOffset now, IEnumerable<Shape> carried)
    {
        if (lastSeen == null || now - lastSeen.SeenAt >= FreshFor)
        {
            return false;
        }

        if (lastSeen.FreeCells == 0)
        {
            return true;
        }

        if (lastSeen.FitProfile == null || carried == null)
        {
            return false;
        }

        return !carried.Any(shape => FitProfile.Fits(lastSeen.FitProfile, shape));
    }
}
