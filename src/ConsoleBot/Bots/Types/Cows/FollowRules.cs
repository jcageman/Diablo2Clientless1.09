using D2NG.Core.D2GS;
using System;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>When a follower walks after the lead, and when it asks the server where it really is.</summary>
public static class FollowRules
{
    /// <summary>The leash: how far the lead may move from the last follow target, or the follower be from it.</summary>
    public const double FollowDistance = 10;

    /// <summary>Further than this the lead is in another area, and walking there is pointless.</summary>
    public const double MaxFollowDistance = 1000;

    /// <summary>The server's last word on the position may be this far from where the client believes it is.</summary>
    public const double ServerAgreementDistance = 5;

    /// <summary>A disagreement shorter than this is the server catching up with a walk, not a refused one.</summary>
    public static readonly TimeSpan DisagreementGrace = TimeSpan.FromSeconds(1);

    /// <summary>A position request holds the loop for a moment, so they are spaced out.</summary>
    public static readonly TimeSpan UpdateRequestGap = TimeSpan.FromSeconds(3);

    /// <summary>How long a follower waits before trying again after finding no path to the lead.</summary>
    public static readonly TimeSpan NoPathBackoff = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Walk when the lead has moved off the last follow target, or when the follower never got to that
    /// target. Skipping on the first alone never noticed a cancelled or stuck walk: the follower never
    /// retried, and the party held for a straggler for twenty seconds, five times in thirteen games.
    /// Following the lead itself instead of the target would keep pulling the amazon onto the barbarian.
    /// </summary>
    public static bool ShouldWalkToLead(Point me, Point lead, Point lastTarget, bool walkInProgress)
    {
        if (walkInProgress || me == null || lead == null || me.Distance(lead) >= MaxFollowDistance)
        {
            return false;
        }

        return lastTarget == null
            ? me.Distance(lead) > FollowDistance
            : lead.Distance(lastTarget) > FollowDistance || me.Distance(lastTarget) > FollowDistance;
    }

    /// <summary>A follower holds to fight for at most this long when the fight costs it no life.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether a follower that has monsters on it stops to fight rather than walk after the lead. Never
    /// while the party retreats or it is too hurt or dry to stand: holding there left a walker fighting
    /// alone in the pack fifty units from a retreating lead. Never as a straggler, which walks on and
    /// swings only when its way is blocked. And not for longer than <see cref="MaxHold"/> when the hold
    /// costs it nothing, which is what holding for a monster nobody can reach looks like.
    /// </summary>
    public static bool ShouldHold(bool threatened, bool nearLead, bool retreating, bool hurt, bool outOfHealing, TimeSpan heldFor, bool lostLifeWhileHeld)
        => threatened && nearLead && !retreating && !hurt && !outOfHealing
            && (heldFor < MaxHold || lostLifeWhileHeld);

    /// <summary>Whether a follow walk under way is headed somewhere the lead has since left.</summary>
    public static bool ShouldRetarget(Point walkTarget, Point leadNow)
        => walkTarget != null && leadNow != null && walkTarget.Distance(leadNow) > FollowDistance;

    /// <summary>Whether the server and the client disagree on where the client is, or the server has not said.</summary>
    public static bool Disagrees(double? serverConfirmedDistance)
        => serverConfirmedDistance is not double confirmed || confirmed > ServerAgreementDistance;

    /// <summary>Whether the client's position has been only its own guess for long enough to ask the server.</summary>
    public static bool ShouldRequestUpdate(double? serverConfirmedDistance, TimeSpan disagreeingFor, TimeSpan sinceLastRequest)
        => Disagrees(serverConfirmedDistance)
            && disagreeingFor >= DisagreementGrace
            && sinceLastRequest >= UpdateRequestGap;
}

/// <summary>
/// A walker's hold on one threat. The <see cref="FollowRules.MaxHold"/> cap holds for the whole threat:
/// cleared when the walk began, the hold restarted 200ms later and cancelled the walk, and a walker beside
/// a harmless monster moved about one tick in sixteen. Only the threat going away, or a hit, starts it over.
/// </summary>
public sealed class HoldTimer
{
    private DateTime? _since;
    private int _lifeAtStart;
    private bool _spent;

    /// <summary>The hold ran out its <see cref="FollowRules.MaxHold"/> without costing life: what is on him is not worth staying for.</summary>
    public bool Spent => _spent;

    public void Reset()
    {
        _since = null;
        _spent = false;
    }

    public bool Update(DateTime now, bool threatened, bool nearLead, bool retreating, bool hurt, bool outOfHealing, int life)
    {
        if (!threatened)
        {
            Reset();
            return false;
        }

        if (_since == null || life < _lifeAtStart)
        {
            _since = now;
            _lifeAtStart = life;
            _spent = false;
        }

        // Measured from the highest life since, so a hit after a potion still counts as one.
        _lifeAtStart = Math.Max(_lifeAtStart, life);
        _spent |= now - _since.Value >= FollowRules.MaxHold;
        return !_spent && FollowRules.ShouldHold(threatened, nearLead, retreating, hurt, outOfHealing, TimeSpan.Zero, lostLifeWhileHeld: false);
    }
}
