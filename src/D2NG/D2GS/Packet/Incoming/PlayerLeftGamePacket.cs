using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2NG.D2GS.Packet
{ 
    internal class PlayerLeftGamePacket : D2gsPacket
    {
        public PlayerLeftGamePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.PlayerLeftGame != (InComingPacket)id)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            Id = reader.ReadUInt32();

        }
        public uint Id { get; }
    }
}
