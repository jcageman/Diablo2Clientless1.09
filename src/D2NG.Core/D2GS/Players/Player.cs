using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Players
{
    public class Player
    {
        public Point Location { get; set; }
        public string Name { get; }
        public uint Id { get; }
        public CharacterClass Class { get; }
        public uint? CorpseId { get; set; }
        public uint? MercId { get; set; }
        public Area? Area { get; set; }
        public Act.Act? Act { get; set; }
        public ConcurrentDictionary<EntityEffect, EntityEffect> Effects { get; private set; } = new ConcurrentDictionary<EntityEffect, EntityEffect>();

        public double BattleOrdersLifePercentage = 0.0;
        public double LifePercentage { get; set; } = 100.0;


        public List<Summon> Summons { get; internal set; } = new List<Summon>();

        public ConcurrentDictionary<DirectoryType, Item> Equipment { get; set; } = new ConcurrentDictionary<DirectoryType, Item>();

        internal Player(AssignPlayerPacket assignPlayer)
        {
            Location = assignPlayer.Location;
            Name = assignPlayer.Name;
            Id = assignPlayer.Id;
            Class = assignPlayer.Class;
        }

        internal Player(PlayerInGamePacket playerInGame)
        {
            Name = playerInGame.Name;
            Id = playerInGame.Id;
            Class = playerInGame.Class;
        }

        public virtual void EquipItem(Item item)
        {
            Equipment.AddOrUpdate(item.Directory, _ => item, (d, e) => item);
        }

        public virtual void UnequipItem(Item item)
        {
            Equipment.TryRemove(item.Directory, out var _);
        }
        public virtual void AddEffect(AddEntityEffectPacket packet)
        {
            Effects.TryAdd(packet.Effect, packet.Effect);
            if (packet.Effect == EntityEffect.BattleOrders)
            {
                BattleOrdersLifePercentage = packet.ExtraData;
            }
        }

        public virtual void UpdateEffects(HashSet<EntityEffect> entityEffects)
        {
            Effects = new ConcurrentDictionary<EntityEffect, EntityEffect>(entityEffects.Select(e => KeyValuePair.Create(e,e)));
            if (!Effects.ContainsKey(EntityEffect.BattleOrders))
            {
                BattleOrdersLifePercentage = 0.0;
            }
        }
    }
}