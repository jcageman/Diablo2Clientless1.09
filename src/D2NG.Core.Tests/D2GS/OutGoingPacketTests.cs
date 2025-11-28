using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.MCP;
using System;
using System.Buffers.Binary;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class OutGoingPacketTests
{

    [Fact]
    public void GameLogonPacket()
    {
        string str = "655553e25d58010209000000006e6f6d616e63657200aa6f4b0000004c";
        var gamehash = str.Substring(2, 8);
        var gametoken = str.Substring(10, 4);
        var packet = new GameLogonPacket(BinaryPrimitives.ReverseEndianness(Convert.ToUInt32(gamehash, 16)), BinaryPrimitives.ReverseEndianness(Convert.ToUInt16(gametoken, 16)), new Character("nomancer", "8980ffffffffff09ffffffffff03ffffffffffffffffffffff0180808080ffffff00".StringToByteArray()));
        var expected = str.StringToByteArray();
        Assert.Equal(29, packet.Raw.Length);
        Assert.Equal(expected, packet.Raw);

    }

    [Fact]
    public void PingPacket()
    {
        string str = "6a8716423a5d000000";
        var packet = new PingPacket();

        Assert.Equal(9, packet.Raw.Length);
        var expected = str.StringToByteArray();
    }

    [Fact]
    public void ClickButtonPacketGold1()
    {
        byte[] expected = [0x4F, 0x14, 0x00, 0x1E, 0x00, 0x06, 0xE6];
        var packet = new ClickButtonPacket(ClickType.MoveGoldFromInventoryToStash, 2024966);

        Assert.Equal(7, packet.Raw.Length);
        Assert.Equal(expected, packet.Raw);
    }

    [Fact]
    public void ClickButtonPacketGold2()
    {
        byte[] expected = [0x4F, 0x14, 0x00, 0x03, 0x00, 0xBA, 0x8A];
        var packet = new ClickButtonPacket(ClickType.MoveGoldFromInventoryToStash, 232122);

        Assert.Equal(7, packet.Raw.Length);
        Assert.Equal(expected, packet.Raw);
    }
    [Fact]
    public void RepairPacket()
    {
        byte[] expected = [0x35, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80];
        var packet = new RepairPacket(new WorldObject(EntityType.NPC, 16, 0, new Point(0, 0), EntityState.Alive, 0));

        Assert.Equal(17, packet.Raw.Length);
        Assert.Equal(expected, packet.Raw);
    }


}
