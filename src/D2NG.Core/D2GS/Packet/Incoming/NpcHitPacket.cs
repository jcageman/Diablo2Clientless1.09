using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    internal class NpcHitPacket : D2gsPacket
    {
        public uint EntityId { get; }

        public EntityType EntityType { get; }
        public EntityState EntityState { get; }

        public byte LifePercentage { get; }

        public NpcHitPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.NPCHit)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            EntityType = (EntityType)reader.ReadByte();
            EntityId = reader.ReadUInt32();
            EntityState = (EntityState)reader.ReadByte();
            reader.ReadByte();
            LifePercentage = reader.ReadByte();
        }
    }
}
