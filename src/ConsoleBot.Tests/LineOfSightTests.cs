using ConsoleBot.Attack;
using System.Linq;
using Xunit;

namespace ConsoleBot.Tests;

public sealed class LineOfSightTests
{
    [Fact]
    public void DiagonalLineStepsAlongItsMinorAxis()
    {
        var points = AttackService.GetPointsOnLine(10, 10, 20, 15).ToList();

        Assert.Equal(11, points.Count);
        Assert.Equal((10, 10), (points[0].X, points[0].Y));
        Assert.Equal((20, 15), (points[^1].X, points[^1].Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    public void MissilesCrossFloorWaterAndTheChaosStar(int cell)
    {
        Assert.False(AttackService.BlocksSight(cell));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(23)]
    [InlineData(37)]
    public void WallsAndEdgesStillBlock(int cell)
    {
        Assert.True(AttackService.BlocksSight(cell));
    }

    [Fact]
    public void LineTowardsSmallerYStepsDown()
    {
        var points = AttackService.GetPointsOnLine(10, 20, 14, 16).ToList();

        Assert.Equal((14, 16), (points[^1].X, points[^1].Y));
        Assert.All(points.Zip(points.Skip(1)), pair => Assert.True(pair.Second.Y <= pair.First.Y));
    }
}
