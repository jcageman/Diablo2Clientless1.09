using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class ReplayLogonTests
{
    [Fact]
    public void TheReplayFindsTheCharacterInATraceThatStartsTheWayALiveOneDoes()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, "client 1.d2trace");
        var joining = SyntheticTrace.Joining().ToList();
        var records = new List<TraceRecord>
        {
            new(TraceRecordKind.D2gsSent, TimeSpan.FromMilliseconds(70), SyntheticTrace.Logon(SyntheticTrace.CharacterName, CharacterClass.Necromancer)),
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(114), joining[0]),
            new(TraceRecordKind.D2gsSent, TimeSpan.FromMilliseconds(115), [(byte)OutGoingPacket.Ping, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(120), [0x02]),
            new(TraceRecordKind.D2gsSent, TimeSpan.FromMilliseconds(121), [(byte)OutGoingPacket.Startup]),
        };
        records.AddRange(joining.Skip(1).Select(p => new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(200), p)));
        TraceFile.Write(path, new TraceHeader { Source = TraceSource.Bot, StartUtc = DateTime.UtcNow, ClientLabel = "client 1", GameName = "game-1" }, records);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.Equal((byte)OutGoingPacket.GameLogon, records[0].Payload[0]);
        Assert.Empty(result.FirstHandlerFailures);
        Assert.NotNull(replayer.Game.Me);
        Assert.Equal(SyntheticTrace.CharacterName, replayer.Game.Me.Name);
        Assert.Equal(1616, replayer.Game.Me.Life);
    }
}
