﻿using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.MCP.Packet;
using Xunit;

namespace D2NG.Core.Tests.MCP
{
    public class PacketTest
    {


        [Fact]
        public void ListCharactersClientPacket()
        {
            var packet = new ListCharactersClientPacket();
            Assert.Equal(new byte[] { 0x07, 0x00, 0x17, 0x08, 0x00, 0x00, 0x00 }, packet.Raw);

        }

        [Fact]
        public void ListCharactersServerPacketSingleCharacter()
        {
            string str = "350017000001000000010047656f646573730089805f0101010119ff610202ff024a4a4a4a4a26ff524a4aff5b80988080ffffff00";
            var packet = new ListCharactersServerPacket(str.StringToByteArray());
            Assert.Single(packet.Characters);
            Assert.Equal("Geodess", packet.Characters[0].Name);
            Assert.Equal((uint)91, packet.Characters[0].Level);

        }

        [Fact]
        public void ListCharactersServerPacketFourCharacters()
        {
            string str = "b70017000004000000040067656f2d616d61008980ffffffffff04ff4fffffff05ffffffffffffffffffffff0180808080ffffff0067656f2d61726d6f7273008980ffffffffff04ff4fffffff05ffffffffffffffffffffff0180808080ffffff0067656f2d756e697175650089803affffffff04ff4fffffff0554ffffffffffffffffffff0180808080ffffff005a6f6e65640089803d01020201ff30ff0202ff01434b4b4b4bffa4ff4b4bff5580988080ffffff00";
            var packet = new ListCharactersServerPacket(str.StringToByteArray());
            Assert.Equal(4, packet.Characters.Count);
        }

        [Fact]
        public void ListCharactersServerPacketNoCharacters()
        {
            string str = "0b00170000000000000000";
            var packet = new ListCharactersServerPacket(str.StringToByteArray());
            Assert.Empty(packet.Characters);

        }

        [Fact]
        public void CreateGameRequestPacket()
        {
            byte[] expected = new byte[] { 0x19, 0x00, 0x03, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x01, 0xFF, 0x08, 0x74, 0x65, 0x73, 0x74, 0x00, 0x42, 0x6C, 0x61, 0x00, 0x47, 0x53, 0x32, 0x00 };
            var packet = new CreateGameRequestPacket(0, Difficulty.Hell, "test", "bla", "GS2");

            Assert.Equal(25, packet.Raw.Length);
            Assert.Equal(expected, packet.Raw);
        }

    }
}
