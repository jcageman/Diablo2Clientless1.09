using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Weapons and shields: each build fights with the weapon kinds its plan lists, judged by what they do for
/// that build - average damage for the physical builds, +skills for the casters - and a better one found by
/// anyone in the party goes to the member it improves most.
/// </summary>
public sealed partial class LevelingGear
{
    /// <summary>A replacement has to beat what is worn by this much, so two near-equal weapons do not swap back and forth.</summary>
    private const double UpgradeMargin = 1.1;

    /// <summary>
    /// A member whose damage does not come from its weapon: the sorceress and necromancer, and the hammerdin once he
    /// fights with Blessed Hammer - judged on damage, his Gladius beat a unique Grand Scepter with +1 to paladin skills.
    /// </summary>
    private static bool IsCaster(PartyMember member)
        => member.Plan.Class is CharacterClass.Sorceress or CharacterClass.Necromancer || (LevelingShop.IsCaster(member) && member.Stage.Attack != Skill.Attack);

    public static bool IsWeaponOrShield(Item item)
        => item.Classification is ClassificationType.Shield or ClassificationType.PaladinShield or ClassificationType.NecromancerShrunkenHead
            || WeaponKinds.Contains(item.Classification);

    private static readonly HashSet<ClassificationType> WeaponKinds =
    [
        ClassificationType.Axe, ClassificationType.Club, ClassificationType.Dagger, ClassificationType.Hammer, ClassificationType.Mace,
        ClassificationType.Polearm, ClassificationType.Scepter, ClassificationType.Spear, ClassificationType.Staff, ClassificationType.Sword,
        ClassificationType.Wand, ClassificationType.Bow, ClassificationType.AmazonBow, ClassificationType.Crossbow, ClassificationType.Javelin,
        ClassificationType.AmazonJavelin, ClassificationType.AmazonSpear, ClassificationType.SorceressOrb, ClassificationType.ThrowingAxe,
        ClassificationType.ThrowingKnife
    ];

    private bool IsTwoHanded(Item item)
    {
        var baseItem = requirements.BaseOf(item);
        return item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow or ClassificationType.Crossbow or ClassificationType.Staff or ClassificationType.Polearm
            || (baseItem != null && baseItem.MinDamage == 0 && baseItem.TwoHandMinDamage > 0);
    }

    /// <summary>What the weapon is worth to this member; negative when it cannot or would not use it.</summary>
    private const int CasterShieldLevel = 30;

    /// <param name="later">With the stats the member's plan gives it later, for a weapon it cannot wield yet.</param>
    public double WeaponScore(PartyMember member, Item item, bool later = false, bool alone = false)
    {
        if (item == null || !member.Plan.Weapons.Contains(item.Classification) || !CanWear(member, item, later: later))
        {
            return -1;
        }

        // A plan with a shield keeps one until whirlwind: the barbarian chose a two-handed battle axe for its damage and
        // stood in fights with nothing to block with.
        if (!IsCaster(member) && member.Plan.Shield && member.Stage.Attack != Skill.Whirlwind && IsTwoHanded(item))
        {
            return -1;
        }

        // A caster takes a staff early for its skills; once it has the mana, a wand or a blade and a shield serve better.
        if (IsCaster(member) && member.Plan.Shield && member.Level >= CasterShieldLevel && IsTwoHanded(item))
        {
            return -1;
        }

        var preference = 1 + 0.02 * (member.Plan.Weapons.Count - member.Plan.Weapons.IndexOf(item.Classification));
        // A caster's weapon is worth what any other piece is: its skills, cast rate, resistances and life. Skills and cast
        // rate alone kept the sorceress on a wand with 20% cast rate and nothing else.
        if (IsCaster(member))
        {
            return StatScore(member, item, alone: alone) * preference;
        }

        var damage = AverageDamage(item) * MasteryFactor(member, item);
        return (damage * (1 + WeaponLeech(member, item, alone)) + ElementalDamage(item) + AttackRatingValue(item)
            + SpeedAndBlock(member, item, alone: alone) + SkillValue(member, item) * 5) * preference;
    }

    /// <summary>
    /// Leech on a weapon as a share of the damage it heals with, up to what the member wants in all: a flat four points a
    /// percent let a Long Bow doing 9 with 7% life and 8% mana leech beat a rare Edge Bow doing 36, and every bow after it.
    /// </summary>
    private double WeaponLeech(PartyMember member, Item item, bool alone = false)
        => (UpToTarget(member, item, StatType.MinimumLifeStolenPerHit, LifeLeechTargetFor(member), alone: alone)
            + UpToTarget(member, item, StatType.MinimumManaStolenPerHit, ManaLeechTarget, alone: alone)) * 0.03;

    /// <summary>
    /// Cast rate for a caster, attack speed for the rest, hit recovery for everyone and extra block for a member with a
    /// shield, wherever they sit: on a weapon, a shield, armor or jewellery alike.
    /// </summary>
    private double SpeedAndBlock(PartyMember member, Item item, DirectoryType? slot = null, bool alone = false)
        => (IsCaster(member) ? CastRate(member, item, slot, alone) : Stat(item, StatType.IncreasedAttackSpeed))
            + Stat(item, StatType.FasterHitRecovery) * 0.5
            + (member.Plan.Shield ? Stat(item, StatType.IncreasedBlocking) * BlockWeight : 0);

    /// <summary>The faster cast rate at which a cast takes one frame less, in 1.09: paladin 15 frames at none, sorceress 13.</summary>
    private static readonly int[] PaladinCastBreakpoints = [9, 18, 30, 48, 75, 125];

    private static readonly int[] SorceressCastBreakpoints = [9, 20, 37, 63, 105, 200];

    /// <summary>A frame less per cast: about 7% more hammers for a paladin at 15 frames, worth some 7 points of fire resistance.</summary>
    private const double CastFrameValue = 50;

    /// <summary>
    /// Faster cast rate by the frames it saves on top of what the rest of the outfit gives. The paladin cast 652 hammers in
    /// 423 s at none while the party's only 20% sat on the orb sorceress, whose Frozen Orb waits out its own one-second delay
    /// whatever her cast rate: for her it is worth only her Static Field and Teleport.
    /// </summary>
    private double CastRate(PartyMember member, Item item, DirectoryType? slot, bool alone)
    {
        var add = Stat(item, StatType.FasterCastRate);
        if (add <= 0)
        {
            return add;
        }

        if (member.Stage.Attack == Skill.FrozenOrb)
        {
            return add * 0.25;
        }

        var breakpoints = member.Plan.Class switch
        {
            CharacterClass.Paladin => PaladinCastBreakpoints,
            CharacterClass.Sorceress => SorceressCastBreakpoints,
            _ => null
        };
        if (breakpoints == null)
        {
            return add;
        }

        var have = Have(member, item, StatType.FasterCastRate, slot, alone);
        return breakpoints.Count(point => have < point && have + add >= point) * CastFrameValue;
    }

    /// <summary>Added fire, lightning and cold damage, at its average like the physical damage it adds to.</summary>
    private static double ElementalDamage(Item item)
        => (Stat(item, StatType.MinimumFireDamage) + Stat(item, StatType.MaximumFireDamage)
            + Stat(item, StatType.MinimumLightningDamage) + Stat(item, StatType.MaximumLightningDamage)
            + Stat(item, StatType.MinimumColdDamage) + Stat(item, StatType.MaximumColdDamage)) / 2.0;

    /// <summary>Attack rating decides how many of the arrows and swings land at all.</summary>
    private static double AttackRatingValue(Item item)
        => Stat(item, StatType.AttackRating) * 0.02 + Stat(item, StatType.ToHitPercent) * 0.2;

    /// <summary>
    /// A weapon mastery only works for its own weapon kind: six points of Mace Mastery did nothing for a
    /// barbarian wielding an axe. Roughly its damage bonus, so a mace that matches beats a slightly better axe.
    /// </summary>
    private static double MasteryFactor(PartyMember member, Item item)
    {
        var mastery = item.Classification switch
        {
            ClassificationType.Mace or ClassificationType.Hammer or ClassificationType.Club or ClassificationType.Scepter => Skill.MaceMastery,
            ClassificationType.Sword => Skill.SwordMastery,
            ClassificationType.Axe => Skill.AxeMastery,
            ClassificationType.Polearm => Skill.PoleArmMastery,
            ClassificationType.Spear => Skill.SpearMastery,
            ClassificationType.Javelin or ClassificationType.ThrowingAxe or ClassificationType.ThrowingKnife => Skill.ThrowingMastery,
            _ => (Skill?)null
        };
        var points = mastery is { } skill ? member.SkillLevel(skill) : 0;
        return points > 0 ? 1.25 + 0.05 * points : 1;
    }

    private const int BlockWeight = 3;

    public double ShieldScore(PartyMember member, Item item, bool later = false, bool alone = false)
    {
        if (item == null || !member.Plan.Shield || !CanWear(member, item, later: later)
            || (item.Classification == ClassificationType.PaladinShield && member.Plan.Class != CharacterClass.Paladin)
            || (item.Classification == ClassificationType.NecromancerShrunkenHead && member.Plan.Class != CharacterClass.Necromancer)
            || item.Classification is not (ClassificationType.Shield or ClassificationType.PaladinShield or ClassificationType.NecromancerShrunkenHead))
        {
            return -1;
        }

        // Block is what a shield is for in melee: each percent of it counts like a few points of defense.
        // Extra block, speed and hit recovery come in through SpeedAndBlock, as on every other item.
        // Resistances as on any other piece: counted one a point, the sorceress's shields were chosen on block alone.
        var block = requirements.BaseOf(item)?.Block ?? 0;
        return 1 + item.Defense + block * BlockWeight + SpeedAndBlock(member, item, alone: alone) + SkillValue(member, item) * SkillWeight(item, 10) + SetBonus(member, item)
            + ResistScore(member, item, alone: alone) + Stat(item, StatType.Life);
    }

    /// <summary>
    /// +skills on a shield or an amulet that also carries resistances: a point of paladin skills is some 8% of the hammerdin's
    /// damage at level 20 (Blessed Hammer +8 on 164-168, Concentration +15% on 345%). Without resistances it stays a small
    /// extra - survivability comes first, and a +2 shield with fire alone does not take a 20-to-all shield's place.
    /// </summary>
    private const int SkillsWithResist = 40;

    private static int SkillWeight(Item item, int otherwise)
        => item.Classification is ClassificationType.Shield or ClassificationType.PaladinShield or ClassificationType.Amulet
            && new[] { StatType.FireResistance, StatType.ColdResistance, StatType.LightningResistance, StatType.PoisonResistance }.Any(type => Stat(item, type) > 0)
            ? SkillsWithResist : otherwise;

    private static int SkillValue(PartyMember member, Item item)
        => item.GetValueOfStatType(ClassSkillsStat(member.Plan.Class))
            // Only skills the plan builds up count: one it gives a single point - a prerequisite like Frost Nova for the
            // orb sorceress - gains nothing from +3 on a staff.
            // The skill it fights with now counts too, a single point or not: +3 Ice Blast is the best staff a leveling
            // orb sorceress can find.
            + member.Plan.Skills.Where(step => step.Skill != null && step.Level > 1).Select(step => step.Skill.Value)
                .Concat(new[] { member.Stage.Attack, member.Stage.Secondary }.OfType<Skill>())
                .Distinct().Sum(skill => item.GetValueToSkill(skill));

    private static StatType ClassSkillsStat(CharacterClass characterClass) => characterClass switch
    {
        CharacterClass.Amazon => StatType.AmazonSkills,
        CharacterClass.Sorceress => StatType.SorceressSkills,
        CharacterClass.Necromancer => StatType.NecromancerSkills,
        CharacterClass.Paladin => StatType.PaladinSkills,
        _ => StatType.BarbarianSkills
    };

    private double AverageDamage(Item item)
    {
        var baseItem = requirements.BaseOf(item);
        if (baseItem == null)
        {
            return 0;
        }

        var (min, max) = item.Classification is ClassificationType.Javelin or ClassificationType.AmazonJavelin or ClassificationType.ThrowingAxe or ClassificationType.ThrowingKnife
            ? (baseItem.MissileMinDamage, baseItem.MissileMaxDamage)
            : baseItem.MinDamage > 0 ? (baseItem.MinDamage, baseItem.MaxDamage) : (baseItem.TwoHandMinDamage, baseItem.TwoHandMaxDamage);
        var enhanced = 1 + Math.Max(Stat(item, StatType.EnhancedMaximumDamage), Stat(item, StatType.EnhancedDamage)) / 100.0;
        // A two-handed weapon carries its added damage in the secondary stats: the rare Edge Bow's +15 maximum was not counted.
        var added = Math.Max(Stat(item, StatType.MinimumDamage), Stat(item, StatType.SecondaryMinimumDamage))
            + Math.Max(Stat(item, StatType.MaximumDamage), Stat(item, StatType.SecondaryMaximumDamage));
        return (min + max) / 2.0 * enhanced + added / 2.0;
    }

    /// <summary>
    /// A property's value. A damage range comes as one property, its maximum carried in the minimum's MaximumValue: read
    /// alone, Vidala's Barb's 1-20 lightning counted as 1 and the bow was sold for the amazon's 9-damage Long Bow.
    /// </summary>
    private static int Stat(Item item, StatType type)
    {
        var paired = type switch
        {
            StatType.MaximumDamage => StatType.MinimumDamage,
            StatType.MaximumFireDamage => StatType.MinimumFireDamage,
            StatType.MaximumLightningDamage => StatType.MinimumLightningDamage,
            StatType.MaximumColdDamage => StatType.MinimumColdDamage,
            _ => (StatType?)null
        };
        return (item.Properties?.GetValueOrDefault(type)?.Value ?? 0)
            + (paired is { } minimum ? item.Properties?.GetValueOrDefault(minimum)?.MaximumValue ?? 0 : 0)
            + SocketedStat(item, type);
    }

    /// <summary>
    /// What diamonds set in a shield add: the server sends a socketed gem as an item of its own, never in the stats of the
    /// shield, so the party's resist shield scored as a plain Large Shield against a magic Bone Shield with 20 to all.
    /// </summary>
    public static int SocketedStat(Item item, StatType type)
        => item.SocketedItems is { Count: > 0 } socketed && item.Classification is ClassificationType.Shield or ClassificationType.PaladinShield
            && type is StatType.FireResistance or StatType.ColdResistance or StatType.LightningResistance or StatType.PoisonResistance
            ? socketed.Values.Sum(gem => DiamondInShield.GetValueOrDefault(gem.Name))
            : 0;

    /// <summary>Every resistance a diamond gives in a shield, by grade (the game's gem table, shield column).</summary>
    private static readonly Dictionary<ItemName, int> DiamondInShield = new()
    {
        [ItemName.ChippedDiamond] = 6, [ItemName.FlawedDiamond] = 8, [ItemName.Diamond] = 11, [ItemName.FlawlessDiamond] = 14, [ItemName.PerfectDiamond] = 19,
    };

    private double Gain(PartyMember member, Item item, bool later = false)
    {
        var equipment = member.Game.Me.Equipment;
        if (WeaponKinds.Contains(item.Classification))
        {
            var score = WeaponScore(member, item, later);
            var worn = equipment.GetValueOrDefault(DirectoryType.RightHand);
            var current = worn == null ? 0 : Math.Max(0, WeaponScore(member, worn));
            if (IsBow(item) && !HasArrows(member))
            {
                return -1;
            }

            return score > current * UpgradeMargin ? score - current : -1;
        }

        var shield = ShieldScore(member, item, later);
        var weapon = equipment.GetValueOrDefault(DirectoryType.RightHand);
        if (shield < 0 || (weapon != null && IsTwoHanded(weapon)))
        {
            return -1;
        }

        var wornShield = equipment.GetValueOrDefault(DirectoryType.LeftHand);
        var currentShield = wornShield == null ? 0 : Math.Max(0, ShieldScore(member, wornShield));
        return shield > currentShield * UpgradeMargin ? shield - currentShield : -1;
    }

    private static bool IsBow(Item item) => item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow;

    private async Task WearWeapon(PartyMember member, Item item)
    {
        if (item == null)
        {
            return;
        }

        var equipment = member.Game.Me.Equipment;
        if (WeaponKinds.Contains(item.Classification))
        {
            var leftHand = equipment.GetValueOrDefault(DirectoryType.LeftHand);
            if (IsTwoHanded(item) && leftHand != null && leftHand.Classification != ClassificationType.Arrows && !await Unequip(member, DirectoryType.LeftHand))
            {
                return;
            }

            if (await Unequip(member, DirectoryType.RightHand) && await Equip(member, item, DirectoryType.RightHand))
            {
                Log.Information("{Character} upgraded its weapon to {Quality} {Item} (score {Score:0.0})", member.Name, item.Quality, item.Name, WeaponScore(member, item));
            }

            return;
        }

        if (await Unequip(member, DirectoryType.LeftHand) && await Equip(member, item, DirectoryType.LeftHand))
        {
            Log.Information("{Character} upgraded its shield to {Quality} {Item}", member.Name, item.Quality, item.Name);
        }
    }
}
