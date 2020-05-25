using D2NG.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.D2GS.Packet
{
    internal class ObjectStatePacket : D2gsPacket
    {
        public ObjectStatePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.ObjectState != (InComingPacket)id)
            {
                throw new D2GSPacketException("Invalid Packet Id");
            }
            ObjectType = reader.ReadByte();
            ObjectId = reader.ReadUInt32();
            reader.ReadByte();
            reader.ReadByte();
            State = (EntityState)reader.ReadUInt32();
        }

        public byte ObjectType { get; }
        public uint ObjectId { get; }

        public EntityState State { get; }
    }
}
