using D2NG.D2GS.Players;
using System.Collections.Generic;

namespace D2NG.D2GS.Items
{
    public class Item
    {
        public Item()
        {
            Prefixes = new List<uint>();
            Suffixes = new List<uint>();
            Sockets = uint.MaxValue;
            Properties = new Dictionary<StatType, ItemProperty>();
        }

        public string Name { get; set; }

        public bool Ethereal { get; set; }

        public uint Sockets { get; set; }

        public QualityType Quality { get; set; }

        public string Type { get; set; }

        public Action Action { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }

        public bool Stackable { get; set; }

        public bool Usable { get; set; }

        public bool Throwable { get; set; }

        public uint Category { get; set; }
        public uint Id { get; set; }
        public bool IsEquipped { get; set; }
        public bool IsInSocket { get; set; }
        public bool IsIdentified { get; set; }
        public bool IsSwitchedIn { get; set; }
        public bool IsSwitchedOut { get; set; }
        public bool IsBroken { get; set; }
        public bool IsPotion { get; set; }
        public bool IsInStore { get; set; }
        public bool IsNotInASocket { get; set; }
        public bool IsEar { get; set; }
        public bool IsStartItem { get; set; }
        public bool IsSimpleItem { get; set; }
        public bool IsPersonalised { get; set; }
        public bool Gambling { get; set; }
        public bool IsRuneword { get; set; }
        public bool Ground { get; set; }
        public VersionType Version { get; set; }
        public bool UnspecifiedDirectory { get; set; }
        public Point Location { get; set; }
        public uint Directory { get; set; }
        public ContainerType Container { get; set; }
        public uint EarLevel { get; set; }
        public string EarName { get; set; }
        public bool IsGold { get; set; }
        public uint Amount { get; set; }
        public uint UsedSockets { get; set; }
        public uint Level { get; set; }
        public bool HasGraphic { get; set; }
        public uint Graphic { get; set; }
        public bool HasColour { get; set; }
        public uint Colour { get; set; }
        public uint Prefix { get; set; }
        public uint Suffix { get; set; }
        public uint SetCode { get; set; }
        public uint UniqueCode { get; set; }
        public List<uint> Prefixes { get; set; }
        public List<uint> Suffixes { get; set; }
        public uint RunewordId { get; set; }
        public uint RunewordParameter { get; set; }
        public SuperiorItemClassType Superiority { get; set; }
        public string PersonalisedName { get; set; }

        public ClassificationType Classification { get; set; }
        public bool IsArmor { get; set; }
        public bool IsWeapon { get; set; }
        public uint Defense { get; set; }
        public bool IsIndestructible { get; set; }
        public uint Durability { get; set; }
        public uint MaximumDurability { get; set; }
        public bool HasSockets { get; set; }

        public Dictionary<StatType, ItemProperty> Properties { get; set; }


        public int GetValueOfStatType(StatType statType)
        {
            return Properties.GetValueOrDefault(statType)?.Value ?? 0;
        }

        public int GetTotalResist()
        {
            var totalResist = 0;
            totalResist += GetValueOfStatType(StatType.ColdResistance);
            totalResist += GetValueOfStatType(StatType.FireResistance);
            totalResist += GetValueOfStatType(StatType.LightningResistance);
            totalResist += GetValueOfStatType(StatType.PoisonResistance) / 2;
            return totalResist;
        }
        public int GetTotalLifeFromStats()
        {
            var totalLife = 0;
            totalLife += GetValueOfStatType(StatType.Life);
            totalLife += GetValueOfStatType(StatType.Dexterity) * 4;
            totalLife += GetValueOfStatType(StatType.Strength) * 4;
            return totalLife;
        }

        public int GetToClassSkills()
        {
            if (GetValueOfStatType(StatType.AllSkills) > 0)
            {
                return GetValueOfStatType(StatType.AllSkills);
            }

            var totalToSkills = 0;
            totalToSkills += GetValueOfStatType(StatType.SorceressSkills);
            totalToSkills += GetValueOfStatType(StatType.NecromancerSkills);
            totalToSkills += GetValueOfStatType(StatType.BarbarianSkills);
            totalToSkills += GetValueOfStatType(StatType.PaladinSkills);
            totalToSkills += GetValueOfStatType(StatType.AmazonSkills);
            totalToSkills += GetValueOfStatType(StatType.AssassinSkills);
            totalToSkills += GetValueOfStatType(StatType.DruidSkills);
            return totalToSkills;
        }

        public int GetValueToSkill(Skill skill)
        {
            var result = 0;
            var singleSkill1 = Properties.GetValueOrDefault(StatType.SingleSkill1);
            if (singleSkill1 != null && singleSkill1.Skill == skill)
            {
                result += singleSkill1.Value;
            }

            var singleSkill2 = Properties.GetValueOrDefault(StatType.SingleSkill2);
            if (singleSkill2 != null && singleSkill2.Skill == skill)
            {
                result += singleSkill2.Value;
            }

            var singleSkill3 = Properties.GetValueOrDefault(StatType.SingleSkill3);
            if (singleSkill3 != null && singleSkill3.Skill == skill)
            {
                result += singleSkill3.Value;
            }

            var singleSkill4 = Properties.GetValueOrDefault(StatType.SingleSkill4);
            if (singleSkill4 != null && singleSkill4.Skill == skill)
            {
                result += singleSkill4.Value;
            }

            return result;
        }

        public string GetFullDescription()
        {
            string fullDescription = $"{Name} ({Level}): \r\n";
            foreach (var property in Properties)
            {
                fullDescription += $"\t{property.Key} : {property.Value.Value} \r\n";
            }

            return fullDescription;
        }
    }
}