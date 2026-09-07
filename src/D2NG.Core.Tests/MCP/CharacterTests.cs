using D2NG.Core.MCP;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using Xunit;

namespace D2NG.Core.Tests.MCP;

public class CharacterTests
{
    [Fact]
    public void DecodesPackedCharacterFlagsAndExposesRawProgressionByte()
    {
        byte[] stats = new byte[28];
        stats[13] = 1;
        stats[25] = 42;
        stats[26] = 0xA5;
        stats[27] = 0x98;

        var character = new Character("Torvald", stats);

        Assert.Equal((byte)12, character.Progression);
        Assert.Equal((byte)0x98, character.RawProgression);
        Assert.True(character.IsExpansion);
        Assert.True(character.IsHardCore);
    }

    [Fact]
    public void DecodesObservedClassicSoftcoreProgression()
    {
        byte[] stats = new byte[28];
        stats[13] = 1;
        stats[25] = 42;
        stats[26] = 0x81;
        stats[27] = 0x82;

        var character = new Character("Torvald", stats);

        Assert.Equal((byte)1, character.Progression);
        Assert.Equal((byte)0x82, character.RawProgression);
        Assert.False(character.IsExpansion);
        Assert.False(character.IsHardCore);
    }

    [Theory]
    [InlineData(false, 0, Difficulty.Normal, Act.Act1, false)]
    [InlineData(false, 3, Difficulty.Normal, Act.Act4, false)]
    [InlineData(false, 4, Difficulty.Nightmare, Act.Act1, false)]
    [InlineData(false, 8, Difficulty.Hell, Act.Act1, false)]
    [InlineData(false, 12, Difficulty.Hell, Act.Act4, true)]
    [InlineData(true, 4, Difficulty.Normal, Act.Act5, false)]
    [InlineData(true, 5, Difficulty.Nightmare, Act.Act1, false)]
    [InlineData(true, 10, Difficulty.Hell, Act.Act1, false)]
    [InlineData(true, 15, Difficulty.Hell, Act.Act5, true)]
    public void DecodesKnownSequentialProgression(
        bool expansion,
        byte raw,
        Difficulty difficulty,
        Act act,
        bool completedHell)
    {
        Assert.True(CharacterProgression.TryCreate(expansion, raw, out var progression));
        Assert.Equal(difficulty, progression.CurrentDifficulty);
        Assert.Equal(act, progression.CurrentAct);
        Assert.Equal(completedHell, progression.HasCompletedHell);
    }

    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 16)]
    [InlineData(true, 255)]
    public void RejectsUnknownProgressionConservatively(bool expansion, byte raw)
    {
        Assert.False(CharacterProgression.TryCreate(expansion, raw, out var progression));
        Assert.Null(progression);
    }
}
