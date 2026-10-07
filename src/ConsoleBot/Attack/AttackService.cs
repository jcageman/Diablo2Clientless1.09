using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.MonsterData;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Attack;

public class AttackService : IAttackService
{
    private readonly IPathingService _pathingService;
    private readonly IMapApiService _mapApiService;
    private readonly ILogger<AttackService> _logger;
    private readonly MonsterResistTable _monsterResists;

    public AttackService(
        IPathingService pathingService,
        IMapApiService mapApiService,
        ILogger<AttackService> logger,
        MonsterResistTable monsterResists)
    {
        _pathingService = pathingService;
        _mapApiService = mapApiService;
        _logger = logger;
        _monsterResists = monsterResists;
    }

    /// <summary>How tightly monsters have to be bunched around the target to be worth whirling through.</summary>
    private const double IdleStepMinDistance = 5;

    private const double IdleStepMaxDistance = 10;

    private const int IdleDangerRadius = 25;

    private const int SurroundedRadius = 12;

    private const int SurroundedCount = 2;

    private const double BossEscortRadius = 12;

    private const int ThinEscort = 2;

    private const double BossSightApproach = 12;

    private const double BossChaseRange = 30;

    private const double AmplifyKeepOutRadius = 15;

    /// <summary>The amazon shoots only along lines this many units clear on either side.</summary>
    private const int AmazonSightClearance = 2;

    /// <summary>
    /// How close the ranged followers go to a boss the party has committed to. Committing only
    /// changed their targets: at a De Seis 54 out the amazon shot at his escort from the kill spot,
    /// at the edge of what the client sees, and the necromancer cursed nothing, while the paladin
    /// fought him alone.
    /// </summary>
    private const double AmazonFocusReach = 30;

    private const double NecromancerFocusReach = 20;

    /// <summary>The barbarian anchors on the amazon and she on him, so neither moved for a De Seis 57 out.</summary>
    private const double BarbarianFocusReach = 10;

    private const int FocusStepMs = 1500;

    private static readonly HashSet<NPCCode> CurseCasters = [NPCCode.OblivionKnight, NPCCode.AbyssKnight];

    /// <summary>Four: at three the left seal's thirty-strong swarm outlived the party; the 80% whirl floor is what keeps a cursed whirl survivable.</summary>
    private const int MaxWhirlwindTargets = 4;

    /// <summary>At 0.6 a whirl started at 2218 life ended at 205; a curse landing mid-whirl returns about 2000, so he starts one only with that much to spare.</summary>
    private const double BarbarianWhirlMinLife = 0.8;

    private const double CorpseExplosionRadius = 6;

    /// <summary>Short whirls in the Sanctuary: a long one commits him to every hit on the line, cursed or not. Six was tried on 15 Sept 2026 and the top seal went from 22 to 30 seconds with two timeouts in fourteen games; eight is the trade.</summary>
    private const double MaxWhirlwindAimDistance = 8;

    private const double WhirlwindPackRadius = 8;

    private const double WhirlwindApproachDistance = 10;

    private static readonly TimeSpan WhirlwindRetryInterval = TimeSpan.FromMilliseconds(100);

    private const double WhirlwindArrivalDistance = 4;


    internal sealed class Line
    {
        public Point StartPoint { get; set; }

        public Point EndPoint { get; set; }
    }

    public async Task<bool> IsInLineOfSight(Client client, Point toLocation)
    {
        return await IsInLineOfSight(client, client.Game.Me.Location, toLocation);
    }

    public async Task<bool> IsInLineOfSight(Client client, Point fromLocation, Point toLocation)
    {
        return await IsInLineOfSight(client, fromLocation, toLocation, 0);
    }

    /// <summary>
    /// Line of sight with <paramref name="clearance"/> units free on either side of the line as well:
    /// the centre line and lines offset that far to each side must all be clear. A line that only
    /// just passes a wall corner is not one Multiple Shot gets through, since the volley fans out
    /// and the monster's position here lags its real one.
    /// </summary>
    private async Task<bool> IsInLineOfSight(Client client, Point fromLocation, Point toLocation, int clearance)
    {
        var directDistance = fromLocation.Distance(toLocation);
        if (directDistance == 0)
        {
            return true;
        }

        var clientArea = client.Game.Area;
        if (clientArea == Area.None)
        {
            return false;
        }

        var areaMap = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, clientArea);
        var sideX = -(toLocation.Y - fromLocation.Y) / directDistance;
        var sideY = (toLocation.X - fromLocation.X) / directDistance;
        for (var offset = -clearance; offset <= clearance; offset++)
        {
            var shiftX = (int)Math.Round(sideX * offset);
            var shiftY = (int)Math.Round(sideY * offset);
            var pointsOnLine = GetPointsOnLine(
                (ushort)(fromLocation.X + shiftX), (ushort)(fromLocation.Y + shiftY),
                (ushort)(toLocation.X + shiftX), (ushort)(toLocation.Y + shiftY));
            foreach (var point in pointsOnLine)
            {
                var row = point.Y - areaMap.LevelOrigin.Y;
                var column = point.X - areaMap.LevelOrigin.X;
                if (row < 0 || row >= areaMap.Map.Length || column < 0 || column >= areaMap.Map[row].Length)
                {
                    return false;
                }

                var mapValue = areaMap.Map[row][column];
                if (!AreaMapExtensions.IsMovable(mapValue) && mapValue != 1)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static IEnumerable<Point> GetPointsOnLine(ushort x0, ushort y0, ushort x1, ushort y1)
    {
        bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep)
        {
            ushort t;
            t = x0; // swap x0 and y0
            x0 = y0;
            y0 = t;
            t = x1; // swap x1 and y1
            x1 = y1;
            y1 = t;
        }
        if (x0 > x1)
        {
            ushort t;
            t = x0; // swap x0 and x1
            x0 = x1;
            x1 = t;
            t = y0; // swap y0 and y1
            y0 = y1;
            y1 = t;
        }
        ushort dx = (ushort)(x1 - x0);
        ushort dy = (ushort)(Math.Abs(y1 - y0));
        ushort error = (ushort)(dx / 2);
        ushort ystep = (ushort)((y0 < y1) ? 1 : -1);
        ushort y = y0;
        for (ushort x = x0; x <= x1; x++)
        {
            yield return new Point((steep ? y : x), (steep ? x : y));
            error = (ushort)(error - dy);
            if (error < 0)
            {
                y += ystep;
                error += dx;
            }
        }
        yield break;
    }

    public async Task<bool> IsVisitable(Client client, Point point)
    {
        var path = await _pathingService.GetPathToLocation(client.Game, point, MovementMode.Walking);
        return path.Count != 0;
    }

    private static List<Point> GetNearbyMonsters(List<Point> enemies, Point location, double distance)
    {
        return enemies.Where(p => p.Distance(location) < distance).ToList();

    }

    private async Task<Point> FindNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, double minDistance = 0, double maxdistance = 30)
    {
        Point bestSpot = null;
        int spotMonsters = int.MaxValue;
        for (int i = 1; i < 5; ++i)
        {
            foreach (var (p1, p2) in new List<(short, short)> {
                (-5,0), (5, 0), (0, -5), (0, 5), (-5, 5), (-5, -5), (5, -5), (5, 5)})
            {
                var x = (short)(p1 * i);
                var y = (short)(p2 * i);

                var distance = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2));
                if (distance < minDistance || distance > maxdistance)
                {
                    continue;
                }

                var tryLocation = toLocation.Add(x, y);
                if (await IsVisitable(client, tryLocation) && await IsInLineOfSight(client, tryLocation, toLocation))
                {
                    var monsters = GetNearbyMonsters(enemies, tryLocation, 5.0).Count;
                    if(monsters < spotMonsters)
                    {
                        spotMonsters = monsters;
                        bestSpot = tryLocation;
                    }
                }
            }
        }

        return bestSpot;
    }

    public async Task<bool> MoveToNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, MovementMode movementMode, double minDistance = 0, double maxDistance = 30)
    {
        var spot = await FindNearbySafeSpot(client, enemies, toLocation, minDistance, maxDistance);
        if (spot != null)
        {
            if (movementMode == MovementMode.Teleport)
            {
                // Out of the game a teleport fails at once, and retrying it for the four seconds held
                // the party in a game the taxi had chickened out of.
                if (await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    return !client.Game.IsInGame() || await client.Game.TeleportToLocationAsync(spot);
                }, TimeSpan.FromSeconds(4)) && client.Game.IsInGame())
                {
                    return true;
                }
            }
            else
            {
                var path = await _pathingService.GetPathToLocation(client.Game, spot, MovementMode.Walking);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking))
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Walking to safe spot failed at {Location}", client.Game.Me.Location);
                    return false;
                }

                return true;
            }
        }
        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("No safe spot found for {Name} on location {Location}", client.Game.Me.Name, client.Game.Me.Location);
        return false;
    }

    public async Task<bool> AssistPlayer(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes = null, Point focus = null, Point standAt = null)
    {
        if (client.Game.IsInTown())
        {
            return true;
        }

        if (client.Game.Me.Class != CharacterClass.Sorceress
            && client.Game.Belt.NumOfHealthPotions() == 0
            && client.Game.Belt.NumOfRejuvenationPotions() == 0
            && !client.Game.Inventory.Items.Any(i => i.Classification == ClassificationType.HealthPotion || i.Classification == ClassificationType.RejuvenationPotion))
        {
            _logger.LogWarning("{Character} has no health potions left, disengaging instead of fighting", client.Game.Me.Name);
            await IdleReposition(client, player.Location);
            return true;
        }

        switch (client.Game.Me.Class)
        {
            case CharacterClass.Amazon:
                return await AmazonAssist(client, player, priorityCodes, focus, standAt);
            case CharacterClass.Sorceress:
                return await SorceressAssist(client, player, priorityCodes);
            case CharacterClass.Necromancer:
                return await NecromancerAssist(client, player, focus);
            case CharacterClass.Paladin:
                return await PaladinAssist(client, player, priorityCodes, focus);
            case CharacterClass.Barbarian:
                return await BarbarianAssist(client, player, priorityCodes, focus);
            case CharacterClass.Druid:
                break;
            case CharacterClass.Assassin:
                break;
        }

        return true;
    }

    private async Task<bool> CloseOnFocus(Client client, Point focus, double reach)
    {
        var me = client.Game.Me.Location;
        if (focus == null || me.Distance(focus) <= reach)
        {
            return false;
        }

        var toward = me.GetPointBeforePointInSameDirection(focus, reach - 3);
        var cancel = new System.Threading.CancellationTokenSource();
        cancel.CancelAfter(FocusStepMs);
        await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, toward, MovementMode.Walking, cancel.Token);
        return true;
    }

    private async Task IdleReposition(Client client, Point anchor)
    {
        var threats = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 20, IdleDangerRadius).ToList();
        if (threats.Count == 0)
        {
            return;
        }

        if (await MoveToNearbySafeSpot(client,
            threats.Select(e => e.Location).ToList(),
            anchor,
            MovementMode.Walking,
            IdleStepMinDistance,
            IdleStepMaxDistance))
        {
            return;
        }

        var from = client.Game.Me.Location;
        double dx = 0;
        double dy = 0;
        foreach (var threat in threats)
        {
            dx += from.X - threat.Location.X;
            dy += from.Y - threat.Location.Y;
        }

        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
        {
            return;
        }

        var step = from.Add((short)(dx / length * IdleStepMaxDistance), (short)(dy / length * IdleStepMaxDistance));
        var path = await _pathingService.GetPathToLocation(client.Game, step, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking);
        }
    }

    /// <param name="standAt">Where she repositions when nothing is in sight; the player she assists when null. A barbarian whirling through a pack by a wall is no place to stand beside.</param>
    private async Task<bool> AmazonAssist(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes, Point focus, Point standAt = null)
    {
        var me = client.Game.Me;
        var enemies = Prioritize(NPCHelpers.GetNearbyNPCs(client, focus ?? player.Location, 20, 40).ToList(), priorityCodes);

        if (me.Attributes[Attribute.Level] < 30 && client.Game.Difficulty > Difficulty.Normal)
        {
            return true;
        }
        else if (me.Attributes[Attribute.Level] < 26 && client.Game.Difficulty == Difficulty.Normal && client.Game.Area == Area.CowLevel)
        {
            return true;
        }

        if (await CloseOnFocus(client, focus, AmazonFocusReach))
        {
            return true;
        }

        // Committed to a boss she stays where she got to; the kill spot is behind her.
        var anchor = focus != null ? me.Location : standAt ?? player.Location;
        var nearest = await GetNearestInSight(client, enemies, AmazonSightClearance);
        if (nearest == null)
        {
            // Everything is behind a wall. Walking towards a blocked trash monster pulled her round
            // the wall and away from the party; for the boss alone she closes to sight range, since
            // a Vizier a few units out of view with the amazon idle cost a whole seal timeout.
            var hiddenBoss = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
            if (hiddenBoss != null && hiddenBoss.Location.Distance(me.Location) > BossSightApproach)
            {
                var toward = me.Location.GetPointBeforePointInSameDirection(hiddenBoss.Location, BossSightApproach);
                var cancel = new System.Threading.CancellationTokenSource();
                cancel.CancelAfter(1500);
                await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, toward, MovementMode.Walking, cancel.Token);
                return true;
            }

            await IdleReposition(client, anchor);
            return true;
        }

        // A boss whose escort is dead is one target however many stragglers stand within forty of
        // the party; Multiple Shot spread over him is wasted, Guided Arrow is not.
        var boss = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
        if (boss != null
            && me.HasSkill(Skill.GuidedArrow)
            && me.Mana > 20
            && enemies.Count(e => e.Id != boss.Id && e.Location.Distance(boss.Location) < BossEscortRadius) <= ThinEscort
            && await IsInLineOfSight(client, me.Location, boss.Location, AmazonSightClearance))
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}, escort thin", client.Game.Me.Name, boss.NPCCode, Skill.GuidedArrow);
            client.Game.RepeatRightHandSkillOnEntity(Skill.GuidedArrow, boss);
            await Task.Delay(200);
        }
        else if (me.HasSkill(Skill.MultipleShot) && me.Mana > 20 && enemies.Count > 5)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.MultipleShot);
            client.Game.RepeatRightHandSkillOnEntity(Skill.MultipleShot, nearest);
            await Task.Delay(200);
        }
        else if (me.HasSkill(Skill.LightningFury) && me.Mana > 20 && enemies.Count > 5)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.LightningFury);
            client.Game.UseRightHandSkillOnEntity(Skill.LightningFury, nearest);
            await Task.Delay(200);
        }
        else if (me.HasSkill(Skill.GuidedArrow) && me.Mana > 20 && enemies.Count < 5)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.GuidedArrow);
            client.Game.RepeatRightHandSkillOnEntity(Skill.GuidedArrow, nearest);
            await Task.Delay(200);
        }
        else if (client.Game.Me.Equipment.TryGetValue(DirectoryType.RightHand, out var weapon)
            && weapon.Classification == ClassificationType.Bow)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.Attack);
            client.Game.RepeatRightHandSkillOnEntity(Skill.Attack, nearest);
            await Task.Delay(200);
        }
        else if (me.Attributes[Attribute.Level] < 10
            && client.Game.Me.Equipment.TryGetValue(DirectoryType.RightHand, out var javalin)
            && javalin.Classification == ClassificationType.Javelin)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.Attack);
            await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService, nearest, MovementMode.Walking);
            client.Game.LeftHandSkillHoldOnEntity(Skill.Attack, nearest);
            await Task.Delay(200);
        }
        else
        {
            await IdleReposition(client, anchor);
        }

        return true;
    }

    /// <summary>
    /// Moves the monsters the caller cares about to the front of an otherwise distance ordered
    /// list, so the party finishes them before it chews through whatever else wandered in.
    /// </summary>
    private static List<WorldObject> Prioritize(List<WorldObject> enemies, IReadOnlyCollection<NPCCode> priorityCodes)
    {
        if (priorityCodes == null || priorityCodes.Count == 0 || enemies.Count == 0)
        {
            return enemies;
        }

        return enemies.OrderBy(e => priorityCodes.Contains(e.NPCCode) ? 0 : 1).ToList();
    }

    private async Task<WorldObject> GetNearestInSight(Client client, List<WorldObject> enemies, int clearance = 0)
    {
        foreach (var enemy in enemies)
        {
            if (await IsInLineOfSight(client, client.Game.Me.Location, enemy.Location, clearance))
            {
                return enemy;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an element is wasted on this monster. On 1.09 immunity is absolute - Conviction and
    /// Lower Resist only began breaking immunities in 1.10 - so an immune target will not take the
    /// damage however long the bot stands there. Burning souls are the case that matters: they are
    /// lightning immune in Hell, so static field never moves their life bar, and a cold sorceress
    /// used to soften one forever instead of casting her orb.
    /// </summary>
    /// <summary>
    /// The kinds of damage this character can deal, taken from the skills it actually has. A
    /// character with no listed skill still counts as physical: every class can swing.
    /// </summary>
    private static IEnumerable<ResistType> DamageTypesOf(Client client)
    {
        // Only for characters that actually swing. Yielding it for everyone made the whole check
        // pointless: a burning soul is physical 30 in Hell, so the first type tested came back
        // harmable and a nova sorceress was told to go and fight the pack she is immune-locked
        // against - which is the exact case this was written to catch.
        if (client.Game.Me.Class != CharacterClass.Sorceress)
        {
            yield return ResistType.Physical;
        }

        if (client.Game.Me.Skills.GetValueOrDefault(Skill.Nova) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.StaticField) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.Lightning) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.ChainLightning) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.LightningFury) > 0)
        {
            yield return ResistType.Lightning;
        }

        if (client.Game.Me.Skills.GetValueOrDefault(Skill.FrozenOrb) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.Blizzard) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.GlacialSpike) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.IceBlast) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.IceBolt) > 0)
        {
            yield return ResistType.Cold;
        }

        if (client.Game.Me.Skills.GetValueOrDefault(Skill.FireBall) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.FireBolt) > 0
            || client.Game.Me.Skills.GetValueOrDefault(Skill.Meteor) > 0)
        {
            yield return ResistType.Fire;
        }
    }

    public bool CanHarm(Client client, NPCCode code, IEnumerable<MonsterEnchantment> enchantments)
    {
        var list = enchantments as IReadOnlyCollection<MonsterEnchantment> ?? [.. enchantments ?? []];
        foreach (var type in DamageTypesOf(client))
        {
            if (!_monsterResists.TryGetResist(code, client.Game.Difficulty, type, list, out var resist)
                || resist < MonsterResistTable.ImmuneAt)
            {
                // Unknown counts as harmable: not knowing is not a reason to walk past something.
                return true;
            }
        }

        return false;
    }

    public bool IsImmuneTo(Client client, WorldObject monster, ResistType type)
    {
        return IsImmune(client, monster, type);
    }

    private bool IsImmune(Client client, WorldObject monster, ResistType type)
    {
        if (_monsterResists.TryGetResist(
                monster.NPCCode, client.Game.Difficulty, type, monster.MonsterEnchantments, out var resist))
        {
            return resist >= MonsterResistTable.ImmuneAt;
        }

        // Without the extracted table, the one case that was found the hard way.
        return type == ResistType.Lightning && monster.NPCCode == NPCCode.BurningSoul;
    }

    private async Task<bool> SorceressAssist(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes)
    {
        var enemies = Prioritize(NPCHelpers.GetNearbyNPCs(client, player.Location, 30, 40).ToList(), priorityCodes);

        var me = client.Game.Me;
        if (me.Mana > 10
            && me.HasSkill(Skill.FrozenArmor)
            && !me.HasSkill(Skill.ShiverArmor)
            && !client.Game.Me.Effects.ContainsKey(EntityEffect.Frozenarmor))
        {
            _logger.LogInformation("Casting {Skill}", Skill.FrozenArmor);
            client.Game.UseRightHandSkillOnLocation(Skill.FrozenArmor, client.Game.Me.Location);
            await Task.Delay(100);
            return true;
        }
        else if (me.Mana > 10
                && me.HasSkill(Skill.ShiverArmor)
                && !client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor))
        {
            _logger.LogInformation("Casting {Skill}", Skill.ShiverArmor);
            client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
            await Task.Delay(100);
            return true;
        }

        var nearest = enemies.FirstOrDefault();
        if (nearest == null)
        {
            return true;
        }

        if (me.HasSkill(Skill.Blizzard) && !me.HasSkill(Skill.FrozenOrb) && me.Mana > 35)
        {
            // Said out loud, like every other class does. Without it a cold sorceress kills all game
            // and the log shows nothing, so she reads as idle to anyone counting attacks - which is
            // exactly how this one was written off as broken for a whole session.
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", me.Name, nearest.NPCCode, Skill.Blizzard);
            client.Game.UseRightHandSkillOnEntity(Skill.Blizzard, nearest);
            await Task.Delay(200);
        }
        else if (me.HasSkill(Skill.FrozenOrb) && me.Mana > 30)
        {
            // Static is lightning, so it does nothing at all to a lightning immune target - and
            // because it never takes their life down, the cold sorceress stayed in this branch
            // softening a burning soul that cannot be softened and never cast her orb. She logged
            // no attacks for a whole session while standing next to things only she could kill.
            var canSoften = me.Skills.GetValueOrDefault(Skill.StaticField) > 10
                && !IsImmune(client, nearest, ResistType.Lightning)
                && ClassHelpers.CanStaticEntity(client, nearest.LifePercentage);

            if (canSoften
                && nearest.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique)
                && nearest.LifePercentage > 20)
            {
                client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            }
            else if (canSoften && nearest.LifePercentage > 50)
            {
                client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            }
            else
            {
                _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", me.Name, nearest.NPCCode, Skill.FrozenOrb);
                client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, nearest);
            }
            
            await Task.Delay(200);
        }
        else if (me.Skills.GetValueOrDefault(Skill.Nova) >= 20 && me.Mana > 30)
        {
            if(nearest.Location.Distance(client.Game.Me.Location) > 10)
            {
                await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService, nearest, MovementHelpers.PreferredMovement(client.Game));
            }
            if (me.Skills.GetValueOrDefault(Skill.StaticField) > 10 && nearest.LifePercentage > 50 && ClassHelpers.CanStaticEntity(client, nearest.LifePercentage))
            {
                client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            }
            else
            {
                client.Game.RepeatRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
            }

            await Task.Delay(200);
        }
        else if (me.HasSkill(Skill.FireBolt)
            && !me.HasSkill(Skill.IceBolt)
            && me.Attributes[Attribute.Level] < 10
            && me.Mana > 5)
        {
            client.Game.UseRightHandSkillOnEntity(Skill.FireBolt, nearest);
            await Task.Delay(200);
        }
        else if (me.HasSkill(Skill.IceBlast)
            && me.Mana > 10)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", me.Name, nearest.NPCCode, Skill.IceBlast);
            client.Game.UseRightHandSkillOnEntity(Skill.IceBlast, nearest);
            await Task.Delay(200);
        }
        else if (me.Attributes[Attribute.Level] < 10 && client.Game.Area < Area.CowLevel)
        {
            await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService, nearest, MovementMode.Walking);
            client.Game.UseRightHandSkillOnEntity(Skill.Attack, nearest);
            await Task.Delay(200);
        }
        else
        {
            // Every branch above declined, so this sorceress stands next to something and does
            // nothing at all - which is how one of ours went a whole session without a single
            // attack. Say which skills she actually has so the gap is visible rather than silent.
            _logger.LogWarning("{Character} cast nothing at {NPCCode}: mana {Mana}, orb {Orb}, blizzard {Blizzard}, glacial {Glacial}, iceblast {IceBlast}, nova {Nova}, static {Static}",
                me.Name, nearest.NPCCode, me.Mana,
                me.Skills.GetValueOrDefault(Skill.FrozenOrb),
                me.Skills.GetValueOrDefault(Skill.Blizzard),
                me.Skills.GetValueOrDefault(Skill.GlacialSpike),
                me.Skills.GetValueOrDefault(Skill.IceBlast),
                me.Skills.GetValueOrDefault(Skill.Nova),
                me.Skills.GetValueOrDefault(Skill.StaticField));
            await Task.Delay(200);
        }

        return true;
    }

    private async Task<bool> PaladinAssist(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes, Point focus)
    {
        var enemies = Prioritize(NPCHelpers.GetNearbyNPCs(client, focus ?? player.Location, 30, 20).ToList(), priorityCodes);

        var me = client.Game.Me;
        if (me.Mana > 20 && me.HasSkill(Skill.HolyShield) && !client.Game.Me.Effects.ContainsKey(EntityEffect.Holyshield))
        {
            _logger.LogInformation("Casting {Skill}", Skill.HolyShield);
            client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
            await Task.Delay(100);
            return true;
        }

        var conviction = me.Skills.GetValueOrDefault(Skill.Conviction);
        // Salvation only against a Conviction aura on the party. A lightning enchanted multishot
        // boss used to switch him to it as well, and with the amazon gone that left a party doing
        // no damage at all; that boss is the necromancer's Life Tap target instead.
        if (me.HasSkill(Skill.Salvation)
            && client.Game.Players.Any(p => p.Effects.ContainsKey(EntityEffect.Convicted)))
        {
            if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) || currentSkill != Skill.Salvation)
            {
                _logger.LogInformation("Changing to {Skill} due to a convicted player", Skill.Salvation);
                client.Game.ChangeSkill(Skill.Salvation, Hand.Right);
            }
        }
        else if (player.Class == CharacterClass.Sorceress && me.HasSkill(Skill.Conviction) && me.Skills.GetValueOrDefault(Skill.BlessedHammer) < 10)
        {
            if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) || currentSkill != Skill.Conviction)
            {
                _logger.LogInformation("Changing to {Skill}", Skill.Conviction);
                client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
            }
        }
        else if (me.HasSkill(Skill.Might) || me.HasSkill(Skill.Concentration) || me.HasSkill(Skill.Fanaticism))
        {
            var damageSkill = me.HasSkill(Skill.Fanaticism) ? Skill.Fanaticism : me.HasSkill(Skill.Concentration) ? Skill.Concentration : Skill.Might;
            if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) || currentSkill != damageSkill)
            {
                _logger.LogInformation("Changing from {CurrentSkill} to {DamageSkill}", currentSkill, damageSkill);
                client.Game.ChangeSkill(damageSkill, Hand.Right);
            }
        }

        var nearest = enemies.FirstOrDefault();
        if (nearest == null)
        {
            return true;
        }

        if (me.Attributes[Attribute.Level] < 25
            && client.Game.Difficulty == Difficulty.Normal
            && client.Game.Area != Area.CowLevel)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.Attack);
            await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService, nearest, MovementMode.Walking);
            client.Game.LeftHandSkillHoldOnEntity(Skill.Attack, nearest);
            await Task.Delay(200);
        }
        // Salvation is an aura like the others: with it up against a lightning enchanted multishot
        // boss he stopped hammering altogether and the party stood without damage.
        else if (me.ActiveSkills.TryGetValue(Hand.Right, out var rightSkill)
            && (rightSkill == Skill.Fanaticism || rightSkill == Skill.Concentration || rightSkill == Skill.Might || rightSkill == Skill.Salvation)
            && me.Skills.GetValueOrDefault(Skill.BlessedHammer) >= 20)
        {
            if (nearest.Location.Distance(client.Game.Me.Location) > 15)
            {
                var goalLocation = await OpenGroundNear(client, client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 4));
                if (client.Game.Me.HasSkill(Skill.Vigor))
                {
                    client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                }

                // Bounded: a walk toward a boss standing on lava never arrives, and unbounded it
                // held this loop past the seal timeout.
                var pathNearest = await _pathingService.GetPathToLocation(client.Game, goalLocation, MovementMode.Walking);
                var walkCancel = new System.Threading.CancellationTokenSource();
                walkCancel.CancelAfter(1500);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking, walkCancel.Token))
                {
                    _logger.LogWarning("Walking to Nearest failed at {Location}", client.Game.Me.Location);
                }
                client.Game.ChangeSkill(rightSkill, Hand.Right);
            }

            client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
        }

        return true;
    }

    /// <summary>
    /// Hammers spiral outwards from the caster and a wall swallows the half that meets it; a
    /// spot with walkable ground on every side lets the whole spiral work. Shifts the goal three
    /// units away from any blocked side.
    /// </summary>
    private async Task<Point> OpenGroundNear(Client client, Point goal)
    {
        var shifted = goal;
        foreach (var (dx, dy) in new (short, short)[] { (HammerClearance, 0), (-HammerClearance, 0), (0, HammerClearance), (0, -HammerClearance) })
        {
            var side = goal.Add(dx, dy);
            if (!await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, client.Game.Area, side))
            {
                shifted = shifted.Add((short)-dx, (short)-dy);
            }
        }

        return shifted;
    }

    private const short HammerClearance = 3;

    private async Task<bool> NecromancerAssist(Client client, Player player, Point focus)
    {
        var me = client.Game.Me;
        var acted = false;
        if (me.Mana > 20 && me.HasSkill(Skill.BoneArmor) && !client.Game.Me.Effects.ContainsKey(EntityEffect.Bonearmor))
        {
            _logger.LogInformation("Casting {Skill}", Skill.BoneArmor);
            client.Game.UseRightHandSkillOnLocation(Skill.BoneArmor, client.Game.Me.Location);
            await Task.Delay(100);
            acted = true;
        }

        if (me.Mana > 40
            && me.HasSkill(Skill.Bloodgolem)
            && !me.Summons.Exists(s => s.NPCCode == NPCCode.BloodGolem))
        {
            _logger.LogInformation("Summoning {NPCCode}", NPCCode.BloodGolem);
            client.Game.UseRightHandSkillOnLocation(Skill.Bloodgolem, client.Game.Me.Location);
            await Task.Delay(200);
            acted = true;
        }

        if (me.Mana > 20
            && me.HasSkill(Skill.ClayGolem)
            && !me.HasSkill(Skill.Bloodgolem)
            && !me.Summons.Exists(s => s.NPCCode == NPCCode.ClayGolem))
        {
            _logger.LogInformation("Summoning {NPCCode}", NPCCode.ClayGolem);
            client.Game.UseRightHandSkillOnLocation(Skill.ClayGolem, client.Game.Me.Location);
            await Task.Delay(200);
            acted = true;
        }

        if (await CloseOnFocus(client, focus, NecromancerFocusReach))
        {
            return true;
        }

        var anchor = focus != null ? me.Location : player.Location;
        var enemies = NPCHelpers.GetNearbyNPCs(client, focus ?? player.Location, 10, 30).ToList();
        var nearest = enemies.FirstOrDefault();
        if (nearest == null)
        {
            if (!acted)
            {
                await IdleReposition(client, anchor);
            }

            return true;
        }

        if (me.Mana > 10
            && me.Attributes[Attribute.Level] < 10
            && me.HasSkill(Skill.Skeletonraise)
            && !me.Summons.Exists(s => s.NPCCode == NPCCode.Skeleton))
        {
            _logger.LogInformation("Summoning {NPCCode}", NPCCode.Skeleton);
            List<WorldObject> corpses = NPCHelpers.GetNearbyCorpses(client, nearest.Location, 1);
            var corpse = corpses.FirstOrDefault();
            if (corpses != null)
            {
                client.Game.UseRightHandSkillOnEntity(Skill.Skeletonraise, corpse);
                await Task.Delay(200);
                return true;
            }
        }

        // Life Tap on what the barbarian is whirling heals him for half of every hit he lands, which
        // is the one thing that offsets a reflected hit. It goes on the monster nearest him, over an
        // Amplify if need be; Amplify is for the packs he is not in.
        var barbarian = client.Game.Players.FirstOrDefault(p => p.Class == CharacterClass.Barbarian && p.Location != null && p.Area == client.Game.Area);
        var nearBarbarian = barbarian == null
            ? []
            : enemies.Where(e => !e.Effects.Contains(EntityEffect.Lifetap) && e.NPCCode != NPCCode.Diablo && e.Location.Distance(barbarian.Location) < 10)
                .OrderBy(e => e.Location.Distance(barbarian.Location))
                .ToList();
        // A lightning enchanted boss with multiple shot is the other thing that kills a whirling
        // barbarian; tapped, every hit on it heals him. Elsewhere the knights make Life Tap the
        // Sanctuary's default on his pack.
        var inSanctuary = client.Game.Area == Area.ChaosSanctuary;
        var tapTarget = nearBarbarian.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)
                && e.MonsterEnchantments.Contains(MonsterEnchantment.MultiShot))
            ?? (inSanctuary
                ? nearBarbarian.FirstOrDefault() ?? enemies.FirstOrDefault(e => !e.Effects.Contains(EntityEffect.Lifetap) && e.NPCCode != NPCCode.Diablo)
                : null);
        // Amplify raises what he deals, and Iron Maiden returns what he deals; an amplified monster
        // in his whirl is a doubled reflection. In the Sanctuary it goes on the far pack only, never
        // on the knights he chases, and a monster carries one curse, so Life Tap on anything that
        // reaches him replaces the Amplify it had. Everywhere else it is the right curse.
        var cursableEnemy = enemies.FirstOrDefault(e => !e.Effects.Contains(EntityEffect.Lifetap)
            && !e.Effects.Contains(EntityEffect.Amplifydamage)
            && (!inSanctuary || (!CurseCasters.Contains(e.NPCCode)
                && (barbarian == null || e.Location.Distance(barbarian.Location) > AmplifyKeepOutRadius))));
        // Diablo comes without knights, so nothing reflects the barbarian's hits there, and doubled
        // physical damage on him shortens the fight more than the barbarian's leech from Life Tap.
        var unamplifiedDiablo = enemies.FirstOrDefault(e => e.NPCCode == NPCCode.Diablo && !e.Effects.Contains(EntityEffect.Amplifydamage));
        if (me.HasSkill(Skill.AmplifyDamage)
            && me.Mana > 5
            && unamplifiedDiablo != null)
        {
            _logger.LogInformation("{Character} casting {Skill} on {NPCCode}", me.Name, Skill.AmplifyDamage, unamplifiedDiablo.NPCCode);
            client.Game.UseRightHandSkillOnEntity(Skill.AmplifyDamage, unamplifiedDiablo);
            await Task.Delay(200);
            acted = true;
        }
        else if (me.HasSkill(Skill.LifeTap)
            && tapTarget != null
            && me.Mana > 15)
        {
            _logger.LogInformation("{Character} casting {Skill} on {NPCCode} {Distance} from the barbarian", me.Name, Skill.LifeTap, tapTarget.NPCCode,
                barbarian == null ? "?" : ((int)tapTarget.Location.Distance(barbarian.Location)).ToString());
            client.Game.UseRightHandSkillOnEntity(Skill.LifeTap, tapTarget);
            await Task.Delay(200);
            acted = true;
        }
        else if (me.HasSkill(Skill.AmplifyDamage)
            && me.Mana > 5
            && cursableEnemy != null)
        {
            _logger.LogInformation("{Character} casting {Skill} on {NPCCode}", me.Name, Skill.AmplifyDamage, cursableEnemy.NPCCode);
            client.Game.UseRightHandSkillOnEntity(Skill.AmplifyDamage, cursableEnemy);
            await Task.Delay(200);
            acted = true;
        }
        else if (me.HasSkill(Skill.CorpseExplosion) && me.Mana > 20)
        {
            // With everything in reach cursed the curses are done; the corpses go off next to the
            // boss first, then under the thickest of what is left. Outside the Sanctuary the blast
            // is only worth it on an amplified target.
            var blastTargets = (inSanctuary ? enemies : enemies.Where(e => e.Effects.Contains(EntityEffect.Amplifydamage)))
                .OrderByDescending(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique))
                .ThenByDescending(e => enemies.Count(o => o.Location.Distance(e.Location) < CorpseExplosionRadius))
                .ToList();
            foreach (var enemy in blastTargets)
            {
                var corpse = NPCHelpers.GetNearbyCorpses(client, enemy.Location, 1).FirstOrDefault();
                if (corpse == null)
                {
                    continue;
                }

                _logger.LogInformation("{Character} casting {Skill} beside {NPCCode}", me.Name, Skill.CorpseExplosion, enemy.NPCCode);
                client.Game.UseRightHandSkillOnEntity(Skill.CorpseExplosion, corpse);
                await Task.Delay(200);
                acted = true;
                break;
            }
        }

        if (!acted)
        {
            await IdleReposition(client, anchor);
        }

        return true;
    }

    private async Task<bool> BarbarianAssist(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes, Point focus)
    {
        var me = client.Game.Me;
        await ClassHelpers.CastAllShouts(client);
        if (await CloseOnFocus(client, focus, BarbarianFocusReach))
        {
            return true;
        }

        var anchor = focus != null ? me.Location : player.Location;

        var enemies = Prioritize(NPCHelpers.GetNearbyNPCs(client, focus ?? player.Location, 10, 20).ToList(), priorityCodes);
        // In the Sanctuary there is always a knight close enough to curse him, so distance is no
        // guard. The knights go first, then whatever is left.
        // Knights, then the boss, then the rest: Infector stood behind a candle holder with the
        // amazon and the paladin missing him while the barbarian whirled trash beside them.
        var nearest = enemies.FirstOrDefault(e => CurseCasters.Contains(e.NPCCode))
            ?? enemies.FirstOrDefault(e => (e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique) || e.NPCCode == NPCCode.Diablo) && e.Location.Distance(me.Location) < BossChaseRange)
            ?? enemies.FirstOrDefault();
        if (nearest == null)
        {
            var onMe = NPCHelpers.GetNearbyNPCs(client, me.Location, 10, SurroundedRadius).ToList();
            if (onMe.Count >= SurroundedCount
                && me.HasSkill(Skill.Whirlwind)
                && me.Mana > 30
                && await WhirlWindThroughPack(client, onMe))
            {
                return true;
            }

            await IdleReposition(client, anchor);
            return true;
        }

        if (me.Attributes[Attribute.Level] < 30 && client.Game.Difficulty > Difficulty.Normal)
        {
            return true;
        }
        else if (me.Attributes[Attribute.Level] < 26 && client.Game.Difficulty == Difficulty.Normal && client.Game.Area == Area.CowLevel)
        {
            return true;
        }

        // Whirlwind is the barbarian's damage and leeches its own mana back when it connects, so it
        // is the first choice against anything. The skills below are what is left when it cannot be
        // used at all: too low a level, or mana already drained by a bad stretch.
        var pack = enemies.Where(e => e.Location.Distance(nearest.Location) < WhirlwindPackRadius).ToList();
        var canWhirlwind = me.HasSkill(Skill.Whirlwind)
            && ((me.Attributes[Attribute.Level] > 33 && client.Game.Difficulty != Difficulty.Normal) || me.Attributes[Attribute.Level] > 40);

        if (canWhirlwind)
        {
            // Whirlwind is the whole attack once he has it; Concentrate is a levelling skill and a
            // held swing under Iron Maiden killed him. Short whirls, never started while cursed
            // (checked at the cast), and out of mana he drinks or stands back.
            var aimPack = pack
                .Where(e => me.Location.Distance(e.Location) <= MaxWhirlwindAimDistance)
                .OrderBy(e => me.Location.Distance(e.Location))
                .Take(MaxWhirlwindTargets)
                .ToList();

            // A whirl started at 58% life through three Venom Lords, cursed on the way, ended at 154
            // life. Below this he stands back until the potions have him whole enough to take one.
            if (me.Life < me.MaxLife * BarbarianWhirlMinLife)
            {
                _logger.LogInformation("{Character} standing back at {Life} of {MaxLife} life", me.Name, me.Life, me.MaxLife);
                await IdleReposition(client, anchor);
                return true;
            }

            if (me.Mana <= 30)
            {
                if (client.Game.UseManaPotion())
                {
                    await Task.Delay(300);
                }
                else
                {
                    _logger.LogInformation("{Character} out of mana and mana potions, standing back", me.Name);
                    await IdleReposition(client, anchor);
                }

                return true;
            }

            if (aimPack.Count > 0 && await WhirlWindThroughPack(client, aimPack))
            {
                return true;
            }

            // Nothing within whirl reach but a target in sight: walk to it. He stood idle for
            // twenty-five seconds with De Seis fifteen units away because the approach only ever
            // happened once something was already within eight of him.
            if (nearest.Location.Distance(me.Location) < BossChaseRange)
            {
                var approach = me.Location.GetPointBeforePointInSameDirection(nearest.Location, WhirlwindApproachDistance - 2);
                var cancel = new System.Threading.CancellationTokenSource();
                cancel.CancelAfter(1200);
                await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, approach, MovementMode.Walking, cancel.Token);
                return true;
            }

            await IdleReposition(client, anchor);
            return true;
        }

        if (me.HasSkill(Skill.Concentrate) && me.Mana > 5)
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.Concentrate);
            await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService, nearest, MovementMode.Walking);
            client.Game.RepeatRightHandSkillOnEntity(Skill.Concentrate, nearest);
            await Task.Delay(200);
        }
        else
        {
            _logger.LogInformation("{Character} attacking {NPCCode} with {Skill}", client.Game.Me.Name, nearest.NPCCode, Skill.Attack);
            await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService, nearest, MovementMode.Walking);
            client.Game.UseRightHandSkillOnEntity(Skill.Attack, nearest);
            await Task.Delay(200);
        }

        return true;
    }

    /// <summary>
    /// Cuts through the middle of a pack and out the far side. Aiming at one monster and stopping
    /// six units past it sweeps almost no ground, so a target that steps aside is missed and the
    /// mana is spent for nothing; crossing the pack keeps enough of them on the path to leech the
    /// mana back.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> _ironMaidenSeen = new();

    /// <summary>
    /// The curse is held for a few seconds after it was last seen: a barbarian started a whirl
    /// between two readings that both had Iron Maiden on him and went from 2034 to 292 in a second.
    /// </summary>
    private static readonly TimeSpan IronMaidenMemory = TimeSpan.FromSeconds(3);

    public bool IsUnderIronMaiden(Client client)
    {
        var me = client.Game.Me;
        if (me.Effects.ContainsKey(EntityEffect.Ironmaiden))
        {
            _ironMaidenSeen[me.Id] = DateTime.Now;
            return true;
        }

        return _ironMaidenSeen.TryGetValue(me.Id, out var seen) && DateTime.Now - seen < IronMaidenMemory;
    }

    private async Task<bool> WhirlWindThroughPack(Client client, List<WorldObject> pack)
    {
        var me = client.Game.Me;

        var nearest = pack.OrderBy(e => me.Location.Distance(e.Location)).First();
        if (me.Location.Distance(nearest.Location) > WhirlwindApproachDistance)
        {
            var approach = me.Location.GetPointBeforePointInSameDirection(nearest.Location, WhirlwindApproachDistance);
            await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, approach, MovementMode.Walking);
        }

        var aim = WhirlwindPlanner.Aim(me.Location, pack.Select(e => e.Location).ToList());
        var target = aim.Target;
        if (target == null)
        {
            return false;
        }

        // Iron Maiden returns every hit of the whirl doubled, and a whirl cannot be stopped once it
        // has started. The knights cast it whenever they like, so their distance says nothing; what
        // has to be true is that he is not cursed at the moment the whirl begins.
        // Iron Maiden only. Holding for Amplify Damage as well kept him out of a whole top seal
        // fight (the knights recast it faster than it expires), the pack stood untouched on the
        // party and four of five left the game.
        if (IsUnderIronMaiden(client))
        {
            _logger.LogInformation("{Character} cursed, not starting a whirl", me.Name);
            return false;
        }

        _logger.LogInformation("{Character} attacking {Count} {NPCCode} with {Skill} towards {Target} covering {Covered}",
            me.Name, pack.Count, pack[0].NPCCode, Skill.Whirlwind, target, aim.Covered);

        var distance = me.Location.Distance(target);
        var started = false;
        var cursed = false;
        var lastCast = DateTime.MinValue;
        var manaBefore = me.Mana;
        var result = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            var whirling = me.Effects.ContainsKey(EntityEffect.Skillmove)
                && me.Effects.ContainsKey(EntityEffect.Uninterruptable);
            if (whirling)
            {
                started = true;
            }
            else if (started)
            {
                return true;
            }

            if (me.Location.Distance(target) <= WhirlwindArrivalDistance)
            {
                return true;
            }

            if (!whirling && !started && IsUnderIronMaiden(client))
            {
                cursed = true;
                return true;
            }

            if (!whirling && DateTime.Now.Subtract(lastCast) >= WhirlwindRetryInterval)
            {
                client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, target);
                lastCast = DateTime.Now;
            }

            return false;
        }, TimeSpan.FromSeconds(distance * 0.2 + 1));

        if (cursed)
        {
            _logger.LogInformation("{Character} cursed before the whirl started, standing down", me.Name);
            return false;
        }

        var manaBurn = pack.Any(e => e.MonsterEnchantments.Contains(MonsterEnchantment.ManaBurn))
            ? ", mana burn"
            : string.Empty;
        _logger.LogInformation("{Character} whirled through {Count} over {Distance:0} units, mana {Before} to {After}, delta {Delta}{ManaBurn}, covering {Covered}",
            me.Name, pack.Count, distance, manaBefore, me.Mana, me.Mana - manaBefore, manaBurn, aim.Covered);

        return result;
    }
}
