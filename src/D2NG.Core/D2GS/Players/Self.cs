using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Concurrent;

namespace D2NG.Core.D2GS.Players
{
    public class Self : Player
    {
        public ConcurrentDictionary<Hand, Skill> ActiveSkills { get; } = new ConcurrentDictionary<Hand, Skill>();
        public ConcurrentDictionary<Skill, int> Skills { get; internal set; } = new ConcurrentDictionary<Skill, int>();
        public ConcurrentDictionary<Skill, int> ItemSkills { get; internal set; } = new ConcurrentDictionary<Skill, int>();
        public ConcurrentDictionary<Attribute, int> Attributes { get; } = new ConcurrentDictionary<Attribute, int>();
        public uint Experience { get; internal set; }
        public int Life { get; internal set; }
        public int Mana { get; internal set; }

        public int MaxLife { get; internal set; }
        public int MaxMana { get; internal set; }
        public int Stamina { get; internal set; }
        public uint LastSelectedWaypointId { get; internal set; }
        public ConcurrentBag<Waypoint> AllowedWaypoints { get; internal set; } = new ConcurrentBag<Waypoint>();

        internal Self(AssignPlayerPacket assignPlayer) : base(assignPlayer)
        {
        }

        internal Self(PlayerInGamePacket playerInGame) : base(playerInGame)
        {
        }
    }
}
