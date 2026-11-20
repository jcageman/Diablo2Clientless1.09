using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

public enum RotationChoice
{
    /// <summary>Nothing in reach is worth a cast from here.</summary>
    Nothing,

    /// <summary>Static has stopped paying and nova would, but no cow is inside the nova radius: get closer.</summary>
    Approach,

    Static,

    /// <summary>One Frost Nova, after which the rotation goes on with static or nova.</summary>
    FrostNova,

    /// <summary>The wave is on her: teleport clear of it, then the rotation goes on from there.</summary>
    Escape,

    Nova,
}

/// <summary>What one sorceress sees of the pack around her, at the cows' simulated positions.</summary>
public sealed record RotationInput
{
    /// <summary>Life percentages of the cows she can hurt inside static range.</summary>
    public IReadOnlyList<double> StaticRangeLives { get; init; } = [];

    /// <summary>Life percentages of the cows she can hurt inside the nova radius.</summary>
    public IReadOnlyList<double> NovaRadiusLives { get; init; } = [];

    /// <summary>Life percentage static takes nothing below: 0 in classic, 60 in expansion.</summary>
    public double StaticFloor { get; init; }

    /// <summary>Percent of a cow's maximum life one nova takes off; see <see cref="SorceressRotation.NovaDamagePercent"/>.</summary>
    public double NovaDamagePercent { get; init; }

    public int Mana { get; init; }

    public int MaxMana { get; init; }

    public int NovaManaCost { get; init; }

    public int FrostNovaManaCost { get; init; }

    public bool Classic { get; init; }

    public bool HasFrostNova { get; init; }

    public bool HasStatic { get; init; } = true;

    /// <summary>Cows inside their attack reach plus what they walk during one cast.</summary>
    public int CowsWithinReach { get; init; }

    public int CowsInFrostNovaRadius { get; init; }

    /// <summary>Of <see cref="CowsInFrostNovaRadius"/>, the ones carrying the cold effect.</summary>
    public int ChilledInFrostNovaRadius { get; init; }

    /// <summary>Live cows of the pack around her that she can hurt.</summary>
    public int PackAlive { get; init; }

    public TimeSpan SinceFrostNova { get; init; } = TimeSpan.MaxValue;

    /// <summary>Distance to the nearest cow she can hurt.</summary>
    public double Nearest { get; init; } = double.MaxValue;

    /// <summary>Life as a fraction of maximum life.</summary>
    public double LifeFraction { get; init; } = 1;

    /// <summary>Of <see cref="CowsWithinReach"/>, the ones carrying the cold effect.</summary>
    public int ChilledWithinReach { get; init; }

    /// <summary>Life lost over the last second, as a fraction of maximum life.</summary>
    public double LifeLostLastSecond { get; init; }

    public TimeSpan SinceEscape { get; init; } = TimeSpan.MaxValue;

    /// <summary>How long one of her casts takes: the cows walk this long between two of them.</summary>
    public TimeSpan CastTime { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The choice the last decision made, which a change has to beat by a margin.</summary>
    public RotationChoice Current { get; init; }
}

/// <summary>
/// Static Field, Frost Nova and Nova for the cow level's nova sorceresses. Every number the rotation
/// turns on is here, so tuning it from traces changes values, not structure.
/// </summary>
public static class SorceressRotation
{
    /// <summary>Static Field takes this percent of a monster's current life (skills.bin Param4).</summary>
    public const double StaticPercent = 25;

    /// <summary>Static Field's mana cost, the same at every level (skills.txt mana 9, lvlmana 0).</summary>
    public const int StaticManaCost = 9;

    /// <summary>
    /// Nova's reach in units. Its missile flies at velocity 24 for 13 frames (missiles.txt), Frost Nova's
    /// for 14 at the same speed, so Frost Nova reaches about a fourteenth further. Ten is what the cow
    /// bot has always used for nova and matches the fights it has recorded.
    /// </summary>
    public const double NovaRadius = 10;

    public const double FrostNovaRadius = NovaRadius * 14 / 13;

    /// <summary>Hell Bovine MeleeRng in monstats, in units.</summary>
    public const double CowMeleeRange = 2;

    /// <summary>Hell Bovine walk speed: Velocity 5 x 0.75 x 25/16 units per second (d2-109 skill).</summary>
    public const double CowWalkSpeed = 5 * 0.75 * 25 / 16.0;

    /// <summary>Average Hell hit points of a Hell Bovine in a one player game, (1382 + 2961) / 2 from monstats.</summary>
    public const double CowHitPoints = (1382 + 2961) / 2.0;

    /// <summary>Monster life rises by half for every player in the game past the first.</summary>
    public const double HitPointsPerExtraPlayer = 0.5;

    /// <summary>The pack share that has to be inside the Frost Nova radius before it is cast, so it slows the pack, not a runner.</summary>
    public const double FrostNovaPackShare = 0.5;

    /// <summary>
    /// Never fewer than this many cows for one Frost Nova. There is no upper cap: capping it at six cast
    /// half as many Frost Novas again (9-11% of her casting against 6-8%) for nothing a pack wave on her
    /// needed, which is the escape's job (ten cows at 44% life, the case the cap was for, escapes).
    /// </summary>
    public const int MinFrostNovaCows = 3;

    /// <summary>With this many cows in reach she does not leave them to fetch battle orders.</summary>
    public const int BattleOrdersTripMaxCowsOnHer = 3;

    /// <summary>
    /// The escape is an emergency brake, not a way of fighting; this count is only a backstop for a whole
    /// pack on her, and only once she is no longer healthy. With static kept on, ten cows reach her all the
    /// time: leaving at ten escaped thirty times a game at 85-100% life, and at twenty whatever her life
    /// half of 4.5 escapes a game still came from it. The two sorceress chickens of the first tuned run, ten
    /// cows at 44% life and fifteen at 61%, are both caught by <see cref="EscapeLowLife"/>.
    /// </summary>
    public const int EscapeCows = 20;

    public const double EscapeCrowdLife = 0.85;

    /// <summary>
    /// Fewer cows than <see cref="EscapeCows"/> make her leave only below <see cref="EscapeLowLife"/>.
    /// Leaving at six mostly unchilled cows as well escaped eighteen times a game and cost more
    /// experience than the chickens it was meant to stop, which the rejoin now brings back in seconds.
    /// </summary>
    public const int EscapeLowLifeCows = 6;

    public const double EscapeLowLife = 0.65;

    /// <summary>Fewer cows in reach make her leave too when they took this share of her life in the last second.</summary>
    public const int EscapePressureCows = 3;

    public const double EscapeLifeDrop = 0.2;

    /// <summary>One escape at a time: the cows need a moment to follow before the next is judged.</summary>
    public static readonly TimeSpan EscapeGap = TimeSpan.FromSeconds(1.5);

    /// <summary>The escape that makes this many within <see cref="RepeatEscapeWindow"/> is one long hop clear of everything: with two, the second.</summary>
    public const int RepeatEscapes = 2;

    public static readonly TimeSpan RepeatEscapeWindow = TimeSpan.FromSeconds(5);

    /// <summary>The cold effect arrives a packet after the cast; no second Frost Nova inside this.</summary>
    public static readonly TimeSpan FrostNovaRecastGap = TimeSpan.FromSeconds(1.5);

    /// <summary>A change between static and nova has to pay this much more than staying, so the rotation does not flap.</summary>
    public const double SwitchMargin = 0.15;

    /// <summary>Below this share of maximum mana, static and nova are compared per point of mana, not per cast.</summary>
    public const double LowManaFraction = 0.3;

    /// <summary>The spell takes whichever of these its cast leaves standing; both are one cast long.</summary>
    public static double StaticDamage(double life, double floor)
        => life <= floor ? 0 : Math.Min(life * StaticPercent / 100, life - floor);

    public static double NovaDamage(double life, double novaDamagePercent) => Math.Min(novaDamagePercent, Math.Max(life, 0));

    /// <summary>Where a cow has to be for Frost Nova to land before it swings: its reach plus a cast's walk.</summary>
    public static double AttackReach(TimeSpan castTime) => CowMeleeRange + CowWalkSpeed * castTime.TotalSeconds;

    /// <summary>The leading edge: a cow this close reaches her during one more cast.</summary>
    public static double FrostNovaTrigger(TimeSpan castTime) => AttackReach(castTime) + (CowWalkSpeed * castTime.TotalSeconds);

    /// <summary>How many cows have to be inside the Frost Nova radius before one is cast.</summary>
    public static int FrostNovaQuorum(int packAlive)
        => Math.Max(MinFrostNovaCows, (int)Math.Ceiling(packAlive * FrostNovaPackShare));

    /// <summary>Mana costs from skills.txt: Nova 15 and Frost Nova 9 at level 1, one more each level.</summary>
    public static int NovaManaCost(int level) => 15 + Math.Max(level - 1, 0);

    public static int FrostNovaManaCost(int level) => 9 + Math.Max(level - 1, 0);

    /// <summary>
    /// Percent of a Hell Bovine's life one nova takes off. Nova's lightning (skills.bin: 1-20 at level 1,
    /// 7 more a level to 8, 8 to 16, 9 beyond) raised by Lightning Mastery (skills.txt: 50%, 12% a level),
    /// against the cow's life raised for the players in the game. Cows have no lightning resistance in Hell.
    /// </summary>
    public static double NovaDamagePercent(int novaLevel, int lightningMasteryLevel, int playersInGame)
    {
        if (novaLevel <= 0)
        {
            return 0;
        }

        var added = 0;
        for (var level = 2; level <= novaLevel; level++)
        {
            added += level <= 8 ? 7 : level <= 16 ? 8 : 9;
        }

        var average = (1 + added + 20 + added) / 2.0;
        var mastery = lightningMasteryLevel > 0 ? 50 + (12 * (lightningMasteryLevel - 1)) : 0;
        var hitPoints = CowHitPoints * (1 + (HitPointsPerExtraPlayer * Math.Max(playersInGame - 1, 0)));
        return 100 * average * (1 + (mastery / 100.0)) / hitPoints;
    }

    /// <summary>
    /// Frost Nova is worth its cast only for the slow it puts on the cows, and only in classic: in
    /// expansion the party's mercenary slows them with Holy Freeze. Cast early it slows the few that
    /// are close and leaves the rest to walk in at full speed, so it waits for the pack's leading edge
    /// to be one cast from her with enough of it inside the radius: one cow well ahead of the others
    /// does not get it. With cows already in reach it goes out whatever the count. Once they are
    /// chilled it is not cast again until most of them have shaken it off.
    /// </summary>
    public static bool FrostNovaDue(RotationInput input)
    {
        if (!FrostNovaReady(input) || !MostlyUnchilled(input))
        {
            return false;
        }

        // Recasting it for every few cows that walk in, and turning static off whenever three stood in
        // reach, took static from 62% of her casting to 45%: static takes a quarter of every cow's life
        // in its range, and kills per minute fell by 7%. The escape, not the rotation, is what covers a
        // wave on her.
        return (input.Nearest <= FrostNovaTrigger(input.CastTime) || input.CowsWithinReach > 0)
            && input.CowsInFrostNovaRadius >= FrostNovaQuorum(input.PackAlive);
    }

    private static bool FrostNovaReady(RotationInput input)
        => input.Classic && input.HasFrostNova
            && input.Mana >= input.FrostNovaManaCost + input.NovaManaCost
            && input.SinceFrostNova >= FrostNovaRecastGap;

    private static bool MostlyUnchilled(RotationInput input)
        => (input.CowsInFrostNovaRadius - input.ChilledInFrostNovaRadius) * 2 > input.CowsInFrostNovaRadius;

    public static bool ShouldEscape(RotationInput input)
        => input.SinceEscape >= EscapeGap
            && ((input.CowsWithinReach >= EscapeCows && input.LifeFraction < EscapeCrowdLife)
                || (input.CowsWithinReach >= EscapeLowLifeCows && input.LifeFraction < EscapeLowLife)
                || (input.CowsWithinReach >= EscapePressureCows && input.LifeLostLastSecond > EscapeLifeDrop));

    /// <summary>
    /// How far an escape lands from the nearest cow: their reach, plus what they walk until the next
    /// escape could be judged. Landing at the base clearance put her seven units in front of the same
    /// wave, which closed it in under half a second.
    /// </summary>
    public static double EscapeClearance(TimeSpan castTime) => AttackReach(castTime) + (CowWalkSpeed * EscapeGap.TotalSeconds);

    /// <summary>
    /// Static takes a quarter of what is left, so it pays most on fresh cows and less with every cast;
    /// nova takes the same amount every cast, but only inside its smaller radius. Both cast in the same
    /// time, so per cast the larger total wins, and per point of mana once mana runs short.
    /// </summary>
    public static RotationChoice Decide(RotationInput input)
    {
        if (ShouldEscape(input))
        {
            return RotationChoice.Escape;
        }

        if (FrostNovaDue(input))
        {
            return RotationChoice.FrostNova;
        }

        var staticGain = input.HasStatic && input.Mana >= StaticManaCost
            ? input.StaticRangeLives.Sum(life => StaticDamage(life, input.StaticFloor))
            : 0;
        var novaGain = input.Mana >= input.NovaManaCost
            ? input.NovaRadiusLives.Sum(life => NovaDamage(life, input.NovaDamagePercent))
            : 0;

        if (novaGain > 0 && NovaPays(input, novaGain, staticGain))
        {
            return RotationChoice.Nova;
        }

        // Nothing inside the nova radius, while the cows static can reach are worth more to nova than
        // to another static: they are low enough that standing back only wastes casts.
        var novaWouldTake = input.Mana >= input.NovaManaCost
            ? input.StaticRangeLives.Sum(life => NovaDamage(life, input.NovaDamagePercent))
            : 0;
        if (input.NovaRadiusLives.Count == 0 && novaWouldTake > staticGain)
        {
            return RotationChoice.Approach;
        }

        return staticGain > 0 ? RotationChoice.Static : RotationChoice.Nothing;
    }

    private static bool NovaPays(RotationInput input, double novaGain, double staticGain)
    {
        if (staticGain <= 0)
        {
            return true;
        }

        var lowMana = input.MaxMana > 0 && input.Mana < LowManaFraction * input.MaxMana;
        var nova = lowMana ? novaGain / Math.Max(input.NovaManaCost, 1) : novaGain;
        var stay = lowMana ? staticGain / StaticManaCost : staticGain;
        var margin = input.Current switch
        {
            RotationChoice.Nova => 1 - SwitchMargin,
            RotationChoice.Static => 1 + SwitchMargin,
            _ => 1,
        };
        return nova > stay * margin;
    }
}
