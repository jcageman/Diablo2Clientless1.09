using ConsoleBot.Helpers;
using Xunit;

namespace ConsoleBot.Tests;

public class SorceressCastTimingTests
{
    [Theory]
    [InlineData(0, 13)]
    [InlineData(8, 13)]
    [InlineData(9, 12)]
    [InlineData(36, 11)]
    [InlineData(37, 10)]
    [InlineData(104, 9)]
    [InlineData(105, 8)]
    [InlineData(199, 8)]
    [InlineData(200, 7)]
    [InlineData(250, 7)]
    public void Cast_frames_follow_the_breakpoints(int fasterCastRate, int frames)
    {
        Assert.Equal(frames, SorceressCastTiming.CastFrames(fasterCastRate));
    }
}
