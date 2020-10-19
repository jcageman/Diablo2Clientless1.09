using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class ReassignPlayerPacket : D2gsPacket
    {
        public ReassignPlayerPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.ReassignPlayer)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            UnitType = (EntityType)reader.ReadByte();
            UnitId = reader.ReadUInt32();
            Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
            _ = reader.ReadByte();
            reader.Close();
        }

        public EntityType UnitType { get; }
        public uint UnitId { get; }
        public Point Location { get; }
    }
}
