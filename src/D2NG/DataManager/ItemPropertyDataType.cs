using D2NG.D2GS.Items;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace D2NG.DataManager
{
    class ItemPropertyDataType
    {
        public List<ItemPropertyEntry> ItemProperties { get; }

        public Dictionary<StatType, ItemPropertyEntry> ItemPropertiesByStatType { get; }

        public ItemPropertyDataType(string file)
        {
            var propertiesMap = GetItemPropertiesMap();
            ItemProperties = new List<ItemPropertyEntry>();
            ItemPropertiesByStatType = new Dictionary<StatType, ItemPropertyEntry>();

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

                    if (tokens.Length != 4)
                    {
                        Log.Error($"Invalid Token Count: {tokens.Length} on line {line}");
                    }

                    string name = tokens[0];
                    int saveBits = int.Parse(tokens[1]);
                    int saveParamBits = int.Parse(tokens[2]);
                    int saveAdd = int.Parse(tokens[3]);
                    var statType = name == "Unused" ? propertiesMap[name + " " + saveBits] : propertiesMap[name];
                    var i = new ItemPropertyEntry(name, statType, saveBits, saveParamBits, saveAdd);
                    ItemProperties.Add(i);
                    ItemPropertiesByStatType.Add(i.StatType, i);
                }
                catch (Exception e)
                {
                    Log.Error("Error parsing ItemPropertyEntry: {0}", e.ToString());
                }
            }
        }

        public ItemPropertyEntry Get(int propertyId)
        {
            return ItemProperties.ElementAtOrDefault(propertyId);
        }

        public ItemPropertyEntry GetByStatType(StatType statType)
        {
            return ItemPropertiesByStatType.GetValueOrDefault(statType);
        }

        private static Dictionary<string, StatType> GetItemPropertiesMap()
        {
            Dictionary<string, StatType> propertiesNameToTypeMap = new Dictionary<string, StatType>();
            propertiesNameToTypeMap["Strength"] = StatType.Strength;
            propertiesNameToTypeMap["Energy"] = StatType.Energy;
            propertiesNameToTypeMap["Dexterity"] = StatType.Dexterity;
            propertiesNameToTypeMap["Vitality"] = StatType.Vitality;
            propertiesNameToTypeMap["All Attributes"] = StatType.AllAttributes;
            propertiesNameToTypeMap["New Skills1"] = StatType.NewSkills1;
            propertiesNameToTypeMap["New Skills2"] = StatType.NewSkills2;
            propertiesNameToTypeMap["Life"] = StatType.Life;
            propertiesNameToTypeMap["Maximum Life"] = StatType.MaximumLife;
            propertiesNameToTypeMap["Mana"] = StatType.Mana;
            propertiesNameToTypeMap["Maximum Mana"] = StatType.MaximumMana;
            propertiesNameToTypeMap["Maximum Stamina"] = StatType.MaximumStamina;
            propertiesNameToTypeMap["Level"] = StatType.Level;
            propertiesNameToTypeMap["Experience"] = StatType.Experience;
            propertiesNameToTypeMap["Gold"] = StatType.Gold;
            propertiesNameToTypeMap["Bank"] = StatType.Bank;
            propertiesNameToTypeMap["Enhanced Defense"] = StatType.EnhancedDefense;
            propertiesNameToTypeMap["Enhanced Maximum Damage"] = StatType.EnhancedMaximumDamage;
            propertiesNameToTypeMap["Enhanced Minimum Damage"] = StatType.EnhancedMinimumDamage;
            propertiesNameToTypeMap["Attack Rating"] = StatType.AttackRating;
            propertiesNameToTypeMap["Increased Blocking"] = StatType.IncreasedBlocking;
            propertiesNameToTypeMap["Minimum Damage"] = StatType.MinimumDamage;
            propertiesNameToTypeMap["Maximum Damage"] = StatType.MaximumDamage;
            propertiesNameToTypeMap["Secondary Minimum Damage"] = StatType.SecondaryMinimumDamage;
            propertiesNameToTypeMap["Secondary Maximum Damage"] = StatType.SecondaryMaximumDamage;
            propertiesNameToTypeMap["Enhanced Damage"] = StatType.EnhancedDamage;
            propertiesNameToTypeMap["Mana Recovery"] = StatType.ManaRecovery;
            propertiesNameToTypeMap["Mana Recovery Bonus"] = StatType.ManaRecoveryBonus;
            propertiesNameToTypeMap["Stamina Recovery Bonus"] = StatType.StaminaRecoveryBonus;
            propertiesNameToTypeMap["Last Experience"] = StatType.LastExperience;
            propertiesNameToTypeMap["Next Experience"] = StatType.NextExperience;
            propertiesNameToTypeMap["Defense"] = StatType.Defense;
            propertiesNameToTypeMap["Defense vs. Missiles"] = StatType.DefenseVsMissiles;
            propertiesNameToTypeMap["Defense vs. Melee"] = StatType.DefenseVsMelee;
            propertiesNameToTypeMap["Damage Reduction"] = StatType.DamageReduction;
            propertiesNameToTypeMap["Magical Damage Reduction"] = StatType.MagicalDamageReduction;
            propertiesNameToTypeMap["Damage Reduction (Percent)"] = StatType.DamageReductionPercent;
            propertiesNameToTypeMap["Magical Damage Reduction (Percent)"] = StatType.MagicalDamageReductionPercent;
            propertiesNameToTypeMap["Maximum Magical Damage Reduction (Percent)"] = StatType.MaximumMagicalDamageReductionPercent;
            propertiesNameToTypeMap["Fire Resistance"] = StatType.FireResistance;
            propertiesNameToTypeMap["Maximum Fire Resistance"] = StatType.MaximumFireResistance;
            propertiesNameToTypeMap["Lightning Resistance"] = StatType.LightningResistance;
            propertiesNameToTypeMap["Maximum Lightning Resistance"] = StatType.MaximumLightningResistance;
            propertiesNameToTypeMap["Cold Resistance"] = StatType.ColdResistance;
            propertiesNameToTypeMap["Maximum Cold Resistance"] = StatType.MaximumColdResistance;
            propertiesNameToTypeMap["Poison Resistance"] = StatType.PoisonResistance;
            propertiesNameToTypeMap["Maximum Poison Resistance"] = StatType.MaximumPoisonResistance;
            propertiesNameToTypeMap["Damage Aura"] = StatType.DamageAura;
            propertiesNameToTypeMap["Minimum Fire Damage"] = StatType.MinimumFireDamage;
            propertiesNameToTypeMap["Maximum Fire Damage"] = StatType.MaximumFireDamage;
            propertiesNameToTypeMap["Minimum Lightning Damage"] = StatType.MinimumLightningDamage;
            propertiesNameToTypeMap["Maximum Lightning Damage"] = StatType.MaximumLightningDamage;
            propertiesNameToTypeMap["Minimum Magical Damage"] = StatType.MinimumMagicalDamage;
            propertiesNameToTypeMap["Maximum Magical Damage"] = StatType.MaximumMagicalDamage;
            propertiesNameToTypeMap["Minimum Cold Damage"] = StatType.MinimumColdDamage;
            propertiesNameToTypeMap["Maximum Cold Damage"] = StatType.MaximumColdDamage;
            propertiesNameToTypeMap["Cold Damage Length"] = StatType.ColdDamageLength;
            propertiesNameToTypeMap["Minimum Poison Damage"] = StatType.MinimumPoisonDamage;
            propertiesNameToTypeMap["Maximum Poison Damage"] = StatType.MaximumPoisonDamage;
            propertiesNameToTypeMap["Poison Damage length"] = StatType.PoisonDamageLength;
            propertiesNameToTypeMap["Minimum Life Stolen Per Hit"] = StatType.MinimumLifeStolenPerHit;
            propertiesNameToTypeMap["Maximum Life Stolen Per Hit"] = StatType.MaximumLifeStolenPerHit;
            propertiesNameToTypeMap["Minimum Mana Stolen Per Hit"] = StatType.MinimumManaStolenPerHit;
            propertiesNameToTypeMap["Maximum Mana Stolen Per Hit"] = StatType.MaximumManaStolenPerHit;
            propertiesNameToTypeMap["Minimum Stamina Drain"] = StatType.MinimumStaminaDrain;
            propertiesNameToTypeMap["Maximum Stamina Drain"] = StatType.MaximumStaminaDrain;
            propertiesNameToTypeMap["Stun Length"] = StatType.StunLength;
            propertiesNameToTypeMap["Velocity Percent"] = StatType.VelocityPercent;
            propertiesNameToTypeMap["Attack Rate"] = StatType.AttackRate;
            propertiesNameToTypeMap["Other Animation Rate"] = StatType.OtherAnimationRate;
            propertiesNameToTypeMap["Quantity"] = StatType.Quantity;
            propertiesNameToTypeMap["Value"] = StatType.Value;
            propertiesNameToTypeMap["Durability"] = StatType.Durability;
            propertiesNameToTypeMap["Maximum Durability"] = StatType.MaximumDurability;
            propertiesNameToTypeMap["Replenish Life"] = StatType.ReplenishLife;
            propertiesNameToTypeMap["Enhanced Maximum Durability"] = StatType.EnhancedMaximumDurability;
            propertiesNameToTypeMap["Enhanced Life"] = StatType.EnhancedLife;
            propertiesNameToTypeMap["Enhanced Mana"] = StatType.EnhancedMana;
            propertiesNameToTypeMap["Attacker Takes Damage"] = StatType.AttackerTakesDamage;
            propertiesNameToTypeMap["Extra Gold"] = StatType.ExtraGold;
            propertiesNameToTypeMap["Better Chance Of Getting Magic Item"] = StatType.BetterChanceOfGettingMagicItem;
            propertiesNameToTypeMap["Knockback"] = StatType.Knockback;
            propertiesNameToTypeMap["Time Duration"] = StatType.TimeDuration;
            propertiesNameToTypeMap["Amazon Skills"] = StatType.AmazonSkills;
            propertiesNameToTypeMap["Paladin Skills"] = StatType.PaladinSkills;
            propertiesNameToTypeMap["Necromancer Skills"] = StatType.NecromancerSkills;
            propertiesNameToTypeMap["Sorceress Skills"] = StatType.SorceressSkills;
            propertiesNameToTypeMap["Barbarian Skills"] = StatType.BarbarianSkills;
            propertiesNameToTypeMap["Unsent Parameter"] = StatType.UnsentParameter;
            propertiesNameToTypeMap["Add Experience"] = StatType.AddExperience;
            propertiesNameToTypeMap["Life After Each Kill"] = StatType.LifeAfterEachKill;
            propertiesNameToTypeMap["Reduce Vendor Prices"] = StatType.ReduceVendorPrices;
            propertiesNameToTypeMap["Double Herb Duration"] = StatType.DoubleHerbDuration;
            propertiesNameToTypeMap["Light Radius"] = StatType.LightRadius;
            propertiesNameToTypeMap["Light Colour"] = StatType.LightColour;
            propertiesNameToTypeMap["Reduced Requirements"] = StatType.ReducedRequirements;
            propertiesNameToTypeMap["Reduced Level Requirement"] = StatType.ReducedLevelRequirement;
            propertiesNameToTypeMap["Increased Attack Speed"] = StatType.IncreasedAttackSpeed;
            propertiesNameToTypeMap["Reduced Level Requirement (Percent)"] = StatType.ReducedLevelRequirementPercent;
            propertiesNameToTypeMap["Last Block Frame"] = StatType.LastBlockFrame;
            propertiesNameToTypeMap["Faster Run Walk"] = StatType.FasterRunWalk;
            propertiesNameToTypeMap["Non Class Skill"] = StatType.NonClassSkill;
            propertiesNameToTypeMap["State"] = StatType.State;
            propertiesNameToTypeMap["Faster Hit Recovery"] = StatType.FasterHitRecovery;
            propertiesNameToTypeMap["Monster Player Count"] = StatType.MonsterPlayerCount;
            propertiesNameToTypeMap["Skill Poison Override Length"] = StatType.SkillPoisonOverrideLength;
            propertiesNameToTypeMap["Faster Block Rate"] = StatType.FasterBlockRate;
            propertiesNameToTypeMap["Skill Bypass Undead"] = StatType.SkillBypassUndead;
            propertiesNameToTypeMap["Skill Bypass Demons"] = StatType.SkillBypassDemons;
            propertiesNameToTypeMap["Faster Cast Rate"] = StatType.FasterCastRate;
            propertiesNameToTypeMap["Skill Bypass Beasts"] = StatType.SkillBypassBeasts;
            propertiesNameToTypeMap["Single Skill1"] = StatType.SingleSkill1;
            propertiesNameToTypeMap["Single Skill2"] = StatType.SingleSkill2;
            propertiesNameToTypeMap["Single Skill3"] = StatType.SingleSkill3;
            propertiesNameToTypeMap["Poison Length Reduction"] = StatType.PoisonLengthReduction;
            propertiesNameToTypeMap["Adds Damage"] = StatType.AddsDamage;
            propertiesNameToTypeMap["Hit Causes Monster To Flee"] = StatType.HitCausesMonsterToFlee;
            propertiesNameToTypeMap["Hit Blinds Target"] = StatType.HitBlindsTarget;
            propertiesNameToTypeMap["Damage To Mana"] = StatType.DamageToMana;
            propertiesNameToTypeMap["Ignore Target's Defense"] = StatType.IgnoreTargetsDefense;
            propertiesNameToTypeMap["Reduce Target's Defense"] = StatType.ReduceTargetsDefense;
            propertiesNameToTypeMap["Prevent Monster Heal"] = StatType.PreventMonsterHeal;
            propertiesNameToTypeMap["Half Freeze Duration"] = StatType.HalfFreezeDuration;
            propertiesNameToTypeMap["To Hit Percent"] = StatType.ToHitPercent;
            propertiesNameToTypeMap["Monster Defense Reduction per Hit"] = StatType.MonsterDefenseReductionPerHit;
            propertiesNameToTypeMap["Damage To Demons"] = StatType.DamageToDemons;
            propertiesNameToTypeMap["Damage To Undead"] = StatType.DamageToUndead;
            propertiesNameToTypeMap["Attack Rating Against Demons"] = StatType.AttackRatingAgainstDemons;
            propertiesNameToTypeMap["Attack Rating Against Undead"] = StatType.AttackRatingAgainstUndead;
            propertiesNameToTypeMap["Throwable"] = StatType.Throwable;
            propertiesNameToTypeMap["Fire Skills"] = StatType.FireSkills;
            propertiesNameToTypeMap["All Skills"] = StatType.AllSkills;
            propertiesNameToTypeMap["Attacker Takes Lightning Damage"] = StatType.AttackerTakesLightningDamage;
            propertiesNameToTypeMap["Iron Maiden Level"] = StatType.IronMaidenLevel;
            propertiesNameToTypeMap["Lifetap Level"] = StatType.LifetapLevel;
            propertiesNameToTypeMap["Thorns Percent"] = StatType.ThornsPercent;
            propertiesNameToTypeMap["Bone Armor"] = StatType.BoneArmor;
            propertiesNameToTypeMap["Maximum Bone Armor"] = StatType.MaximumBoneArmor;
            propertiesNameToTypeMap["Freezes Target"] = StatType.FreezesTarget;
            propertiesNameToTypeMap["Open Wounds"] = StatType.OpenWounds;
            propertiesNameToTypeMap["Crushing Blow"] = StatType.CrushingBlow;
            propertiesNameToTypeMap["Kick Damage"] = StatType.KickDamage;
            propertiesNameToTypeMap["Mana After Each Kill"] = StatType.ManaAfterEachKill;
            propertiesNameToTypeMap["Life After Each Demon Kill"] = StatType.LifeAfterEachDemonKill;
            propertiesNameToTypeMap["Extra Blood"] = StatType.ExtraBlood;
            propertiesNameToTypeMap["Deadly Strike"] = StatType.DeadlyStrike;
            propertiesNameToTypeMap["Fire Absorption (Percent)"] = StatType.FireAbsorptionPercent;
            propertiesNameToTypeMap["Fire Absorption"] = StatType.FireAbsorption;
            propertiesNameToTypeMap["Lightning Absorption (Percent)"] = StatType.LightningAbsorptionPercent;
            propertiesNameToTypeMap["Lightning Absorption"] = StatType.LightningAbsorption;
            propertiesNameToTypeMap["Magic Absorption (Percent)"] = StatType.MagicAbsorptionPercent;
            propertiesNameToTypeMap["Magic Absorption"] = StatType.MagicAbsorption;
            propertiesNameToTypeMap["Cold Absorption (Percent)"] = StatType.ColdAbsorptionPercent;
            propertiesNameToTypeMap["Cold Absorption"] = StatType.ColdAbsorption;
            propertiesNameToTypeMap["Slows Down Enemies"] = StatType.SlowsDownEnemies;
            propertiesNameToTypeMap["Aura"] = StatType.Aura;
            propertiesNameToTypeMap["Indestructible"] = StatType.Indestructible;
            propertiesNameToTypeMap["Cannot Be Frozen"] = StatType.CannotBeFrozen;
            propertiesNameToTypeMap["Stamina Drain (Percent)"] = StatType.StaminaDrainPercent;
            propertiesNameToTypeMap["Reanimate"] = StatType.Reanimate;
            propertiesNameToTypeMap["Piercing Attack"] = StatType.PiercingAttack;
            propertiesNameToTypeMap["Fires Magic Arrows"] = StatType.FiresMagicArrows;
            propertiesNameToTypeMap["Fire Explosive Arrows"] = StatType.FireExplosiveArrows;
            propertiesNameToTypeMap["Minimum Throwing Damage"] = StatType.MinimumThrowingDamage;
            propertiesNameToTypeMap["Maximum Throwing Damage"] = StatType.MaximumThrowingDamage;
            propertiesNameToTypeMap["Skill Hand Of Athena"] = StatType.SkillHandOfAthena;
            propertiesNameToTypeMap["Skill Stamina (Percent)"] = StatType.SkillStaminaPercent;
            propertiesNameToTypeMap["Skill Passive Stamina (Percent)"] = StatType.SkillPassiveStaminaPercent;
            propertiesNameToTypeMap["Concentration"] = StatType.Concentration;
            propertiesNameToTypeMap["Enchant"] = StatType.Enchant;
            propertiesNameToTypeMap["Pierce"] = StatType.Pierce;
            propertiesNameToTypeMap["Conviction"] = StatType.Conviction;
            propertiesNameToTypeMap["Chilling Armor"] = StatType.ChillingArmor;
            propertiesNameToTypeMap["Frenzy"] = StatType.Frenzy;
            propertiesNameToTypeMap["Decrepify"] = StatType.Decrepify;
            propertiesNameToTypeMap["Skill Armor Percent"] = StatType.SkillArmorPercent;
            propertiesNameToTypeMap["Alignment"] = StatType.Alignment;
            propertiesNameToTypeMap["Target 0"] = StatType.Target0;
            propertiesNameToTypeMap["Target 1"] = StatType.Target1;
            propertiesNameToTypeMap["Gold Lost"] = StatType.GoldLost;
            propertiesNameToTypeMap["Conversion Level"] = StatType.ConversionLevel;
            propertiesNameToTypeMap["Conversion Maximum Life"] = StatType.ConversionMaximumLife;
            propertiesNameToTypeMap["Unit Do Overlay"] = StatType.UnitDoOverlay;
            propertiesNameToTypeMap["Druid Skills"] = StatType.DruidSkills;
            propertiesNameToTypeMap["Assassin Skills"] = StatType.AssassinSkills;
            propertiesNameToTypeMap["Single Skill4"] = StatType.SingleSkill4;
            propertiesNameToTypeMap["Armor Override Percent"] = StatType.ArmorOverridePercent;
            propertiesNameToTypeMap["Unused 183"] = StatType.Unused183;
            propertiesNameToTypeMap["Unused 184"] = StatType.Unused184;
            propertiesNameToTypeMap["Unused 185"] = StatType.Unused185;
            propertiesNameToTypeMap["Unused 186"] = StatType.Unused186;
            propertiesNameToTypeMap["Unused 187"] = StatType.Unused187;
            propertiesNameToTypeMap["Skill Tab"] = StatType.SkillTab;
            propertiesNameToTypeMap["Unused 189"] = StatType.Unused189;
            propertiesNameToTypeMap["Unused 190"] = StatType.Unused190;
            propertiesNameToTypeMap["Unused 191"] = StatType.Unused191;
            propertiesNameToTypeMap["Unused 192"] = StatType.Unused192;
            propertiesNameToTypeMap["Unused 193"] = StatType.Unused193;
            propertiesNameToTypeMap["Socket Count"] = StatType.SocketCount;
            propertiesNameToTypeMap["Skill On Striking"] = StatType.SkillOnStriking;
            propertiesNameToTypeMap["Skill On Kill"] = StatType.SkillOnKill;
            propertiesNameToTypeMap["Skill On Death"] = StatType.SkillOnDeath;
            propertiesNameToTypeMap["Skill On Hit"] = StatType.SkillOnHit;
            propertiesNameToTypeMap["Skill On Level Up"] = StatType.SkillOnLevelUp;
            propertiesNameToTypeMap["Unused 200"] = StatType.Unused200;
            propertiesNameToTypeMap["Skill When Struck"] = StatType.SkillWhenStruck;
            propertiesNameToTypeMap["Unused 202"] = StatType.Unused202;
            propertiesNameToTypeMap["Unused 203"] = StatType.Unused203;
            propertiesNameToTypeMap["Charged"] = StatType.Charged;
            propertiesNameToTypeMap["Unused 204"] = StatType.Unused204;
            propertiesNameToTypeMap["Unused 205"] = StatType.Unused205;
            propertiesNameToTypeMap["Unused 206"] = StatType.Unused206;
            propertiesNameToTypeMap["Unused 207"] = StatType.Unused207;
            propertiesNameToTypeMap["Unused 208"] = StatType.Unused208;
            propertiesNameToTypeMap["Unused 209"] = StatType.Unused209;
            propertiesNameToTypeMap["Unused 210"] = StatType.Unused210;
            propertiesNameToTypeMap["Unused 211"] = StatType.Unused211;
            propertiesNameToTypeMap["Unused 212"] = StatType.Unused212;
            propertiesNameToTypeMap["Defense Per Level"] = StatType.DefensePerLevel;
            propertiesNameToTypeMap["Enhanced Defense Per Level"] = StatType.EnhancedDefensePerLevel;
            propertiesNameToTypeMap["Life Per Level"] = StatType.LifePerLevel;
            propertiesNameToTypeMap["Mana Per Level"] = StatType.ManaPerLevel;
            propertiesNameToTypeMap["Maximum Damage Per Level"] = StatType.MaximumDamagePerLevel;
            propertiesNameToTypeMap["Maximum Enhanced Damage Per Level"] = StatType.MaximumEnhancedDamagePerLevel;
            propertiesNameToTypeMap["Strength Per Level"] = StatType.StrengthPerLevel;
            propertiesNameToTypeMap["Dexterity Per Level"] = StatType.DexterityPerLevel;
            propertiesNameToTypeMap["Energy Per Level"] = StatType.EnergyPerLevel;
            propertiesNameToTypeMap["Vitality Per Level"] = StatType.VitalityPerLevel;
            propertiesNameToTypeMap["Attack Rating Per Level"] = StatType.AttackRatingPerLevel;
            propertiesNameToTypeMap["Bonus To Attack Rating Per Level"] = StatType.BonusToAttackRatingPerLevel;
            propertiesNameToTypeMap["Maximum Cold Damage Per Level"] = StatType.MaximumColdDamagePerLevel;
            propertiesNameToTypeMap["Maximum Fire Damage Per Level"] = StatType.MaximumFireDamagePerLevel;
            propertiesNameToTypeMap["Maximum Lightning Damage Per Level"] = StatType.MaximumLightningDamagePerLevel;
            propertiesNameToTypeMap["Maximum Poison Damage Per Level"] = StatType.MaximumPoisonDamagePerLevel;
            propertiesNameToTypeMap["Cold Resistance Per Level"] = StatType.ColdResistancePerLevel;
            propertiesNameToTypeMap["Fire Resistance Per Level"] = StatType.FireResistancePerLevel;
            propertiesNameToTypeMap["Lightning Resistance Per Level"] = StatType.LightningResistancePerLevel;
            propertiesNameToTypeMap["Poison Resistance Per Level"] = StatType.PoisonResistancePerLevel;
            propertiesNameToTypeMap["Cold Absorption Per Level"] = StatType.ColdAbsorptionPerLevel;
            propertiesNameToTypeMap["Fire Absorption Per Level"] = StatType.FireAbsorptionPerLevel;
            propertiesNameToTypeMap["Lightning Absorption Per Level"] = StatType.LightningAbsorptionPerLevel;
            propertiesNameToTypeMap["Poison Absorption Per Level"] = StatType.PoisonAbsorptionPerLevel;
            propertiesNameToTypeMap["Thorns Per Level"] = StatType.ThornsPerLevel;
            propertiesNameToTypeMap["Extra Gold Per Level"] = StatType.ExtraGoldPerLevel;
            propertiesNameToTypeMap["Better Chance Of Getting Magic Item Per Level"] = StatType.BetterChanceOfGettingMagicItemPerLevel;
            propertiesNameToTypeMap["Stamina Regeneration Per Level"] = StatType.StaminaRegenerationPerLevel;
            propertiesNameToTypeMap["Stamina Per Level"] = StatType.StaminaPerLevel;
            propertiesNameToTypeMap["Damage To Demons Per Level"] = StatType.DamageToDemonsPerLevel;
            propertiesNameToTypeMap["Damage To Undead Per Level"] = StatType.DamageToUndeadPerLevel;
            propertiesNameToTypeMap["Attack Rating Against Demons Per Level"] = StatType.AttackRatingAgainstDemonsPerLevel;
            propertiesNameToTypeMap["Attack Rating Against Undead Per Level"] = StatType.AttackRatingAgainstUndeadPerLevel;
            propertiesNameToTypeMap["Crushing Blow Per Level"] = StatType.CrushingBlowPerLevel;
            propertiesNameToTypeMap["Open Wounds Per Level"] = StatType.OpenWoundsPerLevel;
            propertiesNameToTypeMap["Kick Damage Per Level"] = StatType.KickDamagePerLevel;
            propertiesNameToTypeMap["Deadly Strike Per Level"] = StatType.DeadlyStrikePerLevel;
            propertiesNameToTypeMap["Find Gems Per Level"] = StatType.FindGemsPerLevel;
            propertiesNameToTypeMap["Repairs Durability"] = StatType.RepairsDurability;
            propertiesNameToTypeMap["Replenishes Quantity"] = StatType.ReplenishesQuantity;
            propertiesNameToTypeMap["Increased Stack Size"] = StatType.IncreasedStackSize;
            propertiesNameToTypeMap["Find Item"] = StatType.FindItem;
            propertiesNameToTypeMap["Slash Damage"] = StatType.SlashDamage;
            propertiesNameToTypeMap["Slash Damage (Percent)"] = StatType.SlashDamagePercent;
            propertiesNameToTypeMap["Crush Damage"] = StatType.CrushDamage;
            propertiesNameToTypeMap["Crush Damage (Percent)"] = StatType.CrushDamagePercent;
            propertiesNameToTypeMap["Thrust Damage"] = StatType.ThrustDamage;
            propertiesNameToTypeMap["Thrust Damage (Percent)"] = StatType.ThrustDamagePercent;
            propertiesNameToTypeMap["Slash Damage Absorption"] = StatType.SlashDamageAbsorption;
            propertiesNameToTypeMap["Crush Damage Absorption"] = StatType.CrushDamageAbsorption;
            propertiesNameToTypeMap["Thrust Damage Absorption"] = StatType.ThrustDamageAbsorption;
            propertiesNameToTypeMap["Slash Damage Absorption (Percent)"] = StatType.SlashDamageAbsorptionPercent;
            propertiesNameToTypeMap["Crush Damage Absorption (Percent)"] = StatType.CrushDamageAbsorptionPercent;
            propertiesNameToTypeMap["Thrust Damage Absorption (Percent)"] = StatType.ThrustDamageAbsorptionPercent;
            propertiesNameToTypeMap["Defense Per Time"] = StatType.DefensePerTime;
            propertiesNameToTypeMap["Enhanced Defense Per Time"] = StatType.EnhancedDefensePerTime;
            propertiesNameToTypeMap["Life Per Time"] = StatType.LifePerTime;
            propertiesNameToTypeMap["Mana Per Time"] = StatType.ManaPerTime;
            propertiesNameToTypeMap["Maximum Damage Per Time"] = StatType.MaximumDamagePerTime;
            propertiesNameToTypeMap["Maximum Enhanced Damage Per Time"] = StatType.MaximumEnhancedDamagePerTime;
            propertiesNameToTypeMap["Strength Per Time"] = StatType.StrengthPerTime;
            propertiesNameToTypeMap["Dexterity Per Time"] = StatType.DexterityPerTime;
            propertiesNameToTypeMap["Energy Per Time"] = StatType.EnergyPerTime;
            propertiesNameToTypeMap["Vitality Per Time"] = StatType.VitalityPerTime;
            propertiesNameToTypeMap["Attack Rating Per Time"] = StatType.AttackRatingPerTime;
            propertiesNameToTypeMap["Chance To Hit Per Time"] = StatType.ChanceToHitPerTime;
            propertiesNameToTypeMap["Maximum Cold Damage Per Time"] = StatType.MaximumColdDamagePerTime;
            propertiesNameToTypeMap["Maximum Fire Damage Per Time"] = StatType.MaximumFireDamagePerTime;
            propertiesNameToTypeMap["Maximum Lightning Damage Per Time"] = StatType.MaximumLightningDamagePerTime;
            propertiesNameToTypeMap["Maximum Damage Per Poison"] = StatType.MaximumDamagePerPoison;
            propertiesNameToTypeMap["Cold Resistance Per Time"] = StatType.ColdResistancePerTime;
            propertiesNameToTypeMap["Fire Resistance Per Time"] = StatType.FireResistancePerTime;
            propertiesNameToTypeMap["Lightning Resistance Per Time"] = StatType.LightningResistancePerTime;
            propertiesNameToTypeMap["Poison Resistance Per Time"] = StatType.PoisonResistancePerTime;
            propertiesNameToTypeMap["Cold Absorption Per Time"] = StatType.ColdAbsorptionPerTime;
            propertiesNameToTypeMap["Fire Absorption Per Time"] = StatType.FireAbsorptionPerTime;
            propertiesNameToTypeMap["Lightning Absorption Per Time"] = StatType.LightningAbsorptionPerTime;
            propertiesNameToTypeMap["Poison Absorption Per Time"] = StatType.PoisonAbsorptionPerTime;
            propertiesNameToTypeMap["Extra Gold Per Time"] = StatType.ExtraGoldPerTime;
            propertiesNameToTypeMap["Better Chance Of Getting Magic Item Per Time"] = StatType.BetterChanceOfGettingMagicItemPerTime;
            propertiesNameToTypeMap["Regenerate Stamina Per Time"] = StatType.RegenerateStaminaPerTime;
            propertiesNameToTypeMap["Stamina Per Time"] = StatType.StaminaPerTime;
            propertiesNameToTypeMap["Damage To Demons Per Time"] = StatType.DamageToDemonsPerTime;
            propertiesNameToTypeMap["Damage To Undead Per Time"] = StatType.DamageToUndeadPerTime;
            propertiesNameToTypeMap["Attack Rating Against Demons Per Time"] = StatType.AttackRatingAgainstDemonsPerTime;
            propertiesNameToTypeMap["Attack Rating Against Undead Per Time"] = StatType.AttackRatingAgainstUndeadPerTime;
            propertiesNameToTypeMap["Crushing Blow Per Time"] = StatType.CrushingBlowPerTime;
            propertiesNameToTypeMap["Open Wounds Per Time"] = StatType.OpenWoundsPerTime;
            propertiesNameToTypeMap["Kick Damage Per Time"] = StatType.KickDamagePerTime;
            propertiesNameToTypeMap["Deadly Strike Per Time"] = StatType.DeadlyStrikePerTime;
            propertiesNameToTypeMap["Find Gems Per Time"] = StatType.FindGemsPerTime;
            propertiesNameToTypeMap["Enemy Cold Resistance Reduction"] = StatType.EnemyColdResistanceReduction;
            propertiesNameToTypeMap["Enemy Fire Resistance Reduction"] = StatType.EnemyFireResistanceReduction;
            propertiesNameToTypeMap["Enemy Lightning Resistance Reduction"] = StatType.EnemyLightningResistanceReduction;
            propertiesNameToTypeMap["Enemy Poison Resistance Reduction"] = StatType.EnemyPoisonResistanceReduction;
            propertiesNameToTypeMap["Damage vs. Monsters"] = StatType.DamageVsMonsters;
            propertiesNameToTypeMap["Enhanced Damage vs. Monsters"] = StatType.EnhancedDamageVsMonsters;
            propertiesNameToTypeMap["Attack Rating Against Monsters"] = StatType.AttackRatingAgainstMonsters;
            propertiesNameToTypeMap["Bonus To Attack Rating Against Monsters"] = StatType.BonusToAttackRatingAgainstMonsters;
            propertiesNameToTypeMap["Defense vs. Monsters"] = StatType.DefenseVsMonsters;
            propertiesNameToTypeMap["Enhanced Defense vs. Monsters"] = StatType.EnhancedDefenseVsMonsters;
            propertiesNameToTypeMap["Fire Damage Length"] = StatType.FireDamageLength;
            propertiesNameToTypeMap["Minimum Fire Damage Length"] = StatType.MinimumFireDamageLength;
            propertiesNameToTypeMap["Maximum Fire Damage Length"] = StatType.MaximumFireDamageLength;
            propertiesNameToTypeMap["Progressive Damage"] = StatType.ProgressiveDamage;
            propertiesNameToTypeMap["Progressive Steal"] = StatType.ProgressiveSteal;
            propertiesNameToTypeMap["Progressive Other"] = StatType.ProgressiveOther;
            propertiesNameToTypeMap["Progressive Fire"] = StatType.ProgressiveFire;
            propertiesNameToTypeMap["Progressive Cold"] = StatType.ProgressiveCold;
            propertiesNameToTypeMap["Progressive Lightning"] = StatType.ProgressiveLightning;
            propertiesNameToTypeMap["Extra Charges"] = StatType.ExtraCharges;
            propertiesNameToTypeMap["Progressive Attack Rating"] = StatType.ProgressiveAttackRating;
            propertiesNameToTypeMap["Poison Count"] = StatType.PoisonCount;
            propertiesNameToTypeMap["Damage Framerate"] = StatType.DamageFramerate;
            propertiesNameToTypeMap["Pierce IDX"] = StatType.PierceIdx;
            propertiesNameToTypeMap["Fire Mastery"] = StatType.FireMastery;
            propertiesNameToTypeMap["Lightning Mastery"] = StatType.LightningMastery;
            propertiesNameToTypeMap["Cold Mastery"] = StatType.ColdMastery;
            propertiesNameToTypeMap["Poison Mastery"] = StatType.PoisonMastery;
            propertiesNameToTypeMap["Passive Enemy Fire Resistance Reduction"] = StatType.PassiveEnemyFireResistanceReduction;
            propertiesNameToTypeMap["Passive Enemy Lightning Resistance Reduction"] = StatType.PassiveEnemyLightningResistanceReduction;
            propertiesNameToTypeMap["Passive Enemy Cold Resistance Reduction"] = StatType.PassiveEnemyColdResistanceReduction;
            propertiesNameToTypeMap["Passive Enemy Poison Resistance Reduction"] = StatType.PassiveEnemyPoisonResistanceReduction;
            propertiesNameToTypeMap["Critical Strike"] = StatType.CriticalStrike;
            propertiesNameToTypeMap["Dodge"] = StatType.Dodge;
            propertiesNameToTypeMap["Avoid"] = StatType.Avoid;
            propertiesNameToTypeMap["Evade"] = StatType.Evade;
            propertiesNameToTypeMap["Warmth"] = StatType.Warmth;
            propertiesNameToTypeMap["Melee Attack Rating Mastery"] = StatType.MeleeAttackRatingMastery;
            propertiesNameToTypeMap["Melee Damage Mastery"] = StatType.MeleeDamageMastery;
            propertiesNameToTypeMap["Melee Critical Hit Mastery"] = StatType.MeleeCriticalHitMastery;
            propertiesNameToTypeMap["Thrown Weapon Attack Rating Mastery"] = StatType.ThrownWeaponAttackRatingMastery;
            propertiesNameToTypeMap["Thrown Weapon Damage Mastery"] = StatType.ThrownWeaponDamageMastery;
            propertiesNameToTypeMap["Thrown Weapon Critical Hit Mastery"] = StatType.ThrownWeaponCriticalHitMastery;
            propertiesNameToTypeMap["Weapon Block"] = StatType.WeaponBlock;
            propertiesNameToTypeMap["Summon Resist"] = StatType.SummonResist;
            propertiesNameToTypeMap["Modifier List Skill"] = StatType.ModifierListSkill;
            propertiesNameToTypeMap["Modifier List Level"] = StatType.ModifierListLevel;
            propertiesNameToTypeMap["Last Sent Life Percent"] = StatType.LastSentLifePercent;
            propertiesNameToTypeMap["Source Unit Type"] = StatType.SourceUnitType;
            propertiesNameToTypeMap["Source Unit ID"] = StatType.SourceUnitId;
            propertiesNameToTypeMap["Short Parameter 1"] = StatType.ShortParameter1;
            propertiesNameToTypeMap["Quest Item Difficulty"] = StatType.QuestItemDifficulty;
            propertiesNameToTypeMap["Passive Magical Damage Mastery"] = StatType.PassiveMagicalDamageMastery;
            propertiesNameToTypeMap["Passive Magical Resistance Reduction"] = StatType.PassiveMagicalResistanceReduction;
            return propertiesNameToTypeMap;
        }
    }
}
