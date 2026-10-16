using System;

namespace ConsoleBot.Helpers;

/// <summary>
/// How long a sorceress takes to cast a spell with the general cast animation (Nova, Static Field,
/// Teleport), from her faster cast rate. A cast sent before the previous one has finished is ignored
/// by the server, so casting faster than this only costs packets.
/// </summary>
public static class SorceressCastTiming
{
    private static readonly (int Fcr, int Frames)[] Breakpoints =
        [(200, 7), (105, 8), (63, 9), (37, 10), (20, 11), (9, 12), (0, 13)];

    public static int CastFrames(int fasterCastRate)
    {
        foreach (var (fcr, frames) in Breakpoints)
        {
            if (fasterCastRate >= fcr)
            {
                return frames;
            }
        }

        return 13;
    }

    public static TimeSpan CastTime(int fasterCastRate) => TimeSpan.FromSeconds(CastFrames(fasterCastRate) / 25.0);
}
