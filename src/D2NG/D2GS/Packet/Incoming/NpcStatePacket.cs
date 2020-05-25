using D2NG.D2GS.Objects;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.D2GS.Packet
{
    internal class NpcStatePacket : D2gsPacket
    {
        public uint EntityId { get; }
        public EntityState EntityState { get; }

        public Point Location { get; }
        public byte EntityLife { get; }

        public NpcStatePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.NPCState)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            EntityId = reader.ReadUInt32();
            EntityState = (EntityState)reader.ReadByte();
            var x = reader.ReadUInt16();
            var y = reader.ReadUInt16();
            Location = new Point(x, y);
            EntityLife = reader.ReadByte();
        }
    }
}
