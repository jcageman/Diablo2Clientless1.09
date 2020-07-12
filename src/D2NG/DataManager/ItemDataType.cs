using D2NG.D2GS.Items;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace D2NG.DataManager
{
    class ItemDataType
    {
        public Dictionary<string, ItemEntry> Items { get; }

        public ItemDataType(string file)
        {
            Items = new Dictionary<string, ItemEntry>();
            Dictionary<String, ClassificationType> classificationMap = new Dictionary<string, ClassificationType>();
            classificationMap["Amazon Bow"] = ClassificationType.AmazonBow;
            classificationMap["Amazon Javelin"] = ClassificationType.AmazonJavelin;
            classificationMap["Amazon Spear"] = ClassificationType.AmazonSpear;
            classificationMap["Amulet"] = ClassificationType.Amulet;
            classificationMap["Antidote Potion"] = ClassificationType.AntidotePotion;
            classificationMap["Armor"] = ClassificationType.Armor;
            classificationMap["Arrows"] = ClassificationType.Arrows;
            classificationMap["Assassin Katar"] = ClassificationType.AssassinKatar;
            classificationMap["Axe"] = ClassificationType.Axe;
            classificationMap["Barbarian Helm"] = ClassificationType.BarbarianHelm;
            classificationMap["Belt"] = ClassificationType.Belt;
            classificationMap["Body Part"] = ClassificationType.BodyPart;
            classificationMap["Bolts"] = ClassificationType.Bolts;
            classificationMap["Boots"] = ClassificationType.Boots;
            classificationMap["Bow"] = ClassificationType.Bow;
            classificationMap["Circlet"] = ClassificationType.Circlet;
            classificationMap["Club"] = ClassificationType.Club;
            classificationMap["Crossbow"] = ClassificationType.Crossbow;
            classificationMap["Dagger"] = ClassificationType.Dagger;
            classificationMap["Druid Pelt"] = ClassificationType.DruidPelt;
            classificationMap["Ear"] = ClassificationType.Ear;
            classificationMap["Elixir"] = ClassificationType.Elixir;
            classificationMap["Gem"] = ClassificationType.Gem;
            classificationMap["Gloves"] = ClassificationType.Gloves;
            classificationMap["Gold"] = ClassificationType.Gold;
            classificationMap["Grand Charm"] = ClassificationType.GrandCharm;
            classificationMap["Hammer"] = ClassificationType.Hammer;
            classificationMap["Health Potion"] = ClassificationType.HealthPotion;
            classificationMap["Helm"] = ClassificationType.Helm;
            classificationMap["Herb"] = ClassificationType.Herb;
            classificationMap["Javelin"] = ClassificationType.Javelin;
            classificationMap["Jewel"] = ClassificationType.Jewel;
            classificationMap["Key"] = ClassificationType.Key;
            classificationMap["Large Charm"] = ClassificationType.LargeCharm;
            classificationMap["Mace"] = ClassificationType.Mace;
            classificationMap["Mana Potion"] = ClassificationType.ManaPotion;
            classificationMap["Necromancer Shrunken Head"] = ClassificationType.NecromancerShrunkenHead;
            classificationMap["Paladin Shield"] = ClassificationType.PaladinShield;
            classificationMap["Polearm"] = ClassificationType.Polearm;
            classificationMap["Quest Item"] = ClassificationType.QuestItem;
            classificationMap["Rejuvenation Potion"] = ClassificationType.RejuvenationPotion;
            classificationMap["Ring"] = ClassificationType.Ring;
            classificationMap["Rune"] = ClassificationType.Rune;
            classificationMap["Scepter"] = ClassificationType.Scepter;
            classificationMap["Scroll"] = ClassificationType.Scroll;
            classificationMap["Shield"] = ClassificationType.Shield;
            classificationMap["Small Charm"] = ClassificationType.SmallCharm;
            classificationMap["Sorceress Orb"] = ClassificationType.SorceressOrb;
            classificationMap["Spear"] = ClassificationType.Spear;
            classificationMap["Staff"] = ClassificationType.Staff;
            classificationMap["Stamina Potion"] = ClassificationType.StaminaPotion;
            classificationMap["Sword"] = ClassificationType.Sword;
            classificationMap["Thawing Potion"] = ClassificationType.ThawingPotion;
            classificationMap["Throwing Axe"] = ClassificationType.ThrowingAxe;
            classificationMap["Throwing Knife"] = ClassificationType.ThrowingKnife;
            classificationMap["Throwing Potion"] = ClassificationType.ThrowingPotion;
            classificationMap["Tome"] = ClassificationType.Tome;
            classificationMap["Torch"] = ClassificationType.Torch;
            classificationMap["Wand"] = ClassificationType.Wand;

            List<string> lines = new List<string>();

            using (StreamReader r = new StreamReader(file))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            foreach (string line in lines)
            {
                try
                {
                    String[] tokens = line.Split('|');
                    if (tokens.Length == 0)
                    {
                        continue;
                    }

                    if (tokens.Length != 8)
                    {
                        Log.Error($"Invalid Token Count: {tokens.Length} on line {line}");
                    }
                    String name = tokens[0];
                    String code = tokens[1];
                    String classification_string = tokens[2];
                    UInt16 width = UInt16.Parse(tokens[3]);
                    UInt16 height = UInt16.Parse(tokens[4]);
                    bool stackable = UInt32.Parse(tokens[5]) != 0;
                    bool usable = UInt32.Parse(tokens[6]) != 0;
                    bool throwable = UInt32.Parse(tokens[7]) != 0;
                    var classification = classificationMap[classification_string];
                    var i = new ItemEntry(name, code, classification, width, height, stackable, usable, throwable);
                    Items.Add(i.Type, i);
                }
                catch (Exception e)
                {
                    Log.Error("Error parsing ItemDataType: {0}", e.ToString());
                }
            }
        }

        public ItemEntry Get(string code)
        {
            return Items.GetValueOrDefault(code);
        }
    }
}