using D2NG.Core.D2GS;
using System;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>The countdown as the realm sent it on 17 Nov 2026, from the trace of a game it dropped.</summary>
public class GameServerRestartTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 18, 11, 5, DateTimeKind.Utc);

    [Theory]
    [InlineData("This gameserver is restarting in 4 minutes.", 240)]
    [InlineData("This gameserver is restarting in 1 minute.", 60)]
    [InlineData("Restarting gameserver in 30 seconds. Please leave your game.", 30)]
    [InlineData("** GAMESERVER RESTARTING **", 10)]
    [InlineData("The game server will restart in 0 seconds", 0)]
    public void TheCountdownIsRead(string message, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), GameServerRestart.Announced("[administrator]", message));
    }

    [Theory]
    [InlineData("[administrator]", "Welcome to Diablo 09! This game is hosted on our London Server.")]
    [InlineData("", "New content and features will be coming soon!")]
    [InlineData("someone", "This gameserver is restarting in 4 minutes.")]
    public void OtherChatIsNoCountdown(string from, string message)
    {
        Assert.Null(GameServerRestart.Announced(from, message));
    }

    [Fact]
    public void TheEarliestTimeOfARunningCountdownIsKept()
    {
        var known = Now + TimeSpan.FromMinutes(4);

        Assert.Equal(known, GameServerRestart.Merge(known, Now + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30), Now + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ARestartLongOverIsReplaced()
    {
        var old = Now - TimeSpan.FromHours(2);
        var heard = Now + TimeSpan.FromMinutes(4);

        Assert.Equal(heard, GameServerRestart.Merge(old, heard, Now));
    }

    [Fact]
    public void NewGamesWaitUntilTheServerIsBack()
    {
        var restart = Now + TimeSpan.FromSeconds(30);

        Assert.Equal(TimeSpan.FromSeconds(30) + GameServerRestart.Downtime, GameServerRestart.WaitFor(restart, Now));
        Assert.Equal(TimeSpan.Zero, GameServerRestart.WaitFor(restart, restart + GameServerRestart.Downtime));
        Assert.Equal(TimeSpan.Zero, GameServerRestart.WaitFor(null, Now));
    }
}
