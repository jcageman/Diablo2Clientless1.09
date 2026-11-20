using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Trace.Query;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class QueryAdditionTests
{
    private static readonly DateTime Start = CapturedBytes.Start;

    /// <summary>A client casting at a point beside the waypoint angel for a minute, and a second client hitting it with a skill on the unit.</summary>
    private static string AngelGame(TempRoot root)
    {
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "game-1");
        var barbarian = new HandTrace("barbarian", "game-1", Start, "127.0.0.1#258").Join(1, x: 7800, y: 5880)
            .Received(0.5, SyntheticGame.AssignNpc(5, NPCCode.Hadriel, 7800, 5870));
        var sorceress = new HandTrace("sorceress", "game-1", Start, "127.0.0.1#258").Join(2, x: 7802, y: 5880)
            .Received(0.5, SyntheticGame.AssignNpc(5, NPCCode.Hadriel, 7800, 5870));
        for (var t = 1.0; t <= 61; t += 0.25)
        {
            barbarian.Sent(t, SyntheticGame.Location(0x08, 7800, 5872));
        }

        for (var t = 1.1; t <= 31; t += 0.5)
        {
            sorceress.Sent(t, SyntheticGame.Unit(0x0D, EntityType.NPC, 5));
        }

        barbarian.Received(70, SyntheticTrace.LifeManaUpdate);
        sorceress.Received(70, SyntheticTrace.LifeManaUpdate);
        barbarian.WriteTo(folder);
        sorceress.WriteTo(folder);
        return folder;
    }

    /// <summary>A sorceress casting <paramref name="skill"/> at a monster for thirty seconds with no NPCHit or NPCState for it, and <paramref name="answer"/> at ten seconds.</summary>
    private static string DemonGame(TempRoot root, Skill skill, byte[] answer)
    {
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "game-1");
        var sorceress = new HandTrace("sorceress", "game-1", Start, "127.0.0.1#258").Join(2, x: 7802, y: 5880)
            .Received(0.5, SyntheticGame.AssignNpc(5, (NPCCode)82, 7800, 5870))
            .Received(0.6, SyntheticGame.SetSkill(2, Hand.Right, skill));
        for (var t = 1.0; t <= 31; t += 0.5)
        {
            sorceress.Sent(t, SyntheticGame.Unit(0x0D, EntityType.NPC, 5));
        }

        if (answer != null)
        {
            sorceress.Received(10, answer);
        }

        sorceress.Received(40, SyntheticTrace.LifeManaUpdate);
        sorceress.WriteTo(folder);
        return folder;
    }

    private static readonly byte[] StopAtHalfLife = [0x6D, 0x05, 0x00, 0x00, 0x00, .. BitConverter.GetBytes((ushort)7800), .. BitConverter.GetBytes((ushort)5870), 66];

    /// <summary>
    /// A monster's own NPCStop said 51.6% while the replayed Game, which keeps no life from a stop, still said 100%, and a
    /// Cold state landed after Ice Blast: before, both were reported as twenty-five seconds unanswered at 100%.
    /// </summary>
    [Theory]
    [InlineData("stop", false)]
    [InlineData("cold", false)]
    [InlineData("cold after a fire skill", true)]
    [InlineData("nothing", true)]
    public void ALifeBelowFullInAnyPacketOrTheSkillsOwnStateIsAnAnswer(string answer, bool reported)
    {
        using var root = new TempRoot();
        var (skill, packet) = answer switch
        {
            "stop" => (Skill.IceBlast, StopAtHalfLife),
            "cold" => (Skill.IceBlast, SyntheticGame.AddEffect(EntityType.NPC, 5, EntityEffect.Cold)),
            "cold after a fire skill" => (Skill.FireBolt, SyntheticGame.AddEffect(EntityType.NPC, 5, EntityEffect.Cold)),
            _ => (Skill.IceBlast, (byte[])null),
        };

        var found = Incidents.Find(GameQuery.Open(DemonGame(root, skill, packet))).Where(i => i.Kind == AttackingWithoutHitsCheck.Kind).ToList();

        Assert.Equal(reported, found.Count == 1);
        Assert.True(found.Count <= 1);
    }

    [Fact]
    public void AttacksAtAUnitThatNeverTakesAHitAreOneIncidentPerEpisodeWithItsWholeLengthAndEveryClient()
    {
        using var root = new TempRoot();
        var folder = AngelGame(root);

        var found = Incidents.Find(GameQuery.Open(folder)).Where(i => i.Kind == AttackingWithoutHitsCheck.Kind).ToList();

        var incident = Assert.Single(found);
        Assert.Equal("barbarian+sorceress", incident.Client);
        Assert.Contains("class 408", incident.Detail, StringComparison.Ordinal);
        Assert.Contains("not in the friendly/neutral list", incident.Detail, StringComparison.Ordinal);
        Assert.Contains("barbarian 241 attacks", incident.Detail, StringComparison.Ordinal);
        Assert.Contains("over 60s, 60s wasted", incident.Detail, StringComparison.Ordinal);
        Assert.Contains("sorceress 60 attacks", incident.Detail, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(61), incident.End);
    }

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");

    [Fact]
    public void AClockTimeWithinTheGameIsReadAsTheOffsetItNamesAndSaysSo()
    {
        var start = new DateTime(2026, 11, 14, 1, 21, 40, DateTimeKind.Utc);

        var offset = TraceTime.Resolve("02:22:14", start, start.AddMinutes(14), Zone, out var note);

        Assert.Equal(TimeSpan.FromSeconds(34), offset);
        Assert.Contains("02:22:14", note, StringComparison.Ordinal);
        Assert.Contains("01:22:14", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AClockTimeAfterMidnightFindsAGameThatStartedTheDayBefore()
    {
        var start = new DateTime(2026, 11, 13, 22, 59, 30, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromSeconds(45), TraceTime.Resolve("00:00:15", start, start.AddMinutes(10), Zone, out var note));
        Assert.Contains("as an offset it would be 00:15.000", note, StringComparison.Ordinal);
    }

    /// <summary>Central European time: summer time from the last Sunday of March 02:00 to the last Sunday of October 03:00.</summary>
    private static readonly TimeZoneInfo Central = TimeZoneInfo.CreateCustomTimeZone("test-cet", TimeSpan.FromHours(1), "test-cet", "test-cet", "test-cest",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
    ]);

    /// <summary>On 25 October 2026 the clocks go back from 03:00 to 02:00, so 02:45 happens twice: before, the summer-time pass was refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AClockTimeInTheHourTheClocksGoBackIsReadInWhicheverPassTheGameRan(int pass)
    {
        var start = new DateTime(2026, 10, 25, 0, 40, 0, DateTimeKind.Utc).AddHours(pass);

        Assert.Equal(TimeSpan.FromMinutes(5), TraceTime.Resolve("02:45:00", start, start.AddMinutes(10), Central, out var note));
        Assert.Contains($"{start.AddMinutes(5):HH:mm:ss.fff} UTC", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowKeepsTheIncidentsWhoseTimeOrEpisodeOverlapsIt()
    {
        static Incident At(double from, double? to = null)
            => new(TimeSpan.FromSeconds(from), "client1", "kind", "detail") { End = to is { } end ? TimeSpan.FromSeconds(end) : null };
        Incident[] incidents = [At(10), At(55), At(40, 70), At(80, 90), At(130), At(95, 125)];

        var kept = Incidents.Within(incidents, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(100)).Select(i => i.Offset.TotalSeconds);

        Assert.Equal([55, 40, 80, 95], kept);
    }

    [Fact]
    public void AnOffsetInsideTheGameStaysAnOffset()
    {
        var start = new DateTime(2026, 11, 14, 1, 21, 40, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromSeconds(134), TraceTime.Resolve("02:14", start, start.AddMinutes(14), Zone, out var note));
        Assert.Null(note);
    }

    [Fact]
    public void ATimeThatIsNeitherAnOffsetNorAClockTimeOfTheGameIsAnErrorGivingItsSpan()
    {
        var start = new DateTime(2026, 11, 14, 1, 21, 40, DateTimeKind.Utc);

        var error = Assert.Throws<FormatException>(() => TraceTime.Resolve("05:22:14", start, start.AddMinutes(14), Zone, out _));

        Assert.Contains("02:21:40 to 02:35:40 local", error.Message, StringComparison.Ordinal);
        Assert.Contains("01:21:40 to 01:35:40 UTC", error.Message, StringComparison.Ordinal);
        Assert.Contains("14:00.000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttackThatTheServerAnswersWithAHitIsNoIncident()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "game-1");
        var trace = new HandTrace("barbarian", "game-1", Start).Join(1, x: 7800, y: 5880)
            .Received(0.5, SyntheticGame.AssignNpc(5, NPCCode.Zombie, 7800, 5870));
        for (var t = 1.0; t <= 40; t += 0.5)
        {
            trace.Sent(t, SyntheticGame.Unit(0x06, EntityType.NPC, 5));
        }

        trace.Received(20, SyntheticGame.NpcHit(5, 100)).Received(45, SyntheticTrace.LifeManaUpdate).WriteTo(folder);

        Assert.DoesNotContain(Incidents.Find(GameQuery.Open(folder)), i => i.Kind == AttackingWithoutHitsCheck.Kind);
    }
}
