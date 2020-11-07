using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Items
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

        public uint PlayerId { get; set; }

        public byte Unknown2 { get; set; }

        public ItemName Name { get; set; }

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

        public uint BeltRows { get; set; }

        public Dictionary<StatType, ItemProperty> Properties { get; set; }


        public int GetValueOfStatType(StatType statType)
        {
            var result = Properties.GetValueOrDefault(statType)?.Value ?? 0;
            switch (statType)
            {
                case StatType.MaximumDamage:
                    result += Properties.GetValueOrDefault(StatType.MinimumDamage)?.MaximumValue ?? 0;
                    break;
                case StatType.MaximumFireDamage:
                    result += Properties.GetValueOrDefault(StatType.MinimumFireDamage)?.MaximumValue ?? 0;
                    break;
                case StatType.MaximumPoisonDamage:
                    result += Properties.GetValueOrDefault(StatType.MinimumPoisonDamage)?.MaximumValue ?? 0;
                    break;
                case StatType.MaximumColdDamage:
                    result += Properties.GetValueOrDefault(StatType.MinimumColdDamage)?.MaximumValue ?? 0;
                    break;
                case StatType.MaximumLightningDamage:
                    result += Properties.GetValueOrDefault(StatType.MinimumLightningDamage)?.MaximumValue ?? 0;
                    break;
                default:
                    break;
            }

            return result;
        }

        public int GetTotalResistFrLrCr()
        {
            var totalResist = 0;
            totalResist += GetValueOfStatType(StatType.ColdResistance);
            totalResist += GetValueOfStatType(StatType.FireResistance);
            totalResist += GetValueOfStatType(StatType.LightningResistance);
            return totalResist;
        }
        public int GetTotalLifeFromStats(CharacterClass characterClass)
        {
            var totalLife = GetValueOfStatType(StatType.Life); ;
            switch (characterClass)
            {
                case CharacterClass.Amazon:
                case CharacterClass.Sorceress:
                case CharacterClass.Necromancer:
                case CharacterClass.Paladin:
                case CharacterClass.Druid:
                case CharacterClass.Assassin:
                    totalLife += GetValueOfStatType(StatType.Dexterity) * 2;
                    totalLife += GetValueOfStatType(StatType.Strength) * 2;
                    totalLife += GetValueOfStatType(StatType.Mana);
                    break;
                case CharacterClass.Barbarian:
                    totalLife += GetValueOfStatType(StatType.Dexterity) * 4;
                    totalLife += GetValueOfStatType(StatType.Strength) * 4;
                    break;

            }

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
                switch(property.Key)
                {
                    case StatType.SingleSkill1:
                    case StatType.SingleSkill2:
                    case StatType.SingleSkill3:
                    case StatType.SingleSkill4:
                        fullDescription += $"\t{property.Value.Skill} : {property.Value.Value} \r\n";
                        break;
                    case StatType.MinimumDamage:
                    case StatType.MinimumFireDamage:
                    case StatType.MinimumPoisonDamage:
                    case StatType.MinimumColdDamage:
                    case StatType.MinimumLightningDamage:
                        var maximumDamageValue = property.Value.MaximumValue > 0 ? $" - {property.Value.MaximumValue}" : "";
                        fullDescription += $"\t{property.Key} : {property.Value.Value}{maximumDamageValue} \r\n";
                        break;
                    default:
                        fullDescription += $"\t{property.Key} : {property.Value.Value} \r\n";
                        break;
                }
                
            }

            return fullDescription;
        }
    }
}