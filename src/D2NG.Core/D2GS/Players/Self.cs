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

    public int MaxLife { get; private set; }
    public int MaxMana { get; private set; }
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
        UpdateMaxHealthAndMana();
    }

    public override void EquipItem(Item item)
    {
        base.EquipItem(item);
        UpdateWalkingSpeedModifier();
        UpdateMaxHealthAndMana();
    }

    public override void UnequipItem(Item item)
    {
        base.UnequipItem(item);
        UpdateWalkingSpeedModifier();
        UpdateMaxHealthAndMana();
    }

    public override void AddEffect(AddEntityEffectPacket packet)
    {
        base.AddEffect(packet);
        UpdateMaxHealthAndMana();
    }

    public override void UpdateEffects(HashSet<EntityEffect> entityEffects)
    {
        base.UpdateEffects(entityEffects);
        UpdateMaxHealthAndMana();
    }

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

    private void UpdateMaxHealthAndMana()
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
                manaPerLevel = 1.5;
                manaPerEnergy = 1.5;
                manaCorrection = -33;
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
        var totalItemsLife = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.Life));
        var totalItemsMana = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.Mana));
        var totalItemsVita = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.Vitality));
        var totalItemsEnergy = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.Energy));
        var totalLifePerLevel = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.LifePerLevel));
        var totalManaPerLevel = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.ManaPerLevel));
        var lifeBonus = Equipment.Values.Sum(e => e.GetValueOfStatType(StatType.MaximumLife)) + BattleOrdersLifePercentage;
        MaxLife = (int)((lifePerLevel * level + lifePerVita * vita + lifeCorrection + totalItemsLife) * (1 + lifeBonus / 100) + totalItemsVita * 4 + totalLifePerLevel * level);
        MaxMana = (int)((manaPerLevel * level + manaPerEnergy * energy + manaCorrection + totalItemsMana) * (1 + lifeBonus / 100) + totalItemsEnergy * 4 + totalManaPerLevel * level);
    }
}
