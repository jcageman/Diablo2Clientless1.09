using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Generic;

namespace ConsoleBot.Bots.Types.Cows;

public enum AssignOutcome
{
    Ignore,
    Track,
    Move,
    RemoveDead,
}

public enum PruneOutcome
{
    Keep,
    OutOfSight,
    Dead,
}

/// <summary>How the cow manager reads monster assigns and what a client still sees of a tracked monster.</summary>
public static class MonsterTracking
{
    /// <summary>
    /// A corpse coming into view is assigned like a monster, with no life. Taken as a live monster it
    /// opened a cluster nothing would ever clear and drew the sorceresses to bodies.
    /// </summary>
    public static AssignOutcome OnAssign(bool confirmedDead, bool onOurSide, bool tracked, double lifePercentage)
    {
        if (confirmedDead || onOurSide)
        {
            return AssignOutcome.Ignore;
        }

        if (lifePercentage <= 0)
        {
            return tracked ? AssignOutcome.RemoveDead : AssignOutcome.Ignore;
        }

        return tracked ? AssignOutcome.Move : AssignOutcome.Track;
    }

    public static PruneOutcome OnPrune(bool inWorld, EntityState state)
        => !inWorld ? PruneOutcome.OutOfSight
            : IsDead(state) ? PruneOutcome.Dead
            : PruneOutcome.Keep;

    public static bool IsDead(EntityState state) => state is EntityState.Dead or EntityState.Dieing;

    /// <summary>The sighting whose position a packet stated most recently; null without any.</summary>
    public static Point Freshest(IEnumerable<(Point Location, TimeSpan Age)> sightings)
    {
        Point freshest = null;
        var youngest = TimeSpan.MaxValue;
        foreach (var (location, age) in sightings)
        {
            if (location != null && (freshest == null || age < youngest))
            {
                freshest = location;
                youngest = age;
            }
        }

        return freshest;
    }

    /// <summary>Where a cluster's live members stand on average, or <paramref name="fallback"/> when none is tracked.</summary>
    public static Point Centroid(IReadOnlyCollection<Point> members, Point fallback)
    {
        if (members == null || members.Count == 0)
        {
            return fallback;
        }

        double x = 0;
        double y = 0;
        foreach (var member in members)
        {
            x += member.X;
            y += member.Y;
        }

        return new Point((ushort)Math.Round(x / members.Count), (ushort)Math.Round(y / members.Count));
    }
}
