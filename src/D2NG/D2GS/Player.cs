﻿using D2NG.D2GS;

namespace D2NG
{
    public class Player
    {
        public Point Location { get; }
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
    }
}