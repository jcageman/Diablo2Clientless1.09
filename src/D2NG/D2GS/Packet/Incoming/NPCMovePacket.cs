using System.IO;
using System.Text;

namespace D2NG.D2GS.Packet
{
    internal class NPCMovePacket : D2gsPacket
    {
        public uint EntityId { get; }
        public Point Location { get; }

        public NPCMovePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.NPCMove)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            EntityId = reader.ReadUInt32();
            reader.ReadByte();
            var x = reader.ReadUInt16();
            var y = reader.ReadUInt16();
            Location = new Point(x, y);
        }
    }
}
