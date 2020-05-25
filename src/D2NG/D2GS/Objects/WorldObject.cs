namespace D2NG.D2GS.Objects
{
    public class WorldObject : Entity
    {
        public EntityCode Code { get; internal set; }

        public NPCCode NPCCode { get; internal set; }
        public EntityState State { get; internal set; }
        public byte InteractionType { get; }

        public WorldObject(EntityType objectType, uint objectId, EntityCode objectCode, Point location, EntityState state, byte interactionType)
        {
            this.Type = objectType;
            this.Id = objectId;
            this.Code = objectCode;
            this.Location = location;
            this.State = state;
            this.InteractionType = interactionType;
        }
    }
}