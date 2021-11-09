using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Generic;
using Xunit;

namespace D2NG.Core.Tests.D2GS
{
    public class IncomingPacketTests
    {
        [Fact]
        public void GameFlagsPacket()
        {
            var bytes = new byte[] { 0x01, 0x02, 0x04, 0x20, 0x00, 0x00, 0x00, 0x00 };
            var packet = new GameFlags(new D2gsPacket(bytes));
            Assert.Equal(Difficulty.Hell, packet.Difficulty);
            Assert.False(packet.Expansion);
            Assert.False(packet.Hardcore);
            Assert.False(packet.Ladder);

        }

        [Fact]
        public void AssignPlayerPacket()
        {
            var bytes = new byte[] { 0x59, 0x01, 0x00, 0x00, 0x00, 0x02, 0x6E, 0x6F, 0x6D, 0x61, 0x6E, 0x63, 0x65, 0x72, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x00 };
            var packet = new AssignPlayerPacket(new D2gsPacket(bytes));
            Assert.Equal(1U, packet.Id);
            Assert.Equal("nomancer", packet.Name);
            Assert.Equal(CharacterClass.Necromancer, packet.Class);
            Assert.Equal(0, packet.Location.X);
            Assert.Equal(0, packet.Location.Y);
        }

        [Fact]
        public void ActDataPacket()
        {
            var bytes = new byte[] { 0x03, 0x00, 0x73, 0x31, 0x1B, 0x5C, 0x01, 0x00, 0xB0, 0x00, 0x50, 0x24 };
            var packet = new ActDataPacket(new D2gsPacket(bytes));
            Assert.Equal(Act.Act1, packet.Act);
            Assert.Equal(Area.RogueEncampment, packet.Area);

        }

        [Fact]
        public void ChatPacket()
        {
            var bytes = new byte[] { 0x26, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x56, 0x69, 0x73, 0x69, 0x74, 0x20, 0x6F, 0x75, 0x72, 0x20, 0x77, 0x65, 0x62, 0x73, 0x69, 0x74, 0x65, 0x20, 0x61, 0x74, 0x20, 0x44, 0x69, 0x61, 0x62, 0x6C, 0x6F, 0x30, 0x39, 0x2E, 0x63, 0x6F, 0x6D, 0x21, 0x20, 0x52, 0x65, 0x6D, 0x65, 0x6D, 0x62, 0x65, 0x72, 0x20, 0x74, 0x6F, 0x20, 0x6A, 0x6F, 0x69, 0x6E, 0x20, 0x6F, 0x75, 0x72, 0x20, 0x64, 0x69, 0x73, 0x63, 0x6F, 0x72, 0x64, 0x20, 0x63, 0x68, 0x61, 0x6E, 0x6E, 0x65, 0x6C, 0x20, 0x74, 0x6F, 0x6F, 0x21, 0x00 };
            var packet = new ChatPacket(new D2gsPacket(bytes));
            Assert.Equal("", packet.CharacterName);
            Assert.Equal("Visit our website at Diablo09.com! Remember to join our discord channel too!", packet.Message);

        }

        [Fact]
        public void EntityMovePacket()
        {
            var bytes = new byte[] { 0x0F, 0x00, 0x02, 0x00, 0x00, 0x00, 0x17, 0x11, 0x16, 0x99, 0x15, 0x00, 0x16, 0x16, 0x94, 0x15 };
            var packet = new EntityMovePacket(new D2gsPacket(bytes));
            Assert.Equal(2U, packet.UnitId);
            Assert.Equal(EntityType.Player, packet.UnitType);
            Assert.Equal(new Point(5654, 5524), packet.CurrentLocation);
            Assert.Equal(new Point(5649, 5529), packet.MoveToLocation);
        }

        [Fact]
        public void UpdateSelfPacket1()
        {
            var bytes = new byte[] { 0x95, 0x50, 0x06, 0x24, 0x00, 0xCA, 0x20, 0x78, 0xC2, 0xED, 0x00, 0x00, 0x00 };
            var packet = new LifeManaUpdatePacket(new D2gsPacket(bytes));
            Assert.Equal(1616, packet.Life);
            Assert.Equal(72, packet.Mana);
            Assert.Equal(808, packet.Stamina);
            Assert.Equal(new Point(5057, 1902), packet.Location);
        }

        [Fact]
        public void AssignNPC1()
        {
            var bytes = new byte[] { 0x56, 0xE9, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x17, 0x1F, 0x79, 0x20, 0x33, 0x01, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xB0, 0x9D, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x11, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var packet = new AssignNpcPacket(new D2gsPacket(bytes));
            Assert.Equal(NPCCode.VileHunter, packet.UniqueCode);
            Assert.Equal(new Point(7959, 8313), packet.Location);
            Assert.Equal(39.84375, packet.LifePercentage);
            Assert.Equal(new HashSet<MonsterEnchantment> { MonsterEnchantment.ExtraFast, MonsterEnchantment.LightningEnchanted, MonsterEnchantment.Teleportation }, packet.MonsterEnchantments);
        }

        [Fact]
        public void AssignNPC2()
        {
            var bytes = new byte[] { 0x56, 0xE5, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x0C, 0x1F, 0x70, 0x20, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var packet = new AssignNpcPacket(new D2gsPacket(bytes));
            Assert.Equal(NPCCode.VileHunter, packet.UniqueCode);
            Assert.Equal(new Point(7948, 8304), packet.Location);
            Assert.Empty(packet.MonsterEnchantments);
        }

        [Fact]
        public void AssignMerc()
        {
            var bytes = new byte[] { 0x81, 0x07, 0x52, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x8D, 0x51, 0x11, 0x48, 0x03, 0x04, 0x00, 0x00 };
            var packet = new AssignMercPacket(new D2gsPacket(bytes));
            Assert.Equal(1u, packet.PlayerEntityId);
            Assert.Equal(1u, packet.MercEntityId);
        }

        [Fact]
        public void NPCStopPacket()
        {
            var bytes = new byte[] { 0x6D, 0x95, 0x01, 0x00, 0x00, 0x18, 0x62, 0xE7, 0x16, 0x80 };
            var packet = new NPCStopPacket(new D2gsPacket(bytes));
            Assert.Equal(405u, packet.EntityId);
            Assert.Equal(25112, packet.Location.X);
            Assert.Equal(5863, packet.Location.Y);
            Assert.Equal(100, packet.LifePercentage);
        }

        
     }
}
