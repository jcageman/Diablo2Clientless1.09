using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Trace.Query;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

/// <summary>Skill moves, attacks into walls or at nothing, the mana ledger, monster placement per motion model and finding a game.</summary>
public class CombatTests
{
    private const uint Monster = 0x40;

    private static byte[] ClearEffects(uint id)
    {
        var packet = new byte[26];
        packet[0] = 0x12;
        packet[1] = (byte)EntityType.Player;
        BitConverter.GetBytes(id).CopyTo(packet, 2);
        return packet;
    }

    private static byte[] AtPoint(ushort x, ushort y) => SyntheticGame.Location(0x0F, x, y);

    private static HandTrace Fighter(CharacterClass characterClass, Skill skill, ushort monsterY = 1998)
        => new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: characterClass)
            .OutOfTown(1)
            .Received(1.5, SyntheticGame.SetSkill(1, Hand.Right, skill))
            .Received(1.6, SyntheticGame.AssignNpc(Monster, (NPCCode)82, 5160, monsterY))
            .Received(1.7, SyntheticGame.LifeMana(1000, 200, 5160, 2010));

    /// <summary>Four whirls the server starts and ends where they began, hitting nothing, each costing 35 mana.</summary>
    private static string Whirls(TempRoot root)
    {
        var folder = Game1.Folder(root);
        var trace = Fighter(CharacterClass.Barbarian, Skill.Whirlwind);
        for (var i = 0; i < 4; i++)
        {
            var t = 2 + i;
            trace.Sent(t, AtPoint(5160, 1998))
                .Sent(t + 0.1, AtPoint(5160, 1998))
                .Received(t + 0.15, SyntheticGame.AddEffect(EntityType.Player, 1, EntityEffect.Skillmove))
                .Received(t + 0.3, ClearEffects(1))
                .Received(t + 0.4, SyntheticGame.LifeMana(1000, 200 - (35 * (i + 1)), (ushort)(5160 + (i % 2)), 2010));
        }

        trace.WriteTo(folder);
        return folder;
    }

    private static CombatObserver Combat(string folder)
        => GameQuery.Open(folder).ReplayAll(["client1"], TimeSpan.MaxValue, () => new CombatObserver(), o => [o])[0].Observers;

    [Fact]
    public void ASkillMoveRunsFromTheSkillmoveEffectToItsRemovalWithTheServersPositionsAndItsCost()
    {
        using var root = new TempRoot();
        var combat = Combat(Whirls(root));

        Assert.Equal(4, combat.Moves.Count);
        var first = combat.View(combat.Moves[0]);
        Assert.Equal(nameof(Skill.Whirlwind), first.Move.Skill);
        Assert.Equal(new Point(5160, 1998), first.Move.Asked);
        Assert.Equal(2, first.Move.Asks);
        Assert.Equal(TimeSpan.FromSeconds(2), first.Move.AskedAt);
        Assert.Equal(TimeSpan.FromSeconds(2.15), first.Move.Start);
        Assert.Equal(TimeSpan.FromSeconds(2.3), first.Move.End);
        Assert.True(first.Moved <= 1);
        Assert.Empty(first.Hits);
        Assert.Equal(35, first.ManaSpent);
        Assert.All(combat.Moves.Select(combat.View), v => Assert.Equal(35, v.ManaSpent));
    }

    [Fact]
    public void AMoveStartedBeforeThePreviousOnesCostWasStatedStartsFromThatOnesManaAfter()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Fighter(CharacterClass.Barbarian, Skill.Whirlwind)
            .Sent(2, AtPoint(5160, 1998))
            .Received(2.05, SyntheticGame.AddEffect(EntityType.Player, 1, EntityEffect.Skillmove))
            .Received(2.2, ClearEffects(1))
            .Sent(2.25, AtPoint(5160, 1998))
            .Received(2.3, SyntheticGame.AddEffect(EntityType.Player, 1, EntityEffect.Skillmove))
            .Received(2.35, SyntheticGame.LifeMana(1000, 165, 5160, 2010))
            .Received(2.5, ClearEffects(1))
            .Received(2.75, SyntheticGame.LifeMana(1000, 130, 5160, 2010))
            .WriteTo(folder);
        var combat = Combat(folder);

        var views = combat.Moves.Select(combat.View).ToList();

        Assert.Equal([200, 165], views.Select(v => v.ManaBefore));
        Assert.Equal([165, 130], views.Select(v => v.ManaAfter));
    }

    [Fact]
    public void SkillMovesThatWentNowhereAreOneIncidentWithTheirCountAndMana()
    {
        using var root = new TempRoot();
        var found = Game1.Incidents(Whirls(root)).Found;

        var nowhere = Assert.Single(found, i => i.Kind == CombatCheck.NowhereKind);
        Assert.Equal(TimeSpan.FromSeconds(2.15), nowhere.Offset);
        Assert.Equal(TimeSpan.FromSeconds(5.3), nowhere.End);
        Assert.Contains("4 Whirlwind moves", nowhere.Detail, StringComparison.Ordinal);
        Assert.Contains("hit nothing", nowhere.Detail, StringComparison.Ordinal);
        Assert.Contains("140 mana spent on them", nowhere.Detail, StringComparison.Ordinal);
        Assert.Contains("--skill-moves", Query.Incidents.NextCommand("party1", nowhere), StringComparison.Ordinal);
    }

    [Fact]
    public void AWhirlThatHitsAMonsterAlongItsPathIsNotNowhere()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var trace = Fighter(CharacterClass.Barbarian, Skill.Whirlwind, monsterY: 2009);
        for (var i = 0; i < 4; i++)
        {
            var t = 2 + i;
            trace.Sent(t, AtPoint(5160, 2005))
                .Received(t + 0.15, SyntheticGame.AddEffect(EntityType.Player, 1, EntityEffect.Skillmove))
                .Received(t + 0.2, SyntheticGame.NpcHit(Monster, (byte)(90 - i)))
                .Received(t + 0.3, ClearEffects(1))
                .Received(t + 0.4, SyntheticGame.LifeMana(1000, 150, 5160, 2010));
        }

        trace.WriteTo(folder);

        Assert.DoesNotContain(Game1.Incidents(folder).Found, i => i.Kind == CombatCheck.NowhereKind);
        Assert.All(Combat(folder).Moves.Select(m => Combat(folder).View(m)), v => Assert.Single(v.Hits));
    }

    /// <summary>
    /// A Blood Moor 40 subtiles square from (5140, 1980), walkable but for the row y 2002: by default a wall (7: blocks
    /// walking and sight), or with 1 a ledge or water that blocks walking only.
    /// </summary>
    private static MapSource WallAcross(int value = 7)
    {
        var rows = Enumerable.Range(0, 40).Select(r => Enumerable.Repeat(r == 22 ? value : 0, 40).ToArray()).ToArray();
        var json = JsonSerializer.Serialize(new { levelOrigin = new { x = 5140, y = 1980 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });
        return new MapSource("http://map.invalid", null, _ => json);
    }

    /// <summary>A sorceress at (5160, 2010) casting at a point short of a monster at (5160, 1995) behind the wall, with a hit on it the given time after each cast.</summary>
    private static string CastsAcrossTheWall(TempRoot root, Skill skill, double? hitAfter)
    {
        var folder = Game1.Folder(root);
        var trace = Fighter(CharacterClass.Sorceress, skill, monsterY: 1995);
        for (var i = 0; i < 4; i++)
        {
            var t = 2 + (i * 2.5);
            trace.Sent(t, SyntheticGame.Location(0x0C, 5161, 2000));
            if (hitAfter is { } after)
            {
                trace.Received(t + after, SyntheticGame.NpcHit(Monster, (byte)(90 - i)));
            }
        }

        trace.Ticks(12, 14).WriteTo(folder);
        return folder;
    }

    [Fact]
    public void CastsAtAPointAreJudgedByTheMonsterInTheirDirectionAndFlaggedWhenTheLineCrossesAWallAndNothingIsHit()
    {
        using var root = new TempRoot();
        var folder = CastsAcrossTheWall(root, Skill.FrozenOrb, hitAfter: null);

        var wall = Assert.Single(Game1.Incidents(folder, maps: WallAcross()).Found, i => i.Kind == CombatCheck.WallKind);

        Assert.Equal(TimeSpan.FromSeconds(2), wall.Offset);
        Assert.Contains("4 FrozenOrb casts", wall.Detail, StringComparison.Ordinal);
        Assert.Contains($"(x4 at (5160, 1995))", wall.Detail, StringComparison.Ordinal);
        Assert.Contains("(5160, 2002)", wall.Detail, StringComparison.Ordinal);
        Assert.StartsWith("Trace tiles", Query.Incidents.NextCommand("party1", wall), StringComparison.Ordinal);
    }

    [Fact]
    public void MissilesCrossGroundThatBlocksOnlyWalkingAndTilesMarksItApart()
    {
        using var root = new TempRoot();
        var folder = CastsAcrossTheWall(root, Skill.FrozenOrb, hitAfter: null);

        Assert.DoesNotContain(Game1.Incidents(folder, maps: WallAcross(1)).Found, i => i.Kind == CombatCheck.WallKind);
        var grid = WallAcross(1).Grid(1, Difficulty.Normal, Core.D2GS.Act.Area.BloodMoor);
        Assert.Equal('~', grid.Ground(5160, 2002));
        Assert.False(grid.BlocksSight(5160, 2002));
        Assert.Equal('#', WallAcross().Grid(1, Difficulty.Normal, Core.D2GS.Act.Area.BloodMoor).Ground(5160, 2002));
        Assert.True(MapCollision.BlocksSight(7) && MapCollision.BlocksSight(23) && MapCollision.BlocksSight(3));
        Assert.False(MapCollision.BlocksSight(1) || MapCollision.BlocksSight(17) || MapCollision.BlocksSight(5) || MapCollision.BlocksSight(0));
    }

    [Fact]
    public void AWallGroupEndsAtACastThatLanded()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var trace = Fighter(CharacterClass.Sorceress, Skill.IceBolt, monsterY: 1995);
        foreach (var t in new[] { 2.0, 2.3, 2.6, 2.9 })
        {
            trace.Sent(t, SyntheticGame.Location(0x0C, 5161, 2000));
        }

        trace.Sent(4.3, SyntheticGame.Location(0x0C, 5161, 2000)).Received(4.4, SyntheticGame.NpcHit(Monster, 90));
        foreach (var t in new[] { 6.0, 6.5, 7.0, 7.5 })
        {
            trace.Sent(t, SyntheticGame.Location(0x0C, 5161, 2000));
        }

        trace.Ticks(10, 12).WriteTo(folder);

        var walls = Game1.Incidents(folder, maps: WallAcross()).Found.Where(i => i.Kind == CombatCheck.WallKind).ToList();
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)], walls.Select(w => w.Offset));
        Assert.All(walls, w => Assert.Contains("4 IceBolt casts", w.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public void SkillMovesAreNotJudgedAsAttacksIntoAWall()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var trace = Fighter(CharacterClass.Barbarian, Skill.Whirlwind, monsterY: 1995);
        for (var i = 0; i < 6; i++)
        {
            trace.Sent(2 + (i * 0.5), SyntheticGame.Location(0x0F, 5161, 1995));
        }

        trace.Ticks(6, 8).WriteTo(folder);

        Assert.DoesNotContain(Game1.Incidents(folder, maps: WallAcross()).Found, i => i.Kind == CombatCheck.WallKind);
    }

    [Fact]
    public void CastsWithAnUnknownSkillOrAtACorpseAreNotAttacks()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: CharacterClass.Barbarian)
            .OutOfTown(1)
            .Received(1.6, SyntheticGame.AssignNpc(Monster, (NPCCode)82, 5160, 1998))
            .Sent(1.7, AtPoint(5160, 1998))
            .Received(1.8, SyntheticGame.NpcState(Monster, EntityState.Dead, 5160, 1998, 0))
            .Received(2, SyntheticGame.SetSkill(1, Hand.Right, Skill.Whirlwind))
            .Sent(2.5, SyntheticGame.Unit(0x0D, EntityType.NPC, Monster))
            .Received(3, SyntheticGame.SetSkill(1, Hand.Right, Skill.FindItem))
            .Sent(3.5, SyntheticGame.Unit(0x0D, EntityType.NPC, Monster))
            .Sent(3.6, AtPoint(5160, 1998))
            .Ticks(4, 5)
            .WriteTo(folder);

        Assert.Empty(Combat(folder).Casts);
    }

    [Fact]
    public void AHitInsideTheSkillsWindowClearsTheCastAndTheWindowIsPerSkill()
    {
        using var root = new TempRoot();
        var orb = CastsAcrossTheWall(root, Skill.FrozenOrb, hitAfter: 1.6);
        Assert.DoesNotContain(Game1.Incidents(orb, maps: WallAcross()).Found, i => i.Kind is CombatCheck.WallKind or CombatCheck.NothingKind);

        using var other = new TempRoot();
        var bolt = CastsAcrossTheWall(other, Skill.IceBolt, hitAfter: 1.6);
        Assert.Contains(Game1.Incidents(bolt, maps: WallAcross()).Found, i => i.Kind == CombatCheck.WallKind && i.Detail.Contains("4 IceBolt casts", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAMapAttacksThatHitNothingAreStillCaught()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var trace = Fighter(CharacterClass.Sorceress, Skill.IceBolt, monsterY: 1995);
        for (var i = 0; i < 10; i++)
        {
            trace.Sent(2 + (i * 0.4), SyntheticGame.Location(0x0C, 5160, 1995));
        }

        trace.Ticks(7, 8).WriteTo(folder);

        var (found, notes) = Game1.Incidents(folder);
        var nothing = Assert.Single(found, i => i.Kind == CombatCheck.NothingKind);
        Assert.Contains("10 IceBolt casts", nothing.Detail, StringComparison.Ordinal);
        Assert.Contains("15.0 away", nothing.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(found, i => i.Kind == CombatCheck.WallKind);
    }

    [Fact]
    public void APointCastsHitsAreCreditedToItsSkill()
    {
        using var root = new TempRoot();
        var folder = CastsAcrossTheWall(root, Skill.FrozenOrb, hitAfter: 1.6);

        var fights = GameQuery.Open(folder).ReplayAll(["client1"], TimeSpan.MaxValue, () => new FightObserver(), o => [o])[0].Observers;

        Assert.Equal(4, fights.Hits.Count(h => h.Skill == nameof(Skill.FrozenOrb)));
    }

    private static byte[] Drink(uint id) => [0x26, .. BitConverter.GetBytes(id), 0, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] Used(uint id) => [0x3F, 0x00, .. BitConverter.GetBytes(id), 0x00, 0x00];

    [Fact]
    public void ManaFallsGoToTheCastsBeforeThemAndRisesToAPotionRefillingOrRegeneration()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: CharacterClass.Barbarian)
            .OutOfTown(1)
            .Received(1.5, SyntheticGame.SetSkill(1, Hand.Right, Skill.Whirlwind))
            .Received(1.55, [0x1F, 0x09, .. BitConverter.GetBytes(400 << 8)])
            .Received(1.6, ItemPackets.Belt(Action.PutInBelt, 0x50, 0, 0, "mp5"))
            .Received(1.6, ItemPackets.Belt(Action.PutInBelt, 0x51, 1, 0, "mp5"))
            .Received(1.7, SyntheticGame.LifeMana(1000, 200, 5160, 2010))
            .Sent(2, AtPoint(5160, 1998))
            .Received(2.3, SyntheticGame.LifeMana(1000, 165, 5160, 2010))
            .Received(2.6, SyntheticGame.AssignNpc(0x41, (NPCCode)82, 5100, 1950))
            .Received(2.65, SyntheticGame.NpcHit(0x41, 80))
            .Received(2.7, SyntheticGame.LifeMana(1000, 166, 5160, 2010))
            .Sent(4, Drink(0x50))
            .Received(4.1, Used(0x50))
            .Received(4.5, SyntheticGame.LifeMana(1000, 186, 5160, 2010))
            .Sent(5, Drink(0x51))
            .Received(5.1, Used(0x51))
            .Received(9.5, SyntheticGame.LifeMana(1000, 300, 5160, 2010))
            .Received(20, SyntheticGame.LifeMana(1000, 280, 5160, 2010))
            .WriteTo(folder);

        var combat = new CombatObserver();
        var mana = new ManaObserver(TimeSpan.MinValue, TimeSpan.MaxValue, combat);
        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [combat, mana])?.Replayer.Dispose();
        mana.Attribute();
        var causes = mana.Changes.Select(c => (c.At.TotalSeconds, c.Delta, c.Causes.Single().Cause)).ToList();

        Assert.Equal(
        [
            (1.7, 100, ManaObserver.Unattributed),
            (2.3, -35, ManaObserver.OwnCast),
            (2.7, 1, ManaObserver.Regeneration),
            (4.5, 20, ManaObserver.Potions),
            (9.5, 114, ManaObserver.Potions),
            (20, -20, ManaObserver.Unattributed),
        ], causes);
        Assert.Equal(nameof(Skill.Whirlwind), mana.Changes[1].Causes[0].Detail);
        Assert.Equal(2, mana.Drinks.Count);
        Assert.True(mana.Drinks[1].WhileRefilling);
        Assert.Contains(mana.Report("client1", all: false), l => l.Contains("1 of them while an earlier one was still refilling", StringComparison.Ordinal));

        var wasted = Assert.Single(Game1.Replayed(folder, "client1", new PotionWasteCheck()));
        Assert.Contains("1000 ms after the previous mana potion", wasted.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnitsSectionPlacesMonstersByTheMotionModelAsked()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .OutOfTown(1)
            .Received(1.6, SyntheticGame.AssignNpc(Monster, (NPCCode)82, 5160, 1998))
            .Received(2, SyntheticGame.NpcMove(Monster, 5170, 1990))
            .Ticks(3, 4)
            .WriteTo(folder);
        var query = GameQuery.Open(folder);
        string UnitLine(string motion) => StateQuery.Render(query, "client1", TimeSpan.FromSeconds(2.5), "units", motion: motion).Split('\n').Single(l => l.Contains("\"0x40\"", StringComparison.Ordinal));

        Assert.Contains("\"at\":[5170,1990]", UnitLine(null), StringComparison.Ordinal);
        Assert.Contains("\"at\":[5160,1998]", UnitLine("anchored"), StringComparison.Ordinal);
        Assert.Contains("\"legacyAt\":[5170,1990]", UnitLine("anchored"), StringComparison.Ordinal);
        var server = UnitLine("server");
        Assert.Contains("\"at\":[5170,1990]", server, StringComparison.Ordinal);
        Assert.Contains("\"stated\":\"MoveToPoint (0x67) 0.00s before\"", server, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => StateQuery.Render(query, "client1", TimeSpan.FromSeconds(2.5), "units", motion: "teleported"));
    }

    [Fact]
    public void AGameIsFoundByAClockTimeInItsWindowAndItsClientsNameTheirFilesWhenRenamed()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 60, 5057, 1902).WriteTo(folder, "client1-2");
        new HandTrace("client2", "party1", Game1.Start.AddSeconds(2)).Join(2).Ticks(1, 30, 5057, 1902).WriteTo(folder);
        var game = Assert.Single(GameTrace.GamesUnder(Game1.Date(root)));

        Assert.Equal(["client1 (client1-2.d2trace)", "client2"], GameFinder.Clients(game));
        Assert.Equal(TimeSpan.Zero, GameFinder.DistanceTo(game, "12:00:30", TimeZoneInfo.Utc));
        Assert.Equal(TimeSpan.Zero, GameFinder.DistanceTo(game, "11:59:40", TimeZoneInfo.Utc));
        Assert.Equal(TimeSpan.Zero, GameFinder.DistanceTo(game, "2026-11-13 12:01:00", TimeZoneInfo.Utc));
        Assert.True(GameFinder.HasDate("2026-11-13 12:01:00"));
        Assert.False(GameFinder.HasDate("12:01:00"));
        Assert.Equal(TimeSpan.FromMinutes(9.5), GameFinder.DistanceTo(game, "11:50", TimeZoneInfo.Utc));
        Assert.True(GameFinder.DistanceTo(game, "2026-11-14 12:00:00", TimeZoneInfo.Utc) > TimeSpan.FromHours(23));
        Assert.Throws<ArgumentException>(() => GameFinder.DistanceTo(game, "noon", TimeZoneInfo.Utc));
    }
}
