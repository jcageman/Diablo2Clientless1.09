using D2NG.Core.D2GS.Players;
using D2NG.Core.SkillData;
using System;

namespace ConsoleBot.Helpers;

/// <summary>
/// How long a barbarian's shouts last, from the skills.bin parameters: Shout runs par3 + (level - 1) * par4
/// frames, Battle Orders and Battle Command par1 + (level - 1) * par2, at 25 frames a second.
/// </summary>
public static class ShoutDurations
{
    private const double FramesPerSecond = 25;

    public static TimeSpan For(SkillEntry entry, Skill skill, int level)
    {
        var (baseIndex, perLevelIndex) = skill == Skill.Shout ? (2, 3) : (0, 1);
        var frames = entry.Params[baseIndex] + Math.Max(0, level - 1) * entry.Params[perLevelIndex];
        return TimeSpan.FromSeconds(frames / FramesPerSecond);
    }
}
