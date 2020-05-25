using D2NG.D2GS.Packet;

namespace D2NG.D2GS
{
    public class Player
    {
        public Point Location { get; set; }
        public string Name { get; }
        public uint Id { get; }
        public CharacterClass Class { get; }

        internal Player(AssignPlayerPacket assignPlayer)
        {
            Location = assignPlayer.Location;
            Name = assignPlayer.Name;
            Id = assignPlayer.Id;
            Class= assignPlayer.Class;
        }

        internal Player(PlayerInGamePacket playerInGame)
        {
            Name = playerInGame.Name;
            Id = playerInGame.Id;
            Class = playerInGame.Class;
        }
    }
}