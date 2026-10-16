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

    // A stash gem put down on an inventory gem (0x1F): the server reports the gem that landed with
    // SwapInContainer, and the gem it displaced is then on the cursor.
    [Fact]
    public void SwapInContainer_LandsTheItemAndLiftsTheOneItDisplaced()
    {
        var data = CreateGameWithAssignedPlayer();
        var stashGem = Gem(0x7C, D2NG.Core.D2GS.Items.ContainerType.Stash, 7, 1, D2NG.Core.D2GS.Items.Action.PutInContainer);
        var inventoryGem = Gem(0x7E, D2NG.Core.D2GS.Items.ContainerType.Inventory, 2, 2, D2NG.Core.D2GS.Items.Action.PutInContainer);
        data.ItemUpdate(stashGem);
        data.ItemUpdate(inventoryGem);
        data.ItemUpdate(Gem(0x7C, D2NG.Core.D2GS.Items.ContainerType.Stash, 7, 1, D2NG.Core.D2GS.Items.Action.RemoveFromContainer));
        Assert.Equal(0x7CU, data.CursorItem?.Id);

        data.ItemUpdate(Gem(0x7C, D2NG.Core.D2GS.Items.ContainerType.Inventory, 2, 2, D2NG.Core.D2GS.Items.Action.SwapInContainer));

        Assert.NotNull(data.Inventory.FindItemById(0x7C));
        Assert.Null(data.Inventory.FindItemById(0x7E));
        Assert.Equal(0x7EU, data.CursorItem?.Id);
    }

    // A completed trade empties both trade grids with the same removal a lift gets (seen live, 10 Oct
    // 2026); those items are gone, not on the cursor.
    [Fact]
    public void TradeGridRemoval_ReachesTheCursorOnlyWhileTheItemIsBeingLifted()
    {
        var data = CreateGameWithAssignedPlayer();
        data.LiftRequested = 0x22;
        data.ItemUpdate(Gem(0x22, D2NG.Core.D2GS.Items.ContainerType.ForTrade, 0, 0, D2NG.Core.D2GS.Items.Action.PutInContainer));
        data.ItemUpdate(Gem(0x22, D2NG.Core.D2GS.Items.ContainerType.ForTrade, 0, 0, D2NG.Core.D2GS.Items.Action.RemoveFromContainer));
        Assert.Null(data.CursorItem);

        data.LiftRequested = 0x23;
        data.ItemUpdate(Gem(0x23, D2NG.Core.D2GS.Items.ContainerType.ForTrade, 1, 0, D2NG.Core.D2GS.Items.Action.RemoveFromContainer));
        Assert.Equal(0x23U, data.CursorItem?.Id);
    }

    private static D2NG.Core.D2GS.Items.Item Gem(uint id, D2NG.Core.D2GS.Items.ContainerType container, ushort x, ushort y, D2NG.Core.D2GS.Items.Action action)
    {
        return new D2NG.Core.D2GS.Items.Item
        {
            Id = id,
            Container = container,
            Location = new Point(x, y),
            Width = 1,
            Height = 1,
            Action = action,
            Properties = []
        };
    }

    // Packets a sorceress received on joining: her base maximum life as attribute 7 (815 in 256ths),
    // a Grim Helm with 40 life and the perfect ruby in its socket, whose owner field is the helm's id.
    [Fact]
    public void MaximumLifeCountsServerBaseLifeAndSocketedRuby()
    {
        var data = CreateGameWithAssignedPlayer();
        data.SetAttribute(new BaseAttributePacket(new D2gsPacket(System.Convert.FromHexString("1F07002F0300"))));
        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(System.Convert.FromHexString(
            "9D063700030000000001000000110880000124008087960312A6E109F400C3D005DBF0062580084A4640101380B402E200D2090C2785FF"))));
        Assert.Equal(815 + 40, data.Me.MaxLife);

        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(System.Convert.FromHexString(
            "9D1319100400000004030000001800A00001186E7006270702"))));

        Assert.Equal(815 + 40 + 38, data.Me.MaxLife);
    }

    // Battle Orders cast while a character is still loading into an area arrives only in the state
    // list (0x12), without the 98% that 0x13 carries; the last percentage this character got stands in.
    [Fact]
    public void BattleOrdersFromStateListUsesLastKnownPercentage()
    {
        var earlier = CreateGameWithAssignedPlayer();
        earlier.AddEntityEffect(new AddEntityEffectPacket(new D2gsPacket(System.Convert.FromHexString("1300010000002000000062000000"))));
        Assert.Equal(98, earlier.Me.BattleOrdersLifePercentage);

        var data = CreateGameWithAssignedPlayer();
        data.SetAttribute(new BaseAttributePacket(new D2gsPacket(System.Convert.FromHexString("1F07002F0300"))));
        data.UpdateEntityEffects(new UpdateEntityEffectsPacket(new D2gsPacket(System.Convert.FromHexString(
            "1200010000000000000001000000000000000000000000000000"))));

        Assert.Equal((int)(815 * 1.98), data.Me.MaxLife);
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
