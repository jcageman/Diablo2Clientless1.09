using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.MCP;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class GameDataTests
{
    [Fact]
    public void RemovingCorpseClearsOwnerCorpseId()
    {
        var gameFlags = new GameFlags(new D2gsPacket([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
        var characterStats = new byte[28];
        characterStats[13] = 3;
        var data = new GameData(gameFlags, new Character("nomancer", characterStats));
        data.PlayerAssign(new AssignPlayerPacket(new D2gsPacket(
            [0x59, 0x01, 0x00, 0x00, 0x00, 0x02, 0x6E, 0x6F, 0x6D, 0x61, 0x6E, 0x63, 0x65,
                0x72, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x00])));

        data.PlayerCorpseAssign(CreateCorpsePacket(corpseAdded: true));
        Assert.Equal(42U, data.Me.CorpseId);
        Assert.Equal(42U, data.Players.Single(player => player.Id == data.Me.Id).CorpseId);

        data.PlayerCorpseAssign(CreateCorpsePacket(corpseAdded: false));
        Assert.Null(data.Me.CorpseId);
        Assert.Null(data.Players.Single(player => player.Id == data.Me.Id).CorpseId);
    }

    [Fact]
    public void RemovingAPlayerWhoseCorpseSharesItsEntityIdDoesNotThrow()
    {
        var data = CreateGameWithAssignedPlayer();

        data.PlayerCorpseAssign(new CorpseAssignPacket(new D2gsPacket(
            [0x8E, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00])));
        Assert.Equal(1U, data.Players.Single().CorpseId);

        var exception = Record.Exception(() => data.RemoveObject(new RemoveObjectPacket(new D2gsPacket(
            [0x0A, 0x00, 0x01, 0x00, 0x00, 0x00]))));

        Assert.Null(exception);
    }

    private static GameData CreateGameWithAssignedPlayer()
    {
        var gameFlags = new GameFlags(new D2gsPacket([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
        var characterStats = new byte[28];
        characterStats[13] = 3;
        var data = new GameData(gameFlags, new Character("nomancer", characterStats));
        data.PlayerAssign(new AssignPlayerPacket(new D2gsPacket(
            [0x59, 0x01, 0x00, 0x00, 0x00, 0x02, 0x6E, 0x6F, 0x6D, 0x61, 0x6E, 0x63, 0x65,
                0x72, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x00])));
        return data;
    }

    private static CorpseAssignPacket CreateCorpsePacket(bool corpseAdded)
    {
        return new CorpseAssignPacket(new D2gsPacket(
            [0x8E, corpseAdded ? (byte)0x01 : (byte)0x00, 0x01, 0x00, 0x00, 0x00,
                0x2A, 0x00, 0x00, 0x00]));
    }
}
