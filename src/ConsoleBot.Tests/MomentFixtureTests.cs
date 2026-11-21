using ConsoleBot.Attack;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Extensions;

namespace ConsoleBot.Tests;

public class MomentFixtureTests
{
    /// <summary>
    /// The Travincal moment a whirlwind barbarian spent a minute whirling into: the council pack stands on the temple
    /// platform, the barbarian below its edge, and every straight line between them crosses ground nobody walks on.
    /// </summary>
    [Fact]
    public void TheWhirlwindBarbarianHasTheCouncilPackAcrossAWall()
    {
        var moment = MomentFixture.Load("travincal-whirl-wall.json");
        var map = moment.Map();
        var pack = moment.Monsters().Where(m => m.State == EntityState.Alive && m.NPCCode is NPCCode.CouncilMember1 or NPCCode.CouncilMember2 or NPCCode.CouncilMember3).ToList();

        Assert.Equal("Travincal", moment.Area);
        Assert.Equal("Barbarian", moment.SelfClass);
        Assert.Equal(new Point(4818, 1190), moment.Self);
        Assert.NotEmpty(pack);
        Assert.All(pack, m => Assert.InRange(m.Location.Y, 1179, 1184));
        foreach (var monster in pack)
        {
            var blocked = AttackService.GetPointsOnLine(moment.Self.X, moment.Self.Y, monster.Location.X, monster.Location.Y)
                .Select(p => map.Map[p.Y - map.LevelOrigin.Y][p.X - map.LevelOrigin.X])
                .Any(value => !AreaMapExtensions.IsMovable(value));
            Assert.True(blocked, $"{monster.NPCCode} 0x{monster.Id:X} at {monster.Location} is reachable in a straight line");
        }
    }
}
