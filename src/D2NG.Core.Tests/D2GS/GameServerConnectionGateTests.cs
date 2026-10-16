using D2NG.Core.D2GS;
using System;
using System.IO;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public sealed class GameServerConnectionGateTests : IDisposable
{
    private readonly string _ledger = Path.Combine(Path.GetTempPath(), $"gate-test-{Guid.NewGuid():N}.txt");

    private static readonly DateTime Start = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => File.Delete(_ledger);

    [Fact]
    public void FiveConnectionsAtOnceAllGetASlot()
    {
        for (var i = 0; i < GameServerConnectionGate.ConnectionsPerWindow; i++)
        {
            Assert.Equal(TimeSpan.Zero, GameServerConnectionGate.TryTakeSlot(_ledger, Start));
        }
    }

    [Fact]
    public void TheSixthWaitsUntilTheOldestLeavesTheWindow()
    {
        for (var i = 0; i < GameServerConnectionGate.ConnectionsPerWindow; i++)
        {
            GameServerConnectionGate.TryTakeSlot(_ledger, Start.AddSeconds(i));
        }

        var wait = GameServerConnectionGate.TryTakeSlot(_ledger, Start.AddSeconds(10));

        Assert.InRange(wait, GameServerConnectionGate.Window - TimeSpan.FromSeconds(10), GameServerConnectionGate.Window - TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void AWaitingConnectionIsNotCounted()
    {
        for (var i = 0; i < GameServerConnectionGate.ConnectionsPerWindow; i++)
        {
            GameServerConnectionGate.TryTakeSlot(_ledger, Start);
        }

        GameServerConnectionGate.TryTakeSlot(_ledger, Start.AddSeconds(1));

        Assert.Equal(TimeSpan.Zero, GameServerConnectionGate.TryTakeSlot(_ledger, Start + GameServerConnectionGate.Window));
    }

    [Fact]
    public void ConnectionsOlderThanTheWindowAreForgotten()
    {
        for (var i = 0; i < GameServerConnectionGate.ConnectionsPerWindow; i++)
        {
            GameServerConnectionGate.TryTakeSlot(_ledger, Start);
        }

        var later = Start + GameServerConnectionGate.Window + TimeSpan.FromSeconds(1);
        for (var i = 0; i < GameServerConnectionGate.ConnectionsPerWindow; i++)
        {
            Assert.Equal(TimeSpan.Zero, GameServerConnectionGate.TryTakeSlot(_ledger, later));
        }
    }
}
