using D2NG.D2GS.Objects;
using System.IO;
using System.Text;
using D2NG.D2GS.Exceptions;

namespace D2NG.D2GS.Packet.Incoming
{
    internal class AssignNpcPacket : D2gsPacket
    {
        public AssignNpcPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.AssignNPC2 != (InComingPacket)id && InComingPacket.AssignNPC1 != (InComingPacket)id)
            {
                throw new D2GSPacketException("Invalid Packet Id");
            }
            EntityId = reader.ReadUInt32();
            UniqueCode = (NPCCode)reader.ReadUInt16();
            Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
            reader.Close();
        }
        public uint EntityId { get; }
        public NPCCode UniqueCode { get; }
        public Point Location { get; }
    }
}
