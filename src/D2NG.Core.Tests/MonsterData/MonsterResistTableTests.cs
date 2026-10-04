using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.MonsterData;
using System.Collections.Generic;
using Xunit;

namespace D2NG.Core.Tests.MonsterData;

/// <summary>
/// Base resistances are not on the wire, so the bot reads them out of the game's monstats table and
/// adds the modifiers the monster spawned with, which are. The values below are the real ones for
/// those two classes, taken from patch_d2.mpq.
/// </summary>
public class MonsterResistTableTests
{
    private static MonsterResistTable Table()
        => MonsterResistTable.FromEntries(
            [
                new MonsterResistEntry
                {
                    ClassId = (int)NPCCode.OblivionKnight,
                    Class = "OblivionKnight",
                    Fire = [60, 60, 60],
                    Lightning = [60, 60, 60],
                    Cold = [60, 80, 100],
                    Poison = [60, 60, 75]
                },
                new MonsterResistEntry
                {
                    ClassId = (int)NPCCode.BurningSoul,
                    Class = "BurningSoul",
                    Physical = [60, 60, 30],
                    Lightning = [70, 85, 100]
                }
            ],
            null);

    [Fact]
    public void UnknownClassIsReportedAsUnknownRatherThanVulnerable()
    {
        Assert.False(Table().TryGetResist(
            NPCCode.Andarial, Difficulty.Hell, ResistType.Fire, [], out _));
        Assert.False(Table().IsImmune(NPCCode.Andarial, Difficulty.Hell, ResistType.Fire, []));
    }

    [Theory]
    [InlineData(Difficulty.Normal, 70)]
    [InlineData(Difficulty.Nightmare, 85)]
    [InlineData(Difficulty.Hell, 100)]
    public void BurningSoulsOnlyBecomeLightningImmuneInHell(Difficulty difficulty, int expected)
    {
        Assert.True(Table().TryGetResist(
            NPCCode.BurningSoul, difficulty, ResistType.Lightning, [], out var resist));
        Assert.Equal(expected, resist);
        Assert.Equal(
            expected >= MonsterResistTable.ImmuneAt,
            Table().IsImmune(NPCCode.BurningSoul, difficulty, ResistType.Lightning, []));
    }

    /// <summary>
    /// Lord De Seis is an Oblivion Knight, which is why he is cold immune in Hell whatever he rolls,
    /// and on Nightmare only when a modifier pushes his 80 over the line.
    /// </summary>
    [Theory]
    [InlineData(Difficulty.Nightmare, new MonsterEnchantment[] { }, false)]
    [InlineData(Difficulty.Nightmare, new[] { MonsterEnchantment.ColdEnchanted }, true)]
    [InlineData(Difficulty.Nightmare, new[] { MonsterEnchantment.MagicResistant }, true)]
    [InlineData(Difficulty.Nightmare, new[] { MonsterEnchantment.FireEnchanted }, false)]
    [InlineData(Difficulty.Hell, new MonsterEnchantment[] { }, true)]
    [InlineData(Difficulty.Normal, new MonsterEnchantment[] { }, false)]
    [InlineData(Difficulty.Normal, new[] { MonsterEnchantment.ColdEnchanted }, true)]
    public void DeSeisColdImmunityFollowsTheEnchantmentRoll(
        Difficulty difficulty,
        MonsterEnchantment[] enchantments,
        bool immune)
        => Assert.Equal(
            immune,
            Table().IsImmune(NPCCode.OblivionKnight, difficulty, ResistType.Cold, enchantments));

    [Fact]
    public void SpectralHitAddsToAllThreeElementsAtOnce()
    {
        var table = Table();
        IReadOnlyCollection<MonsterEnchantment> enchantments = [MonsterEnchantment.SpectralHit];

        Assert.True(table.TryGetResist(NPCCode.OblivionKnight, Difficulty.Normal, ResistType.Fire, enchantments, out var fire));
        Assert.True(table.TryGetResist(NPCCode.OblivionKnight, Difficulty.Normal, ResistType.Cold, enchantments, out var cold));
        Assert.True(table.TryGetResist(NPCCode.OblivionKnight, Difficulty.Normal, ResistType.Poison, enchantments, out var poison));

        Assert.Equal(80, fire);
        Assert.Equal(80, cold);
        Assert.Equal(60, poison);
    }

    [Fact]
    public void ResistanceNeverFallsBelowMinusOneHundred()
    {
        var table = MonsterResistTable.FromEntries(
            [new MonsterResistEntry { ClassId = (int)NPCCode.Andarial, Fire = [-150, -150, -150] }],
            null);

        Assert.True(table.TryGetResist(NPCCode.Andarial, Difficulty.Normal, ResistType.Fire, [], out var resist));
        Assert.Equal(-100, resist);
    }
}
