using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Players
{
    public class Player
    {
        public Point Location { get; set; }
        public string Name { get; }
        public uint Id { get; }
        public CharacterClass Class { get; }

        public uint? CorpseId { get; set; }

        public HashSet<EntityEffect> Effects { get; internal set; } = new HashSet<EntityEffect>();

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
    }
}