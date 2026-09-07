using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;

namespace D2NG.Core.MCP;

public sealed class CharacterProgression
{
    private CharacterProgression(byte raw, int actsPerDifficulty)
    {
        Raw = raw;
        ActsPerDifficulty = actsPerDifficulty;
    }

    public byte Raw { get; }
    public int ActsPerDifficulty { get; }
    public int CompletedActs => Raw;
    public bool HasCompletedHell => Raw == ActsPerDifficulty * 3;

    public Difficulty CurrentDifficulty
    {
        get
        {
            if (Raw >= ActsPerDifficulty * 2)
            {
                return Difficulty.Hell;
            }

            return Raw >= ActsPerDifficulty ? Difficulty.Nightmare : Difficulty.Normal;
        }
    }

    public Act CurrentAct
    {
        get
        {
            var actIndex = Raw % ActsPerDifficulty;
            if (HasCompletedHell)
            {
                actIndex = ActsPerDifficulty - 1;
            }

            return (Act)actIndex;
        }
    }

    public static bool TryCreate(Character character, out CharacterProgression progression)
    {
        return TryCreate(character.IsExpansion, character.Progression, out progression);
    }

    public static bool TryCreate(bool isExpansion, byte raw, out CharacterProgression progression)
    {
        var actsPerDifficulty = isExpansion ? 5 : 4;
        if (raw > actsPerDifficulty * 3)
        {
            progression = null;
            return false;
        }

        progression = new CharacterProgression(raw, actsPerDifficulty);
        return true;
    }
}
