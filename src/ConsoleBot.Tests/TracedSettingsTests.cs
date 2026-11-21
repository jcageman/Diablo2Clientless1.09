using ConsoleBot.Helpers;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using Xunit;

namespace ConsoleBot.Tests;

public class TracedSettingsTests
{
    [Fact]
    public void OnlyTheBotTypeTheMotionModelAndTheListedSwitchesAreWritten()
    {
        var bot = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["bot:botType"] = "travincal",
            ["bot:monsterMotion"] = "anchored",
            ["bot:travincal:wallAwareWhirl"] = "true",
            ["bot:travincal:movementSpeedFactor"] = "1.2",
            ["bot:travincal:password"] = "secret",
            ["bot:travincal:character"] = "someone",
        }).Build().GetSection("bot");

        var settings = TracedSettings.Of(bot);

        Assert.Contains(("botType", "travincal"), settings);
        Assert.Contains(("monsterMotion", "Simulated"), settings);
        Assert.Contains(settings, s => s.Key == "monsterMotionEffective");
        Assert.Contains(("movementSpeedFactor", "1.2"), settings);
        Assert.DoesNotContain(settings, s => s.Key == "wallAwareWhirl");
        Assert.DoesNotContain(settings, s => s.Value is "secret" or "someone");
    }

    [Fact]
    public void ASwitchTheConfigLeavesOutIsRecordedWithItsDefault()
    {
        var bot = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["bot:botType"] = "travincal",
            ["bot:travincal:identifyWithTome"] = "True",
        }).Build().GetSection("bot");

        var settings = TracedSettings.Of(bot);

        Assert.Contains(("identifyWithTome", "true"), settings);
        Assert.Contains(("movementSpeedFactor", "1"), settings);
        Assert.Contains(("monsterMotion", "Simulated"), settings);
    }
}
