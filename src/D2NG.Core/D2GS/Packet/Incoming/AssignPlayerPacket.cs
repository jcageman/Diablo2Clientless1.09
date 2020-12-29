using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.Extensions;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class AssignPlayerPacket : D2gsPacket
    {
        public AssignPlayerPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            if ((InComingPacket)reader.ReadByte() != InComingPacket.AssignPlayer)
            {
                throw new D2GSPacketException("Expected Packet Type Not Found");
            }
            Id = reader.ReadUInt32();
            Class = (CharacterClass)reader.ReadByte();
            Name = reader.ReadNullTerminatedString();
            var count = Name.Length + 1;
            while (count++ < 16)
            {
                reader.ReadByte();
            }
            Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
        }

        public Point Location { get; }
        public uint Id { get; }
        public CharacterClass Class { get; }
        public string Name { get; }
    }
}