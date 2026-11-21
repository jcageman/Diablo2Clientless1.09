using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// A client standing by a living boss without attacking it: for <see cref="Quiet"/> or longer at least one boss - an act boss
/// or a super unique - stood within <see cref="Reach"/> of it, alive, while the client was in the game, out of town and alive,
/// and sent no attack at any of them. An attack is an attack packet of an attack skill (not a buff, Teleport or a corpse skill)
/// at the boss, at a point within <see cref="PointReach"/> of it or meant for it (the nearest monster in the cast's direction,
/// as <see cref="CombatObserver.MeantFor"/> judges), or of a skill striking around the caster (<see cref="AroundCaster"/>) with
/// the boss in that radius. One incident per stretch, from the moment a boss came within reach or was last attacked.
/// <para>
/// The bots fight a boss last, so a client busy with the boss's pack is left out: a stretch with an attack at other targets
/// every <see cref="Busy"/> or more often, on average. Unique monsters other than super uniques are left out for the same
/// reason. On the 20 Nov 2026 leveling games other uniques gave one game eight stretches besides Diablo's, all of a pack fought
/// minions first; without the busy rule and the hammer's spiral the day gave 346 stretches, 248 at the Chaos seal bosses while
/// their minions were shot; with them 164, 86 at Diablo (29 games) and 117 with no attack at anything.
/// </para>
/// Evidence: on 20 Nov 2026 the leveling party's sorceress and amazon stood 11-27 from Diablo for 50 s without a cast - the
/// bot's line-of-sight test refused him - while the paladin hammered beside him.
/// </summary>
public sealed class BossNotAttackedCheck : IncidentCheck, IFamily
{
    public const string Kind = "boss not attacked";

    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(8);

    public const double Reach = 30;

    public const double PointReach = CombatObserver.HitReach;

    public static readonly TimeSpan Busy = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Skills that strike around the caster wherever they are aimed, and how far: the area spells, and Blessed Hammer, whose
    /// spiral ends ~14 out (D2MOO's PATH_ComputePathBlessedHammer).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> AroundCaster = new Dictionary<string, double>(AreaSpells.Radius, StringComparer.OrdinalIgnoreCase)
    {
        [nameof(Skill.BlessedHammer)] = 14,
    };

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static readonly HashSet<NPCCode> ActBosses = [NPCCode.Andarial, NPCCode.Duriel, NPCCode.Mephisto, NPCCode.Diablo];

    private Stretch _stretch;
    private TimeSpan _lastTick = TimeSpan.FromDays(-1000);
    private List<WorldObject> _inReach = [];

    public string Family => "bot";

    private sealed class Stretch
    {
        public TimeSpan Start;
        public TimeSpan Last;
        public readonly Dictionary<uint, Boss> Bosses = [];
        public readonly Dictionary<string, int> Others = [];
    }

    private sealed class Boss
    {
        public string Name;
        public double Nearest = double.MaxValue;
        public double Farthest;
        public double FirstLife;
        public double LastLife;
    }

    public static bool IsActBossOrSuperUnique(WorldObject unit)
        => ActBosses.Contains(unit.NPCCode) || unit.MonsterEnchantments?.Contains(MonsterEnchantment.IsSuperUnique) == true;

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsSent && p.Length > 0 && p[0] is >= 0x05 and <= 0x11 and not 0x0B && context.Game.Me is { } me)
        {
            Update(context);
            if (_stretch != null && Attack(me, p) is { } skill)
            {
                if (AtABoss(context, me, p, skill) is { } boss)
                {
                    Close(context, context.Now, $"it attacked {boss}");
                    Update(context);
                }
                else
                {
                    var target = Target(context, me, p);
                    _stretch.Others[target] = _stretch.Others.GetValueOrDefault(target) + 1;
                }
            }

            return;
        }

        if (context.Now - _lastTick >= Tick)
        {
            Update(context);
        }
    }

    public override void Finished(ReplayContext context)
    {
        Close(context, _stretch?.Last ?? context.Now, "its trace ended");
        _lastTick = TimeSpan.FromDays(-1000);
        _inReach = [];
    }

    private void Update(ReplayContext context)
    {
        _lastTick = context.Now;
        var why = Unable(context);
        _inReach = why == null ? InReach(context) : [];
        if (_inReach.Count == 0)
        {
            var died = _stretch != null && _stretch.Bosses.Keys.All(id => context.Npc(id) is not { } unit || unit.State is EntityState.Dead or EntityState.Dieing || unit.LifePercentage <= 0);
            Close(context, _stretch?.Last ?? context.Now, why ?? (died ? "the boss died" : $"no living boss was within {Reach:0} any more"));
            return;
        }

        _stretch ??= new Stretch { Start = context.Now };
        _stretch.Last = context.Now;
        foreach (var unit in _inReach)
        {
            if (!_stretch.Bosses.TryGetValue(unit.Id, out var boss))
            {
                var superUnique = unit.MonsterEnchantments?.Contains(MonsterEnchantment.IsSuperUnique) == true && !ActBosses.Contains(unit.NPCCode);
                boss = new Boss { Name = context.UnitName(unit.Id) + (superUnique ? " (super unique)" : ""), FirstLife = unit.LifePercentage };
                _stretch.Bosses[unit.Id] = boss;
            }

            var distance = context.DistanceTo(unit.Location);
            boss.Nearest = Math.Min(boss.Nearest, distance);
            boss.Farthest = Math.Max(boss.Farthest, distance);
            boss.LastLife = unit.LifePercentage;
        }
    }

    /// <summary>Why the client cannot be expected to attack anything now; null when it can.</summary>
    private static string Unable(ReplayContext context)
    {
        if (!context.InGame || context.Game.Me == null || context.Position == null)
        {
            return "it left the game";
        }

        if (context.Life <= 0)
        {
            return "it died";
        }

        return (context.Game.KnownArea.Area ?? context.Game.LastResolvedArea) is not { } area ? "its area was unknown" : ActivityObserver.IsTown(area) ? "it went to town" : null;
    }

    private static List<WorldObject> InReach(ReplayContext context)
    {
        try
        {
            return [.. context.Game.WorldObjects.Values.Where(u => u.Type == EntityType.NPC && u.State is not (EntityState.Dead or EntityState.Dieing) && u.LifePercentage > 0
                && u.Location != null && IsActBossOrSuperUnique(u) && context.DistanceTo(u.Location) <= Reach)];
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The attack skill of the hand the packet uses; null for anything else.</summary>
    private static Skill? Attack(Self me, byte[] p)
    {
        var hand = p[0] <= 0x0A ? Hand.Left : Hand.Right;
        return me.ActiveSkills.TryGetValue(hand, out var skill) && ActivityObserver.IsAttack(skill) && !CombatObserver.CorpseSkills.Contains(skill) ? skill : null;
    }

    private static bool AtPoint(byte[] p) => p[0] is 0x05 or 0x08 or 0x0C or 0x0F;

    private string AtABoss(ReplayContext context, Self me, byte[] p, Skill skill)
    {
        if (!AtPoint(p))
        {
            return p.Length >= 9 && PacketDecoder.U32(p, 1) == (uint)EntityType.NPC && _inReach.FirstOrDefault(u => u.Id == PacketDecoder.U32(p, 5)) is { } named
                ? context.UnitName(named.Id)
                : null;
        }

        if (p.Length < 5 || me.Location is not { } from)
        {
            return null;
        }

        var aim = new Point(PacketDecoder.U16(p, 1), PacketDecoder.U16(p, 3));
        var around = AroundCaster.TryGetValue(skill.ToString(), out var radius) ? radius : -1;
        var struck = _inReach.FirstOrDefault(u => u.Location.Distance(aim) <= PointReach || from.Distance(u.Location) <= around);
        if (struck == null)
        {
            var meant = CombatObserver.MeantFor(context, from, aim).Id;
            struck = _inReach.FirstOrDefault(u => u.Id == meant);
        }

        return struck == null ? null : context.UnitName(struck.Id);
    }

    private static string Target(ReplayContext context, Self me, byte[] p)
    {
        if (!AtPoint(p))
        {
            return p.Length >= 9 && PacketDecoder.U32(p, 1) == (uint)EntityType.NPC ? context.UnitName(PacketDecoder.U32(p, 5)) : "another unit";
        }

        return p.Length >= 5 && me.Location is { } from && CombatObserver.MeantFor(context, from, new Point(PacketDecoder.U16(p, 1), PacketDecoder.U16(p, 3))).Name is { } name
            ? name
            : "a point";
    }

    private void Close(ReplayContext context, TimeSpan end, string how)
    {
        var stretch = _stretch;
        _stretch = null;
        var others = stretch?.Others.Values.Sum() ?? 0;
        if (stretch == null || end - stretch.Start < Quiet || others >= (end - stretch.Start) / Busy)
        {
            return;
        }

        var bosses = string.Join(", ", stretch.Bosses.Values.Select(b => $"{b.Name} {(Math.Round(b.Nearest) == Math.Round(b.Farthest) ? $"{b.Nearest:0}" : $"{b.Nearest:0}-{b.Farthest:0}")} away, life {b.FirstLife:0}% -> {b.LastLife:0}%"));
        var elsewhere = others == 0
            ? "no attack at anything else either"
            : $"{others} attack{(others == 1 ? "" : "s")} at other targets ({string.Join(", ", stretch.Others.OrderByDescending(o => o.Value).Take(3).Select(o => $"{o.Key} x{o.Value}"))}{(stretch.Others.Count > 3 ? $" and {stretch.Others.Count - 3} more" : "")})";
        Report(context, stretch.Start, Kind,
            $"no attack at {(stretch.Bosses.Count == 1 ? "the boss" : "any of the bosses")} for {(end - stretch.Start).TotalSeconds:0.0}s, out of town and alive: {bosses}; {elsewhere}; until {how}",
            end);
    }
}
