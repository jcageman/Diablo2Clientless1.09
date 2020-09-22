﻿namespace D2NG.Core.D2GS.Objects
{
    public class WorldObject : Entity
    {
        public EntityCode Code { get; internal set; }

        public NPCCode NPCCode { get; internal set; }
        public EntityState State { get; internal set; }
        public byte InteractionType { get; }

        public byte LifePercentage { get; internal set; } = 100;

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
}