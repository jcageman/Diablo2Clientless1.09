using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using Xunit;

namespace D2NG.Core.Tests.D2GS
{
    public class ParseItemPacketTests
    {
        [Fact]
        public void ParseItemPacket1()
        {
            var bytes = new byte[] { 0x9D, 0x05, 0x35, 0x10, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x10, 0x4A, 0x12, 0xD6, 0x56, 0x07, 0x02, 0xAE, 0x0D, 0xD2, 0xC4, 0x5C, 0xB8, 0x88, 0x2F, 0xE8, 0x81, 0x40, 0x48, 0x80, 0x2B, 0x01, 0x60, 0x5A, 0xA1, 0x60, 0x23, 0x20, 0x90, 0x49, 0xD1, 0x21, 0x62, 0xF8, 0x0F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.Amulet, packet.Item.Name);
            Assert.Equal(ClassificationType.Amulet, packet.Item.Classification);
            Assert.Equal(6, packet.Item.Properties.Count);
            Assert.Equal(10, packet.Item.Properties[StatType.ColdResistance].Value);
            Assert.Equal(29, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.BarbarianSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.MinimumColdDamage].MinimumValue);
            Assert.Equal(4, packet.Item.Properties[StatType.MinimumColdDamage].MaximumValue);
            Assert.Equal(3, packet.Item.Properties[StatType.DamageReduction].Value);
            Assert.Equal(6, packet.Item.Properties[StatType.Strength].Value);
        }

        [Fact]
        public void ParseItemPacket2()
        {
            var bytes = new byte[] { 0x9C, 0x04, 0x38, 0x07, 0x37, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x00, 0x04, 0x22, 0x36, 0x87, 0x06, 0x82, 0xAF, 0x21, 0x6C, 0x75, 0x00, 0x31, 0x90, 0x05, 0x29, 0xF1, 0x06, 0x23, 0x90, 0x02, 0x0A, 0x0A, 0x04, 0x0C, 0x14, 0x28, 0x33, 0xB2, 0x13, 0x22, 0x29, 0x26, 0x56, 0x4C, 0xB4, 0x98, 0x00, 0x80, 0x72, 0x62, 0xF8, 0x0F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.BoneShield, packet.Item.Name);
            Assert.Equal(ClassificationType.Shield, packet.Item.Classification);
            Assert.Equal(31U, packet.Item.Defense);
            Assert.Equal(40U, packet.Item.Durability);
            Assert.Equal(40U, packet.Item.MaximumDurability);
            Assert.Equal(9, packet.Item.Properties.Count);
            Assert.Equal(30, packet.Item.Properties[StatType.FasterBlockRate].Value);
            Assert.Equal(20, packet.Item.Properties[StatType.IncreasedBlocking].Value);
            Assert.Equal(24, packet.Item.Properties[StatType.EnhancedDefense].Value);
            Assert.Equal(34, packet.Item.Properties[StatType.FireResistance].Value);
            Assert.Equal(19, packet.Item.Properties[StatType.ColdResistance].Value);
            Assert.Equal(19, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(19, packet.Item.Properties[StatType.PoisonResistance].Value);
            Assert.Equal(6, packet.Item.Properties[StatType.AttackerTakesDamage].Value);
            Assert.Equal(8, packet.Item.Properties[StatType.Strength].Value);
        }

        [Fact]
        public void ParseItemPacket3()
        {
            var bytes = new byte[] { 0x9D, 0x08, 0x30, 0x10, 0x12, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x50, 0x4A, 0x10, 0xD6, 0x56, 0x07, 0x02, 0xAD, 0x8D, 0x50, 0xC3, 0x32, 0x98, 0x80, 0x59, 0x48, 0x85, 0x48, 0xB0, 0xE2, 0x62, 0x44, 0x60, 0x45, 0x4A, 0xCA, 0x16, 0x23, 0xFF, 0x01 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.Amulet, packet.Item.Name);
            Assert.Equal(ClassificationType.Amulet, packet.Item.Classification);
            Assert.Equal(5, packet.Item.Properties.Count);
            Assert.Equal(23, packet.Item.Properties[StatType.ColdResistance].Value);
            Assert.Equal(35, packet.Item.Properties[StatType.PoisonResistance].Value);
            Assert.Equal(2, packet.Item.Properties[StatType.SorceressSkills].Value);
            Assert.Equal(7, packet.Item.Properties[StatType.ReplenishLife].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.MagicalDamageReduction].Value);
        }

        [Fact]
        public void ParseItemPacket4()
        {
            var bytes = new byte[] { 0x9D, 0x08, 0x3A, 0x07, 0x5C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x08, 0x80, 0x00, 0x01, 0xB0, 0x00, 0x80, 0x07, 0xB7, 0x06, 0x12, 0xA7, 0x31, 0x0B, 0x95, 0x08, 0x9D, 0x90, 0x05, 0x0D, 0x20, 0x06, 0x8C, 0xB8, 0xA9, 0x89, 0x16, 0x27, 0x63, 0x4A, 0x27, 0x28, 0x52, 0x50, 0xAC, 0xA0, 0x10, 0x21, 0x50, 0xA0, 0xCC, 0xC8, 0xFE, 0x03 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.BarbedShield, packet.Item.Name);
            Assert.Equal(ClassificationType.Shield, packet.Item.Classification);
            Assert.Equal(60U, packet.Item.Defense);
            Assert.True(packet.Item.HasSockets);
            Assert.Equal(1U, packet.Item.Sockets);
            Assert.Equal(1U, packet.Item.UsedSockets);
            Assert.Equal(53U, packet.Item.Durability);
            Assert.Equal(55U, packet.Item.MaximumDurability);
            Assert.Equal(8, packet.Item.Properties.Count);
            Assert.Equal(17, packet.Item.Properties[StatType.FasterHitRecovery].Value);
            Assert.Equal(30, packet.Item.Properties[StatType.FasterBlockRate].Value);
            Assert.Equal(20, packet.Item.Properties[StatType.IncreasedBlocking].Value);
            Assert.Equal(20, packet.Item.Properties[StatType.FireResistance].Value);
            Assert.Equal(20, packet.Item.Properties[StatType.ColdResistance].Value);
            Assert.Equal(20, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(39, packet.Item.Properties[StatType.PoisonResistance].Value);
            Assert.Equal(2, packet.Item.Properties[StatType.DamageReduction].Value);
        }

        [Fact]
        public void ParseItemPacket5()
        {
            var bytes = new byte[] { 0x9D, 0x08, 0x2F, 0x10, 0x12, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0xF0, 0x30, 0x21, 0x97, 0xE6, 0x06, 0x02, 0xED, 0x95, 0xCE, 0x48, 0x40, 0xD3, 0x64, 0x94, 0x11, 0x30, 0x98, 0x12, 0x2A, 0xA9, 0x12, 0x2B, 0xB9, 0x92, 0x59, 0xA1, 0x95, 0xFF };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.Ring, packet.Item.Name);
            Assert.Equal(ClassificationType.Ring, packet.Item.Classification);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(11, packet.Item.Properties.Count);
            Assert.Equal(20, packet.Item.Properties[StatType.Mana].Value);
            Assert.Equal(25, packet.Item.Properties[StatType.EnhancedMana].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.MinimumLightningDamage].MinimumValue);
            Assert.Equal(12, packet.Item.Properties[StatType.MinimumLightningDamage].MaximumValue);
            Assert.Equal(1, packet.Item.Properties[StatType.AmazonSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.PaladinSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.NecromancerSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.SorceressSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.BarbarianSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.DruidSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.AssassinSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.AllSkills].Value);
        }

        [Fact]
        public void ParseItemPacket6()
        {
            var bytes = new byte[] { 0x9D, 0x05, 0x36, 0x05, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x10, 0x04, 0x22, 0x13, 0x86, 0x07, 0x82, 0xA5, 0xE1, 0x3B, 0xF1, 0x07, 0x4B, 0xF0, 0x0B, 0x2B, 0x10, 0x07, 0x53, 0x00, 0xF3, 0x91, 0xE2, 0x41, 0x46, 0x70, 0xE0, 0x8A, 0xBA, 0xC8, 0x4E, 0x3C, 0x58, 0x10, 0xFC, 0x07 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.DoubleAxe, packet.Item.Name);
            Assert.Equal(ClassificationType.Axe, packet.Item.Classification);
            Assert.Equal(31U, packet.Item.Durability);
            Assert.Equal(48U, packet.Item.MaximumDurability);
            Assert.Equal(6, packet.Item.Properties.Count);
            Assert.Equal(15, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.MinimumLightningDamage].MinimumValue);
            Assert.Equal(7, packet.Item.Properties[StatType.MinimumLightningDamage].MaximumValue);
            Assert.Equal(2, packet.Item.Properties[StatType.BarbarianSkills].Value);
            Assert.Equal(30, packet.Item.Properties[StatType.IncreasedAttackSpeed].Value);
            Assert.Equal(15, packet.Item.Properties[StatType.FireResistance].Value);
            Assert.Equal(2, packet.Item.Properties[StatType.MaximumDamage].Value);
        }

        [Fact]
        public void ParseItemPacket7()
        {
            var bytes = new byte[] { 0x9D, 0x08, 0x38, 0x05, 0x3B, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x08, 0x80, 0x00, 0x01, 0x90, 0x04, 0x90, 0x73, 0xD6, 0x06, 0x12, 0xAD, 0xE1, 0xBB, 0xF0, 0x03, 0x4B, 0xB0, 0x07, 0xF3, 0x30, 0x02, 0x65, 0x80, 0x57, 0x17, 0x13, 0xDC, 0x88, 0xB0, 0x6A, 0x95, 0x8C, 0x00, 0x41, 0x8A, 0x03, 0x1E, 0x85, 0x0B, 0xC2, 0x7F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.MarteldeFer, packet.Item.Name);
            Assert.Equal(ClassificationType.Hammer, packet.Item.Classification);
            Assert.Equal(6, packet.Item.Properties.Count);
            Assert.Equal(110, packet.Item.Properties[StatType.AttackRating].Value);
            Assert.Equal(171, packet.Item.Properties[StatType.EnhancedMaximumDamage].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.MinimumLightningDamage].Value);
            Assert.Equal(8, packet.Item.Properties[StatType.MinimumLightningDamage].MaximumValue);
            Assert.Equal(7, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(5, packet.Item.Properties[StatType.MinimumLifeStolenPerHit].Value);
            Assert.Equal(2, packet.Item.Properties[StatType.SecondaryMinimumDamage].Value);
        }

        [Fact]
        public void ParseItemPacket8()
        {
            var bytes = new byte[] { 0x9D, 0x08, 0x31, 0x01, 0x29, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x70, 0x70, 0x60, 0x56, 0xC7, 0x06, 0x02, 0xE7, 0xB1, 0x05, 0xAC, 0x18, 0x19, 0x41, 0x30, 0x74, 0x62, 0x24, 0xC5, 0xC8, 0x8A, 0x91, 0x16, 0x23, 0x4E, 0x14, 0x59, 0xEC, 0x09, 0xB2, 0xFF };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.FullPlateMail, packet.Item.Name);
            Assert.Equal(ClassificationType.Armor, packet.Item.Classification);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(162U, packet.Item.Defense);
            Assert.Equal(70U, packet.Item.Durability);
            Assert.Equal(70U, packet.Item.MaximumDurability);
            Assert.Equal(8, packet.Item.Properties.Count);
            Assert.Equal(134, packet.Item.Properties[StatType.EnhancedDefense].Value);
            Assert.Equal(35, packet.Item.Properties[StatType.FireResistance].Value);
            Assert.Equal(35, packet.Item.Properties[StatType.ColdResistance].Value);
            Assert.Equal(35, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(35, packet.Item.Properties[StatType.PoisonResistance].Value);
            Assert.Equal(10, packet.Item.Properties[StatType.AttackerTakesDamage].Value);
            Assert.Equal(2, packet.Item.Properties[StatType.LightRadius].Value);
            Assert.Equal(100, packet.Item.Properties[StatType.ExtraGold].Value);
        }

        [Fact]
        public void ParseItemPacket9()
        {
            var bytes = new byte[] { 0x9D, 0x05, 0x2E, 0x10, 0x13, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x10, 0x0C, 0x4A, 0x77, 0xC6, 0x06, 0x82, 0xE5, 0x91, 0x06, 0x16, 0x48, 0x28, 0xA4, 0x41, 0x6D, 0xC8, 0xF0, 0x13, 0x30, 0x02, 0x0C, 0x3E, 0x50, 0x00, 0xA1, 0xC3, 0x7F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.LightGauntlets, packet.Item.Name);
            Assert.Equal(ClassificationType.Gloves, packet.Item.Classification);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(12U, packet.Item.Defense);
            Assert.Equal(10U, packet.Item.Durability);
            Assert.Equal(18U, packet.Item.MaximumDurability);
            Assert.Equal(6, packet.Item.Properties.Count);
            Assert.Equal(20, packet.Item.Properties[StatType.FasterCastRate].Value);
            Assert.Equal(25, packet.Item.Properties[StatType.ManaRecoveryBonus].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.FireSkills].Value);
            Assert.Equal(1, packet.Item.Properties[StatType.MinimumFireDamage].MinimumValue);
            Assert.Equal(6, packet.Item.Properties[StatType.MinimumFireDamage].MaximumValue);
            Assert.Equal(10, packet.Item.Properties[StatType.Defense].Value);
            Assert.Equal(29, packet.Item.Properties[StatType.EnhancedDefense].Value);
        }

        [Fact]
        public void ParseItemPacket10()
        {
            var bytes = new byte[] { 0x9D, 0x05, 0x2D, 0x05, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x10, 0x46, 0x2A, 0x36, 0x47, 0x07, 0x02, 0x2E, 0xB1, 0x85, 0x29, 0x40, 0x41, 0x59, 0x91, 0x12, 0x71, 0x8D, 0x8E, 0xC1, 0xA6, 0x46, 0x68, 0xA3, 0x42, 0xFC, 0x07 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.BattleStaff, packet.Item.Name);
            Assert.Equal(QualityType.Magical, packet.Item.Quality);
            Assert.Equal(ClassificationType.Staff, packet.Item.Classification);
            Assert.Equal(5, packet.Item.Properties.Count);
            Assert.Equal(2, packet.Item.Properties[StatType.SorceressSkills].Value);
            Assert.Equal(4, packet.Item.Properties[StatType.ReplenishLife].Value);
            Assert.Equal(3, packet.Item.GetValueToSkill(Skill.EnergyShield));
            Assert.Equal(1, packet.Item.GetValueToSkill(Skill.ChainLightning));
            Assert.Equal(2, packet.Item.GetValueToSkill(Skill.StaticField));
        }

        [Fact]
        public void ParseItemPacket11()
        {
            var bytes = new byte[] { 0x9C, 0x04, 0x17, 0x10, 0x1B, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x00, 0x10, 0x22, 0xF6, 0x86, 0x07, 0x82, 0x86, 0xF0, 0x1F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.HoradricCube, packet.Item.Name);
            Assert.Equal(QualityType.Normal, packet.Item.Quality);
            Assert.Equal(ClassificationType.QuestItem, packet.Item.Classification);
            Assert.Empty(packet.Item.Properties);
        }

        [Fact]
        public void ParseItemPacket12()
        {
            var bytes = new byte[] { 0x9D, 0x15, 0x39, 0x01, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x00, 0x80, 0x00, 0x01, 0x00, 0x88, 0x28, 0xE7, 0x76, 0x06, 0x82, 0xDA, 0x31, 0x05, 0x3B, 0x68, 0x38, 0x4C, 0xA0, 0x00, 0x45, 0x51, 0x51, 0x58, 0x14, 0x17, 0x25, 0xCB, 0x85, 0x90, 0x39, 0xA1, 0x90, 0x42, 0x61, 0x85, 0x42, 0x0B, 0x05, 0x08, 0x5D, 0xFE, 0x03 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.RingMail, packet.Item.Name);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(ClassificationType.Armor, packet.Item.Classification);
            Assert.Equal(12, packet.Item.Properties.Count);
            Assert.Equal(50, packet.Item.Properties[StatType.DefenseVsMelee].Value);
            Assert.Equal(5, packet.Item.Properties[StatType.MaximumFireResistance].Value);
            Assert.Equal(5, packet.Item.Properties[StatType.MaximumColdResistance].Value);
            Assert.Equal(5, packet.Item.Properties[StatType.MaximumLightningResistance].Value);
            Assert.Equal(5, packet.Item.Properties[StatType.MaximumPoisonResistance].Value);
        }

        [Fact]
        public void ParseItemPacket13()
        {
            var bytes = new byte[] { 0x9D, 0x15, 0x34, 0x05, 0x44, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x00, 0x80, 0x00, 0x01, 0x00, 0x08, 0x98, 0x77, 0xE7, 0x06, 0x82, 0xE5, 0xB1, 0x00, 0x1E, 0x17, 0x32, 0x82, 0x04, 0x29, 0x50, 0x12, 0xB4, 0xA4, 0x91, 0xAD, 0x51, 0x22, 0xD8, 0x34, 0x09, 0x6D, 0x84, 0x8C, 0x5A, 0x4C, 0xC6, 0x7F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.YewWand, packet.Item.Name);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(ClassificationType.Wand, packet.Item.Classification);
            Assert.Equal(8, packet.Item.Properties.Count);
            Assert.Equal(1, packet.Item.Properties[StatType.MinimumLightningDamage].Value);
            Assert.Equal(40, packet.Item.Properties[StatType.LightningResistance].Value);
            Assert.Equal(3, packet.Item.GetValueToSkill(Skill.AmplifyDamage));
            Assert.Equal(1, packet.Item.GetValueToSkill(Skill.Terror));
            Assert.Equal(2, packet.Item.GetValueToSkill(Skill.CorpseExplosion));
            Assert.Equal(3, packet.Item.GetValueToSkill(Skill.IronMaiden));
        }

        [Fact]
        public void ParseItemPacket14()
        {
            var bytes = new byte[] { 0x9C, 0x00, 0x1B, 0x10, 0x87, 0x00, 0x00, 0x00, 0x10, 0x20, 0x80, 0x00, 0x01, 0x6C, 0x94, 0xA8, 0xF0, 0xA3, 0xEC, 0x0D, 0x0D, 0x04, 0xCC, 0xE3, 0xFF, 0xFF, 0x03 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.EssenceOfHatred, packet.Item.Name);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(ClassificationType.Essence, packet.Item.Classification);
        }

        [Fact]
        public void ParseItemPacketElementSkillsCharm()
        {
            var bytes = new byte[] { 0x9C, 0x04, 0x1D, 0x10, 0x64, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x64, 0x00, 0x04, 0x38, 0xD6, 0x36, 0x03, 0x02, 0x2D, 0x15, 0xC6, 0x00, 0x80, 0x57, 0x0C, 0xFF, 0x01, 0x47, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.GrandCharm, packet.Item.Name);
            Assert.Equal(QualityType.Magical, packet.Item.Quality);
            Assert.Equal(SkillTab.DruidElemental, packet.Item.Properties[StatType.SkillTab1].SkillTab);
            Assert.Equal(1, packet.Item.Properties[StatType.SkillTab1].Value);
        }

        [Fact]
        public void ParseItemPacketColdSkillsCharm()
        {
            var bytes = new byte[] { 0x9C, 0x04, 0x1D, 0x10, 0x03, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x64, 0x00, 0xAE, 0x32, 0xD6, 0x36, 0x03, 0x02, 0x2D, 0x15, 0xAE, 0x00, 0x80, 0x57, 0x09, 0xFF, 0x01 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.GrandCharm, packet.Item.Name);
            Assert.Equal(QualityType.Magical, packet.Item.Quality);
            Assert.Equal(SkillTab.SorceressColdSpells, packet.Item.Properties[StatType.SkillTab1].SkillTab);
            Assert.Equal(1, packet.Item.Properties[StatType.SkillTab1].Value);
        }

        [Fact]
        public void ParseArmOfKingLeoric()
        {
            var bytes = new byte[] { 0x9D, 0x15, 0x3B, 0x05, 0x02, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x00, 0x80, 0x00, 0x64, 0x00, 0x08, 0x98, 0x23, 0x76, 0x07, 0x02, 0xED, 0xD1, 0x08, 0x1E, 0x18, 0xBC, 0x90, 0x48, 0xD6, 0x45, 0x15, 0xB2, 0x29, 0xCA, 0xB0, 0x08, 0x45, 0x1A, 0x4F, 0xAF, 0x23, 0xD6, 0x34, 0x11, 0x6C, 0xA0, 0x88, 0x36, 0x45, 0x46, 0x2D, 0x23, 0xE3, 0x3F };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.TombWand, packet.Item.Name);
            Assert.Equal(QualityType.Unique, packet.Item.Quality);
            Assert.Equal(SkillTab.NecromancerCurses, packet.Item.Properties[StatType.SkillTab1].SkillTab);
            Assert.Equal(SkillTab.NecromancerSummoningSpells, packet.Item.Properties[StatType.SkillTab2].SkillTab);
            Assert.Equal(Skill.Terror, packet.Item.Properties[StatType.SingleSkill1].Skill);
            Assert.Equal(Skill.RaiseSkeletalMage, packet.Item.Properties[StatType.SingleSkill2].Skill);
            Assert.Equal(Skill.SkeletonMastery, packet.Item.Properties[StatType.SingleSkill3].Skill);
            Assert.Equal(Skill.RaiseSkeleton, packet.Item.Properties[StatType.SingleSkill4].Skill);
            Assert.Equal(Skill.BoneSpirit, packet.Item.Properties[StatType.SkillWhenStruck1].Skill);
            Assert.Equal(Skill.BonePrison, packet.Item.Properties[StatType.SkillWhenStruck2].Skill);
        }

        [Fact]
        public void HellspawnSkull()
        {
            var bytes = new byte[] { 0x9D, 0x05, 0x4C, 0x0A, 0x76, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x64, 0x10, 0x26, 0xE2, 0x56, 0x36, 0x06, 0x82, 0xAE, 0x39, 0x02, 0xD7, 0xAA, 0x02, 0x28, 0x9E, 0x34, 0x18, 0x83, 0x3B, 0xE8, 0x07, 0x4C, 0x28, 0x14, 0x20, 0x5C, 0x60, 0x26, 0xA5, 0x20, 0x85, 0x9D, 0x48, 0xB0, 0x30, 0xC0, 0x30, 0x00, 0x35, 0x48, 0x71, 0x00, 0xE0, 0xB4, 0xA6, 0x8B, 0x60, 0x13, 0x25, 0xB4, 0xC1, 0x12, 0x72, 0x80, 0x40, 0x42, 0xF6, 0x1F, 0x47, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var packet = new ParseItemPacket(new D2gsPacket(bytes));
            Assert.Equal(ItemName.HellspawnSkull, packet.Item.Name);
            Assert.Equal(QualityType.Rare, packet.Item.Quality);
        }
    }
}
