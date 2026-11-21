using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Motion;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using Xunit;

namespace ConsoleBot.Tests;

public class MonsterMotionSetupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("legacy")]
    [InlineData("anchored")]
    public void EveryClientStartsSimulatedWhateverTheConfigSays(string configured)
    {
        var before = MonsterMotionSettings.DefaultMode;
        try
        {
            var values = new Dictionary<string, string>();
            if (configured != null)
            {
                values["bot:monsterMotion"] = configured;
            }

            MonsterMotionSetup.Apply(new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("bot"));
            Assert.Equal(MonsterMotion.Simulated, new MonsterMotionSettings().Mode);
        }
        finally
        {
            MonsterMotionSettings.DefaultMode = before;
        }
    }
}
