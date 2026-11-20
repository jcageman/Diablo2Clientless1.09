using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Trace.Query;
using System;
using System.Linq;
using Xunit;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

/// <summary>Loot left on the ground and bosses a client stood by without attacking.</summary>
public class LootAndBossTests
{
    private const ushort Y = 2010;

    /// <summary>Joined in town, moved by the server into the Blood Moor at (<paramref name="x"/>, 2010) at 1 s, and back to town at 10 s.</summary>
    private static HandTrace Member(string label, uint id, ushort x, CharacterClass characterClass = CharacterClass.Sorceress)
        => new HandTrace(label, "party1", Game1.Start).Join(id, characterClass: characterClass)
            .Received(1, Packets.MapReveal(x, Y, Area.BloodMoor))
            .Received(1.01, SyntheticGame.Reassign(id, x, Y))
            .Ticks(2, 9, x, Y)
            .Received(10, SyntheticGame.Reassign(id, 5057, 1902))
            .Ticks(11, 12, 5057, 1902);

    /// <summary>
    /// Two members 15 apart see six drops: a magic ring nobody goes for (0x80), a rare one the second takes (0x81), a potion
    /// (0x82), a unique the server drops back after the first asks for it (0x83), a set ring a player dropped (0x84) and a
    /// magic ring 50 from both (0x85).
    /// </summary>
    private static string Drops(TempRoot root)
    {
        var folder = Game1.Folder(root);
        HandTrace Sees(HandTrace member) => member
            .Received(3, ItemPackets.Loot(0x80, QualityType.Magical, 5165, 2012))
            .Received(3, ItemPackets.Loot(0x81, QualityType.Rare, 5162, 2011))
            .Received(3, ItemPackets.OnGround(0x82, "hp5", 5163, 2012))
            .Received(3, ItemPackets.Loot(0x83, QualityType.Unique, 5164, 2009))
            .Received(3, ItemPackets.Loot(0x84, QualityType.Set, 5166, 2010, Action.DropToGround))
            .Received(3, ItemPackets.Loot(0x85, QualityType.Magical, 5160, 2060))
            .Received(4.05, ItemPackets.Removed(0x81))
            .Received(5.05, ItemPackets.Removed(0x83))
            .Received(5.06, ItemPackets.Loot(0x83, QualityType.Unique, 5164, 2009, Action.DropToGround));
        Sees(Member("client1", 1, 5160))
            .Sent(5, Packets.Pickup(0x83))
            .WriteTo(folder);
        Sees(Member("client2", 2, 5175))
            .Sent(4, Packets.Pickup(0x81))
            .Received(4.06, ItemPackets.Item(Action.PutInContainer, 0x81, "rin"))
            .WriteTo(folder);
        return folder;
    }

    [Fact]
    public void LootNobodyTookIsOneIncidentPerItemFromTheNearestMember()
    {
        using var root = new TempRoot();
        var left = Game1.Incidents(Drops(root)).Found.Where(i => i.Kind == LootLeftBehindCheck.Kind).ToList();

        Assert.Equal(2, left.Count);
        var ring = left.Single(i => i.Detail.Contains("0x80", StringComparison.Ordinal));
        Assert.Equal("client1", ring.Client);
        Assert.Equal(TimeSpan.FromSeconds(3), ring.Offset);
        Assert.Equal(TimeSpan.FromSeconds(10), ring.End);
        Assert.StartsWith("Ring (Magical) 0x80 dropped at (5165, 2012), 5 from client1, the nearest of the 2 clients that saw it drop", ring.Detail, StringComparison.Ordinal);
        Assert.Contains("nobody took it and no client asked to pick it up; client1 went to town (RogueEncampment) at 00:10.000", ring.Detail, StringComparison.Ordinal);
        var refused = left.Single(i => i.Detail.Contains("0x83", StringComparison.Ordinal));
        Assert.Contains("pick-up asked by client1 x1, and the server dropped it back on its spot after a pick-up", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AloneAClientReportsWhatItSawLeftButNotWhatWasTakenInItsSight()
    {
        using var root = new TempRoot();

        var left = Game1.Replayed(Drops(root), "client1", new LootLeftBehindCheck());

        Assert.Equal(["0x80", "0x83"], left.Select(i => i.Detail.Split(' ')[2]).Order());
    }

    private const uint Boss = 0x50;

    private static byte[] SuperUnique(uint id, NPCCode code, ushort x, ushort y)
    {
        var packet = SyntheticGame.AssignNpc(id, code, x, y);
        packet[37] = (byte)MonsterEnchantment.IsSuperUnique;
        return packet;
    }

    private static HandTrace Fighter(CharacterClass characterClass, Skill skill, byte[] boss)
        => new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: characterClass)
            .OutOfTown(1)
            .Received(1.5, SyntheticGame.SetSkill(1, Hand.Right, skill))
            .Received(2, boss)
            .Ticks(2, 20);

    [Fact]
    public void ABossStoodByWithoutAnAttackIsOneIncidentUntilTheAttack()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Fighter(CharacterClass.Amazon, Skill.MagicArrow, SyntheticGame.AssignNpc(Boss, NPCCode.Diablo, 5172, 2010))
            .Sent(14, SyntheticGame.Unit(0x0D, EntityType.NPC, Boss))
            .WriteTo(folder);

        var ignored = Assert.Single(Game1.Replayed(folder, "client1", new BossNotAttackedCheck()));

        Assert.Equal(BossNotAttackedCheck.Kind, ignored.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), ignored.Offset);
        Assert.Equal(TimeSpan.FromSeconds(14), ignored.End);
        Assert.StartsWith("no attack at the boss for 12.0s, out of town and alive: Diablo 0x50 12 away, life 100% -> 100%", ignored.Detail, StringComparison.Ordinal);
        Assert.EndsWith("no attack at anything else either; until it attacked Diablo 0x50", ignored.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A paladin hammering on its own spot 10 from a super unique (the spiral reaches it), an amazon shooting the boss's pack
    /// first, and an amazon by a plain monster.
    /// </summary>
    [Fact]
    public void ABossInAHammersSpiralABossFoughtLastOrAPlainMonsterIsNoIncident()
    {
        using var root = new TempRoot();
        var hammered = Fighter(CharacterClass.Paladin, Skill.BlessedHammer, SuperUnique(Boss, (NPCCode)82, 5170, 2010));
        var pack = Fighter(CharacterClass.Amazon, Skill.MagicArrow, SuperUnique(Boss, (NPCCode)82, 5172, 2010))
            .Received(2, SyntheticGame.AssignNpc(Boss + 1, (NPCCode)82, 5168, 2012));
        for (var at = 3.0; at < 20; at += 2)
        {
            hammered.Sent(at, SyntheticGame.Location(0x0C, 5160, 2010));
            pack.Sent(at, SyntheticGame.Unit(0x0D, EntityType.NPC, Boss + 1));
        }

        hammered.WriteTo(Game1.Folder(root, "hammered"));
        pack.WriteTo(Game1.Folder(root, "pack"));
        Fighter(CharacterClass.Amazon, Skill.MagicArrow, SyntheticGame.AssignNpc(Boss, (NPCCode)82, 5172, 2010)).WriteTo(Game1.Folder(root, "plain"));

        Assert.All(["hammered", "pack", "plain"], game => Assert.Empty(Game1.Replayed(Game1.Folder(root, game), "client1", new BossNotAttackedCheck())));
    }
}
