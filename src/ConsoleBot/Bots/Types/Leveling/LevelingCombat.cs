using ConsoleBot.Attack;
using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Leveling.Plans;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

public enum Delivery
{
    Melee,
    Missile,
    AroundSelf,
    AtLocation,
    OnCorpse,
    Whirl
}

/// <param name="Mana">Roughly what a cast costs at a low level; below it the character swings instead.</param>
/// <param name="Reach">How close the target must be for the skill to land.</param>
public sealed record SkillUse(Delivery Delivery, double Mana, double Reach);

/// <summary>
/// Fights with whatever the member's build plan says for its level, from plain Attack at level 1 on.
/// The farming bots' attack logic assumes high-level characters; this one only knows the plan.
/// </summary>
public sealed class LevelingCombat(IPathingService pathingService, IMapApiService mapApiService, IAttackService attackService)
{
    private const double MeleeReach = 3;

    private static readonly Dictionary<Skill, SkillUse> Uses = new()
    {
        [Skill.Attack] = new(Delivery.Melee, 0, MeleeReach),
        [Skill.Bash] = new(Delivery.Melee, 2, MeleeReach),
        [Skill.Concentrate] = new(Delivery.Melee, 2, MeleeReach),
        [Skill.Zeal] = new(Delivery.Melee, 2, MeleeReach),
        [Skill.Smite] = new(Delivery.Melee, 2, MeleeReach),
        [Skill.Jab] = new(Delivery.Melee, 2, MeleeReach),
        [Skill.PowerStrike] = new(Delivery.Melee, 2, MeleeReach),
        [Skill.ChargedStrike] = new(Delivery.Melee, 4, MeleeReach),
        [Skill.FireBolt] = new(Delivery.Missile, 3, 13),
        [Skill.IceBolt] = new(Delivery.Missile, 3, 13),
        [Skill.ChargedBolt] = new(Delivery.Missile, 3, 8),
        [Skill.IceBlast] = new(Delivery.Missile, 6, 13),
        [Skill.GlacialSpike] = new(Delivery.Missile, 10, 13),
        [Skill.MagicArrow] = new(Delivery.Missile, 1.5, 15),
        [Skill.MultipleShot] = new(Delivery.Missile, 4, 16),
        [Skill.GuidedArrow] = new(Delivery.Missile, 8, 18),
        [Skill.LightningFury] = new(Delivery.Missile, 10, 15),
        [Skill.Teeth] = new(Delivery.Missile, 3, 11),
        [Skill.Nova] = new(Delivery.AroundSelf, 15, 6),
        [Skill.FrostNova] = new(Delivery.AroundSelf, 9, 6),
        [Skill.StaticField] = new(Delivery.AroundSelf, 9, 6),
        [Skill.BlessedHammer] = new(Delivery.AroundSelf, 5, 5),
        [Skill.Blizzard] = new(Delivery.AtLocation, 23, 15),
        [Skill.FrozenOrb] = new(Delivery.AtLocation, 25, 12),
        [Skill.CorpseExplosion] = new(Delivery.OnCorpse, 15, 15),
        [Skill.Whirlwind] = new(Delivery.Whirl, 25, 6)
    };

    /// <summary>
    /// One action against the nearest monster within <paramref name="radius"/> of <paramref name="anchor"/>.
    /// False when nothing was there to fight.
    /// </summary>
    public async Task<bool> FightTick(PartyMember member, Point anchor, double radius)
    {
        if (!member.InGame || member.Game.IsInTown())
        {
            return false;
        }

        if (member.Shouting)
        {
            await Task.Delay(100);
            return true;
        }

        try
        {
            return await FightTickCore(member, anchor, radius);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            Log.Warning("{Character} could not act: {Reason}", member.Name, exception.Message);
            await Task.Delay(300);
            return false;
        }
    }

    /// <summary>Catacomb walls are one tile thick: a monster four away can be on the far side of one.</summary>
    private const double AlwaysInSight = 1.5;

    private async Task<bool> InSight(PartyMember member, Point target)
    {
        var from = member.Game.Me.Location;
        if (target.Distance(from) <= AlwaysInSight)
        {
            return true;
        }

        if (ClosedDoorBetween(member.Game, from, target))
        {
            return false;
        }

        return await attackService.IsInLineOfSight(member.Client, target);
    }

    /// <summary>The map draws every door as open for pathing, so a door still shut in the game is checked here.</summary>
    private static bool ClosedDoorBetween(D2NG.Core.Game game, Point from, Point to)
        => game.WorldObjects.Where(entry => entry.Key.Item2 == EntityType.Object).Select(entry => entry.Value)
            .Any(door => D2NG.Navigation.Extensions.AreaMapExtensions.DoorObjectCodes.Contains((int)door.Code) && door.State == EntityState.Alive
                && DistanceToSegment(door.Location, from, to) <= 2.5);

    private static double DistanceToSegment(Point point, Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);
        double nearestX = from.X + t * dx, nearestY = from.Y + t * dy;
        return Math.Sqrt((point.X - nearestX) * (point.X - nearestX) + (point.Y - nearestY) * (point.Y - nearestY));
    }

    private async Task<bool> FightTickCore(PartyMember member, Point anchor, double radius)
    {
        var game = member.Game;
        var me = game.Me;
        // A ranged member counts monsters as far as it shoots: kept 20 from Diablo and stepped back to 27, the amazon left him
        // out at 25 and stood the fight without an arrow.
        var counted = member.Role == PartyRole.Ranged ? Math.Max(radius + 10, GuidedArrowBossReach) : radius + 10;
        var enemies = NPCHelpers.GetNearbyNPCs(member.Client, anchor, 12, (int)radius)
            .Where(npc => npc.Location.Distance(me.Location) < counted && !Ignored(member, npc) && !Untargetable.Contains(npc.NPCCode))
            .OrderBy(npc => npc.Location.Distance(me.Location))
            .ToList();
        // A shaman behind its pack is not among the twelve nearest, and while rushing it can stand outside the small
        // fight radius: revivers are looked for on their own, so the pack it keeps raising stops coming back.
        var reviverRadius = (int)Math.Max(radius, Party.EngageRadius);
        foreach (var reviver in NPCHelpers.GetNearbyNPCs(member.Client, me.Location, 40, reviverRadius)
            .Where(npc => Revivers.Contains(npc.NPCCode) && !Ignored(member, npc) && enemies.All(known => known.Id != npc.Id)))
        {
            enemies.Add(reviver);
        }

        // Behind a wall or a closed door a monster is left alone: it cannot see the party either, and the amazon
        // walking round to get a shot pulled the next room onto the party.
        var visible = new List<WorldObject>();
        var closeRange = member.Role != PartyRole.Ranged;
        foreach (var npc in enemies)
        {
            // Frogs wait in the marsh water: only a ranged member can hit them there. A melee member walked to the shore
            // and stood hitting nothing; it takes them once they jump out onto land.
            if (await InSight(member, npc.Location) && (!closeRange || npc.Location.Distance(me.Location) <= MeleeReach + 1
                || await pathingService.IsNavigatablePointInArea(game.MapId, AreaTraveler.MapDifficulty, game.Area, npc.Location)))
            {
                visible.Add(npc);
            }
        }

        enemies = visible;
        if (enemies.Count == 0)
        {
            // Between fights the paladin walks with Vigor, as the CS bot's does; the fight aura comes back with the next pack.
            if (member.SkillLevel(Skill.Vigor) > 0 && (!me.ActiveSkills.TryGetValue(Hand.Right, out var current) || current != Skill.Vigor))
            {
                game.ChangeSkill(Skill.Vigor, Hand.Right);
            }

            return false;
        }

        // Once low, a member keeps out of the fight until its potions have it well up again: the barbarian kept hitting
        // Andariel until he chickened, and stepping back a few yards only to swing again left the party hitting on at 25%.
        if (me.Life < me.MaxLife * LowLifeStepOut && _recovering.TryAdd(member.Name, true))
        {
            D2NG.Core.Tracing.TraceEvents.Wait(game, "low life", true);
            // Poison keeps eating what the potions put back: an antidote as the member starts backing off clears it.
            if (game.Inventory.Items.FirstOrDefault(item => item.Name == ItemName.AntidotePotion) is { } antidote)
            {
                game.ActivateInventoryItem(antidote);
                Log.Information("{Character} drank an antidote backing off at {Life} of {Max} life", member.Name, me.Life, me.MaxLife);
            }
        }
        else if (me.Life >= me.MaxLife * RecoveredLife && _recovering.TryRemove(member.Name, out _))
        {
            D2NG.Core.Tracing.TraceEvents.Wait(game, "low life", false);
        }

        if (_recovering.ContainsKey(member.Name))
        {
            // A boss reaches further than a pack: from Diablo the member backs off well out of his reach.
            var nearBoss = enemies.FirstOrDefault(npc => (npc.IsBoss || npc.NPCCode == FacedLast) && npc.Location.Distance(me.Location) <= BossRecoverDistance);
            if (nearBoss != null)
            {
                await StepAwayFrom(member, [nearBoss], BossRecoverDistance);
            }
            else if (enemies[0].Location.Distance(me.Location) <= RecoverDistance)
            {
                await StepAwayFrom(member, enemies.Where(npc => npc.Location.Distance(me.Location) <= RecoverDistance).ToList(), RecoverStep);
            }
            else
            {
                await Task.Delay(300);
            }

            return true;
        }

        // A fire enchanted monster explodes as it dies: two of them dying beside the amazon in Sewers 2 took 504 of her
        // 484 life. Ranged members keep clear of one about to die, melee members once their own life is down.
        if (member.Role == PartyRole.Ranged || me.Life < me.MaxLife * BlastRiskLife)
        {
            var dying = enemies.Where(npc => npc.MonsterEnchantments.Contains(MonsterEnchantment.FireEnchanted)
                && npc.LifePercentage <= FireBlastLife && npc.Location.Distance(me.Location) < FireBlastClear).ToList();
            if (dying.Count > 0)
            {
                await StepAwayFrom(member, dying, FireBlastClear);
                return true;
            }
        }

        // Cursed to take more from every hit, a ranged member keeps the pack out of reach while it lasts: the Oblivion Knights'
        // Amplify Damage doubles a Doom Knight's swing and Decrepify adds half to it, and the sorceress has the least life to
        // take it with. The barbarian does not: he whirls on and leeches through it.
        if (member.Role == PartyRole.Ranged && DamageCurses.Any(me.Effects.ContainsKey)
            && enemies.Where(npc => npc.Location.Distance(me.Location) < CursedKeepAway).ToList() is { Count: > 0 } close)
        {
            await StepAwayFrom(member, close, CursedKeepAway);
            return true;
        }

        // Every boss near, not only the one hunted: Urdar, a super unique, killed the amazon in the River of Flame from
        // seven yards on the way to Diablo. The amazon shoots from well back, the sorceress from her usual distance.
        if (member.Role == PartyRole.Ranged
            && enemies.FirstOrDefault(npc => npc.IsBoss && npc.NPCCode != FacedLast
                && npc.Location.Distance(me.Location) < (LongGuidedArrow(member, Skill.GuidedArrow, npc) ? GuidedArrowBossDistance : RangedFromBoss)) is { } closeBoss)
        {
            await StepAwayFrom(member, [closeBoss], LongGuidedArrow(member, Skill.GuidedArrow, closeBoss) ? GuidedArrowBossDistance : RangedFromBoss);
            return true;
        }

        // A ranged member keeps the boss at arm's length: the amazon and the sorceress walked right up to Andariel.
        if (FacedLast is { } boss)
        {
            if (member.Role == PartyRole.Ranged && enemies.FirstOrDefault(npc => npc.NPCCode == boss) is { } near
                && near.Location.Distance(me.Location) < (LongGuidedArrow(member, Skill.GuidedArrow, near) ? GuidedArrowBossDistance : RangedFromBoss))
            {
                await StepBack(member, near);
                return true;
            }
        }

        var stage = member.Stage;
        // Damage auras only: Resist Lightning or Cleansing in place of Fanaticism or Concentration cost the whole party its
        // damage. Resistances come from gear, which the gear score weighs up to the difficulty's cap.
        var wantedAura = stage.Aura;
        // Fanaticism takes over only with points in it: at 2 it did less for the hammers than Concentration at 20.
        if (wantedAura == Skill.Fanaticism && member.SkillLevel(Skill.Fanaticism) < FanaticismFrom
            && member.SkillLevel(Skill.Concentration) > member.SkillLevel(Skill.Fanaticism))
        {
            wantedAura = Skill.Concentration;
        }

        // Fanaticism for the barbarian's and the amazon's attacks and Concentration for his own hammers, taking turns: on 1.09
        // an aura lands at once and lingers about 8 s after the switch, so 6 s turns keep both on the party (the user's pick,
        // as the Baal bot's paladin turns Conviction and Fanaticism).
        if (wantedAura is Skill.Fanaticism or Skill.Concentration
            && member.SkillLevel(Skill.Fanaticism) >= FanaticismFrom && member.SkillLevel(Skill.Concentration) >= FanaticismFrom)
        {
            wantedAura = DateTime.UtcNow.Ticks / AuraTurn.Ticks % 2 == 0 ? Skill.Fanaticism : Skill.Concentration;
        }
        // The paladin ran dry crossing the River of Flame, drinking a mana potion every ten seconds with 61 mana: one
        // point of Meditation quadruples his recovery and the sorceress's beside him.
        if (wantedAura != null && member.SkillLevel(Skill.Meditation) > 0)
        {
            var mana = (double)game.Me.Mana / Math.Max(1, game.Me.MaxMana);
            if (mana < MeditateBelow)
            {
                _meditating.Add(member.Name);
            }
            else if (mana > MeditateUntil)
            {
                _meditating.Remove(member.Name);
            }

            if (_meditating.Contains(member.Name))
            {
                wantedAura = Skill.Meditation;
            }
        }

        if (wantedAura is { } aura && member.SkillLevel(aura) > 0
            && (!me.ActiveSkills.TryGetValue(Hand.Right, out var active) || active != aura))
        {
            game.ChangeSkill(aura, Hand.Right);
        }

        // Shamans bring their dead back and greater mummies raise theirs: hitting the nearest monster first, the
        // party fought the same DarkOnes at Andariel's door until it ran out of potions.
        // A cold caster aims at what its cold can hurt: on 1.09 immunity is absolute, and an orb at a cold immune does nothing.
        var hurt = ColdSkills.Contains(stage.Attack)
            ? enemies.Where(npc => !attackService.IsImmuneTo(member.Client, npc, D2NG.Core.MonsterData.ResistType.Cold)).ToList() is { Count: > 0 } hurtable ? hurtable : enemies
            : enemies;
        var target = hurt.FirstOrDefault(npc => FocusFirst.Contains(npc.NPCCode))
            ?? hurt.FirstOrDefault(npc => Revivers.Contains(npc.NPCCode))
            // The boss drawn back to a cleared room is faced alone: its minions die first, then it.
            ?? hurt.FirstOrDefault(npc => npc.NPCCode != FacedLast)
            ?? hurt[0];
        if (_lastTarget.GetValueOrDefault(member.Name) != target.Id)
        {
            _lastTarget[member.Name] = target.Id;
            var why = FocusFirst.Contains(target.NPCCode) ? "focus caster" : Revivers.Contains(target.NPCCode) ? "reviver"
                : target.NPCCode == FacedLast ? "boss last" : "nearest";
            D2NG.Core.Tracing.TraceEvents.Target(game, target.Id, target.NPCCode.ToString(), why);
        }
        Track(member, target);
        var skill = Choose(member, stage, target, enemies);
        if (skill == Skill.Attack && NoMeleeFallback(member))
        {
            return await HoldBack(member, enemies);
        }

        // Out of mana for her arrows the amazon shoots plain ones: Attack with a bow is ranged, and taken for a swing it
        // walked her up to the monster.
        var use = skill == Skill.Attack && HoldsBow(member) ? BowAttack : Uses.GetValueOrDefault(skill) ?? Uses[Skill.Attack];
        // Iron Maiden returns physical hits to the one dealing them, and a whirl cannot be stopped once begun: the
        // Chaos Sanctuary's Oblivion Knights cast it, and the CS bot's barbarian went from 2034 to 292 in one whirl.
        // A cursed member backs off until it wears off.
        // Slowed - decrepified or chilled - a slow weapon's swing takes long enough for the pack to answer every one. A whirl
        // is not held for it: it only moves slower, and the risk there is Iron Maiden landing during the longer whirl.
        var slowed = use.Delivery == Delivery.Melee && SlowedWithSlowWeapon(member);
        // Blessed Hammer is not reflected: the hammerdin goes on hitting under Iron Maiden.
        var reflected = use.Delivery is Delivery.Melee or Delivery.Whirl;
        if (slowed || (reflected && attackService.IsUnderIronMaiden(member.Client)))
        {
            if (DateTime.UtcNow - _lastCurseLog.GetValueOrDefault(member.Name) > TimeSpan.FromSeconds(10))
            {
                _lastCurseLog[member.Name] = DateTime.UtcNow;
                Log.Information("{Character} is {Why}: not attacking until it wears off", member.Name, slowed ? "slowed with a slow weapon" : "under Iron Maiden");
            }

            await StepAwayFrom(member, enemies.Where(npc => npc.Location.Distance(me.Location) <= RecoverDistance).ToList(), RecoverStep);
            await Task.Delay(300);
            return true;
        }

        switch (use.Delivery)
        {
            case Delivery.Melee:
                await Close(member, target, MeleeReach);
                if (skill == Skill.Attack || stage.Aura != null)
                {
                    game.LeftHandSkillHoldOnEntity(skill, target);
                }
                else
                {
                    game.UseRightHandSkillOnEntity(skill, target);
                }

                await Task.Delay(250);
                break;
            case Delivery.Missile:
                if (member.Role == PartyRole.Ranged && target.Location.Distance(me.Location) < RangedMinimum)
                {
                    await StepBack(member, target);
                }

                // Guided Arrow finds its target: from 30 on the amazon shoots a boss from well back and draws it to the party.
                var reach = LongGuidedArrow(member, skill, target) ? GuidedArrowBossReach : use.Reach;
                var inSight = await attackService.IsInLineOfSight(member.Client, target.Location);
                if (!inSight && member.Role == PartyRole.Ranged && await ShootingSpotFor(member, target, enemies, reach) is { } spot)
                {
                    await WalkToSpot(member, spot);
                }
                else if (target.Location.Distance(me.Location) > reach || !inSight)
                {
                    await Close(member, target, reach - 3);
                }

                game.UseRightHandSkillOnEntity(skill, target);
                await Task.Delay(220);
                break;
            case Delivery.AroundSelf:
                if (skill == Skill.BlessedHammer)
                {
                    await HammerSpot(member, target, enemies, use.Reach - 1);
                    game.ShiftHoldLeftHandSkillOnLocation(skill, game.Me.Location);
                    await Task.Delay(250);
                    break;
                }

                await Close(member, target, use.Reach - 1);
                game.UseRightHandSkillOnLocation(skill, me.Location);
                await Task.Delay(250);
                break;
            case Delivery.AtLocation:
                if (skill == Skill.FrozenOrb && StaticBoss(member, enemies) is { } staticBoss && await StaticOnBoss(member, staticBoss))
                {
                    break;
                }

                var castReach = KeepFromBoss(member, target) is { } keep ? Math.Max(use.Reach, keep + BossCastMargin) : use.Reach;
                if (target.Location.Distance(me.Location) > castReach)
                {
                    await Close(member, target, castReach - 2);
                }

                game.UseRightHandSkillOnLocation(skill, target.Location);
                if (skill != Skill.FrozenOrb)
                {
                    await Task.Delay(300);
                    break;
                }

                // Frozen Orb waits a second between casts: Static Field fits in that second, a quarter of the target's
                // life each time - the sorceress cast it in 2% of a five-minute Diablo fight.
                await Task.Delay(OrbCast);
                if (StaticWorthIt(member, enemies))
                {
                    game.UseRightHandSkillOnLocation(Skill.StaticField, me.Location);
                }

                await Task.Delay(1000 - OrbCast);
                break;
            case Delivery.OnCorpse:
                var corpse = NPCHelpers.GetNearbyCorpses(member.Client, target.Location, 1).FirstOrDefault();
                if (corpse == null)
                {
                    return await Fallback(member, stage, target);
                }

                if (corpse.Location.Distance(me.Location) > use.Reach)
                {
                    await Close(member, corpse, use.Reach - 2);
                }

                game.UseRightHandSkillOnEntity(skill, corpse);
                await Task.Delay(300);
                break;
            case Delivery.Whirl:
                return await Whirl(member, target, enemies);
        }

        return true;
    }

    /// <summary>Monsters this close count for where the hammers are thrown from.</summary>
    private const double HammerLook = 15;

    /// <summary>A shorter step than this to a better hammer spot is not taken: the server left the paladin where he stood.</summary>
    private const double MinHammerStep = 2;

    /// <summary>
    /// Where the paladin throws his hammers from: the spot within a few steps whose spiral reaches the most of the
    /// monsters near before a wall swallows it (<see cref="BlessedHammerSpiral"/>). He moves only for more hits; with none
    /// from anywhere near, he closes in on the target as before.
    /// </summary>
    private async Task HammerSpot(PartyMember member, WorldObject target, List<WorldObject> enemies, double reach)
    {
        var game = member.Game;
        var map = await mapApiService.GetArea(game.MapId, AreaTraveler.MapDifficulty, game.Area);
        if (map == null)
        {
            await Close(member, target, reach);
            return;
        }

        var me = game.Me.Location;
        var open = BlessedHammerSpiral.OpenOn(map);
        var monsters = enemies.Where(npc => npc.Location.Distance(me) <= HammerLook).Select(npc => npc.Location).Append(target.Location).Distinct().ToList();
        var here = BlessedHammerSpiral.Hits(me, monsters, open);
        var spot = BlessedHammerSpiral.BestSpot(me, monsters, open);
        var there = BlessedHammerSpiral.Hits(spot, monsters, open);
        // A cell or so is not worth the step: a one-cell run at Diablo left him where he stood, asked again every cast.
        if (there > here && spot.Distance(me) >= MinHammerStep)
        {
            // A real run: a step of a few subtiles through MoveToAsync is a location update the server does not act on.
            Log.Information("{Character} steps from {From} to {To} for its hammers: {There} monsters in reach instead of {Here}", member.Name, me, spot, there, here);
            await AttackService.StepWhileHammering(game, spot);
            return;
        }

        if (here == 0)
        {
            await Close(member, target, reach);
        }
    }

    private const double WhirlLength = 5;

    /// <summary>The CS bot's MaxWhirlwindAimDistance: eight was its trade between short whirls and the top seal's time.</summary>
    private const double SanctuaryWhirlAim = 8;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FailedWhirls> _failedWhirls = new();

    /// <summary>
    /// A whirl along a lane the map shows open, through as many of the monsters near as it can: aimed five past a target
    /// the client placed on top of him, it went five to the east into the Chaos Sanctuary's wall, and the server dropped
    /// 65 whirls in 40 seconds - no mana spent, nothing hit - while the barbarian stood in the pack. A whirl that costs no
    /// mana never ran; its direction is refused from there for a few seconds and another lane is tried.
    /// </summary>
    private async Task<bool> Whirl(PartyMember member, WorldObject target, List<WorldObject> enemies)
    {
        var game = member.Game;
        var me = game.Me.Location;
        var failed = _failedWhirls.GetOrAdd(member.Name, _ => new FailedWhirls());
        // Short whirls in the Sanctuary, as the CS bot's: a long one commits him to every hit on the line, and an Iron Maiden
        // landing in it returned enough to send him out "whirling under Iron Maiden" twice in ninety seconds. A target further
        // off is closed on first; he whirls on from there.
        var aim = game.Area == D2NG.Core.D2GS.Act.Area.ChaosSanctuary ? SanctuaryWhirlAim : Party.EngageRadius;
        if (target.Location.Distance(me) > aim)
        {
            await Close(member, target, aim - 2);
            return true;
        }

        var map = await mapApiService.GetArea(game.MapId, AreaTraveler.MapDifficulty, game.Area);
        var near = enemies.Where(npc => npc.Location.Distance(me) <= aim).Select(npc => npc.Location).Append(target.Location).Distinct().ToList();
        Point through;
        if (map == null)
        {
            through = Past(me, target.Location, WhirlLength);
        }
        else
        {
            var plan = WallAwareWhirl.PlanWhirl(map, me, near, WhirlLength, end => failed.Refuses(me, end, DateTime.Now));
            if (plan.Target == null && plan.WalkTo != null)
            {
                await game.RunToAsync(plan.WalkTo);
                return true;
            }

            if (plan.Target == null)
            {
                // Nothing to whirl along from here: a step towards the target opens another lane.
                await Close(member, target, 2);
                return true;
            }

            through = plan.Target;
        }

        var manaBefore = game.Me.Mana;
        game.UseRightHandSkillOnLocation(Skill.Whirlwind, through);
        await Task.Delay(600);
        if (game.Me.Mana >= manaBefore - WhirlManaSeen)
        {
            failed.Record(me, through, DateTime.Now);
        }

        return true;
    }

    /// <summary>A whirl that ran cost him at least this much mana by the time the next decision comes.</summary>
    private const int WhirlManaSeen = 5;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastCurseLog = new();

    private static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IgnoreFor = TimeSpan.FromMinutes(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, uint), (DateTime Since, double Life)> _engaged = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, uint), DateTime> _ignored = new();

    /// <summary>
    /// Unit ids start over in every game: a timer kept from the last one gave up a Nightmare Zombie six seconds after
    /// it came into sight, "no damage in 20s", and a monster left alone there would be skipped here for a minute.
    /// </summary>
    public void ForgetGame()
    {
        _engaged.Clear();
        _ignored.Clear();
        _lastTarget.Clear();
    }

    private bool Ignored(PartyMember member, WorldObject npc)
        => _ignored.TryGetValue((member.Name, npc.Id), out var until) && until > DateTime.UtcNow;

    /// <summary>
    /// A target that loses no life for a while is out of reach - across water, behind a wall the map does not
    /// show - and swinging at it held the whole party for the fight limit. It is left alone for a minute.
    /// </summary>
    private void Track(PartyMember member, WorldObject target)
    {
        var key = (member.Name, target.Id);
        var now = DateTime.UtcNow;
        var seen = _engaged.GetOrAdd(key, (now, target.LifePercentage));
        // Within reach counts as progress too: a client is not sent every hit, so a stale life record looked like
        // no damage, and members walked away from monsters they were killing - 56 died in five minutes at
        // Andariel's door while every member kept "leaving" its target. Only one never come close is out of reach.
        if (target.LifePercentage < seen.Life || target.Location.Distance(member.Game.Me.Location) <= ReachedDistance(member))
        {
            _engaged[key] = (now, target.LifePercentage);
        }
        else if (now - seen.Since > NoProgressLimit)
        {
            _ignored[key] = now + IgnoreFor;
            _engaged.TryRemove(key, out _);
            Log.Information("{Character} leaves {Monster} at {Location} alone: no damage in {Seconds}s", member.Name, target.NPCCode, target.Location, NoProgressLimit.TotalSeconds);
        }
    }

    /// <summary>
    /// Traps are monsters to the server but cannot be hurt: with a target in reach counted as progress, the party
    /// fought a gargoyle trap in the Catacombs for fifteen minutes. They are never targets.
    /// </summary>
    public static readonly HashSet<NPCCode> Untargetable =
    [
        NPCCode.GargoyleTrap, NPCCode.ATrap1, NPCCode.ATrap2, NPCCode.ATrap3, NPCCode.ATrap4, NPCCode.ATrap5,
        NPCCode.ATrap6, NPCCode.ATrap7, NPCCode.TrapedSoul1, NPCCode.TrapedSoul2,
        // The Arcane Sanctuary's towers cannot be killed: standing to hit a Lightning Spire chickened three members.
        NPCCode.LightningSpire, NPCCode.FireTower
    ];

    /// <summary>
    /// Casters whose own spells are the danger, killed before anything else: left for last like Andariel, the
    /// Summoner cast 154 times at the party while it fought his Ghoul Lords and chickened three members. Burning Souls
    /// are frail but their lightning took 141-280 life in under 0.2 s from three members in the City of the Damned.
    /// </summary>
    // Oblivion Knights curse: Iron Maiden from them held the barbarian out of the Chaos fight about once a minute.
    public static readonly HashSet<NPCCode> FocusFirst = [NPCCode.Summoner, NPCCode.BurningSoul, NPCCode.BlackSoul, NPCCode.OblivionKnight];

    private static readonly HashSet<NPCCode> Revivers =
    [
        NPCCode.FallenShamen, NPCCode.CarverShamen, NPCCode.DevilkinShamen, NPCCode.DarkShamen, NPCCode.WarpedShamen,
        NPCCode.RatManShamen, NPCCode.FetishShamen, NPCCode.FlayerShamen, NPCCode.SoulKillerShamen, NPCCode.StygianDollShamen,
        NPCCode.HallowOne, NPCCode.Guardian, NPCCode.Unraveler, NPCCode.HoradrimAncient,
        // Greater mummies raise the dead around them and fire unholy bolts: fourteen Ghoul Lords in the Arcane
        // Sanctuary chickened the leader through three rejuvenations while the party fought the Specters first.
        NPCCode.GhoulLord, NPCCode.NightLord, NPCCode.DarkLord, NPCCode.BloodLord
    ];

    /// <summary>The boss being fought, attacked only once nothing else is left in reach.</summary>
    public NPCCode? FacedLast { get; set; }

    private static double ReachedDistance(PartyMember member) => member.Role == PartyRole.Ranged ? 20 : MeleeReach + 2;

    /// <summary>The plan's attack, its secondary against what the attack cannot hurt, Attack when mana or gear is missing.</summary>
    private Skill Choose(PartyMember member, CombatStage stage, WorldObject target, List<WorldObject> enemies)
    {
        if (PackShot(member, stage, target, enemies))
        {
            return Skill.MultipleShot;
        }

        foreach (var skill in new[] { stage.Attack, stage.Secondary }.OfType<Skill>())
        {
            if (skill == Skill.Attack)
            {
                return skill;
            }

            if (member.SkillLevel(skill) <= 0 || !Uses.TryGetValue(skill, out var use) || member.Game.Me.Mana < use.Mana)
            {
                continue;
            }

            if (NeedsBow(skill) && !HoldsBow(member))
            {
                continue;
            }

            // Not walked up to for: left with a cold immune, the orb sorceress would have closed in on it to cast.
            if (skill == Skill.StaticField && (target.LifePercentage < 40 || target.Location.Distance(member.Game.Me.Location) > StaticRadius(member)))
            {
                continue;
            }

            if (ColdSkills.Contains(skill) && attackService.IsImmuneTo(member.Client, target, D2NG.Core.MonsterData.ResistType.Cold))
            {
                continue;
            }

            return skill;
        }

        return Skill.Attack;
    }

    /// <summary>A weapon's base speed from the game data; above zero swings slower than normal.</summary>
    public static Func<D2NG.Core.D2GS.Items.Item, int?> WeaponSpeed { get; set; }

    private static readonly EntityEffect[] SlowingEffects = [EntityEffect.Decrepify, EntityEffect.Cold, EntityEffect.Slowed, EntityEffect.Freeze];

    private static bool SlowedWithSlowWeapon(PartyMember member)
    {
        var me = member.Game.Me;
        return SlowingEffects.Any(me.Effects.ContainsKey)
            && me.Equipment.GetValueOrDefault(D2NG.Core.D2GS.Items.DirectoryType.RightHand) is { } weapon
            && WeaponSpeed?.Invoke(weapon) > 0;
    }

    private const int MeleeFallbackUntil = 25;

    /// <summary>
    /// A caster out of mana - sorceress, necromancer, hammerdin - walks up and swings only while young: past 25 the monsters
    /// hit too hard for what the weapon does, and the orb sorceress walked into Chaos's Oblivion Knights to swing at them
    /// under Iron Maiden.
    /// </summary>
    private static bool NoMeleeFallback(PartyMember member) => LevelingShop.IsCaster(member) && member.Level >= MeleeFallbackUntil;

    /// <summary>Out of reach of the monsters close by while the mana comes back.</summary>
    private async Task<bool> HoldBack(PartyMember member, List<WorldObject> enemies)
    {
        var near = enemies.Where(npc => npc.Location.Distance(member.Game.Me.Location) <= RecoverDistance).ToList();
        if (near.Count > 0)
        {
            await StepAwayFrom(member, near, RecoverStep);
        }

        await Task.Delay(300);
        return true;
    }

    private async Task<bool> Fallback(PartyMember member, CombatStage stage, WorldObject target)
    {
        var secondary = stage.Secondary is { } skill && skill != Skill.CorpseExplosion && member.SkillLevel(skill) > 0 ? skill : Skill.Attack;
        if (secondary == Skill.Attack && NoMeleeFallback(member))
        {
            return await HoldBack(member, [target]);
        }

        var use = secondary == Skill.Attack && HoldsBow(member) ? BowAttack : Uses.GetValueOrDefault(secondary) ?? Uses[Skill.Attack];
        await Close(member, target, use.Delivery == Delivery.Melee ? MeleeReach : use.Reach - 3);
        if (secondary == Skill.Attack)
        {
            member.Game.LeftHandSkillHoldOnEntity(secondary, target);
        }
        else
        {
            member.Game.UseRightHandSkillOnEntity(secondary, target);
        }

        await Task.Delay(250);
        return true;
    }

    private static readonly HashSet<Skill> ColdSkills = [Skill.FrozenOrb, Skill.Blizzard, Skill.IceBolt, Skill.IceBlast, Skill.GlacialSpike, Skill.FrostNova];

    private const double MultipleShotMana = 20;

    /// <summary>
    /// Multiple Shot for a pack, as the cow amazon shoots: its fan of arrows, each piercing on, against Guided Arrow's one. A boss
    /// keeps Guided Arrow.
    /// </summary>
    private bool PackShot(PartyMember member, CombatStage stage, WorldObject target, List<WorldObject> enemies)
        => (stage.Attack == Skill.MultipleShot || stage.Secondary == Skill.MultipleShot)
            && member.SkillLevel(Skill.MultipleShot) > 0 && HoldsBow(member) && member.Game.Me.Mana >= MultipleShotMana
            && !target.IsBoss && target.NPCCode != FacedLast
            && IsPack(target.Location, enemies.Select(npc => npc.Location).ToList());

    private const double PackRadius = 8;

    private const int PackSize = 4;

    /// <summary>Whether at least <see cref="PackSize"/> monsters, the target among them, stand within <see cref="PackRadius"/> of it.</summary>
    public static bool IsPack(Point target, IReadOnlyList<Point> monsters)
        => monsters.Count(monster => monster.Distance(target) <= PackRadius) >= PackSize;

    private static bool NeedsBow(Skill skill) => skill is Skill.MagicArrow or Skill.MultipleShot or Skill.GuidedArrow;

    private static bool HoldsBow(PartyMember member)
        => member.Game.Items.Values.Any(item => item.IsEquipped
            && item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow or ClassificationType.Crossbow);

    private const double RangedMinimum = 8;

    /// <summary>The curses that make every hit taken count more.</summary>
    private static readonly EntityEffect[] DamageCurses = [EntityEffect.Amplifydamage, EntityEffect.Decrepify];

    private const double CursedKeepAway = 8;

    private const int FanaticismFrom = 10;

    private static readonly TimeSpan AuraTurn = TimeSpan.FromSeconds(6);

    private const int OrbCast = 500;

    /// <summary>Static Field's reach grows with its level; kept on the short side of what the skill reaches.</summary>
    private static double StaticRadius(PartyMember member) => Math.Min(15, 4 + 0.6 * member.SkillLevel(Skill.StaticField));

    /// <summary>
    /// Static Field fills the orb's one-second wait whenever anything within reach is not lightning immune - on 1.09 immunity
    /// is absolute (the user's rule). Judged on the orb's target alone, above 40%, she cast it after one orb in four; the cold
    /// immunes beside it, which her orb cannot touch, are the ones it is most for.
    /// </summary>
    private bool StaticWorthIt(PartyMember member, List<WorldObject> enemies)
        => member.SkillLevel(Skill.StaticField) > 0 && member.Game.Me.Mana >= Uses[Skill.StaticField].Mana + Uses[Skill.FrozenOrb].Mana
            && enemies.Any(npc => npc.Location.Distance(member.Game.Me.Location) <= StaticRadius(member)
                && !attackService.IsImmuneTo(member.Client, npc, D2NG.Core.MonsterData.ResistType.Lightning));

    private const double StaticBossFrom = 40;

    private const double StaticBossReach = 25;

    private const int StaticCast = 450;

    /// <summary>
    /// A boss above 40% within a teleport of the sorceress that lightning hurts: Static Field takes a quarter of what it has
    /// left a cast, far more than an orb does, so it gets Static alone until it is down to 40% (the user's pick).
    /// </summary>
    private WorldObject StaticBoss(PartyMember member, List<WorldObject> enemies)
        => member.SkillLevel(Skill.StaticField) <= 0 || member.Game.Me.Mana < Uses[Skill.StaticField].Mana ? null
            : enemies.FirstOrDefault(npc => (npc.IsBoss || npc.NPCCode == FacedLast) && npc.LifePercentage > StaticBossFrom
                && npc.Location.Distance(member.Game.Me.Location) <= StaticBossReach
                && !attackService.IsImmuneTo(member.Client, npc, D2NG.Core.MonsterData.ResistType.Lightning));

    /// <summary>Set by the bot: the monster level of the area the member stands in, when the table has it.</summary>
    public static Func<PartyMember, int?> AreaMonsterLevel { get; set; }

    private const double BoldFromLife = 0.7;

    /// <summary>
    /// Whether the member may go in after a boss rather than wait where it stands: at or above the area's monster level and
    /// with most of its life. A party some levels below the monsters plays as safe as before (the user's rule: how bold
    /// depends on the party's level).
    /// </summary>
    private static bool PlaysBold(PartyMember member)
        => AreaMonsterLevel?.Invoke(member) is int monsters && member.Level >= monsters && member.Game.Me.Life >= member.Game.Me.MaxLife * BoldFromLife;

    /// <summary>
    /// Into static reach of the boss - by teleport when she has it and plays bold, kept as far as she keeps from bosses - then
    /// Static. False when the boss is out of reach and she stays where she is: the orb goes instead.
    /// </summary>
    private async Task<bool> StaticOnBoss(PartyMember member, WorldObject boss)
    {
        var game = member.Game;
        var keep = KeepFromBoss(member, boss) ?? RangedFromBoss;
        if (boss.Location.Distance(game.Me.Location) > StaticRadius(member) - 1)
        {
            if (!PlaysBold(member))
            {
                return false;
            }

            var spot = game.Me.Location.GetPointBeforePointInSameDirection(boss.Location, Math.Min(keep, StaticRadius(member) - 2));
            if (member.SkillLevel(Skill.Teleport) > 0 && game.Me.Mana >= Uses[Skill.StaticField].Mana + TeleportMana)
            {
                await game.TeleportToLocationAsync(spot, 0);
            }
            else
            {
                await Close(member, boss, StaticRadius(member) - 2);
            }
        }

        if (boss.Location.Distance(game.Me.Location) > StaticRadius(member))
        {
            return false;
        }

        game.UseRightHandSkillOnLocation(Skill.StaticField, game.Me.Location);
        await Task.Delay(StaticCast);
        return true;
    }

    private const int TeleportMana = 24;

    private const int LongGuidedArrowFrom = 30;
    private const double GuidedArrowBossReach = 30;
    private const double GuidedArrowBossDistance = 20;

    private bool LongGuidedArrow(PartyMember member, Skill skill, WorldObject target)
        => skill == Skill.GuidedArrow && member.Level >= LongGuidedArrowFrom && member.SkillLevel(Skill.GuidedArrow) > 0
            && (target.IsBoss || target.NPCCode == FacedLast);

    /// <summary>How far a ranged member keeps from this monster, or null for one it need not keep away from.</summary>
    public double? KeepFromBoss(PartyMember member, WorldObject npc)
        => member.Role == PartyRole.Ranged && (npc.IsBoss || npc.NPCCode == FacedLast)
            ? LongGuidedArrow(member, Skill.GuidedArrow, npc) ? GuidedArrowBossDistance : RangedFromBoss
            : null;

    /// <summary>
    /// A spell cast at a boss's spot reaches past the distance kept from it: Frozen Orb's 12 closed the sorceress in to 10 of
    /// Diablo, where keeping 12 from him stepped her straight back out.
    /// </summary>
    private const double BossCastMargin = 4;

    private static readonly SkillUse BowAttack = new(Delivery.Missile, 0, 15);
    private const double RangedFromBoss = 12;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _lastTarget = new();
    private const double LowLifeStepOut = 0.45;


    /// <summary>The death blast reaches about six (Pindleskin's own, measured); below this life a monster can die any hit.</summary>
    private const double FireBlastClear = 8;
    private const double FireBlastLife = 35;
    private const double BlastRiskLife = 0.6;
    private const double RecoveredLife = 0.6;
    private const double RecoverDistance = 12;
    private const double RecoverStep = 12;
    private const double BossRecoverDistance = 20;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _recovering = new();

    /// <summary>A ranged member with a monster on top of it backs off a few steps before shooting again.</summary>
    /// <summary>
    /// Away from the middle of every monster near, not from the nearest one: stepping back from one Burning Soul walked the
    /// paladin into the rest of the pack at a third of his life, and he died there.
    /// </summary>
    private const double MeditateBelow = 0.25;
    private const double MeditateUntil = 0.7;
    private readonly HashSet<string> _meditating = [];

    private async Task StepAwayFrom(PartyMember member, List<WorldObject> near, double distance)
    {
        if (near.Count == 0)
        {
            return;
        }

        var me = member.Game.Me.Location;
        var centre = new Point((ushort)near.Average(npc => npc.Location.X), (ushort)near.Average(npc => npc.Location.Y));
        var away = Past(centre, me, distance);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            // The sorceress teleports out: walking back a few steps in Hell left her where the pack still reached.
            await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, away, MovementHelpers.PreferredMovement(member.Game), cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The point <paramref name="distance"/> past <paramref name="to"/> as seen from <paramref name="from"/>. Standing on
    /// <paramref name="from"/> leaves no direction: the plain helper then answers NaN, cast to (0, 0), and the member
    /// walked toward the map's corner.
    /// </summary>
    private static Point Past(Point from, Point to, double distance)
        => from.Distance(to) < 1
            ? new Point((ushort)(to.X + distance), to.Y)
            : from.GetPointPastPointInSameDirection(to, distance);

    private async Task StepBack(PartyMember member, WorldObject target, double distance = 6)
    {
        var me = member.Game.Me.Location;
        var away = Past(target.Location, me, distance);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, away, MovementMode.Walking, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Walks toward the target until within <paramref name="reach"/>, for at most a second and a half.</summary>
    private async Task<Point> ShootingSpotFor(PartyMember member, WorldObject target, List<WorldObject> enemies, double reach)
    {
        var game = member.Game;
        var map = await mapApiService.GetArea(game.MapId, AreaTraveler.MapDifficulty, game.Area);
        if (map?.Map == null)
        {
            return null;
        }

        int? Cell(Point point)
        {
            var row = point.Y - map.LevelOrigin.Y;
            var column = point.X - map.LevelOrigin.X;
            return row < 0 || row >= map.Map.Length || column < 0 || column >= map.Map[row].Length ? null : map.Map[row][column];
        }

        return ShootingSpot(game.Me.Location, target.Location, enemies.Select(npc => npc.Location).ToList(), reach, Cell);
    }

    private async Task WalkToSpot(PartyMember member, Point spot)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, spot, MovementMode.Walking, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Debug("{Character} could not walk to its shooting spot {Spot}: {Reason}", member.Name, spot, exception.Message);
        }
    }

    private const double ShootFromAtLeast = 10;

    private const double ShootFromAtMost = 18;

    private const double ShootingSpotDetour = 25;

    /// <summary>
    /// Where a ranged member shoots from when the target is out of sight: a walkable spot that sees it from 10 to 18 away with
    /// no monster within 8, the nearest to the member. Closing in on it instead walked the inside of a bend, the shortest way,
    /// and the amazon saw round a sharp corner only once she stood beside the pack; the wide side of the turn sees it from afar.
    /// Null when no such spot lies within 25 of the member.
    /// </summary>
    public static Point ShootingSpot(Point me, Point target, IReadOnlyList<Point> monsters, double reach, Func<Point, int?> cell)
    {
        Point best = null;
        var bestDetour = double.MaxValue;
        for (var range = Math.Min(reach - 3, ShootFromAtMost); range >= ShootFromAtLeast; range -= 2)
        {
            for (var degrees = 0; degrees < 360; degrees += 15)
            {
                var angle = degrees * Math.PI / 180;
                var spot = new Point((ushort)Math.Round(target.X + range * Math.Cos(angle)), (ushort)Math.Round(target.Y + range * Math.Sin(angle)));
                var detour = spot.Distance(me);
                if (detour >= bestDetour || detour > ShootingSpotDetour || cell(spot) is not int value || !D2NG.Navigation.Extensions.AreaMapExtensions.IsMovable(value)
                    || monsters.Any(monster => monster.Distance(spot) < RangedMinimum)
                    || AttackService.GetPointsOnLine(spot.X, spot.Y, target.X, target.Y).Any(point => cell(point) is not int seen || AttackService.BlocksSight(seen)))
                {
                    continue;
                }

                best = spot;
                bestDetour = detour;
            }
        }

        return best;
    }

    private async Task Close(PartyMember member, WorldObject target, double reach)
    {
        var game = member.Game;
        if (target.Location.Distance(game.Me.Location) <= Math.Max(reach, 1))
        {
            return;
        }

        var goal = reach <= MeleeReach ? target.Location : game.Me.Location.GetPointBeforePointInSameDirection(target.Location, reach);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            await MovementHelpers.MoveToLocation(game, pathingService, mapApiService, goal, MovementMode.Walking, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Debug("{Character} could not close on {Target}: {Reason}", member.Name, target.NPCCode, exception.Message);
        }
    }
}
