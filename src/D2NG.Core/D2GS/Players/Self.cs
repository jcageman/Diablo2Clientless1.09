using System;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Players;

public class Self : Player
{
    public ConcurrentDictionary<Hand, Skill> ActiveSkills { get; } = new ConcurrentDictionary<Hand, Skill>();
    public ConcurrentDictionary<Skill, int> Skills { get; internal set; } = new ConcurrentDictionary<Skill, int>();
    public ConcurrentDictionary<Skill, int> ItemSkills { get; internal set; } = new ConcurrentDictionary<Skill, int>();
    public ConcurrentDictionary<Attribute, int> Attributes { get; } = new ConcurrentDictionary<Attribute, int>();
    public uint Experience { get; internal set; }
    public int Life { get; internal set; }
    public int Mana { get; internal set; }

    // Computed on every read rather than cached on events: a charm or socketed item arriving after
    // the last trigger left the maximums stale, and the sums are over a dozen items.
    public int MaxLife => Maximums().Life;
    public string MaxLifeBreakdown => Maximums(withBreakdown: true).LifeBreakdown;
    public int MaxMana => Maximums().Mana;
    public string MaxManaBreakdown => Maximums(withBreakdown: true).ManaBreakdown;

    /// <summary>The charms carried in the inventory, which count towards life and mana like worn items.</summary>
    internal Func<IEnumerable<Item>> CarriedCharms { get; set; } = () => [];
    public int Stamina { get; internal set; }

    public double WalkingSpeedMultiplier { get; set; } = 1.0;
    public uint LastSelectedWaypointId { get; internal set; }
    public ConcurrentBag<Waypoint> AllowedWaypoints { get; internal set; } = [];
    internal Self(AssignPlayerPacket assignPlayer) : base(assignPlayer)
    {
    }

    internal Self(PlayerInGamePacket playerInGame) : base(playerInGame)
    {
    }

    public double GetLifeFraction()
    {
        return (double)Life / MaxLife;
    }

    public bool HasSkill(Skill skill)
    {
        return (Skills.TryGetValue(skill, out var value) && value > 0)
            || (ItemSkills.TryGetValue(skill, out var itemValue) && itemValue > 0);
    }

    internal void UpdateAttribute(Attribute attribute, int value)
    {
        Attributes[attribute] = value;
    }

    public override void EquipItem(Item item)
    {
        base.EquipItem(item);
        UpdateWalkingSpeedModifier();
    }

    public override void UnequipItem(Item item)
    {
        base.UnequipItem(item);
        UpdateWalkingSpeedModifier();
    }

    public override void AddEffect(AddEntityEffectPacket packet)
    {
        base.AddEffect(packet);
        if (packet.Effect == EntityEffect.BattleOrders && BattleOrdersLifePercentage > 0)
        {
            LastBattleOrdersPercentage[Name] = BattleOrdersLifePercentage;
        }
    }

    public override void UpdateEffects(HashSet<EntityEffect> entityEffects)
    {
        base.UpdateEffects(entityEffects);

        // Battle Orders cast while we were still loading into the area arrives only in the state list,
        // which carries no value, so the maximum stayed unbuffed while life was doubled. The percentage
        // depends only on the caster's level, so the last one this character received stands in.
        if (Effects.ContainsKey(EntityEffect.BattleOrders) && BattleOrdersLifePercentage == 0
            && LastBattleOrdersPercentage.TryGetValue(Name, out var lastPercentage))
        {
            BattleOrdersLifePercentage = lastPercentage;
        }
    }

    private static readonly ConcurrentDictionary<string, double> LastBattleOrdersPercentage = new();

    private void UpdateWalkingSpeedModifier()
    {
        var walkingSpeedIncreasedMultiplier = 1.0;
        var speedItems = Equipment.Values.Select(i => i.GetValueOfStatType(StatType.FasterRunWalk));
        if (speedItems.Any())
        {
            walkingSpeedIncreasedMultiplier += speedItems.Aggregate((agg, frw) => agg + frw) / (double)100;
        }

        if (Skills.TryGetValue(Skill.IncreasedSpeed, out var increasedSpeedSkill) && increasedSpeedSkill > 0)
        {
            walkingSpeedIncreasedMultiplier += (13 + 4 * increasedSpeedSkill) / (double)100;
        }

        if (Skills.TryGetValue(Skill.Vigor, out var vigorSkill) && vigorSkill > 0)
        {
            walkingSpeedIncreasedMultiplier += (13 + 2.5 * vigorSkill) / 100;
        }

        WalkingSpeedMultiplier = walkingSpeedIncreasedMultiplier;
    }

    private (int Life, int Mana, string LifeBreakdown, string ManaBreakdown) Maximums(bool withBreakdown = false)
    {
        var lifePerLevel = 1.0;
        var lifePerVita = 1.0;
        var lifeCorrection = 0.0;
        var manaPerLevel = 1.0;
        var manaPerEnergy = 1.0;
        var manaCorrection = 0.0;
        switch (Class)
        {
            case CharacterClass.Amazon:
                lifePerLevel = 2;
                lifePerVita = 3;
                lifeCorrection = -12;
                manaPerLevel = 1.5;
                manaPerEnergy = 1.5;
                manaCorrection = -6;
                break;
            case CharacterClass.Sorceress:
                lifePerLevel = 1;
                lifePerVita = 2;
                lifeCorrection = 19;
                manaPerLevel = 2;
                manaPerEnergy = 2;
                manaCorrection = -37;
                break;
            case CharacterClass.Necromancer:
                lifePerLevel = 1.5;
                lifePerVita = 2;
                lifeCorrection = 13.5;
                manaPerLevel = 2;
                manaPerEnergy = 2;
                manaCorrection = -23;
                break;
            case CharacterClass.Paladin:
                lifePerLevel = 2;
                lifePerVita = 3;
                lifeCorrection = -22;
                manaPerLevel = 2;
                manaPerEnergy = 2;
                manaCorrection = -6;
                break;
            case CharacterClass.Barbarian:
                lifePerLevel = 2;
                lifePerVita = 4;
                lifeCorrection = -47;
                manaPerLevel = 2;
                manaPerEnergy = 2;
                manaCorrection = 1;
                break;
            case CharacterClass.Druid:
                lifePerLevel = 1.5;
                lifePerVita = 2;
                lifeCorrection = 3.5;
                manaPerLevel = 2;
                manaPerEnergy = 2;
                manaCorrection = -18;
                break;
            case CharacterClass.Assassin:
                lifePerLevel = 2;
                lifePerVita = 3;
                lifeCorrection = -12;
                manaPerLevel = 1.5;
                manaPerEnergy = 1.75;
                manaCorrection = -17;
                break;
        }

        var level = Attributes.TryGetValue(Attribute.Level, out var attLevel) ? attLevel : 0;
        var vita = Attributes.TryGetValue(Attribute.Vitality, out var attVita) ? attVita : 0;
        var energy = Attributes.TryGetValue(Attribute.Energy, out var attEnergy) ? attEnergy : 0;

        // The server sends the base maximums itself. They include the quest life (+20 on a sorceress)
        // that no level and vitality formula can know about, so the formula is only the fallback.
        var baseLife = Attributes.TryGetValue(Attribute.MaxLife, out var attMaxLife) ? attMaxLife >> 8 : lifePerLevel * level + lifePerVita * vita + lifeCorrection;
        var baseMana = Attributes.TryGetValue(Attribute.MaxMana, out var attMaxMana) ? attMaxMana >> 8 : manaPerLevel * level + manaPerEnergy * energy + manaCorrection;

        var sources = Equipment.Values.Concat(CarriedCharms()).ToList();
        int Sum(StatType type) => sources.Sum(i => i.GetValueOfStatType(type));
        // Per level stats are stored in eighths and in PerLevel, not Value: a Shako's 1.5 life and mana
        // per level read as nothing at all.
        double PerLevel(StatType type) => sources.Sum(i => i.Properties != null && i.Properties.TryGetValue(type, out var p) ? p.PerLevel : 0) * level / 8.0;

        var itemsLife = sources.Sum(i => i.GetValueOfStatType(StatType.Life) + i.SocketedLife());
        var itemsVita = Sum(StatType.Vitality);
        var itemsMana = Sum(StatType.Mana);
        var itemsEnergy = Sum(StatType.Energy);
        var lifeFromLevel = PerLevel(StatType.LifePerLevel);
        var manaFromLevel = PerLevel(StatType.ManaPerLevel);
        var lifePercent = Sum(StatType.EnhancedLife) + BattleOrdersLifePercentage;
        var manaPercent = Sum(StatType.EnhancedMana) + BattleOrdersLifePercentage;

        var life = (int)((baseLife + itemsLife + itemsVita * lifePerVita + lifeFromLevel) * (1 + lifePercent / 100));
        var mana = (int)((baseMana + itemsMana + itemsEnergy * manaPerEnergy + manaFromLevel) * (1 + manaPercent / 100));
        if (!withBreakdown)
        {
            return (life, mana, null, null);
        }

        var items = string.Join(", ", sources.Select(i => $"{i.Directory} {i.Name}"));
        var lifeBreakdown = $"base life {baseLife}, level {level}, vitality {vita}, item life {itemsLife}, item vitality {itemsVita}, life from level {lifeFromLevel:F0}, life % {lifePercent}, from {items}";
        var manaBreakdown = $"base mana {baseMana}, level {level}, energy {energy}, item mana {itemsMana}, item energy {itemsEnergy}, mana from level {manaFromLevel:F0}, mana % {manaPercent}, from {items}";
        return (life, mana, lifeBreakdown, manaBreakdown);
    }
}
