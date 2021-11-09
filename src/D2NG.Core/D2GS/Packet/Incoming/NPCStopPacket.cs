using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class NPCStopPacket : D2gsPacket
    {
        public uint EntityId { get; }
        public Point Location { get; }

        public double LifePercentage { get; }
        public NPCStopPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.NPCStop)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            EntityId = reader.ReadUInt32();
            var x = reader.ReadUInt16();
            var y = reader.ReadUInt16();
            Location = new Point(x, y);
            LifePercentage = reader.ReadByte() / 1.28;
        }
    }
}
