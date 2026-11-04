using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Objects;

public class WorldObject : Entity
{
    public EntityCode Code { get; internal set; }

    public NPCCode NPCCode { get; internal set; }
    public EntityState State { get; internal set; }
    public byte InteractionType { get; }

    public double LifePercentage { get; internal set; } = 100;

    public Area TownPortalArea { get; internal set; }

    public uint TownPortalOwnerId { get; internal set; }

    public HashSet<EntityEffect> Effects { get; internal set; } = [];

    public HashSet<MonsterEnchantment> MonsterEnchantments { get; internal set; } = [];

    /// <summary>A unique monster (the boss flag of the NPC assignment), super uniques included.</summary>
    public bool IsBoss { get; internal set; }

    public bool IsChampion { get; internal set; }

    public bool IsBossMinion { get; internal set; }

    public WorldObject(EntityType objectType, uint objectId, EntityCode objectCode, Point location, EntityState state, byte interactionType)
    {
        Type = objectType;
        Id = objectId;
        Code = objectCode;
        Location = location;
        State = state;
        InteractionType = interactionType;
    }
}