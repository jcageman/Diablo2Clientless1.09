using System;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Action = D2NG.Core.D2GS.Items.Action;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    internal class ParseItemPacket : D2gsPacket
    {
        public Item Item { get; }

        public ParseItemPacket(D2gsPacket packet) : base(packet.Raw)
        {
            Item = Parse(Raw.ToList());
        }

        private static void GenericInfo(BitReader reader, ref Item item) // get basic info such as item
        {
            byte packet = reader.ReadByte();
            item.Action = (Action)reader.ReadByte();
            item.Category = reader.ReadByte();
            _ = reader.ReadByte();
            item.Id = reader.ReadUInt32();
            if (packet == 0x9d)
            {
                item.EntityType = (EntityType)reader.ReadByte();
                item.PlayerId = reader.ReadUInt32();
            }
        }

        private static void StatusInfo(BitReader reader, ref Item item) // get info for basic status info
        {
            item.IsEquipped = reader.ReadBit();
            reader.ReadBit();
            reader.ReadBit();
            item.IsInSocket = reader.ReadBit();
            item.IsIdentified = reader.ReadBit();
            reader.ReadBit();
            item.IsSwitchedIn = reader.ReadBit();
            item.IsSwitchedOut = reader.ReadBit();
            item.IsBroken = reader.ReadBit();
            reader.ReadBit();
            item.IsPotion = reader.ReadBit();
            item.HasSockets = reader.ReadBit();
            reader.ReadBit();
            item.IsInStore = reader.ReadBit();
            item.IsNotInASocket = reader.ReadBit();
            reader.ReadBit();
            item.IsEar = reader.ReadBit();
            item.IsStartItem = reader.ReadBit();
            reader.ReadBit();
            reader.ReadBit();
            reader.ReadBit();
            item.IsSimpleItem = reader.ReadBit();
            item.Ethereal = reader.ReadBit();
            reader.ReadBit();
            item.IsPersonalised = reader.ReadBit();
            item.Gambling = reader.ReadBit();
            item.IsRuneword = reader.ReadBit();
            reader.Read(5);
            item.Version = (VersionType)reader.ReadByte();
        }

        private static void ReadLocation(BitReader reader, ref Item item)
        {
            _ = reader.Read(2);
            item.Ground = reader.Read(3) == 0x03;

            if (item.Ground)
            {
                var x = reader.ReadUInt16();
                var y = reader.ReadUInt16();
                item.Location = new Point(x, y);
            }
            else
            {
                item.Directory = (byte)reader.Read(4);
                var x = (byte)reader.Read(4);
                var y = (byte)reader.Read(3);
                item.Location = new Point(x, y);
                item.Container = (ContainerType)(reader.Read(4));
            }
            item.UnspecifiedDirectory = false;

            if (item.Action == Action.AddToShop || item.Action == Action.RemoveFromShop)
            {
                long container = (long)(item.Container);
                container |= 0x80;
                if ((container & 1) != 0)
                {
                    container--; //remove first bit
                    item.Location = new Point(item.Location.X, (ushort)(item.Location.Y + 8));
                }
                item.Container = (ContainerType)container;
            }
            else if (item.Action == Action.PutInBelt || item.Action == Action.RemoveFromBelt || (item.IsPotion && item.Action == Action.UpdateStats))
            {
                item.Container = ContainerType.Belt;
                item.Location = new Point((ushort)(item.Location.X % 4), (ushort)(item.Location.X / 4));
            }
            else if (item.Container == ContainerType.Unspecified)
            {
                if (item.Directory == (uint)DirectoryType.not_applicable)
                {
                    if (item.IsInSocket)
                    {
                        //y is ignored for this container type, x tells you the index
                        item.Container = ContainerType.Item;
                    }
                }
                else
                {
                    item.UnspecifiedDirectory = true;
                }
            }
        }

        public static bool EarInfo(BitReader reader, ref Item item)
        {
            if (item.IsEar)
            {
                reader.Read(3);
                item.EarLevel = (byte)reader.Read(7);
                item.EarName = reader.ReadString();
                return true;
            }
            else
            {
                return false;
            }
        }

        public static bool ReadItemType(BitReader reader, ref Item item) // gets the 3 letter item code
        {
            byte[] code_bytes = new byte[4];
            for (int i = 0; i < code_bytes.Length; i++)
            {
                code_bytes[i] = reader.ReadByte();
            }
            code_bytes[3] = 0;

            item.Type = Encoding.ASCII.GetString(code_bytes).Substring(0, 3).ToLower();

            var entry = DataManager.DataManager.Instance.ItemData.Get(item.Type);
            if (entry == null)
            {
                Log.Error($"Failed to look up item of type {item.Type} in item data table");
                throw new NotImplementedException($"Failed to look up item of type {item.Type} in item data table");
            }

            item.Name = entry.Name;
            item.Width = entry.Width;
            item.Height = entry.Height;
            item.IsArmor = entry.IsArmor();
            item.IsWeapon = entry.IsWeapon();
            item.BeltRows = entry.GetBeltRows();
            item.Stackable = entry.Stackable;
            item.Usable = entry.Usable;
            item.Throwable = entry.Throwable;
            item.Classification = entry.Classification;

            if (item.Type == "gld")
            {
                item.IsGold = true;
                bool big_pile = reader.ReadBit();
                if (big_pile)
                {
                    item.Amount = (uint)reader.Read(32);
                }
                else
                {
                    item.Amount = (uint)reader.Read(12);
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        public static void ReadSocketInfo(BitReader reader, ref Item item)
        {
            item.UsedSockets = (byte)reader.Read(3);
        }

        public static bool ReadLevelQuality(BitReader reader, ref Item item)
        {
            item.Quality = QualityType.Normal;
            if (item.IsSimpleItem || item.Gambling)
            {
                return false;
            }
            item.Level = (byte)reader.Read(7);
            item.Quality = (QualityType)(reader.Read(4));
            return true;
        }

        public static void ReadGraphicInfo(BitReader reader, ref Item item)
        {
            item.HasGraphic = reader.ReadBit();
            if (item.HasGraphic)
            {
                item.Graphic = (byte)reader.Read(3);
            }

            item.HasColour = reader.ReadBit();
            if (item.HasColour)
            {
                item.Colour = (ushort)reader.Read(11);
            }
        }

        public static void ReadIdentifiedInfo(BitReader reader, ref Item item)
        {
            if (item.IsIdentified)
            {
                switch (item.Quality)
                {
                    case QualityType.Inferior:
                        item.Prefix = (byte)reader.Read(3);
                        break;
                    case QualityType.Superior:
                        item.Superiority = (SuperiorItemClassType)(reader.Read(3));
                        break;
                    case QualityType.Magical:
                        item.Prefix = (uint)reader.Read(11);
                        item.Suffix = (uint)reader.Read(11);
                        break;
                    case QualityType.Crafted:
                    case QualityType.Rare:
                        item.Prefix = (uint)reader.Read(8) - 156;
                        item.Suffix = (uint)reader.Read(8) - 1;
                        break;
                    case QualityType.Set:
                        item.SetCode = (uint)reader.Read(12);
                        break;
                    case QualityType.Unique:
                        if (item.Type != "std") //standard of heroes exception?
                        {
                            item.UniqueCode = (uint)reader.Read(12);
                        }
                        break;
                    default:
                        // No additional bits to read for item.Quality
                        break;
                }
            }

            if (item.Quality == QualityType.Rare || item.Quality == QualityType.Crafted)
            {
                for (var i = 0; i < 3; i++)
                {
                    var hasPrefix = reader.ReadBit();
                    if (hasPrefix)
                    {
                        item.Prefixes.Add((uint)reader.Read(11));
                    }
                    var hasSuffix = reader.ReadBit();
                    if (hasSuffix)
                    {
                        item.Suffixes.Add((uint)reader.Read(11));
                    }
                }
            }

            if (item.IsRuneword)
            {
                item.RunewordId = (uint)reader.Read(12);
                item.RunewordParameter = (byte)reader.Read(4);
            }

            if (item.IsPersonalised)
            {
                var personalisedName = new List<byte>();
                personalisedName.Add(reader.ReadByte());
                while (personalisedName.Last() != 0x00)
                {
                    personalisedName.Add(reader.ReadByte()); // 16 characters of 7 bits each for the name of the ear to process later
                }
                item.PersonalisedName = Encoding.ASCII.GetString(personalisedName.ToArray());
            }

            if (item.IsArmor)
            {
                item.Defense = (uint)reader.Read(10) - 10;
            }

            if (item.Type == "7cr")
            {
                reader.Read(8);
            }
            else if (item.IsArmor || item.IsWeapon)
            {
                item.MaximumDurability = (byte)reader.Read(8);
                item.IsIndestructible = item.MaximumDurability == 0 ? true : false;
                if(!item.IsIndestructible)
                {
                    item.Durability = (byte)reader.Read(8);
                }
            }
            if (item.HasSockets)
            {
                item.Sockets = (byte)reader.Read(4);
            }

            if (item.Stackable)
            {
                if (item.Usable) { reader.Read(5); }

                item.Amount = (uint)reader.Read(9);
            }

            if (!item.IsIdentified) { return; }

            if (item.Quality == QualityType.Set) { reader.Read(5); }

            while (true)
            {
                var statId = reader.Read(9);

                if (statId == 0x1ff)
                {
                    break;
                }

                if (!ProcessItemStat(statId, reader, out var itemProperty)) return;

                item.Properties.Add(itemProperty.Type, itemProperty);
            }

            //Seems the packet never contains all skills property, simply all skills listed, for ease of use we store allskills
            if (item.Properties.ContainsKey(StatType.PaladinSkills)
                && item.Properties.ContainsKey(StatType.NecromancerSkills)
                && item.Properties.ContainsKey(StatType.SorceressSkills)
                && item.Properties.ContainsKey(StatType.BarbarianSkills)
                && item.Properties.ContainsKey(StatType.DruidSkills)
                && item.Properties.ContainsKey(StatType.AssassinSkills))
            {
                item.Properties.Add(StatType.AllSkills, new ItemProperty() { Type = StatType.AllSkills, Value = item.Properties[StatType.PaladinSkills].Value });
            }
        }

        public static bool ProcessItemStat(int statId, BitReader reader, out ItemProperty itemProperty)
        {
            //lot of information for 1.09 from https://user.xmission.com/~trevin/DiabloIIv1.09_Magic_Properties.shtml
            //base of code from https://github.com/MephisTools/diablo2-protocol/blob/master/lib/utils/itemParser.js
            itemProperty = new ItemProperty();
            var propertyData = DataManager.DataManager.Instance.ItemPropertyData.Get(statId);
            if (propertyData == null)
            {
                Log.Error($"Invalid item stat ID: {statId}");
                return false;
            }

            itemProperty.Type = propertyData.StatType;
            switch (propertyData.StatType)
            {
                case StatType.DefensePerLevel:
                case StatType.EnhancedDefensePerLevel:
                case StatType.LifePerLevel:
                case StatType.ManaPerLevel:
                case StatType.MaximumDamagePerLevel:
                case StatType.MaximumEnhancedDamagePerLevel:
                case StatType.StrengthPerLevel:
                case StatType.DexterityPerLevel:
                case StatType.EnergyPerLevel:
                case StatType.VitalityPerLevel:
                case StatType.AttackRatingPerLevel:
                case StatType.BonusToAttackRatingPerLevel:
                case StatType.MaximumColdDamagePerLevel:
                case StatType.MaximumFireDamagePerLevel:
                case StatType.MaximumLightningDamagePerLevel:
                case StatType.MaximumPoisonDamagePerLevel:
                case StatType.ColdResistancePerLevel:
                case StatType.FireResistancePerLevel:
                case StatType.LightningResistancePerLevel:
                case StatType.PoisonResistancePerLevel:
                case StatType.ColdAbsorptionPerLevel:
                case StatType.FireAbsorptionPerLevel:
                case StatType.LightningAbsorptionPerLevel:
                case StatType.PoisonAbsorptionPerLevel:
                case StatType.ThornsPerLevel:
                case StatType.ExtraGoldPerLevel:
                case StatType.BetterChanceOfGettingMagicItemPerLevel:
                case StatType.StaminaRegenerationPerLevel:
                case StatType.StaminaPerLevel:
                case StatType.DamageToDemonsPerLevel:
                case StatType.DamageToUndeadPerLevel:
                case StatType.AttackRatingAgainstDemonsPerLevel:
                case StatType.AttackRatingAgainstUndeadPerLevel:
                case StatType.CrushingBlowPerLevel:
                case StatType.OpenWoundsPerLevel:
                case StatType.KickDamagePerLevel:
                case StatType.DeadlyStrikePerLevel:
                case StatType.FindGemsPerLevel:
                    {
                        itemProperty.PerLevel = reader.Read(propertyData.SaveBits);
                        return true;
                    }
            }

            if (propertyData.SaveParamBits > 0)
            {
                switch (propertyData.StatType)
                {
                    case StatType.SingleSkill1:
                    case StatType.SingleSkill2:
                    case StatType.SingleSkill3:
                    case StatType.SingleSkill4:
                        itemProperty.Skill = (Skill)reader.Read(propertyData.SaveParamBits);
                        itemProperty.Value = reader.Read(propertyData.SaveBits);
                        return true;
                    case StatType.SkillTab1:
                    case StatType.SkillTab2:
                    case StatType.SkillTab3:
                    case StatType.SkillTab4:
                    case StatType.SkillTab5:
                    case StatType.SkillTab6:
                        itemProperty.SkillTab = (SkillTab)reader.Read(5);
                        itemProperty.Value = reader.Read(5);
                        return true;
                    case StatType.SkillOnHit:
                    case StatType.SkillWhenStruck1:
                    case StatType.SkillWhenStruck2:
                    case StatType.SkillWhenStruck3:
                    case StatType.SkillOnStriking:
                    case StatType.SkillOnLevelUp:
                        itemProperty.Skill = (Skill)reader.Read(9);
                        itemProperty.SkillLevel = reader.Read(5);
                        itemProperty.SkillChance = reader.Read(7);
                        return true;
                    case StatType.Charged:
                        itemProperty.Skill = (Skill)reader.Read(9);
                        itemProperty.SkillLevel = reader.Read(5);
                        itemProperty.Charges = reader.Read(8);
                        itemProperty.MaximumCharges = reader.Read(8);
                        return true;
                }
            }

            switch (propertyData.StatType)
            {
                case StatType.EnhancedMaximumDamage:
                case StatType.EnhancedMinimumDamage:
                    {
                        itemProperty.MinimumValue = reader.Read(propertyData.SaveBits);
                        itemProperty.MaximumValue = reader.Read(propertyData.SaveBits);
                        itemProperty.Value = itemProperty.MinimumValue;
                        return true;
                    }

                case StatType.MinimumFireDamage:
                    {
                        itemProperty.MinimumValue = reader.Read(propertyData.SaveBits);
                        var maximumDamagePropertyData = DataManager.DataManager.Instance.ItemPropertyData.GetByStatType(StatType.MaximumFireDamage);
                        itemProperty.MaximumValue = reader.Read(maximumDamagePropertyData.SaveBits);
                        itemProperty.Value = itemProperty.MinimumValue;
                        return true;
                    }

                case StatType.MinimumLightningDamage:
                    {
                        itemProperty.MinimumValue = reader.Read(propertyData.SaveBits);
                        var maximumDamagePropertyData = DataManager.DataManager.Instance.ItemPropertyData.GetByStatType(StatType.MaximumLightningDamage);
                        itemProperty.MaximumValue = reader.Read(maximumDamagePropertyData.SaveBits);
                        itemProperty.Value = itemProperty.MinimumValue;
                        return true;
                    }

                case StatType.MinimumMagicalDamage:
                    {
                        itemProperty.MinimumValue = reader.Read(propertyData.SaveBits);
                        var maximumDamagePropertyData = DataManager.DataManager.Instance.ItemPropertyData.GetByStatType(StatType.MaximumMagicalDamage);
                        itemProperty.MaximumValue = reader.Read(maximumDamagePropertyData.SaveBits);
                        itemProperty.Value = itemProperty.MinimumValue;
                        return true;
                    }

                case StatType.MinimumColdDamage:
                    {
                        itemProperty.MinimumValue = reader.Read(propertyData.SaveBits);
                        var maximumDamagePropertyData = DataManager.DataManager.Instance.ItemPropertyData.GetByStatType(StatType.MaximumColdDamage);
                        itemProperty.MaximumValue = reader.Read(maximumDamagePropertyData.SaveBits);
                        itemProperty.Value = itemProperty.MinimumValue;
                        reader.Read(8); //duration
                        return true;
                    }

                case StatType.MinimumPoisonDamage:
                    {
                        itemProperty.MinimumValue = reader.Read(propertyData.SaveBits);
                        var maximumDamagePropertyData = DataManager.DataManager.Instance.ItemPropertyData.GetByStatType(StatType.MaximumPoisonDamage);
                        itemProperty.MaximumValue = reader.Read(maximumDamagePropertyData.SaveBits);
                        itemProperty.Value = itemProperty.MinimumValue;
                        reader.Read(8); //duration
                        return true;
                    }

                case StatType.RepairsDurability:
                case StatType.ReplenishesQuantity:
                    {
                        itemProperty.Value = reader.Read(propertyData.SaveBits);
                        return true;
                    }

                default:
                    {
                        itemProperty.Value = reader.Read(propertyData.SaveBits) - propertyData.SaveAdd;
                        return true;
                    }
            }
        }

        public static Item Parse(List<byte> packet)
        {
            var item = new Item();
            var reader = new BitReader(packet.ToArray());
            try
            {
                GenericInfo(reader, ref item);
                StatusInfo(reader, ref item);
                ReadLocation(reader, ref item);
                if (EarInfo(reader, ref item))
                {
                    return item;
                }
                if (ReadItemType(reader, ref item))
                {
                    return item;
                }
                ReadSocketInfo(reader, ref item);
                if (!ReadLevelQuality(reader, ref item))
                {
                    return item;
                }
                ReadGraphicInfo(reader, ref item);
                ReadIdentifiedInfo(reader, ref item);
            }
            catch (System.Exception e)
            {
                Log.Error(e, "Failed to parse item");
            }
            return item;
        }
    }
}