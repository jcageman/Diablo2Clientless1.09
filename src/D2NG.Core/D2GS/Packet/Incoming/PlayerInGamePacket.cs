using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.Extensions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    internal class PlayerInGamePacket : D2gsPacket
    {
        public PlayerInGamePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.PlayerInGame != (InComingPacket)id)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            reader.ReadUInt16(); // packet length
            Id = reader.ReadUInt32();
            Class = (CharacterClass)reader.ReadByte();
            Name = reader.ReadNullTerminatedString();
        }
        public uint Id { get; }
        public CharacterClass Class { get; }
        public string Name { get; }
    }
}
